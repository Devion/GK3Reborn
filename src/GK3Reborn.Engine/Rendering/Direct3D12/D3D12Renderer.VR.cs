using System.Numerics;
using GK3Reborn.Rendering.OpenXR;
using GK3Reborn.Rendering.Shaders;
using GK3Reborn.Rendering.Upscaling;
using GK3Reborn.Rendering.VR;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.OpenXR;

namespace GK3Reborn.Rendering.Direct3D12;

public sealed unsafe partial class D3D12Renderer
{
    private OpenXrSession? _vr;
    private readonly D3D12VrSwapchain?[] _eyes = new D3D12VrSwapchain?[2];
    private D3D12VrSwapchain? _vrPanel;
    private D3D12VrSwapchain? _vrWrist;
    private D3D12ScreenPass? _vrOutput, _vrMovie, _vrFade;
    private Format _vrFormat;
    private IReadOnlyList<Particle> _roomParticles = [];
    public IVrSession? VirtualReality => _vr;

    private void OpenVr(OpenXrSession session)
    {
        _vr = session;
        try
        {
            session.Attach(_context.Device, _context.Queue);
            uint count = 0;
            OpenXrSession.Check(session.Api.EnumerateSwapchainFormats(session.Session, 0, &count, null), "count formats");
            long[] formats = new long[count];
            fixed (long* values = formats)
            { OpenXrSession.Check(session.Api.EnumerateSwapchainFormats(session.Session, count, &count, values), "get formats"); }
            _vrFormat = formats.Contains((long)Format.FormatR8G8B8A8UnormSrgb) ? Format.FormatR8G8B8A8UnormSrgb : Format.FormatB8G8R8A8UnormSrgb;
            if (!formats.Contains((long)_vrFormat)) { throw new InvalidOperationException("OpenXR runtime offers no supported sRGB colour format."); }
            var views = new ViewConfigurationView[2];
            views[0].Type = views[1].Type = StructureType.ViewConfigurationView;
            fixed (ViewConfigurationView* values = views)
            {
                OpenXrSession.Check(session.Api.EnumerateViewConfigurationView(session.Instance, session.SystemId,
                    ViewConfigurationType.PrimaryStereo, 2, &count, values), "get stereo configuration");
            }
            if (count != 2) { throw new InvalidOperationException("OpenXR runtime does not offer two stereo views."); }
            for (int eye = 0; eye < 2; eye++)
            {
                _eyes[eye] = new D3D12VrSwapchain(session, _context,
                    (int)views[eye].RecommendedImageRectWidth, (int)views[eye].RecommendedImageRectHeight, _vrFormat);
            }
            _vrPanel = new D3D12VrSwapchain(session, _context, 1280, 720, _vrFormat);
            _vrWrist = new D3D12VrSwapchain(session, _context, 640, 360, _vrFormat);
            _vrOutput = D3D12ScreenPass.Create(_context, _pipeline.Compiler, OutputShaders.Vertex, OutputShaders.Fragment,
                "vr-output", 2, 48, [_vrFormat]);
            _vrMovie = D3D12ScreenPass.Create(_context, _pipeline.Compiler, MovieShaders.Vertex, MovieShaders.Fragment,
                "vr-movie", 1, 32, [_vrFormat]);
            _vrFade = D3D12ScreenPass.Create(_context, _pipeline.Compiler, FadeShaders.Vertex, FadeShaders.Fragment,
                "vr-fade", 0, 32, [_vrFormat], blend: true);
        }
        catch { CloseVr(); throw; }
    }

