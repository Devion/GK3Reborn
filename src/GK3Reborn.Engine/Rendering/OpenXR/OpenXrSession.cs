using System.Numerics;
using System.Text;
using GK3Reborn.Rendering.VR;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace GK3Reborn.Rendering.OpenXR;

/// <summary>OpenXR lifecycle, predicted tracking and controller actions. Graphics resources
/// are attached by the renderer after querying the runtime's device requirements.</summary>
public sealed unsafe class OpenXrSession : IVrSession, IDisposable
{
    // The loader remains mapped for process lifetime, like the engine's other native APIs.
    private static readonly Lazy<XR> Loader = new(XR.GetApi);
    internal XR Api { get; }
    internal Instance Instance;
    internal Session Session;
    internal Space WorldSpace;
    internal Space ViewSpace;
    internal ulong SystemId;
    internal readonly View[] Views = new View[2];
    internal long DisplayTime { get; private set; }
    internal bool FrameOpen { get; private set; }
    internal bool ShouldRender { get; private set; }
    internal GraphicsRequirementsD3D12KHR Requirements { get; private set; }
    private bool _running;
    private bool _disposed;
    private bool _lost;
    private bool _roomPending = true;
    private long? _spaceChangeTime;
    
    private ActionSet _actions;
    private XrAction _aim, _trigger, _stick, _menu, _alternate, _inventory, _journal, _recenter, _grip, _squeeze, _indexTouch, _thumbTouch;
    private readonly Space[] _gripSpaces = new Space[2];
    private readonly ulong[] _hands = new ulong[2];
    private readonly Space[] _aimSpaces = new Space[2];
    private SessionState _state;
    public VrRig Rig { get; }
    public VrInput Input { get; private set; }
    public TeleportArc Teleport { get; } = new();
    public VrPreferences Preferences { get; set; } = new();
    public VrPanel Panel { get; } = new();
    public bool Transitioning { get; set; }
    public bool Focused => _state == SessionState.Focused && !_lost;
    public bool ExitRequested => _lost;
    public bool RoomPending => _roomPending;
    public bool ComfortBlocked { get; set; }

    private OpenXrSession(XR api, VrRig rig) { Api = api; Rig = rig; }

