// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Game;

/// <summary>
/// Holding a jet of water on something until it comes down.
/// </summary>
public sealed class WaterAiming
{
    /// <summary>How long the water must stay on the nest, in seconds.</summary>
    public const float SecondsNeeded = 10f;

    /// <summary>How close the jet must be to count, as a fraction of the panel's width.</summary>
    public const float OnTargetRadius = 0.075f;

    private const float JetLag = 3.4f;
    private const float SwayRate = 0.55f;
    private const float SwayWidth = 0.10f;
    private const float SwayHeight = 0.035f;
    private const float BleedRate = 0.5f;

    private float _clock;

    /// <summary>Where the player is pointing, in panel space: 0-1 across and down.</summary>
    public Vector2 Aim { get; private set; } = new(0.5f, 0.8f);

    /// <summary>Where the water is actually landing, which trails the aim.</summary>
    public Vector2 Jet { get; private set; } = new(0.5f, 0.8f);

    /// <summary>Where the nest is, swaying.</summary>
    public Vector2 Nest { get; private set; } = new(0.5f, 0.28f);

    /// <summary>How long the water has been on the nest, in seconds.</summary>
    public float Held { get; private set; }

    /// <summary>How far along, nought to one.</summary>
    public float Progress => Math.Clamp(Held / SecondsNeeded, 0f, 1f);

    /// <summary>Whether the jet is on the nest right now.</summary>
    public bool OnTarget => Vector2.Distance(Jet, Nest) <= OnTargetRadius;

    /// <summary>Whether the nest has had enough.</summary>
    public bool Done => Held >= SecondsNeeded;

    /// <summary>Points the hose.</summary>
    /// <param name="at">Where, in panel space.</param>
    public void PointAt(Vector2 at) =>
        Aim = new Vector2(Math.Clamp(at.X, 0f, 1f), Math.Clamp(at.Y, 0f, 1f));

    /// <summary>Advances the water, the nest and the clock.</summary>
    /// <param name="seconds">How much time has passed.</param>
    /// <returns>True on the frame it finishes.</returns>
    public bool Advance(float seconds)
    {
        if (seconds <= 0f || Done)
        {
            return false;
        }

        _clock += seconds;

        // The nest sways on a pair of unequal periods, so it never quite repeats and the
        // player cannot settle the jet somewhere and stop watching.
        Nest = new Vector2(
            0.5f + (MathF.Sin(_clock * SwayRate) * SwayWidth),
            0.28f + (MathF.Sin(_clock * SwayRate * 1.7f) * SwayHeight));

        // The jet chases the aim rather than arriving at it. Framerate-independent, so the
        // hose does not become easier to hold on a faster machine.
        float chase = 1f - MathF.Exp(-JetLag * seconds);
        Jet += (Aim - Jet) * chase;

        bool was = Done;

        Held = Math.Clamp(
            Held + (OnTarget ? seconds : -seconds * BleedRate),
            0f,
            SecondsNeeded);

        return !was && Done;
    }
}
