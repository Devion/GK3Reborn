using System.Diagnostics;
using System.Numerics;
using GK3Reborn.Platform;

namespace GK3Reborn.Rendering.VR;

/// <summary>Routes headset controls through the same input paths as desktop UI.</summary>
public sealed class VrWindowControls(IVrSession session, ISyntheticInput window)
{
    private VrInput _previous;
    private bool _armed, _selected;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _nextScroll;

    public void Poll()
    {
        session.BeginFrame();
        if (session.ExitRequested) { window.RequestClose(); return; }
        VrInput input = session.Input;
        VrPanel panel = session.Panel;
        if (!session.Focused || !input.HeadTracked || session.Transitioning)
        {
            window.ExternalPrimaryHeld = false;
            panel.PointerOnPanel = false;
            _selected = _armed = false;
            _previous = input;
            return;
        }
        if (!panel.Placed) { panel.Place(input.Head); }
        if (!_armed)
        {
            _armed = input.Select < 0.35f && !input.Menu && !input.Alternate && !input.Inventory && !input.Journal && !input.Recenter;
            _previous = input;
            return;
        }
        if (input.Menu && !_previous.Menu) { panel.Place(input.Head); Back(); }
        if (input.Alternate && !_previous.Alternate)
        {
            if (panel.Keyboard) { panel.Keyboard = false; }
            else if (panel.WorldMenu || !panel.Interactive) { window.Press(PointerButton.Secondary); }
            else { Back(); }
        }
        if (input.Inventory && !_previous.Inventory) { panel.Place(input.Head); window.Press(CameraAction.Inventory); }
        if (input.Journal && !_previous.Journal) { panel.Place(input.Head); window.Press(CameraAction.Journal); }
        if (input.Recenter && !_previous.Recenter) { panel.Place(input.Head); }
        Vector2? hit = input.RightTracked ? panel.Hit(input.RightAim) : null;
        panel.PointerOnPanel = hit is not null;
        if (hit is { } point)
        {
            window.MovePointer(new Vector2(point.X * window.FramebufferWidth, point.Y * window.FramebufferHeight) / window.DpiScale);
        }
        else
        {
            // Never leave a clickable cursor on the last row when the ray has left the panel.
            window.MovePointer(new Vector2(-1000, -1000));
        }
        bool selected = input.RightTracked && (input.Select > 0.65f || (_selected && input.Select > 0.35f));
        if (selected && !_selected)
        {
            if (!panel.Interactive)
            {
                window.Press(PointerButton.Secondary);
            }
            else if (hit is { } keyPoint && panel.KeyAt(keyPoint) is { } key)
            {
                switch (key)
                {
                    case "keyboard": panel.Keyboard = !panel.Keyboard; break;
                    case "backspace": window.Press(EditKey.Backspace); break;
                    case "enter": window.Press(EditKey.Enter); break;
                    case "space": window.Type(" "); break;
                    default: window.Type(key); break;
                }
            }
            else if (hit is not null) { window.Press(PointerButton.Primary); }
        }
        window.ExternalPrimaryHeld = panel.Interactive && hit is { } heldAt && panel.KeyAt(heldAt) is null && selected;
        if (panel.Interactive && MathF.Abs(input.Turn.Y) > 0.5f && _clock.Elapsed.TotalSeconds >= _nextScroll)
        {
            window.Scroll(MathF.Sign(input.Turn.Y));
            _nextScroll = _clock.Elapsed.TotalSeconds + 0.12;
        }
        _selected = selected;
        _previous = input;
    }

    private void Back()
    {
        window.Press(EditKey.Escape);
        window.Press(CameraAction.Quit);
    }
}