    private bool DrawVr(float red, float green, float blue)
    {
        OpenXrSession vr = _vr!;
        bool submitted = false;
        try
        {
            if (!vr.BeginFrame()) { return false; }
            if (_needsRecreate) { Recreate(); }
            Retarget();
            _ring.Wait();
            float elapsed = Pace();
            _scene?.TrackedHands?.Update(vr, elapsed);
            _scene?.Settle(); _scene?.Flush((int)_ring.Index);
            _pipeline.Upscaling = UpscalePlan.None;
            _pipeline.Reflections = new ReflectionPlan(0f, false);
            _pipeline.Frames.Seconds = (float)_wind.Elapsed.TotalSeconds;
            _pipeline.Frames.EmissiveGain = _output_.EmissiveGain;
            _pipeline.DeltaSeconds = elapsed;
            Camera template = _camera ?? new Camera();
            if (!vr.Rig.Placed && vr.Input.HeadTracked)
            {
                vr.Rig.Place(template.Position, 0, vr.Input.Head, vr.Rig.Mode);
            }
            if (!vr.Panel.Placed && vr.Input.HeadTracked) { vr.Panel.Place(vr.Input.Head); }
            vr.Panel.Wrist.Track(vr.Input, vr.Focused, vr.Transitioning);
            SetVrParticles(vr);
            CompositionLayerProjectionView* projectionViews = stackalloc CompositionLayerProjectionView[2];
            for (int eye = 0; eye < 2; eye++)
            {
                D3D12VrSwapchain target = _eyes[eye]!;
                View view = vr.Views[eye];
                Camera camera = vr.Rig.Eye(OpenXrSession.Pose(view.Pose), OpenXrSession.Tangents(view.Fov), template);
                // A single serialized raster pipeline avoids sharing temporal history between eyes.
                _pipeline.Reset = true;
                _pipeline.Prepare(target.Width, target.Height, (red, green, blue), camera, _scene);
                target.Acquire();
                ID3D12GraphicsCommandList4* list = _ring.Begin();
                FramePicture picture = _pipeline.Draw(list, _scene, camera, (red, green, blue));
                _vrOutput!.Draw(list, [target.Target], [picture.Colour, _pipeline.Guides ?? picture.Colour],
                    new OutputTuning(new Vector4(0, 200, 1, (float)_output_.ToneMap), Vector4.Zero, Vector4.Zero), target.Width, target.Height);
                if (Fade > 0 || vr.ComfortBlocked)
                {
                    _vrFade!.Draw(list, [target.Target], [],
                        new FadeConstants(new Vector4(FadeColour, MathF.Max(Fade, vr.ComfortBlocked ? 0.65f : 0)), DisplayEncode.Standard), target.Width, target.Height);
                }
                // A spectator sees the left-eye image without a third scene render.
                bool mirror = eye == 0 && !_needsRecreate && _window.FramebufferWidth > 0 && _window.FramebufferHeight > 0;
                if (mirror)
                {
                    uint buffer = _swapchain.CurrentBuffer;
                    _swapchain.Transition(list, buffer, ResourceStates.RenderTarget);
                    DisplayEncode display = Encoding();
                    _output!.Draw(list, [_swapchain.RenderTarget(buffer)], [picture.Colour, _pipeline.Guides ?? picture.Colour],
                        new OutputTuning(new Vector4(display.Transfer, display.PaperWhite, display.Headroom, (float)_output_.ToneMap),
                            Vector4.Zero, Vector4.Zero), _swapchain.Size.Width, _swapchain.Size.Height);
                    _swapchain.Transition(list, buffer, ResourceStates.Present);
                }
                _ring.Submit(); _ring.Wait();
                if (mirror) { _needsRecreate |= !_swapchain.Present(false); _presentedAnything = true; }
                target.Release();
                projectionViews[eye] = new CompositionLayerProjectionView
                {
                    Type = StructureType.CompositionLayerProjectionView,
                    Pose = view.Pose,
                    Fov = view.Fov,
                    SubImage = target.SubImage
                };
            }
            _scene?.Advance();
            var projection = new CompositionLayerProjection
            {
                Type = StructureType.CompositionLayerProjection,
                Space = vr.WorldSpace,
                ViewCount = 2,
                Views = projectionViews
            };
            CompositionLayerBaseHeader** layers = stackalloc CompositionLayerBaseHeader*[3];
            layers[0] = (CompositionLayerBaseHeader*)&projection;
            uint layerCount = 1;
            var panel = new CompositionLayerQuad();
            if (!vr.Transitioning && (_list is not null || _film is not null))
            {
                DrawVrPanel();
                panel = new CompositionLayerQuad
                {
                    Type = StructureType.CompositionLayerQuad,
                    Space = vr.WorldSpace,
                    EyeVisibility = EyeVisibility.Both,
                    LayerFlags = CompositionLayerFlags.BlendTextureSourceAlphaBit,
                    SubImage = _vrPanel!.SubImage,
                    Pose = new Posef
                    {
                        Position = new Vector3f(vr.Panel.Pose.Position.X, vr.Panel.Pose.Position.Y, vr.Panel.Pose.Position.Z),
                        Orientation = new Quaternionf(vr.Panel.Pose.Orientation.X, vr.Panel.Pose.Orientation.Y,
                            vr.Panel.Pose.Orientation.Z, vr.Panel.Pose.Orientation.W),
                    },
                    Size = new Extent2Df(VrPanel.Width, VrPanel.Height)
                };

                layers[1] = (CompositionLayerBaseHeader*)&panel; layerCount++;
            }
            var wrist = new CompositionLayerQuad();
            if (vr.Panel.Wrist is { Visible: true, Overlay: { } wristOverlay } device)
            {
                DrawVrWrist(wristOverlay);
                wrist = new CompositionLayerQuad
                {
                    Type = StructureType.CompositionLayerQuad,
                    Space = vr.WorldSpace,
                    EyeVisibility = EyeVisibility.Both,
                    LayerFlags = CompositionLayerFlags.BlendTextureSourceAlphaBit,
                    SubImage = _vrWrist!.SubImage,
                    Pose = new Posef
                    {
                        Position = new Vector3f(device.Pose.Position.X, device.Pose.Position.Y, device.Pose.Position.Z),
                        Orientation = new Quaternionf(device.Pose.Orientation.X, device.Pose.Orientation.Y,
                            device.Pose.Orientation.Z, device.Pose.Orientation.W),
                    },
                    Size = new Extent2Df(VrWristPanel.Width, VrWristPanel.Height)
                };
                layers[layerCount++] = (CompositionLayerBaseHeader*)&wrist;
            }
            vr.EndFrame(layers, layerCount);
            submitted = true;
            return true;
        }
        finally
        {
            if (!submitted && vr.FrameOpen)
            {
                _ring.Wait();
                foreach (D3D12VrSwapchain? eye in _eyes) { eye?.Release(); }
                _vrPanel?.Release();
                _vrWrist?.Release();
                vr.EndFrame(null, 0);
            }
        }
    }