    public static OpenXrSession Create(float scale, VrCameraMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("VR currently requires Windows and Direct3D 12.");
        }
        XR api;
        try { api = Loader.Value; }
        catch (Exception error) when (error is DllNotFoundException or FileNotFoundException)
        {
            throw new InvalidOperationException("OpenXR loader missing. Install the packaged openxr_loader.dll and select Meta or SteamVR as the active OpenXR runtime.", error);
        }
        var result = new OpenXrSession(api, new VrRig(scale) { Mode = mode });
        try { result.Initialize(); return result; }
        catch { result.Dispose(); throw; }
    }

    private void Initialize()
    {
        uint count = 0;
        Check(Api.EnumerateInstanceExtensionProperties((byte*)null, 0, &count, null), "enumerate extensions");
        var extensions = new ExtensionProperties[count];
        for (int i = 0; i < extensions.Length; i++) { extensions[i].Type = StructureType.ExtensionProperties; }
        bool d3d12 = false;
        fixed (ExtensionProperties* values = extensions)
        {
            Check(Api.EnumerateInstanceExtensionProperties((byte*)null, count, &count, values), "enumerate extensions");
            for (int i = 0; i < count; i++)
            {
                if (Read(values[i].ExtensionName) == "XR_KHR_D3D12_enable") { d3d12 = true; }
            }
        }
        if (!d3d12) { throw new InvalidOperationException("The active OpenXR runtime does not offer Direct3D 12. Select the PC Meta or SteamVR runtime."); }
        byte[] extension = Encoding.UTF8.GetBytes("XR_KHR_D3D12_enable\0");
        fixed (byte* name = extension)
        {
            byte* enabled = name;
            var info = new InstanceCreateInfo { Type = StructureType.InstanceCreateInfo,
                EnabledExtensionCount = 1, EnabledExtensionNames = &enabled };
            Write(info.ApplicationInfo.ApplicationName, 128, "GK3Reborn");
            Write(info.ApplicationInfo.EngineName, 128, "GK3Reborn");
            info.ApplicationInfo.ApiVersion = 1UL << 48; // OpenXR 1.0 core; no optional vendor dependencies.
            Check(Api.CreateInstance(ref info, ref Instance), "create instance");
        }
        var system = new SystemGetInfo { Type = StructureType.SystemGetInfo, FormFactor = FormFactor.HeadMountedDisplay };
        Check(Api.GetSystem(Instance, ref system, ref SystemId), "find headset (connect it and start its PC runtime)");
        Silk.NET.Core.PfnVoidFunction function = default;
        Check(Api.GetInstanceProcAddr(Instance, "xrGetD3D12GraphicsRequirementsKHR", ref function), "get graphics requirements function");
        var requirements = new GraphicsRequirementsD3D12KHR { Type = StructureType.GraphicsRequirementsD3D12Khr };
        var get = (delegate* unmanaged[Stdcall]<Instance, ulong, GraphicsRequirementsD3D12KHR*, Result>)function.Handle;
        Check(get(Instance, SystemId, &requirements), "query graphics requirements");
        Requirements = requirements;
    }

    internal void Attach(void* device, void* queue)
    {
        var binding = new GraphicsBindingD3D12KHR { Type = StructureType.GraphicsBindingD3D12Khr, Device = device, Queue = queue };
        var info = new SessionCreateInfo { Type = StructureType.SessionCreateInfo, Next = &binding, SystemId = SystemId };
        Check(Api.CreateSession(Instance, ref info, ref Session), "create session");
        var space = new ReferenceSpaceCreateInfo { Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = ReferenceSpaceType.Stage, PoseInReferenceSpace = Identity };
        Result stage = Api.CreateReferenceSpace(Session, ref space, ref WorldSpace);
        if (stage == Result.ErrorReferenceSpaceUnsupported)
        {
            // LOCAL starts at eye level. Put its origin 1.65 m below it when STAGE is unavailable.
            space.ReferenceSpaceType = ReferenceSpaceType.Local;
            space.PoseInReferenceSpace.Position.Y = -1.65f;
            Check(Api.CreateReferenceSpace(Session, ref space, ref WorldSpace), "create local floor space");
        }
        else { Check(stage, "create stage space"); }
        space.ReferenceSpaceType = ReferenceSpaceType.View;
        space.PoseInReferenceSpace = Identity;
        Check(Api.CreateReferenceSpace(Session, ref space, ref ViewSpace), "create head space");
        CreateActions();
    }

    public void EnterRoom() { _roomPending = true; Teleport.Cancel(); if (Input.HeadTracked) { Panel.Place(Input.Head); } }
    public void PlacedRoom() => _roomPending = false;

    public bool BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (FrameOpen) { return ShouldRender; }
        PollEvents();
        if (!_running || _lost) { Input = default; Teleport.Cancel(); return false; }
        var wait = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        var state = new FrameState { Type = StructureType.FrameState };
        Check(Api.WaitFrame(Session, ref wait, ref state), "wait for headset frame");
        var begin = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        Check(Api.BeginFrame(Session, ref begin), "begin headset frame");
        FrameOpen = true;
        DisplayTime = state.PredictedDisplayTime;
        bool reanchorPanel = false;
        if (_spaceChangeTime is { } changedAt && DisplayTime >= changedAt)
        {
            EnterRoom(); _spaceChangeTime = null; reanchorPanel = true;
        }
        ShouldRender = state.ShouldRender != 0;
        var locate = new ViewLocateInfo { Type = StructureType.ViewLocateInfo,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo, DisplayTime = DisplayTime, Space = WorldSpace };
        var viewState = new ViewState { Type = StructureType.ViewState };
        uint count = 0;
        fixed (View* eyes = Views)
        {
            eyes[0].Type = eyes[1].Type = StructureType.View;
            Check(Api.LocateView(Session, ref locate, ref viewState, 2, &count, eyes), "locate eyes");
        }
        const ViewStateFlags valid = ViewStateFlags.PositionValidBit | ViewStateFlags.OrientationValidBit;
        ShouldRender &= count == 2 && (viewState.ViewStateFlags & valid) == valid;
        Input = ReadInput();
        if (reanchorPanel && Input.HeadTracked) { Panel.Place(Input.Head); }
        if (!Focused || !Input.HeadTracked) { Teleport.Cancel(); }
        return ShouldRender;
    }

    private void PollEvents()
    {
        while (true)
        {
            var data = new EventDataBuffer { Type = StructureType.EventDataBuffer };
            Result result = Api.PollEvent(Instance, ref data);
            if (result == Result.EventUnavailable) { break; }
            Check(result, "poll events");
            if (data.Type == StructureType.EventDataSessionStateChanged)
            {
                var changed = (EventDataSessionStateChanged*)&data;
                _state = changed->State;
                if (_state == SessionState.Ready)
                {
                    var begin = new SessionBeginInfo { Type = StructureType.SessionBeginInfo,
                        PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo };
                    Check(Api.BeginSession(Session, ref begin), "begin session");
                    _running = true;
                }
                else if (_state == SessionState.Stopping)
                {
                    Check(Api.EndSession(Session), "end session");
                    _running = false;
                }
                else if (_state is SessionState.Exiting or SessionState.LossPending) { _lost = true; _running = false; }
                if (!Focused) { Input = default; Teleport.Cancel(); }
            }
            else if (data.Type == StructureType.EventDataReferenceSpaceChangePending)
            {
                // Re-anchor on the next tracked frame instead of applying a recenter as locomotion.
                _spaceChangeTime = ((EventDataReferenceSpaceChangePending*)&data)->ChangeTime;
            }
            else if (data.Type == StructureType.EventDataInstanceLossPending) { _lost = true; _running = false; }
        }
    }

    internal void EndFrame(CompositionLayerBaseHeader** layers, uint count)
    {
        if (!FrameOpen) { return; }
        FrameOpen = false;
        var end = new FrameEndInfo { Type = StructureType.FrameEndInfo, DisplayTime = DisplayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque, LayerCount = count, Layers = layers };
        Check(Api.EndFrame(Session, ref end), "submit headset frame");
    }

    private void CreateActions()
    {
        _hands[0] = Path("/user/hand/left"); _hands[1] = Path("/user/hand/right");
        var set = new ActionSetCreateInfo { Type = StructureType.ActionSetCreateInfo };
        Write(set.ActionSetName, 64, "gameplay"); Write(set.LocalizedActionSetName, 128, "Gameplay");
        Check(Api.CreateActionSet(Instance, ref set, ref _actions), "create action set");
        _aim = Action("aim", ActionType.PoseInput);
        _trigger = Action("trigger", ActionType.FloatInput);
        _stick = Action("stick", ActionType.Vector2fInput);
        _menu = Action("menu", ActionType.BooleanInput);
        _alternate = Action("alternate", ActionType.BooleanInput);
        _inventory = Action("inventory", ActionType.BooleanInput);
        _journal = Action("journal", ActionType.BooleanInput);
        _recenter = Action("recenter_panel", ActionType.BooleanInput);
        _grip = Action("hand_pose", ActionType.PoseInput);
        _squeeze = Action("grasp", ActionType.FloatInput);
        _indexTouch = Action("index_touch", ActionType.BooleanInput);
        _thumbTouch = Action("thumb_touch", ActionType.BooleanInput);
        Suggest("/interaction_profiles/oculus/touch_controller", "thumbstick", "menu/click", "b/click");
        Suggest("/interaction_profiles/valve/index_controller", "thumbstick", "b/click", "b/click");
        Suggest("/interaction_profiles/htc/vive_controller", "trackpad", "menu/click", "menu/click");
        Suggest("/interaction_profiles/microsoft/motion_controller", "thumbstick", "menu/click", "menu/click");
        ActionSet actions = _actions;
        var attach = new SessionActionSetsAttachInfo { Type = StructureType.SessionActionSetsAttachInfo,
            CountActionSets = 1, ActionSets = &actions };
        Check(Api.AttachSessionActionSets(Session, ref attach), "attach actions");
        for (int i = 0; i < 2; i++)
        {
            var space = new ActionSpaceCreateInfo { Type = StructureType.ActionSpaceCreateInfo,
                Action = _aim, SubactionPath = _hands[i], PoseInActionSpace = Identity };
            Check(Api.CreateActionSpace(Session, ref space, ref _aimSpaces[i]), "create controller space");
            space.Action = _grip;
            Check(Api.CreateActionSpace(Session, ref space, ref _gripSpaces[i]), "create hand space");
        }
    }

    private XrAction Action(string name, ActionType type)
    {
        fixed (ulong* hands = _hands)
        {
            var info = new ActionCreateInfo { Type = StructureType.ActionCreateInfo, ActionType = type,
                CountSubactionPaths = 2, SubactionPaths = hands };
            Write(info.ActionName, 64, name); Write(info.LocalizedActionName, 128, name);
            XrAction action = default;
            Check(Api.CreateAction(_actions, ref info, ref action), "create " + name);
            return action;
        }
    }

    private void Suggest(string profile, string stick, string menu, string alternate)
    {
        var bindings = new List<ActionSuggestedBinding>();
        foreach (string side in new[] { "left", "right" })
        {
            bindings.Add(new(_aim, Path($"/user/hand/{side}/input/aim/pose")));
            bindings.Add(new(_grip, Path($"/user/hand/{side}/input/grip/pose")));
            string squeeze = profile.Contains("vive_controller") || profile.Contains("motion_controller") ? "squeeze/click" : "squeeze/value";
            bindings.Add(new(_squeeze, Path($"/user/hand/{side}/input/{squeeze}")));
            if (profile.Contains("oculus") || profile.Contains("index_controller"))
            {
                bindings.Add(new(_indexTouch, Path($"/user/hand/{side}/input/trigger/touch")));
                bindings.Add(new(_thumbTouch, Path($"/user/hand/{side}/input/thumbstick/touch")));
                if (profile.Contains("oculus"))
                {
                    bindings.Add(new(_thumbTouch, Path($"/user/hand/{side}/input/thumbrest/touch")));
                    foreach (string button in side == "left" ? new[] { "x", "y" } : new[] { "a", "b" })
                    {
                        bindings.Add(new(_thumbTouch, Path($"/user/hand/{side}/input/{button}/touch")));
                    }
                }
            }
            bindings.Add(new(_trigger, Path($"/user/hand/{side}/input/trigger/value")));
            bindings.Add(new(_stick, Path($"/user/hand/{side}/input/{stick}")));
        }
        bindings.Add(new(_menu, Path($"/user/hand/left/input/{menu}")));
        bindings.Add(new(_alternate, Path($"/user/hand/right/input/{alternate}")));
        bool touch = profile.Contains("oculus");
        bool index = profile.Contains("index_controller");
        string inventory = touch || index ? "a/click" : stick + "/click";
        string journal = touch ? "x/click" : index ? "a/click" : stick + "/click";
        bindings.Add(new(_inventory, Path("/user/hand/right/input/" + inventory)));
        bindings.Add(new(_journal, Path("/user/hand/left/input/" + journal)));
        if (touch) { bindings.Add(new(_recenter, Path("/user/hand/left/input/y/click"))); }
        ActionSuggestedBinding[] array = [.. bindings];
        fixed (ActionSuggestedBinding* values = array)
        {
            var info = new InteractionProfileSuggestedBinding { Type = StructureType.InteractionProfileSuggestedBinding,
                InteractionProfile = Path(profile), CountSuggestedBindings = (uint)array.Length, SuggestedBindings = values };
            Result result = Api.SuggestInteractionProfileBinding(Instance, ref info);
            if (result != Result.ErrorPathUnsupported) { Check(result, "bind " + profile); }
        }
    }

    private VrInput ReadInput()
    {
        (VrPose head, bool tracked) = Locate(ViewSpace);
        if (!Focused) { return new VrInput(head, default, default, tracked, false, false, 0, 0, default, default, false, false); }
        var active = new ActiveActionSet { ActionSet = _actions };
        var sync = new ActionsSyncInfo { Type = StructureType.ActionsSyncInfo, CountActiveActionSets = 1, ActiveActionSets = &active };
        Result result = Api.SyncAction(Session, ref sync);
        if (result == Result.SessionNotFocused) { return default; }
        Check(result, "sync controllers");
        (VrPose left, bool leftTracked) = Locate(_aimSpaces[0]);
        (VrPose right, bool rightTracked) = Locate(_aimSpaces[1]);
        (VrPose leftGrip, bool leftGripTracked) = Locate(_gripSpaces[0]);
        (VrPose rightGrip, bool rightGripTracked) = Locate(_gripSpaces[1]);
        return new VrInput(head, left, right, tracked, leftTracked, rightTracked,
            Float(_trigger, 0), Float(_trigger, 1), Stick(0), Stick(1), Boolean(_menu, 0), Boolean(_alternate, 1))
        {
            Inventory = Boolean(_inventory, 1), Journal = Boolean(_journal, 0), Recenter = Boolean(_recenter, 0),
            LeftGrip = leftGrip, RightGrip = rightGrip, LeftGripTracked = leftGripTracked, RightGripTracked = rightGripTracked,
            LeftSqueeze = Float(_squeeze, 0), RightSqueeze = Float(_squeeze, 1),
            LeftIndexTouch = Boolean(_indexTouch, 0), RightIndexTouch = Boolean(_indexTouch, 1),
            LeftThumbTouch = Boolean(_thumbTouch, 0), RightThumbTouch = Boolean(_thumbTouch, 1),
        };
    }

    private (VrPose, bool) Locate(Space space)
    {
        var location = new SpaceLocation { Type = StructureType.SpaceLocation };
        Check(Api.LocateSpace(space, WorldSpace, DisplayTime, ref location), "locate tracking space");
        const SpaceLocationFlags valid = SpaceLocationFlags.PositionValidBit | SpaceLocationFlags.OrientationValidBit
            | SpaceLocationFlags.PositionTrackedBit | SpaceLocationFlags.OrientationTrackedBit;
        return (Pose(location.Pose), (location.LocationFlags & valid) == valid);
    }
    private float Float(XrAction action, int hand)
    {
        var info = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = action, SubactionPath = _hands[hand] };
        var state = new ActionStateFloat { Type = StructureType.ActionStateFloat };
        Check(Api.GetActionStateFloat(Session, ref info, ref state), "read trigger");
        return state.IsActive != 0 ? state.CurrentState : 0f;
    }
    private bool Boolean(XrAction action, int hand)
    {
        var info = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = action, SubactionPath = _hands[hand] };
        var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
        Check(Api.GetActionStateBoolean(Session, ref info, ref state), "read button");
        return state.IsActive != 0 && state.CurrentState != 0;
    }
    private Vector2 Stick(int hand)
    {
        var info = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = _stick, SubactionPath = _hands[hand] };
        var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };
        Check(Api.GetActionStateVector2(Session, ref info, ref state), "read stick");
        return state.IsActive != 0 ? new Vector2(state.CurrentState.X, state.CurrentState.Y) : Vector2.Zero;
    }
    private ulong Path(string name) { ulong path = 0; Check(Api.StringToPath(Instance, name, ref path), "resolve " + name); return path; }
    internal static VrPose Pose(Posef pose) => new(new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
        new Quaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W));
    internal static Posef Identity => new() { Orientation = new Quaternionf(0, 0, 0, 1) };
    internal static Vector4 Tangents(Fovf fov) => new(MathF.Tan(fov.AngleLeft), MathF.Tan(fov.AngleRight), MathF.Tan(fov.AngleDown), MathF.Tan(fov.AngleUp));
    internal static void Check(Result result, string operation)
    {
        if ((int)result < 0)
        {
            string help = result is Result.ErrorRuntimeUnavailable or Result.ErrorFormFactorUnavailable
                ? " Connect the headset, start Meta Quest Link or SteamVR, and select it as the active OpenXR runtime." : string.Empty;
            throw new InvalidOperationException($"OpenXR could not {operation}: {result}.{help}");
        }
    }
    private static string Read(byte* text) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;
    private static void Write(byte* target, int capacity, string value)
    {
        Span<byte> destination = new(target, capacity); destination.Clear();
        Encoding.UTF8.GetBytes(value, destination[..(capacity - 1)]);
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (FrameOpen)
        {
            try { EndFrame(null, 0); }
            catch (InvalidOperationException error) { Foundation.Diagnostics.Log.Warning(error.Message); }
        }
        foreach (Space space in _aimSpaces.Concat(_gripSpaces)) { if (space.Handle != 0) { Api.DestroySpace(space); } }
        if (ViewSpace.Handle != 0) { Api.DestroySpace(ViewSpace); }
        if (WorldSpace.Handle != 0) { Api.DestroySpace(WorldSpace); }
        if (Session.Handle != 0) { Api.DestroySession(Session); }
        if (_actions.Handle != 0) { Api.DestroyActionSet(_actions); }
        if (Instance.Handle != 0) { Api.DestroyInstance(Instance); }
    }
}
