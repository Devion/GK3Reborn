using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;

namespace GK3Reborn.Rendering.VR;

/// <summary>Places tracking in a live room and commits movement through its navigation rules.</summary>
public sealed class VrLocomotion
{
    private Vector3? _lastEye;

    public Camera Update(IVrSession session, Camera authored, FirstPerson walker,
        SceneUpdate update, string ego, SceneInteraction interaction, bool allowed, float seconds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(interaction);
        VrInput input = session.Input;
        VrRig rig = session.Rig;
        VrPreferences preferences = session.Preferences.Clamped();
        rig.HeightOffsetMetres = preferences.HeightOffsetMetres;
        if (!input.HeadTracked)
        {
            session.Teleport.Cancel(); session.ComfortBlocked = true;
            return authored;
        }
        bool firstPerson = rig.Mode == VrCameraMode.FirstPerson;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ego, update.ModelNamed(ego)?.Name ?? ego };
        Vector3 actor = update.Where(ego) ?? update.ModelNamed(ego)?.Standing.Translation ?? walker.Position;
        if (session.RoomPending || !rig.Placed)
        {
            float heading = firstPerson ? update.Turned(ego) ?? walker.Yaw
                : MathF.Atan2(authored.Target.X - authored.Position.X, authored.Target.Z - authored.Position.Z);
            rig.Place(firstPerson ? actor : authored.Position, heading, input.Head, rig.Mode);
            session.PlacedRoom(); _lastEye = null;
        }
        // Scripts animate actors and authored cameras, never the physical playspace.
        // A scene change places it once; only explicit player locomotion moves it thereafter.
        bool controls = allowed && session.Focused;
        if (controls)
        {
            if (preferences.SmoothTurning) { rig.SmoothTurn(input.Turn.X, input.Head, preferences.TurnDegreesPerSecond, seconds); }
            else { rig.SnapTurn(input.Turn.X, input.Head); }
        }
        else { session.Teleport.Cancel(); }
        if (controls && preferences.Locomotion is VrLocomotionMode.Smooth or VrLocomotionMode.Both)
        {
            Vector2 stick = FirstPerson.Pushed(input.Move);
            Vector3 forward = rig.Direction(input.Head, -Vector3.UnitZ); forward.Y = 0;
            if (forward.LengthSquared() > 0.001f)
            {
                forward = Vector3.Normalize(forward);
                Vector3 right = Vector3.Cross(Vector3.UnitY, forward);
                if (firstPerson)
                {
                    Vector3 currentEye = rig.Point(input.Head.Position);
                    Vector3 start = new(currentEye.X, rig.Origin.Y, currentEye.Z);
                    walker.Stand(start, MathF.Atan2(forward.X, forward.Z));
                    walker.Speed = rig.UnitsPerMetre * preferences.MoveMetresPerSecond;
                    FirstPersonStep step = walker.Advance(new FirstPersonInput(stick, Vector2.Zero, false), Math.Clamp(seconds, 0, 0.1f));
                    Vector3 displacement = step.Position - start;
                    if (Collide(currentEye, currentEye + displacement) is null) { rig.Move(displacement); }
                }
                else { rig.Move((right * stick.X + forward * stick.Y) * (rig.UnitsPerMetre * preferences.MoveMetresPerSecond * Math.Clamp(seconds, 0f, 0.1f))); }
            }
        }
        Vector3 eye = rig.Point(input.Head.Position);
        Vector3 feet = new(eye.X, rig.Origin.Y, eye.Z);
        float? height = walker.Stairs?.Invoke(feet) ?? walker.Ground?.Invoke(feet);
        bool ValidDestination(Vector3 point)
        {
            if (walker.Ground is null || walker.CanStand?.Invoke(point) == false) { return false; }
            float? ground = walker.Stairs?.Invoke(point) ?? walker.Ground(point);
            if (ground is null || MathF.Abs(ground.Value - point.Y) > 3f) { return false; }
            // A landing needs the player's footprint, not just the centre pixel of the boundary.
            float radius = rig.UnitsPerMetre * 0.18f;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.Tau / 8;
                Vector3 around = point + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                if (walker.CanStand?.Invoke(around) == false) { return false; }
                float? edge = walker.Stairs?.Invoke(around) ?? walker.Ground(around);
                if (edge is null || MathF.Abs(edge.Value - point.Y) > radius) { return false; }
            }
            return true;
        }
        TeleportHit? Collide(Vector3 from, Vector3 to)
        {
            Vector3 segment = to - from;
            float length = segment.Length();
            if (length < 0.001f) { return null; }
            if (interaction.Cast(new Ray(from, segment / length), ignored) is { } hit && hit.Distance <= length)
            {
                return new TeleportHit(hit.Point, ValidDestination(hit.Point));
            }
            return null;
        }
        Vector3? landed = session.Teleport.Update(input.Teleport, controls && input.LeftTracked && preferences.Locomotion is VrLocomotionMode.Teleport or VrLocomotionMode.Both,
            rig.Aim(input.LeftAim), rig.UnitsPerMetre, Collide);
        if (landed is { } target && ValidDestination(target))
        {
            rig.Teleport(target, input.Head); eye = rig.Point(input.Head.Position);
            feet = target; height = target.Y; _lastEye = null;
        }
        // Walk boundaries constrain locomotion, not leaning over tables or looking
        // around an authored cinematic placement. Missing navigation is not a blackout.
        bool blocked = firstPerson && _lastEye is { } oldEye && Collide(oldEye, eye) is not null;
        session.ComfortBlocked = blocked;
        if (firstPerson && !blocked && controls && height is not null && walker.CanStand?.Invoke(feet) != false)
        {
            feet.Y = height!.Value;
            // Keep the stage floor aligned with a sloping authored floor.
            if (input.Move.LengthSquared() > 0.04f && preferences.Locomotion is VrLocomotionMode.Smooth or VrLocomotionMode.Both)
            { rig.Move(Vector3.UnitY * (feet.Y - rig.Origin.Y)); }
            Vector3 look = rig.Direction(input.Head, -Vector3.UnitZ);
            walker.Stand(feet, MathF.Atan2(look.X, look.Z));
            update.Step(ego, feet, walker.Yaw);
        }
        // Advance even after an intersection, so a stale segment cannot latch black.
        _lastEye = rig.Point(input.Head.Position);
        return rig.Eye(input.Head, new Vector4(-1, 1, -1, 1), authored);
    }
}