    private void DrawVrPanel()
    {
        D3D12VrSwapchain target = _vrPanel!;
        target.Acquire();
        ID3D12GraphicsCommandList4* list = _ring.Begin();
        float* clear = stackalloc float[4];
        clear[0] = clear[1] = clear[2] = clear[3] = 0;
        list->ClearRenderTargetView(target.Target, clear, 0, null);
        if (_film is not null)
        {
            _film.Transition(list, ResourceStates.AllShaderResource);
            (float sx, float sy) = PictureFit.Fit(_film.Width, _film.Height, target.Width, target.Height, _coverFilm);
            _vrMovie!.Draw(list, [target.Target], [_film], new MovieConstants(new Vector4(sx, sy, 0, 0), DisplayEncode.Standard), target.Width, target.Height);
        }
        if (_list is not null)
        {
            _overlay.Retarget(_vrFormat);
            _overlay.Display = DisplayEncode.Standard;
            Overlay panelList = _window is Platform.SilkGameWindow window
                ? _list.WithPointer(window.PointerPosition * window.DpiScale, _vr!.Panel) : _list;
            _overlay.Prepare(panelList, _ring.Index);
            _overlay.Record(list, target.Target, target.Width, target.Height);
        }
        _ring.Submit(); _ring.Wait(); target.Release();
    }

