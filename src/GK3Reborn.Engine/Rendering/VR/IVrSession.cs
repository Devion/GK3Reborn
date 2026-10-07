namespace GK3Reborn.Rendering.VR;

/// <summary>Tracking consumed by gameplay; native images remain in the renderer.</summary>
public interface IVrSession
{
    VrRig Rig { get; }
    VrInput Input { get; }
    TeleportArc Teleport { get; }
    VrPreferences Preferences { get; set; }
    VrPanel Panel { get; }
    bool Transitioning { get; set; }
    bool Focused { get; }
    bool ExitRequested => false;
    bool RoomPending { get; }
    bool ComfortBlocked { get; set; }
    void PlacedRoom();
    bool BeginFrame();
    void EnterRoom();
}
