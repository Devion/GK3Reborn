using System.Numerics;

namespace GK3Reborn.Rendering.VR;

/// <summary>A collision point on one segment of the teleport preview.</summary>
public readonly record struct TeleportHit(Vector3 Point, bool Walkable);

/// <summary>Ballistic preview and release-to-commit input, shared by every headset.</summary>
public sealed class TeleportArc
{
    private readonly List<Vector3> _points = [];
    private bool _held;
    private bool _armed = true;
    public IReadOnlyList<Vector3> Points => _points;
    public Vector3? Destination { get; private set; }

    public void Cancel()
    {
        _held = false;
        _armed = false;
        Destination = null;
        _points.Clear();
    }

    public Vector3? Update(float trigger, bool allowed, Ray aim, float scale,
        Func<Vector3, Vector3, TeleportHit?> collide)
    {
        ArgumentNullException.ThrowIfNull(collide);
        if (!allowed || !float.IsFinite(trigger))
        {
            Cancel();
            return null;
        }
        bool down = trigger >= (_held ? 0.35f : 0.65f);
        if (!down)
        {
            Vector3? result = _held && _armed ? Destination : null;
            _held = false;
            _armed = true;
            Destination = null;
            _points.Clear();
            return result;
        }
        if (!_armed)
        {
            return null;
        }
        _held = true;
        Destination = null;
        _points.Clear();
        _points.Add(aim.Origin);
        Vector3 velocity = aim.Direction * (6f * scale);
        Vector3 gravity = -Vector3.UnitY * (9.81f * scale);
        for (int segment = 1; segment <= 100; segment++)
        {
            float time = segment * 0.025f;
            Vector3 next = aim.Origin + velocity * time + gravity * (0.5f * time * time);
            if (collide(_points[^1], next) is { } hit)
            {
                _points.Add(hit.Point);
                if (hit.Walkable)
                {
                    Destination = hit.Point;
                }
                break;
            }
            _points.Add(next);
        }
        return null;
    }
}