    private void DrawVrWrist(Overlay overlay)
    {
        D3D12VrSwapchain target = _vrWrist!;
        target.Acquire();
        _overlay.Retarget(_vrFormat);
        _overlay.Display = DisplayEncode.Standard;
        _overlay.Prepare(overlay, _ring.Index);
        ID3D12GraphicsCommandList4* list = _ring.Begin();
        float* clear = stackalloc float[4];
        clear[0] = clear[1] = clear[2] = clear[3] = 0;
        list->ClearRenderTargetView(target.Target, clear, 0, null);
        _overlay.Record(list, target.Target, target.Width, target.Height);
        _ring.Submit(); _ring.Wait(); target.Release();
    }

    private void SetVrParticles(OpenXrSession vr)
    {
        var particles = new List<Particle>(_roomParticles);
        Vector4 tint = vr.Teleport.Destination is null ? new Vector4(1, 0.1f, 0.05f, 1) : new Vector4(0.05f, 0.8f, 1, 1);
        IReadOnlyList<Vector3> points = vr.Teleport.Points;
        float spacing = vr.Rig.UnitsPerMetre * 0.015f;
        for (int i = 1; i < points.Count; i++)
        {
            float length = Vector3.Distance(points[i - 1], points[i]);
            int steps = Math.Max(1, (int)MathF.Ceiling(length / spacing));
            for (int step = 0; step < steps; step++)
            { particles.Add(new Particle(Vector3.Lerp(points[i - 1], points[i], (float)step / steps), spacing, tint, 0, 1)); }
        }
        if (vr.Teleport.Destination is { } destination)
        {
            for (int i = 0; i < 64; i++)
            {
                float angle = i * MathF.Tau / 64;
                particles.Add(new Particle(destination + new Vector3(MathF.Cos(angle) * 0.2f, 0.015f, MathF.Sin(angle) * 0.2f) * vr.Rig.UnitsPerMetre,
                    spacing, tint, 0, 1));
            }
        }
        if (vr.Input.RightTracked && vr.Focused && !vr.Transitioning)
        {
            Ray ray = vr.Rig.Aim(vr.Input.RightAim);
            Vector3 end = vr.Panel.WorldTarget ?? ray.Origin + ray.Direction * (vr.Rig.UnitsPerMetre * 3);
            if (vr.Panel.Interactive && vr.Panel.Hit(vr.Input.RightAim) is { } hit)
            {
                Vector3 local = new((hit.X - 0.5f) * VrPanel.Width, (0.5f - hit.Y) * VrPanel.Height, 0);
                end = vr.Rig.Point(vr.Panel.Pose.Position + Vector3.Transform(local, vr.Panel.Pose.Orientation));
            }
            if (vr.Panel.Wrist.Hit(vr.Input.RightAim) is { } wristHit)
            {
                Vector3 local = new((wristHit.X - 0.5f) * VrWristPanel.Width, (0.5f - wristHit.Y) * VrWristPanel.Height, 0);
                end = vr.Rig.Point(vr.Panel.Wrist.Pose.Position + Vector3.Transform(local, vr.Panel.Wrist.Pose.Orientation));
            }
            for (int i = 0; i <= 100; i++)
            {
                particles.Add(new Particle(Vector3.Lerp(ray.Origin, end, i / 100f), vr.Rig.UnitsPerMetre * 0.003f,
                    new Vector4(0.15f, 0.8f, 1, 0.7f), 0, 1));
            }
            particles.Add(new Particle(end, vr.Rig.UnitsPerMetre * 0.012f, Vector4.One, 0, 1));
        }
        _pipeline.SetParticles(particles);
    }

    private void CloseVr()
    {
        _vrOutput?.Dispose(); _vrOutput = null;
        _vrMovie?.Dispose(); _vrMovie = null;
        _vrFade?.Dispose(); _vrFade = null;
        foreach (D3D12VrSwapchain? eye in _eyes) { eye?.Dispose(); }
        _vrPanel?.Dispose(); _vrPanel = null;
        _vrWrist?.Dispose(); _vrWrist = null;
        // The runtime's graphics binding must outlive its session.
        _vr?.Dispose();
        _vr = null;
    }
}
