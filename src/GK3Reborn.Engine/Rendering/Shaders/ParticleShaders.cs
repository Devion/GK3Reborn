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
/// <param name="Eye">
/// xyz: where the camera is, in world space.
/// </param>
/// <remarks>
/// The eye is here for the one sprite that is a volume rather than a picture. A flame is
/// raymarched through its own quad, and a march needs the ray it is marching along: the
/// corner's world position gives the far end of it and this gives the near end. Every
/// other kind of sprite ignores it.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParticleConstants(
    Matrix4x4 ViewProjection, Vector4 Right, Vector4 Up, Vector4 Eye);

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
            new Vector4(up, MathF.Max(emissiveGain, 1f)),
            new Vector4(camera.Position, 0f));
    }

    /// <summary>The vertex stage.</summary>
    public const string Vertex = """
        #version 450

        layout(location = 0) in vec4 inPositionAndSize;
        layout(location = 1) in vec4 inCornerAndShape;
        layout(location = 2) in vec4 inTint;
        layout(location = 3) in vec4 inPlume;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 right;    // xyz: the camera's right
            vec4 up;       // xyz: its up; w: how far above white a self-lit thing may go
            vec4 eye;      // xyz: where the camera is
        } push;

        layout(location = 0) out vec2 outCorner;
        layout(location = 1) out vec4 outTint;
        layout(location = 2) out float outShape;
        layout(location = 3) out vec3 outWorld;
        layout(location = 4) out vec4 outPlume;
        layout(location = 5) out vec3 outCentre;

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

            // And, for the one sprite that is a volume, where this corner actually is and
            // what the plume standing behind it is. A flame is marched through the quad
            // rather than painted on it, and the march needs a ray in world space: this
            // corner is the far end of it and the eye is the near one.
            outWorld = world;
            outPlume = inPlume;
            outCentre = inPositionAndSize.xyz;
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
            vec4 eye;
        } push;

        layout(location = 0) in vec2 inCorner;
        layout(location = 1) in vec4 inTint;
        layout(location = 2) in float inShape;
        layout(location = 3) in vec3 inWorld;
        layout(location = 4) in vec4 inPlume;
        layout(location = 5) in vec3 inCentre;

        layout(location = 0) out vec4 outColor;

        // How many samples a flame is marched with. Thirty is where the bands stop being
        // visible on the largest fire in the game seen from across its own room; fewer
        // shows as rings inside the plume, more changes nothing anybody can see.
        const int FireSteps = 30;

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

        // One number in [0,1) per point in space, and no transcendental in it. Grain above
        // is a sine hash and stays one — it is what the smoke has always been drawn with,
        // and it is asked twice a pixel. A flame asks this seven hundred times a pixel,
        // and a sine hash there is both slower and less stable: fract(sin(x)) is the one
        // construct in this file whose answer differs between drivers.
        float Spark(vec3 at)
        {
            vec3 p = fract(at * vec3(0.1031, 0.1030, 0.0973));
            p += dot(p, p.yxz + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        // Value noise in three dimensions: the hash at the eight corners of a cell,
        // smoothly interpolated.
        float Billow(vec3 at)
        {
            vec3 cell = floor(at);
            vec3 within = at - cell;
            vec3 weight = within * within * (3.0 - (2.0 * within));

            float a = mix(Spark(cell), Spark(cell + vec3(1.0, 0.0, 0.0)), weight.x);
            float b = mix(Spark(cell + vec3(0.0, 1.0, 0.0)),
                          Spark(cell + vec3(1.0, 1.0, 0.0)), weight.x);
            float c = mix(Spark(cell + vec3(0.0, 0.0, 1.0)),
                          Spark(cell + vec3(1.0, 0.0, 1.0)), weight.x);
            float d = mix(Spark(cell + vec3(0.0, 1.0, 1.0)),
                          Spark(cell + vec3(1.0, 1.0, 1.0)), weight.x);

            return mix(mix(a, b, weight.y), mix(c, d, weight.y), weight.z);
        }

        // Three octaves, which is where a plume stops looking like a lava lamp: the first
        // is the body of the fire, the second the tongues that come away from it, the
        // third the ragged edge that says it is burning rather than glowing. Each octave
        // is smaller and therefore scrolls faster, which is the right way round — a small
        // eddy in a fire moves quickly and the body of it barely moves at all.
        float Turbulence(vec3 at)
        {
            return (0.55 * Billow(at)) +
                   (0.30 * Billow(at * 2.07)) +
                   (0.15 * Billow(at * 4.31));
        }

        // What a flame is the colour of, from a dying ember at nought to the white middle
        // of it at one.
        //
        // Not a blackbody curve, deliberately. A real one at the temperature of burning
        // wood is orange from end to end; the white heart of a flame is soot glowing far
        // hotter than the gas around it, and it is the part the eye actually reads as fire.
        vec3 Burning(float heat)
        {
            vec3 colour = vec3(0.45, 0.02, 0.00);

            colour = mix(colour, vec3(1.00, 0.16, 0.01), smoothstep(0.02, 0.22, heat));
            colour = mix(colour, vec3(1.00, 0.42, 0.05), smoothstep(0.20, 0.48, heat));
            colour = mix(colour, vec3(1.00, 0.68, 0.18), smoothstep(0.46, 0.76, heat));
            colour = mix(colour, vec3(1.00, 0.90, 0.62), smoothstep(0.82, 1.00, heat));

            return colour;
        }

        // How much fire there is at one point of the plume, and how hot it is there.
        //
        // `at` is the point relative to the foot of the flame, in world units. `shape` is
        // the kind's profile: where the plume is widest, how sharply it tapers, how much
        // the turbulence eats it and how far it leans.
        vec2 Plume(vec3 at, float height, float radius, float t, vec4 shape, float norm)
        {
            // A flame does not only lean, it lengthens and shortens: how much fuel is
            // reaching the tip varies from moment to moment and the tip is where it shows.
            // Two rates that share no common multiple, so it never repeats.
            float breath = 1.0 + (shape.w *
                ((0.14 * sin(t * 2.9)) + (0.09 * sin((t * 4.7) + 1.7))));

            float h = at.y / (height * breath);

            if (h < -0.02 || h > 1.06)
            {
                return vec2(0.0);
            }

            // A flame licks. It is held still where it is fed and loose above that, so the
            // sway goes with the square of the height rather than with the height: a fire
            // that leans from its base is a flag, not a flame.
            // Mostly the square of the height, because a flame is held where it is fed
            // and loose above that — a fire that leans from its base is a flag. A little of
            // it linear all the same: a draught strong enough to bend the tip of a candle
            // moves the whole of it, and a flame pinned at the wick and waving only at the
            // top is a rubber shape rather than a light.
            float lick = (h * h * 0.80) + (h * 0.20);

            vec2 lean = vec2(
                sin((t * 1.7) + (h * 4.3)) + (0.55 * sin((t * 3.1) + (h * 7.9))),
                cos((t * 1.5) + (h * 3.9)) + (0.55 * cos((t * 2.7) + (h * 6.3))));

            vec2 across = at.xz - (lean * radius * shape.w * lick * 0.45);
            float rad = length(across);

            // How wide the plume is here: it swells just above the fuel and tapers to a
            // point. Where the widest part falls is most of what separates a candle from a
            // bowl of fire, and it is the two exponents that put it there.
            float rise = pow(clamp(h + 0.12, 0.0, 1.2), shape.x);
            float fall = pow(clamp(1.0 - (h * 0.94), 0.0, 1.0), shape.y);
            float wide = radius * rise * fall * norm;

            // Cheap rejection before the noise, which is all this function costs. The
            // furthest the turbulence below can push the edge out is a fraction of the
            // radius, so anything past that is air whatever the noise says.
            if (rad > wide + (radius * shape.z * 0.75))
            {
                return vec2(0.0);
            }

            // Taller than it is wide, and rising. Fire is drawn upward by its own heat,
            // so its structure is streaks rather than lumps — noise with the same scale on
            // every axis reads as boiling cloud, which is what this looked like until the
            // vertical scale was pulled out from under the other two.
            vec3 flow = vec3(
                (across.x / radius) * 3.4,
                (h * 2.6) - (t * 1.6),
                (across.y / radius) * 3.4);

            float turb = Turbulence(flow);

            // The noise moves the edge of the plume in and out; it does not punch holes
            // through it. Thresholding the noise against a solid profile gives a lump
            // with bites taken out of it — hard-edged, flat-topped, and unmistakably a
            // shape rather than a fire. Displacing the boundary instead is what gives a
            // flame an outline that is never still, and it is the single thing that makes
            // this read as burning.
            //
            // How far it can push grows with height, because the gas is held where it is
            // fed and free above that.
            float bite = shape.z * (0.22 + smoothstep(-0.10, 0.85, h));
            float edge = (wide - rad) + ((turb - 0.5) * radius * bite);

            // Ramped over a band inside that edge, so the plume has a soft skin and a full
            // middle rather than being a silhouette with a gradient painted on it.
            float density = clamp(edge / max(radius * 0.35, 1e-4), 0.0, 1.0);

            density = density * density * (3.0 - (2.0 * density));

            // And a hand's breadth of fade at the very foot of it. A fire does end flat
            // where it meets what it is burning, but ending on a ruled line is the one
            // thing left that says this was drawn rather than lit.
            density *= smoothstep(-0.03, 0.07, h);

            // Hottest low down and where there is most of it, and streaked by the same
            // noise that shaped it, which is what puts the bright veins through a fire.
            //
            // A temperature and not an amount, which is the distinction the first attempt
            // at this got wrong: it has to reach one in the heart of the plume whatever is
            // there, or the density multiplies in a second time and again through the
            // colour, and the fire comes out at a thousandth of the brightness it should
            // be. Nothing was drawn at all.
            float middle = clamp(1.0 - (rad / max(wide, 1e-4)), 0.0, 1.0);

            float heat = smoothstep(0.02, 0.95, density) *
                         (1.0 - (0.55 * smoothstep(0.02, 1.00, h))) *
                         (0.18 + (0.52 * middle) + (0.30 * turb));

            return vec2(density, clamp(heat, 0.0, 1.0));
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
            // An open flame, which is the one thing this pass draws that is not a picture
            // at all. Everything else here is a sprite with something painted on it; this
            // is a volume of burning gas, marched through the quad it was given.
            if (inShape >= 3.5)
            {
                float height = inPlume.x;
                float radius = inPlume.y;
                float t = inPlume.z;
                float kind = inPlume.w;

                // The three fires the game has, and they are three different things: a
                // candle is a teardrop, a hearth is a wood fire whose tongues come away
                // from it, and the temple's bowl is a body of burning fuel that barely has
                // an outline. Where the plume is widest, how sharply it tapers, how much
                // the turbulence eats it and how much it moves.
                //
                // <b>The candle moves the most, and the first pass at this had it moving
                // the least.</b> It is the same reading as the flicker of the light —
                // a small flame is pushed about by every draught in the room and a bonfire
                // takes time to move — and getting it backwards is what made a hanging
                // lantern look like a painting of one. What the bowl of fire has instead is
                // turbulence: it churns without going anywhere.
                vec4 shape = kind < 0.5
                    ? vec4(0.55, 0.90, 0.55, 0.70)
                    : (kind < 1.5 ? vec4(0.38, 1.00, 1.15, 0.45)
                                  : vec4(0.34, 1.05, 1.30, 0.44));

                // How blue the foot of it is: a candle burns clean enough to show the
                // colour of the fuel going before there is any soot in it to glow, and a
                // wood fire makes soot from the bottom up and shows none of it.
                float blue = kind < 0.5 ? 0.85 : (kind < 1.5 ? 0.08 : 0.00);

                // And how much of the wall behind the heart of it is taken away. Not a
                // detail: a fire drawn as pure light over a lit hearth adds orange to a
                // beige wall and comes out pale peach, which is what the bar's fireplace
                // looked like beside the opaque card it replaced. A wood fire is thick with
                // soot and genuinely hides what is behind it; a candle barely does.
                float soot = kind < 0.5 ? 0.10 : (kind < 1.5 ? 0.70 : 0.75);

                // Normalised so the widest part of the plume is the width it was handed
                // whatever the kind. The artists painted the flame that size and lit the
                // room for it; a fire half as wide again would be a different fire.
                float peak = shape.x / (shape.x + shape.y);
                float norm = 1.0 / max(
                    pow(peak + 0.12, shape.x) * pow(1.0 - (peak * 0.94), shape.y), 1e-4);

                vec3 foot = inCentre - vec3(0.0, height * 0.5, 0.0);
                vec3 origin = push.eye.xyz;
                vec3 ray = normalize(inWorld - origin);
                vec3 rel = origin - foot;

                // Where along the ray the plume's own cylinder is. Most of every pixel of
                // the quad is outside it — a square around a flame is mostly not flame —
                // and finding that out with two quadratics is what keeps thirty samples
                // from being spent on air.
                float top = height * 1.25;
                float reach = radius * 1.50;

                float near = 0.0;
                float far = 1e9;

                if (abs(ray.y) < 1e-6)
                {
                    if (rel.y < 0.0 || rel.y > top)
                    {
                        discard;
                    }
                }
                else
                {
                    float ta = -rel.y / ray.y;
                    float tb = (top - rel.y) / ray.y;

                    near = max(near, min(ta, tb));
                    far = min(far, max(ta, tb));
                }

                float qa = dot(ray.xz, ray.xz);
                float qb = 2.0 * dot(rel.xz, ray.xz);
                float qc = dot(rel.xz, rel.xz) - (reach * reach);

                if (qa < 1e-9)
                {
                    // Looking straight down the flame's own axis, which is what the
                    // temple's close-up camera over its bowl of fire is doing.
                    if (qc > 0.0)
                    {
                        discard;
                    }
                }
                else
                {
                    float root = (qb * qb) - (4.0 * qa * qc);

                    if (root <= 0.0)
                    {
                        discard;
                    }

                    float span = sqrt(root);

                    near = max(near, (-qb - span) / (2.0 * qa));
                    far = min(far, (-qb + span) / (2.0 * qa));
                }

                near = max(near, 0.0);

                if (far <= near)
                {
                    discard;
                }

                float along = (far - near) / float(FireSteps);

                // What the ray gathered crossing the plume, in world units of burning gas.
                vec3 raw = vec3(0.0);

                for (int i = 0; i < FireSteps; i++)
                {
                    vec3 at = (origin + (ray * (near + ((float(i) + 0.5) * along)))) - foot;
                    vec2 burning = Plume(at, height, radius, t, shape, norm);

                    if (burning.x <= 0.002)
                    {
                        continue;
                    }

                    vec3 colour = Burning(burning.y);

                    // The blue at the foot of a candle is the fuel burning before there is
                    // any soot in it to glow. A wood fire makes soot from the bottom up and
                    // shows none of it.
                    colour = mix(
                        colour,
                        vec3(0.30, 0.55, 1.00),
                        blue * (1.0 - smoothstep(0.02, 0.22, at.y / height)) * 0.55);

                    raw += colour * (0.25 + (0.75 * burning.y)) * burning.x * along;
                }

                float lum = max(max(raw.r, raw.g), raw.b);

                if (lum <= 1e-4)
                {
                    discard;
                }

                // How much there is decides how brightly it is drawn; it does not decide
                // the colour. A ray through the heart of a bowl of fire crosses ten
                // times as much burning gas as one through a tongue at the top, and adding
                // the samples up as they stand makes the heart blow out to white while
                // everything above the rim goes to nothing. Saturating the total and
                // dividing the colour back out keeps the hue the plume actually had — and
                // keeps the variation through it, which averaging the samples destroys and
                // which is the difference between a fire and a warm smudge.
                float lit = pow(
                    1.0 - exp(-(lum * 1.6) / max(radius, 1e-4)), 0.5);

                // A flame is light before it is a thing, so it is allowed above white for
                // the reason an ember is: on an HDR display a fire is several times the
                // brightness of the wall it stands against.
                vec3 glow = (raw / lum) * lit * 1.6 *
                            inTint.rgb * inTint.a * push.up.w;

                // Steeply, so that only the body of the fire hides anything. Thin tongues
                // that took a share of the wall away in proportion to their own faint light
                // is how soot reads as a dirty rag laid over the top of a fire.
                float sooted = clamp(pow(lit, 1.8) * soot, 0.0, 0.90);

                if (max(max(glow.r, glow.g), glow.b) <= 0.003 && sooted <= 0.003)
                {
                    discard;
                }

                // Premultiplied like everything else here, and the two channels say two
                // different things: the colour is the light the fire adds, and the alpha is
                // the little of the wall behind it that its soot takes away. A flame is
                // both and mostly the first, which is why one blend still does all of this.
                outColor = vec4(glow, sooted);
                return;
            }

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
