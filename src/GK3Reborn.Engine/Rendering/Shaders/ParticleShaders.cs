// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>What the particle pass needs to turn a point into a sprite.</summary>
/// <param name="ViewProjection">The camera, as the room's own pass had it.</param>
/// <param name="Right">xyz: the camera's right in world space.</param>
/// <param name="Up">xyz: its up; w: how much above white a self-lit thing may be drawn.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParticleConstants(
    Matrix4x4 ViewProjection, Vector4 Right, Vector4 Up);

/// <summary>
/// Smoke and embers, drawn over the finished room.
/// </summary>
/// <remarks>
/// <para>
/// The renderer is deferred and its material pass cannot blend: every surface in the game
/// is opaque or cut out with a hard alpha test, which is what the 1999 art was drawn for.
/// Smoke is the one thing in this project that genuinely needs a blend, so it is a forward
/// pass of its own, drawn after the picture is composed and tested against the depth the
/// room left behind.
/// </para>
/// <para>
/// <b>One blend does both kinds.</b> Colours arrive premultiplied by their own alpha and
/// the blend is <c>ONE, ONE_MINUS_SRC_ALPHA</c>, so what a fragment writes in the alpha
/// channel decides what it does: an ember writes zero and is added to the wall behind it,
/// smoke writes its coverage and hides it. Two blends would mean two pipelines and a sort
/// that kept them apart, and embers would still have to be drawn after the smoke they are
/// flying through.
/// </para>
/// <para>
/// <b>There is no texture.</b> A sprite is a disc with a soft edge and, for smoke, a little
/// noise cut out of it — three lines of arithmetic against a bitmap that would have to be
/// authored, packed, shipped and looked up. It also means a particle is as sharp as the
/// display is, at any size, which a 32-pixel puff from 1999 would not be.
/// </para>
/// </remarks>
public static class ParticleShaders
{
    /// <summary>Describes the camera for one frame's particles.</summary>
    /// <param name="camera">The camera the room was drawn with.</param>
    /// <param name="viewProjection">Its matrix, jitter and all.</param>
    /// <param name="emissiveGain">How far above white a self-lit thing may be drawn.</param>
    /// <returns>The block both stages read.</returns>
    /// <remarks>
    /// The basis is built the way the sky's is — see <see cref="SkyboxShaders.Describe"/> —
    /// rather than read out of a view matrix, and for the same reason: the rows of a view
    /// matrix are the basis of its inverse, and a sprite built from them faces the right way
    /// until the camera turns.
    /// </remarks>
    public static ParticleConstants Describe(
        Camera camera, Matrix4x4 viewProjection, float emissiveGain = 1f)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 up = Vector3.Cross(forward, right);

