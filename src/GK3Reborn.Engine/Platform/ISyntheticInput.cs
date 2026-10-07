using System.Numerics;

namespace GK3Reborn.Platform;

/// <summary>Input from tracked controllers, using the desktop UI's event paths.</summary>
public interface ISyntheticInput
{
    int FramebufferWidth { get; }
    int FramebufferHeight { get; }
    float DpiScale { get; }
    bool ExternalPrimaryHeld { get; set; }
    void RequestClose();
    void MovePointer(Vector2 position);
    void Press(PointerButton button);
    void Press(EditKey key);
    void Press(CameraAction action);
    void Scroll(int amount);
    void Type(string text);
}
