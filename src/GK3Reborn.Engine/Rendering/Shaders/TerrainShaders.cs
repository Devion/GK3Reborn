// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>What every backdrop stage but the sky is told about the frame.</summary>
/// <remarks>
/// <para>
/// One block for the ground, the impostors and the modelled trees, because all three stand
/// in the same metric space, are lit by the same sun and are seen through the same air. A
/// stage reading a different one of these would be a wood floating off its own hillside.
/// </para>
/// <para>
/// A hundred and twenty-eight bytes exactly, which is the push-constant ceiling every
/// Vulkan implementation is required to offer and the size the Direct3D root signature
/// reserves thirty-two root constants for. Nothing may be added to it without something
/// else coming out.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainConstants
{
    /// <summary>Backdrop space to clip, the camera's offset included.</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>Toward the sun in the backdrop's frame; w is zero for a sunless hour.</summary>
    public Vector4 Sun;

    /// <summary>Tile metres, tint amount, haze per metre, grid extent.</summary>
    public Vector4 Params;

    /// <summary>The camera in backdrop metres; w is the mean cloud cover.</summary>
    public Vector4 Eye;

    /// <summary>
    /// How far the modelled trees reached, how many kinds of them there are, spare, and the
    /// height the haze thins over.
    /// </summary>
    /// <remarks>
    /// The first two are what tells the impostor stage where to start: the cones are drawn
    /// beyond the band the models cover, and the band is decided per frame by where the
    /// camera stands. The last is the air. See <c>TerrainShaders.Fragment</c>'s airMass.
    /// </remarks>
    public Vector4 Haze;
}

/// <summary>What the generated sky is told about the frame.</summary>
/// <remarks>
/// The camera's basis rather than its matrices, for the same reason
/// <see cref="SkyboxConstants"/> carries one: the ray through a pixel is built forwards
/// from the basis, never by inverting a projection.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainSkyConstants
{
    /// <summary>Where the camera looks.</summary>
    public Vector4 Forward;

    /// <summary>Its right, with the tangent of half the horizontal field of view in w.</summary>
    public Vector4 Right;

    /// <summary>Its up, with the tangent of half the vertical field of view in w.</summary>
    public Vector4 Up;

    /// <summary>Width and height in pixels.</summary>
    public Vector4 Viewport;

    /// <summary>Toward the sun, world frame; w is zero for a sunless hour.</summary>
    public Vector4 Sun;

    /// <summary>Coverage, scale, and a stable per-scene offset for procedural clouds.</summary>
    public Vector4 Clouds;
}