        return new ParticleConstants(
            viewProjection,
            new Vector4(right, 0f),
            new Vector4(up, MathF.Max(emissiveGain, 1f)));
    }

    /// <summary>The vertex stage.</summary>
    public const string Vertex = """
        #version 450

        layout(location = 0) in vec4 inPositionAndSize;
        layout(location = 1) in vec4 inCornerAndShape;
        layout(location = 2) in vec4 inTint;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 right;    // xyz: the camera's right
            vec4 up;       // xyz: its up; w: how far above white a self-lit thing may go
        } push;

        layout(location = 0) out vec2 outCorner;
        layout(location = 1) out vec4 outTint;
        layout(location = 2) out float outShape;

        void main()
        {
            // The sprite is square in *view* space, so it faces the camera from wherever it
            // is looked at. Turning it about the view axis first is what keeps a hundred
            // discs from looking like a hundred copies of one disc.
            float spin = inCornerAndShape.z;
            float c = cos(spin);
            float s = sin(spin);

            vec2 corner = vec2(
                (inCornerAndShape.x * c) - (inCornerAndShape.y * s),
                (inCornerAndShape.x * s) + (inCornerAndShape.y * c));

            vec3 world = inPositionAndSize.xyz +
                         (push.right.xyz * corner.x * inPositionAndSize.w) +
                         (push.up.xyz * corner.y * inPositionAndSize.w);

            gl_Position = push.viewProjection * vec4(world, 1.0);

            // The untumbled corner, so the disc below is round rather than turned with it.
            // A bird is the other way round and gets the turn for free: its silhouette is
            // drawn in this frame, so the spin that swung the quad swings the bird with it
            // and is how a bird crossing the view lies along the way it is going.
            outCorner = inCornerAndShape.xy;
            outTint = inTint;
            outShape = inCornerAndShape.w;
        }
        """;

    /// <summary>The fragment stage.</summary>
    public const string Fragment = """
        #version 450

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 right;
            vec4 up;
        } push;

        layout(location = 0) in vec2 inCorner;
        layout(location = 1) in vec4 inTint;
        layout(location = 2) in float inShape;

        layout(location = 0) out vec4 outColor;

        // One number in [0,1) per point, stable and cheap. Not a good hash by any standard;
        // the requirement is that neighbours differ, not that the distribution is uniform.
        float Grain(vec2 at)
        {
            return fract(sin(dot(at, vec2(127.1, 311.7))) * 43758.5453);
        }

        // Value noise: the grain at the corners of a cell, smoothly interpolated. Two
        // octaves is enough to make a disc look like a lump of smoke and no more than that
        // is affordable on something drawn a hundred times over the same pixels.
        float Cloud(vec2 at)
        {
            vec2 cell = floor(at);
            vec2 within = at - cell;
            vec2 weight = within * within * (3.0 - (2.0 * within));

            float a = Grain(cell);
            float b = Grain(cell + vec2(1.0, 0.0));
            float c = Grain(cell + vec2(0.0, 1.0));
            float d = Grain(cell + vec2(1.0, 1.0));

            return mix(mix(a, b, weight.x), mix(c, d, weight.x), weight.y);
        }

        // How much of this pixel a bird covers, in the sprite's own untumbled frame: the
        // wings along x, the head along +y, and the beat from nought to one.
        //
        // Two strokes and nothing else. The wings are a pair of tapering blades whose tips
        // rise and fall with the beat and sweep a little back as they go out, and the body
        // is a short ellipse laid along the flight direction. At the size a bird in the sky
        // actually draws — a dozen pixels across, often fewer — that is the whole of what
        // the eye is reading, and a bitmap of a bird would be a blur at the same size.
        float Bird(vec2 at, float beat)
        {
            float span = abs(at.x);

            if (span > 1.0)
            {
                return 0.0;
            }

            // A wing goes further up than down. The upstroke is where a bird gathers the
            // air and it is what makes a distant flock read as flapping rather than as
            // flickering; a beat symmetric about the body reads as a blinking dash.
            float wave = sin(6.28318530718 * beat);
            float tip = (wave > 0.0 ? 0.62 : 0.42) * wave;

            // Where the middle of the wing is at this point along it. Raised towards the
            // tip by the beat, and swept back a little whatever the beat: a wing held
            // square to the body is an aeroplane.
            float middle = (tip * pow(span, 1.6)) - (0.12 * span);

            // And how deep it is there, tapering to nothing at the tip.
            float depth = (0.155 * pow(max(1.0 - span, 0.0), 0.45)) + 1e-5;

            float wing = 1.0 - smoothstep(0.55, 1.0, abs(at.y - middle) / depth);

            // The last tenth, so the wing ends in a point rather than a cut.
            wing *= 1.0 - smoothstep(0.86, 1.0, span);

            float body = 1.0 - smoothstep(
                0.75, 1.05, length(vec2(at.x / 0.085, (at.y + 0.02) / 0.34)));

            return max(wing, body);
        }

        void main()
        {
            // A bird, which is the one thing this pass draws that is not a disc. Tested
            // half a unit clear of the disc range so that nothing an ember could round to
            // can land in it. See Particle.Shape.
            if (inShape >= 1.5)
            {
                float covered = Bird(inCorner, inShape - 2.0) * inTint.a;

                if (covered <= 0.004)
                {
                    discard;
                }

                // Premultiplied and never additive: a bird is a thing between the eye and
                // the sky and it takes the sky's light away, which is the whole of what a
                // silhouette is.
                outColor = vec4(inTint.rgb * covered, covered);
                return;
            }

            float radius = length(inCorner);

            if (radius >= 1.0)
            {
                discard;
            }

            // A soft disc. Squared rather than linear, because a linear falloff has a
            // visible edge where it reaches zero and a hundred of those overlapping is a
            // hundred circles rather than a cloud.
            float disc = 1.0 - radius;
            float coverage = disc * disc;

            if (inShape < 0.5)
            {
                // Smoke is not a disc. Two octaves of noise, keyed off the sprite's own
                // spin through the corner it was given, break the outline up so that
                // overlapping puffs read as one body of smoke.
                float lumps =
                    (0.65 * Cloud((inCorner * 2.3) + vec2(inTint.a * 7.0))) +
                    (0.35 * Cloud(inCorner * 5.7));

                coverage *= 0.45 + (0.85 * lumps);
            }

            float alpha = clamp(inTint.a * coverage, 0.0, 1.0);

            if (alpha <= 0.002)
            {
                discard;
            }

            // Premultiplied, so one blend does both kinds: the colour is written weighted by
            // its own coverage, and the alpha channel says how much of what is behind to
            // take away. An ember says none of it and is therefore added.
            //
            // An ember is light rather than a surface, so it is the one thing here allowed
            // above white — the same allowance a bulb gets in the room's own pass, and the
            // same reason: on an HDR display a spark is several times the brightness of the
            // wall it flies past.
            float gain = mix(1.0, push.up.w, inShape);

            outColor = vec4(inTint.rgb * alpha * gain, alpha * (1.0 - inShape));
        }
        """;
}