/// <summary>
/// The reconstructed horizon's stages: the ground, its forest as impostors and as models,
/// and the generated sky behind all three.
/// </summary>
/// <remarks>
/// <para>
/// Written once, in GLSL, and compiled for whichever backend is asking — the Direct3D side
/// transpiles it. Lifted out of <c>TerrainPipeline</c> so that the second backend can draw
/// a backdrop without owning a copy of the recipe: a shader that exists twice is a shader
/// where the two horizons drift apart, and the drift is a hillside that is a slightly
/// different colour on one machine.
/// </para>
/// <para>
/// The full recipe and why each rule exists is
/// <c>ContentWorkspace/enhanced/skyboxes/terrain-plan.md</c>.
/// </para>
/// </remarks>
public static class TerrainShaders
{
    public const string Vertex = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;  // terrain space to clip, camera offset included
            vec4 sun;             // xyz: toward the sun, in terrain space; w: 1 by day
            vec4 params;          // x: tile metres, y: tint amount, z: haze per metre, w: extent
            vec4 eye;             // xyz: camera in terrain space; w: mean cloud cover
            vec4 haze;            // w: how many metres the haze thins over
        } push;

        layout(location = 0) out vec3 vWorld;
        layout(location = 1) out vec3 vNormal;

        void main()
        {
            vWorld = inPosition;
            vNormal = inNormal;

            vec4 clip = push.viewProjection * vec4(inPosition, 1.0);

            // The room's projection and this one share nothing, so the backdrop takes
            // the far tail of the depth buffer for itself: every fragment lands in
            // [0.999, 1), the room is always nearer, the sky at 1.0 is always farther,
            // and the backdrop still sorts against itself inside the tail.
            float zNdc = clamp(clip.z / max(clip.w, 1e-4), 0.0, 1.0);
            clip.z = (0.9990 + 0.000999 * zNdc) * clip.w;
            gl_Position = clip;
        }
        """;

    public const string Fragment = """
        #version 450

        layout(binding = 0) uniform sampler2D tileForest;
        layout(binding = 1) uniform sampler2D tileRock;
        layout(binding = 2) uniform sampler2D tileGrass;
        layout(binding = 3) uniform sampler2D tileDirt;
        layout(binding = 4) uniform sampler2D splat;
        layout(binding = 5) uniform sampler2D tint;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
            vec4 haze;
        } push;

        layout(location = 0) in vec3 vWorld;
        layout(location = 1) in vec3 vNormal;

        layout(location = 0) out vec4 outColor;

        // The same texture at two scales, mixed: a single period is visible from one
        // ridge to the next, the pair never lines up.
        vec3 tile2(sampler2D t, vec2 uv)
        {
            return mix(texture(t, uv).rgb, texture(t, uv * 0.23 + vec2(7.31, 3.7)).rgb, 0.45);
        }

        // Ground texture projected down onto the terrain, and from the side where the
        // terrain is a wall.
        //
        // Projecting from above alone is right for a hillside and catastrophic for a cliff:
        // a face at eighty degrees moves almost nothing in x or z as it climbs, so one row
        // of texels is smeared from the foot of the crag to its top. Every ridge in the
        // reconstruction came out combed with vertical streaks, and no amount of filtering
        // touches it, because the stretch is in the coordinate rather than in the sampling.
        //
        // The two side projections are single-scale where the downward one is doubled. The
        // second scale is there to hide the repeat over a kilometre of open ground, and a
        // rock face carries no such run; a cliff needs a coordinate that climbs, which is
        // all this gives it.
        vec3 ground(sampler2D t, vec3 at, vec3 blend)
        {
            return (texture(t, at.zy).rgb * blend.x)
                 + (tile2(t, at.xz) * blend.y)
                 + (texture(t, at.xy).rgb * blend.z);
        }

        // ---- Aerial perspective ------------------------------------------------------
        //
        // What makes a valley read as a valley. Air is not clear: over a kilometre it
        // takes most of the contrast out of a hillside and leaves it the colour of the
        // sky, and the eye reads how much of that has happened as distance. Without it
        // a ridge two kilometres away is drawn as crisply as the wall in front of the
        // camera, and the whole backdrop reads as a painted flat rather than as country.
        //
        // Two things it has to get right, and the first is the one a constant fog gets
        // wrong. **Density falls off with height.** The haze pools in the bottom of a
        // valley and thins over the ridges, so a distant crest stands clear of the murk
        // its own foot is buried in — which is exactly the shape the eye reads as depth
        // in real hill country. A fog that ignores height paints the crest and the floor
        // the same grey and flattens the two together.
        //
        // The integral is exact rather than sampled. Density along the ray is
        // rho * exp(-y / H), so what is wanted is its integral between the eye and the
        // fragment; over a straight segment that has a closed form in the two endpoint
        // heights, and it costs two exponentials instead of a loop.

        float airMass(vec3 at, vec3 eye, float density, float scaleHeight)
        {
            float run = length(at - eye);

            if (run < 1e-3 || density <= 0.0)
            {
                return 0.0;
            }

            // Clamped, because the heights are relative to a camera that may stand near
            // the top of the reconstruction: an unbounded exponential below it would put
            // a hundred times the density in the bottom of a gorge.
            float low = clamp(eye.y / scaleHeight, -2.0, 12.0);
            float high = clamp(at.y / scaleHeight, -2.0, 12.0);
            float rise = high - low;

            // The limit of the integral as the two ends level out, which is the common
            // case for anything near the horizon and the branch that would divide by
            // nothing.
            float column = abs(rise) < 1e-4
                ? exp(-low)
                : (exp(-low) - exp(-high)) / rise;

            return density * run * max(column, 0.0);
        }

        // What the air is the colour of. Away from the sun that is the sky at the
        // horizon, which is what the terrain has to dissolve into or it ends at a line.
        // Toward it the haze is forward-scattering and goes bright and warm, which is
        // the other half of what makes distance read — and it is why a hill looks
        // further away when the sun is behind it.
        vec3 airColour(vec3 towards, vec3 sun, float day)
        {
            vec3 cool = mix(vec3(0.045, 0.055, 0.085), vec3(0.74, 0.81, 0.88), day);
            vec3 warm = mix(vec3(0.055, 0.060, 0.090), vec3(0.98, 0.94, 0.86), day);

            float facing = max(dot(towards, sun), 0.0);

            return mix(cool, warm, pow(facing, 3.0) * 0.65 * day);
        }

        void main()
        {
            vec2 gridUv = (vWorld.xz / (2.0 * push.params.w)) + 0.5;
            vec4 w = texture(splat, gridUv);

            float away = length(vWorld - push.eye.xyz);

            // A cliff is rock whatever grew on the map: the weights were read off a
            // painting seen from face on, and a face-on painting has no slopes in it.
            //
            // Let go of with distance, and that is not a saving. The rule turns a smooth
            // interpolated normal into a hard step between two very different colours, so
            // on a ridge whose faces are a pixel wide it flips grey-green-grey across the
            // slope and reads as a weave crawling over the mountain. Near enough to see the
            // rock face it is worth having; a kilometre out the ground colour already
            // carries what the far hillside is.
            float slope = 1.0 - clamp(vNormal.y, 0.0, 1.0);
            w.g = max(w.g, smoothstep(0.5, 0.8, slope) * exp(-away / 500.0));
            w /= max(w.r + w.g + w.b + w.a, 1e-4);

            vec2 uv = vWorld.xz / push.params.x;
            vec3 at = vWorld / push.params.x;

            // Sharpened, so that a hillside is projected from above and only a face steep
            // enough to streak pays for the other two samples.
            vec3 blend = pow(abs(normalize(vNormal)), vec3(4.0));
            blend /= max(blend.x + blend.y + blend.z, 1e-4);

            vec3 albedo = (w.r * ground(tileForest, at, blend))
                        + (w.g * ground(tileRock, at, blend))
                        + (w.b * ground(tileGrass, at, blend))
                        + (w.a * ground(tileDirt, at, blend));

            // Distant ground keeps its colour and loses its texture. High-contrast
            // detail a kilometre out is smaller than a pixel, and what sub-pixel detail
            // does under a turning camera is shimmer - it reads as crawling light even
            // though the sun never moves. The far colour is the same tiles almost all
            // the way down their own mip chains.
            vec3 calm = (w.r * textureLod(tileForest, uv, 7.0).rgb)
                      + (w.g * textureLod(tileRock, uv, 7.0).rgb)
                      + (w.b * textureLod(tileGrass, uv, 7.0).rgb)
                      + (w.a * textureLod(tileDirt, uv, 7.0).rgb);
            //
            // Four hundred metres rather than the seven hundred it was. A tile repeats
            // every few metres, so by half a kilometre one period is a pixel or two wide
            // and beating against the pixel grid — which is the interference the far
            // hillsides showed. Fixed rather than scaled to the terrain, because what
            // decides it is the size of a texel on the screen and not how wide the map is.
            albedo = mix(calm, albedo, exp(-away / 400.0));

            // Hue only: the vista's colour mood without the old painting's darkness.
            vec3 mood = texture(tint, gridUv).rgb;
            float luminance = dot(mood, vec3(0.299, 0.587, 0.114));
            albedo = mix(albedo, albedo * (mood / max(luminance, 1e-3)), push.params.y);

            // A sunless hour is a dark one: the night sets carry their day sibling's
            // geometry and colours, and the hour's whole difference is made here.
            // The cloud sheet is local detail in the sky pass, but its mean coverage
            // still belongs in the ground light. Heavy overcast softens the hard key and
            // returns part of it as diffuse sky light instead of leaving sunlit grass
            // blazing underneath a grey ceiling.
            float overcast = clamp(push.eye.w, 0.0, 1.0);
            float toSun = max(dot(normalize(vNormal), push.sun.xyz), 0.0) * push.sun.w
                        * mix(1.0, 0.56, overcast);
            vec3 ambient = mix(vec3(0.045, 0.055, 0.085), vec3(0.26, 0.30, 0.38), push.sun.w);
            ambient *= mix(1.0, 1.16, overcast);
            vec3 lit = albedo * (ambient + (vec3(1.38, 1.26, 1.06) * toSun));

            // The canopy's shadow: ground under a dense wood is darker than the open
            // hillside, which is what visually plants the trees standing on it.
            lit *= 1.0 - (0.32 * w.r);

            // And then the air in the way, measured from where the camera stands in the
            // backdrop rather than from its centre. See airMass.
            vec3 towards = normalize(vWorld - push.eye.xyz);
            vec3 haze = airColour(towards, push.sun.xyz, push.sun.w);
            float fog = 1.0 - exp(-airMass(vWorld, push.eye.xyz, push.params.z, push.haze.w));
            outColor = vec4(mix(lit, haze, fog), 1.0);
        }
        """;

    public const string TreeVertex = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec4 inPlace;   // xyz: base of the tree; w: scale
        layout(location = 3) in float inTurn;   // yaw, radians
        layout(location = 4) in float inKind;   // which impostor shape this is

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
            vec4 haze;
        } push;

        layout(location = 0) out vec3 vWorld;
        layout(location = 1) out vec3 vNormal;
        layout(location = 2) out float vSeed;
        layout(location = 3) out float vCrown;
        layout(location = 4) out float vKind;

        void main()
        {
            vKind = inKind;

            float c = cos(inTurn);
            float s = sin(inTurn);
            mat3 turn = mat3(c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c);

            // Identical cones read as a wall; a second, independent stretch of each
            // tree's height breaks the ridge line into crowns.
            vSeed = fract(inTurn * 7.13 + inPlace.x * 0.017);
            vec3 shaped = inPosition * vec3(1.0, mix(0.75, 1.35, fract(vSeed * 9.7)), 1.0);

            // A tree a couple of kilometres out is thinner than a pixel, and sub-pixel
            // triangles shimmer under a turning camera. Those trees sink away over a
            // distance band; the forest colour in the ground tiles carries the far
            // hillsides from there.
            float fadeFrom = max(700.0, push.params.w * 0.30);
            float away = length(inPlace.xyz - push.eye.xyz);
            float keep = 1.0 - smoothstep(fadeFrom, fadeFrom * 1.9, away);

            // And the near end, where the modelled trees take over. haze.x is how far out
            // the renderer's own selection actually reached this frame — not a constant,
            // because it is bounded by a triangle budget and so retreats in a dense wood
            // and runs further in a thin one. haze.y says which species have a model at
            // all: scrub has none, and cutting it here would leave bare ground.
            //
            // Both ends of the swap are a band rather than a line. A cone that vanished
            // in one frame and a tree that appeared in the next would be the pop this
            // whole arrangement exists to avoid; over a fifth of the distance they cross
            // through each other and the eye reads one tree.
            float has = mod(floor(push.haze.y / exp2(clamp(inKind, 0.0, 3.0))), 2.0);

            if (push.haze.x > 0.0 && has > 0.5)
            {
                keep *= smoothstep(push.haze.x * 0.80, push.haze.x, away);
            }

            vec3 world = inPlace.xyz + (turn * (shaped * (inPlace.w * keep)));
            vWorld = world;
            vNormal = turn * inNormal;
            // Against the shape's own height rather than a constant fourteen metres, or a
            // shrub is shaded as though it were the bottom quarter of a fir.
            float tall = inKind > 2.5 ? 3.6 : (inKind > 1.5 ? 17.0 : (inKind > 0.5 ? 11.0 : 14.0));
            vCrown = clamp(inPosition.y / tall, 0.0, 1.0);

            vec4 clip = push.viewProjection * vec4(world, 1.0);
            float zNdc = clamp(clip.z / max(clip.w, 1e-4), 0.0, 1.0);
            clip.z = (0.9990 + 0.000999 * zNdc) * clip.w;
            gl_Position = clip;
        }
        """;

    public const string TreeFragment = """
        #version 450

        layout(binding = 4) uniform sampler2D splat;
        layout(binding = 5) uniform sampler2D tint;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
            vec4 haze;
        } push;

        layout(location = 0) in vec3 vWorld;
        layout(location = 1) in vec3 vNormal;
        layout(location = 2) in float vSeed;
        layout(location = 3) in float vCrown;
        layout(location = 4) in float vKind;

        layout(location = 0) out vec4 outColor;

        // ---- Aerial perspective ------------------------------------------------------
        //
        // What makes a valley read as a valley. Air is not clear: over a kilometre it
        // takes most of the contrast out of a hillside and leaves it the colour of the
        // sky, and the eye reads how much of that has happened as distance. Without it
        // a ridge two kilometres away is drawn as crisply as the wall in front of the
        // camera, and the whole backdrop reads as a painted flat rather than as country.
        //
        // Two things it has to get right, and the first is the one a constant fog gets
        // wrong. **Density falls off with height.** The haze pools in the bottom of a
        // valley and thins over the ridges, so a distant crest stands clear of the murk
        // its own foot is buried in — which is exactly the shape the eye reads as depth
        // in real hill country. A fog that ignores height paints the crest and the floor
        // the same grey and flattens the two together.
        //
        // The integral is exact rather than sampled. Density along the ray is
        // rho * exp(-y / H), so what is wanted is its integral between the eye and the
        // fragment; over a straight segment that has a closed form in the two endpoint
        // heights, and it costs two exponentials instead of a loop.

        float airMass(vec3 at, vec3 eye, float density, float scaleHeight)
        {
            float run = length(at - eye);

            if (run < 1e-3 || density <= 0.0)
            {
                return 0.0;
            }

            // Clamped, because the heights are relative to a camera that may stand near
            // the top of the reconstruction: an unbounded exponential below it would put
            // a hundred times the density in the bottom of a gorge.
            float low = clamp(eye.y / scaleHeight, -2.0, 12.0);
            float high = clamp(at.y / scaleHeight, -2.0, 12.0);
            float rise = high - low;

            // The limit of the integral as the two ends level out, which is the common
            // case for anything near the horizon and the branch that would divide by
            // nothing.
            float column = abs(rise) < 1e-4
                ? exp(-low)
                : (exp(-low) - exp(-high)) / rise;

            return density * run * max(column, 0.0);
        }

        // What the air is the colour of. Away from the sun that is the sky at the
        // horizon, which is what the terrain has to dissolve into or it ends at a line.
        // Toward it the haze is forward-scattering and goes bright and warm, which is
        // the other half of what makes distance read — and it is why a hill looks
        // further away when the sun is behind it.
        vec3 airColour(vec3 towards, vec3 sun, float day)
        {
            vec3 cool = mix(vec3(0.045, 0.055, 0.085), vec3(0.74, 0.81, 0.88), day);
            vec3 warm = mix(vec3(0.055, 0.060, 0.090), vec3(0.98, 0.94, 0.86), day);

            float facing = max(dot(towards, sun), 0.0);

            return mix(cool, warm, pow(facing, 3.0) * 0.65 * day);
        }

        void main()
        {
            // Each shape's own green, varied per tree, pulled toward the vista's own
            // colour so a wood follows the painting the way the ground does. A conifer is
            // darker and bluer than a broadleaf and scrub is browner than either, and
            // across a valley that difference reads before the silhouette does.
            vec3 dark = vec3(0.030, 0.062, 0.038);
            vec3 pale = vec3(0.105, 0.158, 0.072);

            if (vKind > 0.5 && vKind < 1.5)
            {
                dark = vec3(0.046, 0.088, 0.032);
                pale = vec3(0.152, 0.205, 0.080);
            }
            else if (vKind > 2.5)
            {
                dark = vec3(0.068, 0.082, 0.036);
                pale = vec3(0.158, 0.164, 0.078);
            }

            // Two draws from the same seed rather than one: the mix says which green this
            // tree is, and the second says how much light it gets at all. A wood whose
            // trees differ only in hue still reads as one painted surface, because what
            // the eye picks a tree out by across a valley is that it is darker or lighter
            // than the one beside it.
            vec3 albedo = mix(dark, pale, vSeed)
                        * mix(0.62, 1.30, fract(vSeed * 31.7));
            vec2 gridUv = (vWorld.xz / (2.0 * push.params.w)) + 0.5;
            vec3 mood = texture(tint, gridUv).rgb;
            float luminance = dot(mood, vec3(0.299, 0.587, 0.114));
            albedo = mix(albedo, albedo * (mood / max(luminance, 1e-3)), 0.4);

            // The occlusion that makes a mass of cones read as trees. Vertical: a
            // canopy is dark at its floor and lit at its crown. Crowd: a tree deep in
            // the wood is shaded by its neighbours — the forest weight under it says
            // how deep — while a tree on the edge stands in the open.
            float density = texture(splat, gridUv).r;
            float occlusion = mix(0.28, 1.0, vCrown) * (1.0 - (0.50 * density * (1.0 - vCrown)));

            float overcast = clamp(push.eye.w, 0.0, 1.0);
            float toSun = max(dot(normalize(vNormal), push.sun.xyz), 0.0) * push.sun.w
                        * mix(1.0, 0.56, overcast);
            vec3 ambient = mix(vec3(0.045, 0.055, 0.085), vec3(0.26, 0.30, 0.38), push.sun.w);
            ambient *= mix(1.0, 1.16, overcast);
            vec3 lit = albedo * ((ambient * occlusion)
                               + (vec3(1.38, 1.26, 1.06) * toSun * mix(0.55, 1.0, vCrown)));

            // The same air the ground is behind, or a wood would stand out of the haze
            // its own hillside is buried in. See airMass.
            vec3 towards = normalize(vWorld - push.eye.xyz);
            vec3 haze = airColour(towards, push.sun.xyz, push.sun.w);
            float fog = 1.0 - exp(-airMass(vWorld, push.eye.xyz, push.params.z, push.haze.w));
            outColor = vec4(mix(lit, haze, fog), 1.0);
        }
        """;

    /// <remarks>
    /// <para>
    /// The near band of the same forest, drawn as the models the rooms plant rather than
    /// as cones. The instance stream is the impostors' own, six floats a tree, so a tree
    /// that crosses the band changes only what it is built out of — the placement, the
    /// scale, the yaw and the height jitter are read the same way on both sides, and a
    /// silhouette that moved as it swapped would be the one thing worse than the cone.
    /// </para>
    /// <para>
    /// Scaled uniformly by its own height. A grown tree is normalised to one unit tall
    /// with its base at the origin, and the impostor's height for that species is what a
    /// scale of one means, so the two agree about how tall a given tree is by
    /// construction.
    /// </para>
    /// </remarks>
    public const string TreeModelVertex = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec2 inTexCoord;
        layout(location = 3) in vec4 inPlace;   // xyz: base of the tree; w: scale
        layout(location = 4) in float inTurn;   // yaw, radians
        layout(location = 5) in float inKind;   // which species this is

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
            vec4 haze;
        } push;

        layout(location = 0) out vec3 vWorld;
        layout(location = 1) out vec3 vNormal;
        layout(location = 2) out vec2 vTexCoord;
        layout(location = 3) out float vSeed;
        layout(location = 4) out float vCrown;

        void main()
        {
            float c = cos(inTurn);
            float s = sin(inTurn);
            mat3 turn = mat3(c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c);

            // The impostors' seed and the impostors' stretch, so a tree is the same
            // height whichever of the two is drawing it.
            vSeed = fract(inTurn * 7.13 + inPlace.x * 0.017);

            float tall = inKind > 2.5 ? 3.6 : (inKind > 1.5 ? 17.0 : (inKind > 0.5 ? 11.0 : 14.0));
            float size = tall * inPlace.w * mix(0.75, 1.35, fract(vSeed * 9.7));

            vec3 world = inPlace.xyz + (turn * (inPosition * size));

            vWorld = world;
            vNormal = turn * inNormal;
            vTexCoord = inTexCoord;

            // The model is one unit tall, so its own y is how far up the tree this is.
            vCrown = clamp(inPosition.y, 0.0, 1.0);

            vec4 clip = push.viewProjection * vec4(world, 1.0);
            float zNdc = clamp(clip.z / max(clip.w, 1e-4), 0.0, 1.0);
            clip.z = (0.9990 + 0.000999 * zNdc) * clip.w;
            gl_Position = clip;
        }
        """;

    public const string TreeModelFragment = """
        #version 450

        layout(binding = 4) uniform sampler2D splat;
        layout(binding = 5) uniform sampler2D tint;

        // The one thing this tree is painted with, bound per part: a trunk bitmap for the
        // bole and a cut-out spray of leaves for everything else.
        layout(set = 1, binding = 0) uniform sampler2D sheet;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
            vec4 haze;
        } push;

        layout(location = 0) in vec3 vWorld;
        layout(location = 1) in vec3 vNormal;
        layout(location = 2) in vec2 vTexCoord;
        layout(location = 3) in float vSeed;
        layout(location = 4) in float vCrown;

        layout(location = 0) out vec4 outColor;

        // ---- Aerial perspective ------------------------------------------------------
        //
        // What makes a valley read as a valley. Air is not clear: over a kilometre it
        // takes most of the contrast out of a hillside and leaves it the colour of the
        // sky, and the eye reads how much of that has happened as distance. Without it
        // a ridge two kilometres away is drawn as crisply as the wall in front of the
        // camera, and the whole backdrop reads as a painted flat rather than as country.
        //
        // Two things it has to get right, and the first is the one a constant fog gets
        // wrong. **Density falls off with height.** The haze pools in the bottom of a
        // valley and thins over the ridges, so a distant crest stands clear of the murk
        // its own foot is buried in — which is exactly the shape the eye reads as depth
        // in real hill country. A fog that ignores height paints the crest and the floor
        // the same grey and flattens the two together.
        //
        // The integral is exact rather than sampled. Density along the ray is
        // rho * exp(-y / H), so what is wanted is its integral between the eye and the
        // fragment; over a straight segment that has a closed form in the two endpoint
        // heights, and it costs two exponentials instead of a loop.

        float airMass(vec3 at, vec3 eye, float density, float scaleHeight)
        {
            float run = length(at - eye);

            if (run < 1e-3 || density <= 0.0)
            {
                return 0.0;
            }

            // Clamped, because the heights are relative to a camera that may stand near
            // the top of the reconstruction: an unbounded exponential below it would put
            // a hundred times the density in the bottom of a gorge.
            float low = clamp(eye.y / scaleHeight, -2.0, 12.0);
            float high = clamp(at.y / scaleHeight, -2.0, 12.0);
            float rise = high - low;

            // The limit of the integral as the two ends level out, which is the common
            // case for anything near the horizon and the branch that would divide by
            // nothing.
            float column = abs(rise) < 1e-4
                ? exp(-low)
                : (exp(-low) - exp(-high)) / rise;

            return density * run * max(column, 0.0);
        }

        // What the air is the colour of. Away from the sun that is the sky at the
        // horizon, which is what the terrain has to dissolve into or it ends at a line.
        // Toward it the haze is forward-scattering and goes bright and warm, which is
        // the other half of what makes distance read — and it is why a hill looks
        // further away when the sun is behind it.
        vec3 airColour(vec3 towards, vec3 sun, float day)
        {
            vec3 cool = mix(vec3(0.045, 0.055, 0.085), vec3(0.74, 0.81, 0.88), day);
            vec3 warm = mix(vec3(0.055, 0.060, 0.090), vec3(0.98, 0.94, 0.86), day);

            float facing = max(dot(towards, sun), 0.0);

            return mix(cool, warm, pow(facing, 3.0) * 0.65 * day);
        }

        void main()
        {
            vec4 texel = texture(sheet, vTexCoord);

            // One rule for both kinds of part. A leaf card is a shape cut out of a spray
            // and needs the test; a trunk bitmap has no alpha channel at all, so it is
            // opaque everywhere and the test never fires on it. That is what lets the
            // bark and the leaves share one pipeline.
            if (texel.a < 0.35)
            {
                discard;
            }

            vec3 albedo = texel.rgb;

            // The same two variations the impostors carry, at a quarter of the strength:
            // a modelled tree already differs from its neighbour in silhouette, and a
            // wood whose greens are as spread as the cones' were reads as painted.
            albedo *= mix(0.86, 1.14, fract(vSeed * 31.7));

            vec2 gridUv = (vWorld.xz / (2.0 * push.params.w)) + 0.5;
            vec3 mood = texture(tint, gridUv).rgb;
            float luminance = dot(mood, vec3(0.299, 0.587, 0.114));
            albedo = mix(albedo, albedo * (mood / max(luminance, 1e-3)), 0.25);

            // Crowd shade, as the impostors have it: a tree deep in the wood is shaded by
            // its neighbours and one on the edge stands in the open. The models carry
            // their own baked occlusion in the card, so this is the part of it the card
            // cannot know — how much wood is around this tree.
            float density = texture(splat, gridUv).r;
            float occlusion = mix(0.55, 1.0, vCrown) * (1.0 - (0.34 * density * (1.0 - vCrown)));

            float overcast = clamp(push.eye.w, 0.0, 1.0);

            // Two-sided: a leaf card is one triangle and the sun is behind half of them.
            vec3 facing = normalize(vNormal);
            float toSun = abs(dot(facing, push.sun.xyz)) * push.sun.w * mix(1.0, 0.56, overcast);

            vec3 ambient = mix(vec3(0.045, 0.055, 0.085), vec3(0.26, 0.30, 0.38), push.sun.w);
            ambient *= mix(1.0, 1.16, overcast);

            vec3 lit = albedo * ((ambient * occlusion)
                               + (vec3(1.38, 1.26, 1.06) * toSun * mix(0.6, 1.0, vCrown)));

            vec3 towards = normalize(vWorld - push.eye.xyz);
            vec3 haze = airColour(towards, push.sun.xyz, push.sun.w);
            float fog = 1.0 - exp(-airMass(vWorld, push.eye.xyz, push.params.z, push.haze.w));
            outColor = vec4(mix(lit, haze, fog), 1.0);
        }
        """;

    public const string SkyVertex = """
        #version 450

        // One triangle covering the screen, from the vertex index alone, at the far
        // plane so the terrain and the room have both already claimed their pixels.
        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((corner * 2.0) - 1.0, 1.0, 1.0);
        }
        """;

    public const string SkyFragment = """
        #version 450

        layout(push_constant) uniform Push
        {
            vec4 forward;   // xyz: where the camera looks
            vec4 right;     // xyz: its right;  w: tan of half the horizontal fov
            vec4 up;        // xyz: its up;     w: tan of half the vertical fov
            vec4 viewport;  // xy: size in pixels
            vec4 sun;       // xyz: toward the sun, world frame; w: 1 by day
            vec4 clouds;    // x: coverage; y: scale; zw: stable scene offset
        } push;

        layout(location = 0) out vec4 outColor;

        // A volumetric field sampled on the sky dome has no UV seam and, crucially,
        // does not change scale when the camera pitches. The previous flat cloud plane
        // was a perspective projection: looking toward or away from its normal made the
        // same cloud appear to stretch and zoom.
        float cloudWaves(vec3 p)
        {
            // Smooth in every direction by construction: unlike value noise this has no
            // grid cells whose boundaries can become polygons after coverage is applied.
            // The two low-frequency waves bend the phases of the smaller ones, breaking
            // up their bands into broad, connected cloud masses.
            float warpA = sin(dot(p, vec3(0.73, 0.41, 0.55)));
            float warpB = sin(dot(p, vec3(-0.48, 0.84, 0.25)) + (warpA * 0.75));

            float value = sin(dot(p, vec3(0.31, -0.57, 0.76)) + (warpB * 1.20)) * 0.50;
            value += sin(dot(p * 1.91, vec3(-0.67, -0.22, 0.71))
                       - (warpA * 0.65)) * 0.25;
            value += sin(dot(p * 3.67, vec3(0.12, 0.93, -0.35))
                       + (warpB * 0.45)) * 0.14;
            value += sin(dot(p * 7.13, vec3(0.89, -0.31, -0.32))
                       - (warpA * 0.28)) * 0.07;

            return clamp(0.5 + (value / 1.92), 0.0, 1.0);
        }

        void main()
        {
            vec2 ndc = ((gl_FragCoord.xy / push.viewport.xy) * 2.0) - 1.0;
            vec3 ray = normalize(push.forward.xyz
                               + (push.right.xyz * (ndc.x * push.right.w))
                               - (push.up.xyz * (ndc.y * push.up.w)));

            // Bright at the horizon, deeper overhead, near-black when the hour has no
            // sun. The painted mountains that used to live in the cubemap are real
            // geometry now.
            float day = push.sun.w;
            float height = clamp(ray.y, 0.0, 1.0);
            vec3 zenith = mix(vec3(0.012, 0.018, 0.038), vec3(0.22, 0.42, 0.72), day);
            vec3 horizon = mix(vec3(0.045, 0.055, 0.085), vec3(0.74, 0.81, 0.88), day);
            vec3 sky = mix(horizon, zenith, pow(height, 0.55));

            // World direction is the coordinate. Turning the camera only reveals a
            // different part of this fixed sphere; it cannot squeeze, translate or zoom
            // the pattern. The offset selects a stable arrangement for each scene.
            vec3 cloudPoint = (ray * (4.2 * push.clouds.y))
                            + vec3(push.clouds.z, push.clouds.w,
                                   (push.clouds.z * 0.37) - (push.clouds.w * 0.61));
            float broad = cloudWaves(cloudPoint * 0.78);
            float detail = cloudWaves(
                (cloudPoint * 2.17) + vec3(-9.4, 6.7, 3.1));
            float cloudField = (broad * 0.76) + (detail * 0.24);

            // Coverage changes the threshold, while a softer secondary veil joins the
            // individual masses into the layered stratus expected from an overcast sky.
            float threshold = mix(0.72, 0.38, clamp(push.clouds.x, 0.0, 1.0));
            float cloudBody = smoothstep(threshold - 0.09, threshold + 0.14, cloudField);
            float cloudVeil = smoothstep(threshold - 0.22, threshold + 0.07, broad) * 0.46;
            float cloudDensity = max(cloudBody, cloudVeil);
            float cloudAlpha = cloudDensity * smoothstep(0.018, 0.16, ray.y);
            cloudAlpha *= mix(0.52, 1.0, day);

            // The underside stays cool and weighty, but thinner folds and the higher
            // part of the dome still catch diffuse daylight. A narrow warm edge close
            // to the sun keeps the layer dimensional instead of reading as grey fog.
            float sunHeight = max(push.sun.y, 0.0) * day;
            float diffuseLight = clamp(0.24 + (0.38 * sunHeight)
                                     + (0.22 * (1.0 - cloudDensity))
                                     + (0.18 * detail), 0.0, 1.0);
            vec3 cloudShadow = mix(vec3(0.018, 0.024, 0.040),
                                   vec3(0.31, 0.37, 0.45), day);
            vec3 cloudLight = mix(vec3(0.065, 0.075, 0.105),
                                  vec3(0.74, 0.77, 0.79), day);
            vec3 cloudColor = mix(cloudShadow, cloudLight, diffuseLight);
            cloudColor *= mix(0.82, 1.0, smoothstep(0.05, 0.48, ray.y));

            float facing = max(dot(ray, push.sun.xyz), 0.0);
            float cloudEdge = smoothstep(0.12, 0.42, cloudDensity)
                            * (1.0 - smoothstep(0.48, 0.90, cloudDensity));
            cloudColor += vec3(1.0, 0.91, 0.73) * day * cloudEdge
                        * pow(facing, 18.0) * (0.20 + (0.55 * sunHeight));

            sky = mix(sky, cloudColor, clamp(cloudAlpha, 0.0, 0.96));

            // Dense cloud locally hides the solar disc and damps its halo; breaks in
            // the cover still reveal both, which is much more convincing than drawing
            // the sun unconditionally over the cloud colour.
            float sunTransmission = mix(1.0, 0.035, clamp(cloudAlpha, 0.0, 1.0));
            sky += day * (vec3(1.0, 0.92, 0.75) * pow(facing, 900.0) * 4.0
                            * sunTransmission
                        + vec3(0.9, 0.82, 0.62) * pow(facing, 12.0) * 0.16
                            * mix(1.0, 0.30, cloudAlpha));

            outColor = vec4(sky, 1.0);
        }
        """;
}
