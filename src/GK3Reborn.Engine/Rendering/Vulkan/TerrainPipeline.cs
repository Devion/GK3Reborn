using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws the reconstructed horizon: real terrain, its forest, and a generated sky with
/// procedural cloud cover, where the painted skybox was.
/// </summary>
/// <remarks>
/// <para>
/// The backdrop lives in its own metric space — metres around the scene's centre — and
/// is drawn with its own projection, so no room unit ever meets a terrain metre except
/// at one constant: <see cref="MetersPerUnit"/> turns the camera's offset from the
/// scene centre into a movement through the backdrop, which is what gives the horizon
/// parallax instead of the swimming a camera-glued skybox shows on every cut and glide.
/// </para>
/// <para>
/// It cannot share the room's depth range — the room's projection has no idea what four
/// kilometres are — so the vertex stages squeeze the backdrop's whole depth into the far
/// tail of the buffer, above 0.999. The room always wins the depth test against it, the
/// backdrop still sorts against itself inside the tail, and the generated sky at exactly
/// 1.0 loses to both.
/// </para>
/// <para>
/// When this draws, the painted cubemap does not: its mountains are baked into the
/// picture and would double-expose against the reconstructed ridge. The sky here is a
/// atmosphere and cloud layer with the scene's own sun in it, near-black when the hour
/// has no sun, and the cubemap survives only as the fallback for a backdrop that would
/// not build.
/// </para>
/// <para>
/// Texturing is four tileable ground textures blended by the offline splat weights,
/// with rock forced onto steep faces, projected from above on a hillside and from the
/// side on a cliff, each sampled at two scales so the repeat period never shows, and the
/// vista's colour applied hue-only over the top. The forest is the offline tree instances
/// drawn as four impostor silhouettes — a spruce, a broadleaf, a cypress and scrub — one
/// instanced draw apiece over slices of one buffer; stand-ins at backdrop distances, to
/// be traded for the modelled trees when a LOD path exists. The full recipe and why each
/// rule exists: <c>ContentWorkspace/enhanced/skyboxes/terrain-plan.md</c>.
/// </para>
/// </remarks>
public sealed unsafe class TerrainPipeline : IDisposable
{
    /// <summary>Floats per placed tree: where it is, how big, which way round, which shape.</summary>
    private const int Stride = 6;

    /// <summary>How many metres of backdrop one unit of room is worth.</summary>
    /// <remarks>
    /// GK3's people are about seventy units for a grown adult, so a unit is roughly an
    /// inch; 0.025 keeps a walk across a courtyard a walk, not a flight.
    /// </remarks>
    public const float MetersPerUnit = 0.025f;

    private const string VertexSource = """
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

    private const string FragmentSource = """
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

    private const string TreeVertexSource = """
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

    private const string TreeFragmentSource = """
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
    private const string TreeModelVertexSource = """
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

    private const string TreeModelFragmentSource = """
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

    private const string SkyVertexSource = """
        #version 450

        // One triangle covering the screen, from the vertex index alone, at the far
        // plane so the terrain and the room have both already claimed their pixels.
        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((corner * 2.0) - 1.0, 1.0, 1.0);
        }
        """;

    private const string SkyFragmentSource = """
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

    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private ShaderModule _treeVertexModule;
    private ShaderModule _treeFragmentModule;
    private ShaderModule _skyVertexModule;
    private ShaderModule _skyFragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private PipelineLayout _skyLayout;
    private Pipeline _pipeline;
    private Pipeline _treePipeline;
    private Pipeline _skyPipeline;
    private VulkanBuffer? _vertices;
    private VulkanBuffer? _indices;
    private uint _indexCount;
    private VulkanBuffer? _treeVertices;
    private VulkanBuffer? _treeIndices;
    private VulkanBuffer? _treeInstances;
    private ShaderModule _modelVertexModule;
    private ShaderModule _modelFragmentModule;
    private DescriptorSetLayout _sheetLayout;
    private DescriptorPool _sheetPool;
    private PipelineLayout _modelLayout;
    private Pipeline _modelPipeline;
    private VulkanBuffer? _modelVertices;
    private VulkanBuffer? _modelIndices;
    private VulkanBuffer? _modelInstances;
    private uint _modelCount;
    private readonly List<VulkanTexture> _sheets = [];
    private readonly List<DescriptorSet> _sheetSets = [];
    private uint _treeCount;
    private readonly VulkanTexture?[] _textures = new VulkanTexture?[6];
    private float _extent;
    private float[] _heights = [];
    private int _grid;
    private Vector3? _sunDirection;
    private float _azimuth;
    private Vector3 _anchorUnits;

    private TerrainPipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>How many metres of ground one tile of texture covers.</summary>
    public float TileMeters { get; set; } = 60f;

    /// <summary>How far the camera is kept above the backdrop's own ground, in metres.</summary>
    /// <remarks>
    /// About a person's eye height. What it guards is not the view from a hill — where
    /// the camera stands tens of metres over the terrain and should — but the case where
    /// <see cref="LiftMeters"/> is larger than the ground under the viewpoint, which
    /// buries the camera and turns the whole horizon into a rising wall.
    /// </remarks>
    public float ClearanceMeters { get; set; } = 2f;

    /// <summary>How far the whole backdrop is raised against the camera, in metres.</summary>
    /// <remarks>
    /// The offline heights put the panorama's own camera at zero, but the room's
    /// cameras stand wherever the scenes put them — often high enough that whole
    /// hillsides sink below the visible horizon. Raising the backdrop is done by
    /// standing the camera lower in it, which carries the fog along for free.
    /// </remarks>
    public float LiftMeters { get; set; } = 12f;

    /// <summary>How strongly the vista's colour is laid over the tiles, zero to one.</summary>
    public float TintAmount { get; set; } = 0.6f;

    /// <summary>
    /// How much of the light a metre of air at the valley floor takes out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set so that a hillside half a kilometre off has lost about a third of its
    /// contrast and one two kilometres off is very nearly the colour of the sky. That is
    /// a clear day in hill country rather than a foggy one, and it is the number the
    /// backdrop was missing: at the 1.6e-4 it carried before, a ridge at the far rim of
    /// a 1.5 km reconstruction was still 94% itself, which is to say the fog was not
    /// there.
    /// </para>
    /// <para>
    /// Per metre, and deliberately not scaled to the size of the set. What decides how
    /// hazy a mountain looks is how far away it is, and a reconstruction that reaches
    /// six kilometres should have a hazier rim than one that reaches one.
    /// </para>
    /// </remarks>
    public float HazeDensity { get; set; } = 6.5e-4f;

    /// <summary>How many metres the haze thins over, above the camera.</summary>
    /// <remarks>
    /// The scale height of the air, and what makes this aerial perspective rather than
    /// distance fog. At a hundred and thirty metres a ridge rising two hundred above the
    /// camera sits in a fifth of the density its own foot does, so it stands clear of
    /// the murk in the valley below it — which is the shape the eye reads as depth in
    /// real country, and the reason a flat fog makes hills look like a painted flat.
    /// </remarks>
    public float HazeHeight { get; set; } = 130f;

    /// <summary>Fraction of the procedural sky occupied by cloud, zero to one.</summary>
    public float CloudCoverage { get; set; } = 0.78f;

    /// <summary>Frequency of the cloud forms; smaller values make broader masses.</summary>
    public float CloudScale { get; set; } = 1f;

    /// <summary>Creates the pipeline for one scene's backdrop.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="backdrop">The terrain, forest and layers to build and draw.</param>
    /// <returns>The pipeline.</returns>
    public static TerrainPipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler,
        TerrainBackdrop backdrop)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(backdrop);

        var pipeline = new TerrainPipeline(context)
        {
            _extent = backdrop.ExtentMeters,
            _sunDirection = backdrop.SunDirection,
            _azimuth = backdrop.Azimuth,
            _anchorUnits = backdrop.AnchorUnits,
        };

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                VertexSource, ShaderStage.Vertex, "terrain.vert", "main", ShaderLanguage.Glsl));
            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                FragmentSource, ShaderStage.Fragment, "terrain.frag", "main", ShaderLanguage.Glsl));
            pipeline._treeVertexModule = pipeline.CreateModule(compiler.Compile(
                TreeVertexSource, ShaderStage.Vertex, "trees.vert", "main", ShaderLanguage.Glsl));
            pipeline._treeFragmentModule = pipeline.CreateModule(compiler.Compile(
                TreeFragmentSource, ShaderStage.Fragment, "trees.frag", "main", ShaderLanguage.Glsl));
            pipeline._skyVertexModule = pipeline.CreateModule(compiler.Compile(
                SkyVertexSource, ShaderStage.Vertex, "horizon-sky.vert", "main", ShaderLanguage.Glsl));
            pipeline._skyFragmentModule = pipeline.CreateModule(compiler.Compile(
                SkyFragmentSource, ShaderStage.Fragment, "horizon-sky.frag", "main", ShaderLanguage.Glsl));

            pipeline._modelVertexModule = pipeline.CreateModule(compiler.Compile(
                TreeModelVertexSource, ShaderStage.Vertex, "horizon-tree-model.vert", "main",
                ShaderLanguage.Glsl));

            pipeline._modelFragmentModule = pipeline.CreateModule(compiler.Compile(
                TreeModelFragmentSource, ShaderStage.Fragment, "horizon-tree-model.frag",
                "main", ShaderLanguage.Glsl));

            pipeline.BuildMesh(backdrop);
            pipeline.BuildTrees(backdrop);

            // The tiles repeat and are colour; the splat is data and must not be
            // sRGB-decoded or wrapped; the tint is colour but clamped like the splat.
            //
            // **All six carry a mip chain, and the last two are why the ridges used to
            // crawl.** A thousand-cell splat map is stretched over a kilometre and a half
            // of terrain, so a mountain at the far edge of it puts twenty cells inside one
            // pixel. Sampled from the top level with no chain to fall back on, that pixel
            // takes whichever cell it happens to land in — rock here, forest at the
            // neighbouring pixel, rock again at the next — and a hillside a kilometre away
            // comes out as a shimmering grey-and-green weave that moves with the camera.
            // It is the most visible thing in an outdoor scene and it is one flag.
            pipeline._textures[0] = VulkanTexture.Create(context, backdrop.TileForest);
            pipeline._textures[1] = VulkanTexture.Create(context, backdrop.TileRock);
            pipeline._textures[2] = VulkanTexture.Create(context, backdrop.TileGrass);
            pipeline._textures[3] = VulkanTexture.Create(context, backdrop.TileDirt);
            //
            // Blocks where the pack holds them, which is the same picture with its chain
            // already built and no PNG decode in front of it. The linear/sRGB choice moves
            // into the block format there — BC7_UNORM for the weights, BC7_UNORM_SRGB for
            // the tint — so it is stated once either way.
            pipeline._textures[4] = backdrop.SplatBlocks is { } splat
                ? VulkanTexture.Create(context, splat, SamplerAddressMode.ClampToEdge)
                : VulkanTexture.Create(
                    context, backdrop.Splat, mipmaps: true,
                    SamplerAddressMode.ClampToEdge, linear: true);

            pipeline._textures[5] = backdrop.TintBlocks is { } tint
                ? VulkanTexture.Create(context, tint, SamplerAddressMode.ClampToEdge)
                : VulkanTexture.Create(
                    context, backdrop.Tint, mipmaps: true, SamplerAddressMode.ClampToEdge);

            pipeline.CreateDescriptors();

            // Before the pipelines, because the models' own descriptor layout is one of
            // the two the pipeline that draws them is built against.
            pipeline.BuildTreeModels(backdrop);
            pipeline.BuildPipelines(colorFormat, depthFormat);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Records the backdrop: terrain, forest, then the sky behind them.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="camera">Where the player is looking from, in room units.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    public void Record(CommandBuffer command, Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (_vertices is null || _indices is null || width <= 0 || height <= 0)
        {
            return;
        }

        // The camera's offset from the scene centre, turned into backdrop metres and
        // into the backdrop's own frame — the sky's azimuth separates the two. Clamped
        // so no camera the scripts place can leave the grid or dive through a ridge.
        Matrix4x4 intoTerrain = Matrix4x4.CreateRotationY(-_azimuth);
        Vector3 offset = Vector3.TransformNormal(
            (camera.Position - _anchorUnits) * MetersPerUnit, intoTerrain);

        float reach = _extent * 0.25f;
        if (offset.Length() > reach)
        {
            offset = Vector3.Normalize(offset) * reach;
        }

        offset.Y -= LiftMeters;

        // And never below the ground it is standing on. The lift is a constant and the
        // reconstruction is not: a set whose panorama saw almost nothing is nearly all
        // fill, and its fill sits close to zero — so twelve metres of lift put the camera
        // a few metres *under* the surface, and every direction became a wall of hillside
        // rising out of the bottom of the frame. CSD is the set that did it. Raising
        // rather than clamping, because a lookout genuinely stands sixty metres over its
        // own valley and that has to survive.
        offset.Y = MathF.Max(offset.Y, Ground(offset.X, offset.Z) + ClearanceMeters);

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 forwardT = Vector3.TransformNormal(forward, intoTerrain);
        Vector3 upT = Vector3.TransformNormal(camera.Up, intoTerrain);

        Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(offset, offset + forwardT, upT);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            camera.FieldOfView, (float)width / height, 2f, _extent * 3f);
        projection.M22 *= -1;

        // The sun the scene is lit by, brought into the backdrop's frame so a slope
        // faces it the same way the room's shadows say it should.
        Vector4 sun = _sunDirection is { } travelling
            ? new Vector4(
                Vector3.TransformNormal(Vector3.Normalize(-travelling), intoTerrain), 1f)
            : new Vector4(0f, 1f, 0f, 0f);

        // Before the push, because how far the modelled trees reached is one of the
        // numbers in it: the impostors are told to start where the models ran out.
        SelectTreeModels(offset);

        var push = new TerrainPush
        {
            ViewProjection = view * projection,
            Sun = sun,
            Params = new Vector4(TileMeters, TintAmount, HazeDensity, _extent),
            Eye = new Vector4(offset, Math.Clamp(CloudCoverage, 0f, 1f)),
            Haze = new Vector4(
                _modelReach, _modelKinds, 0f, MathF.Max(1f, HazeHeight)),
        };

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);

        DescriptorSet set = _set;
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        _vk.CmdPushConstants(
            command, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<TerrainPush>(), &push);

        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);
        Silk.NET.Vulkan.Buffer vertexBuffer = _vertices.Handle;
        ulong offsetZero = 0;
        _vk.CmdBindVertexBuffers(command, 0, 1, in vertexBuffer, in offsetZero);
        _vk.CmdBindIndexBuffer(command, _indices.Handle, 0, IndexType.Uint32);
        _vk.CmdDrawIndexed(command, _indexCount, 1, 0, 0, 0);

        if (_treeInstances is not null && _treeCount > 0)
        {
            _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _treePipeline);

            Silk.NET.Vulkan.Buffer* treeStreams = stackalloc Silk.NET.Vulkan.Buffer[2]
            {
                _treeVertices!.Handle,
                _treeInstances.Handle,
            };
            ulong* treeOffsets = stackalloc ulong[2] { 0, 0 };
            _vk.CmdBindVertexBuffers(command, 0, 2, treeStreams, treeOffsets);
            _vk.CmdBindIndexBuffer(command, _treeIndices!.Handle, 0, IndexType.Uint16);

            for (int kind = 0; kind < _stands.Length; kind++)
            {
                if (_stands[kind].Count == 0)
                {
                    continue;
                }

                (uint firstIndex, int vertexOffset, uint indexCount) = _impostors[kind];

                _vk.CmdDrawIndexed(
                    command, indexCount, _stands[kind].Count,
                    firstIndex, vertexOffset, _stands[kind].First);
            }
        }

        // And the near band as real trees. After the impostors rather than before, so the
        // cheap pass has already put its depth down and the alpha-tested cards — which are
        // the expensive fragments here — are rejected wherever a cone is already nearer.
        if (_modelPipeline.Handle != 0 && _modelInstances is not null && _modelCount > 0)
        {
            _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _modelPipeline);

            DescriptorSet ground = _set;
            _vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Graphics, _modelLayout, 0, 1, in ground, 0, null);
            _vk.CmdPushConstants(
                command, _modelLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, (uint)Marshal.SizeOf<TerrainPush>(), &push);

            Silk.NET.Vulkan.Buffer* modelStreams = stackalloc Silk.NET.Vulkan.Buffer[2]
            {
                _modelVertices!.Handle,
                _modelInstances.Handle,
            };
            ulong* modelOffsets = stackalloc ulong[2] { 0, 0 };
            _vk.CmdBindVertexBuffers(command, 0, 2, modelStreams, modelOffsets);
            _vk.CmdBindIndexBuffer(command, _modelIndices!.Handle, 0, IndexType.Uint32);

            int bound = -1;

            for (int model = 0; model < _models.Length; model++)
            {
                if (_modelStands[model].Count == 0)
                {
                    continue;
                }

                foreach ((int sheet, uint firstIndex, uint indexCount) in _models[model].Parts)
                {
                    if (sheet != bound)
                    {
                        DescriptorSet painted = _sheetSets[sheet];
                        _vk.CmdBindDescriptorSets(
                            command, PipelineBindPoint.Graphics, _modelLayout, 1, 1,
                            in painted, 0, null);

                        bound = sheet;
                    }

                    _vk.CmdDrawIndexed(
                        command, indexCount, _modelStands[model].Count, firstIndex,
                        _models[model].VertexOffset, _modelStands[model].First);
                }
            }
        }

        // The sky last, at the far plane, over exactly the pixels nothing claimed. Its
        // basis must be the same orthonormal basis CreateLookAt uses. Passing camera.Up
        // directly works only at zero pitch; as the view tilts it shears every ray away
        // from the centre, which made a fixed cloud field appear to zoom and squint.
        Vector3 skyRight = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 skyUp = Vector3.Cross(forward, skyRight);
        float tanHalfFov = MathF.Tan(camera.FieldOfView / 2f);

        var skyPush = new SkyPush
        {
            Forward = new Vector4(forward, 0f),
            Right = new Vector4(skyRight, tanHalfFov * width / height),
            Up = new Vector4(skyUp, tanHalfFov),
            Viewport = new Vector4(width, height, 0f, 0f),
            Sun = _sunDirection is { } sunWorld
                ? new Vector4(Vector3.Normalize(-sunWorld), 1f)
                : new Vector4(0f, 1f, 0f, 0f),
            Clouds = new Vector4(
                Math.Clamp(CloudCoverage, 0f, 1f),
                Math.Clamp(CloudScale, 0.25f, 4f),
                19.7f + (MathF.Sin(_azimuth) * 13.1f),
                -7.3f + (MathF.Cos(_azimuth) * 17.9f)),
        };

        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _skyPipeline);
        _vk.CmdPushConstants(
            command, _skyLayout, ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<SkyPush>(), &skyPush);
        _vk.CmdDraw(command, 3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DestroyPipeline(ref _pipeline);
        DestroyPipeline(ref _treePipeline);
        DestroyPipeline(ref _skyPipeline);

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _layout, null);
            _layout = default;
        }

        if (_skyLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _skyLayout, null);
            _skyLayout = default;
        }

        if (_pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }

        if (_setLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
            _setLayout = default;
        }

        DestroyModule(ref _vertexModule);
        DestroyModule(ref _fragmentModule);
        DestroyModule(ref _treeVertexModule);
        DestroyModule(ref _treeFragmentModule);
        DestroyModule(ref _skyVertexModule);
        DestroyModule(ref _skyFragmentModule);

        _vertices?.Dispose();
        _vertices = null;
        _indices?.Dispose();
        _indices = null;
        _treeVertices?.Dispose();
        _treeVertices = null;
        _treeIndices?.Dispose();
        _treeIndices = null;
        _modelVertices?.Dispose();
        _modelVertices = null;
        _modelIndices?.Dispose();
        _modelIndices = null;
        _modelInstances?.Dispose();
        _modelInstances = null;

        foreach (VulkanTexture sheet in _sheets)
        {
            sheet.Dispose();
        }

        _sheets.Clear();
        _sheetSets.Clear();

        if (_modelPipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, _modelPipeline, null);
            _modelPipeline = default;
        }

        if (_modelLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _modelLayout, null);
            _modelLayout = default;
        }

        if (_sheetPool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_context.Device, _sheetPool, null);
            _sheetPool = default;
        }

        if (_sheetLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_context.Device, _sheetLayout, null);
            _sheetLayout = default;
        }

        if (_modelVertexModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _modelVertexModule, null);
            _modelVertexModule = default;
        }

        if (_modelFragmentModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _modelFragmentModule, null);
            _modelFragmentModule = default;
        }
        _treeInstances?.Dispose();
        _treeInstances = null;

        for (int i = 0; i < _textures.Length; i++)
        {
            _textures[i]?.Dispose();
            _textures[i] = null;
        }
    }

    private void DestroyPipeline(ref Pipeline pipeline)
    {
        if (pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, pipeline, null);
            pipeline = default;
        }
    }

    private void DestroyModule(ref ShaderModule module)
    {
        if (module.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, module, null);
            module = default;
        }
    }

    /// <summary>One corner of the grid: where it is and which way its ground faces.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TerrainVertex(Vector3 Position, Vector3 Normal);

    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainPush
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
        /// The air: w is the height the haze thins over, and xyz are spare.
        /// </summary>
        /// <remarks>
        /// A whole vector for one number, and it takes the push block to the 128 bytes
        /// every Vulkan implementation is required to offer — which is the ceiling this
        /// has to live under. The three spare floats are where the next thing about the
        /// air goes, and there will be one.
        /// </remarks>
        public Vector4 Haze;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyPush
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

    private void BuildMesh(TerrainBackdrop backdrop)
    {
        // Every other grid cell: 512 by 512 corners over the 1024 grid is a quarter of
        // the vertices for a silhouette the eye cannot tell apart at these distances.
        const int Stride = 2;

        int grid = backdrop.Grid;
        float extent = backdrop.ExtentMeters;
        float[] heights = backdrop.Heights;

        if (heights.Length != grid * grid)
        {
            throw new VulkanException(
                $"A terrain backdrop's heights are {heights.Length} values for a " +
                $"{grid} by {grid} grid.");
        }

        int side = ((grid - 1) / Stride) + 1;
        float step = (2f * extent) / (grid - 1);

        // Kept, so the camera can be told what the ground under it is doing. See Ground.
        _heights = heights;
        _grid = grid;

        var vertices = new TerrainVertex[side * side];

        for (int row = 0; row < side; row++)
        {
            int gz = Math.Min(row * Stride, grid - 1);

            for (int column = 0; column < side; column++)
            {
                int gx = Math.Min(column * Stride, grid - 1);

                // Central differences over the vertices that are actually drawn.
                //
                // They used to be taken over single cells of the full-resolution grid, on
                // the reasoning that a vertex the stride skipped should still bend its
                // neighbours' normals. It does — and the detail it bends them by is finer
                // than the surface carrying it, so neighbouring vertices a stride apart get
                // normals from unrelated cells. On a ridge whose faces are a pixel or two
                // wide, that lights adjacent triangles differently for no reason the shape
                // shows, and the ridge crawls. The normal has to describe the surface that
                // is there.
                float left = heights[(gz * grid) + Math.Max(gx - Stride, 0)];
                float right = heights[(gz * grid) + Math.Min(gx + Stride, grid - 1)];
                float near = heights[(Math.Max(gz - Stride, 0) * grid) + gx];
                float far = heights[(Math.Min(gz + Stride, grid - 1) * grid) + gx];

                var normal = Vector3.Normalize(
                    new Vector3(left - right, 2f * Stride * step, near - far));

                vertices[(row * side) + column] = new TerrainVertex(
                    new Vector3(
                        (gx * step) - extent,
                        heights[(gz * grid) + gx],
                        (gz * step) - extent),
                    normal);
            }
        }

        uint[] indices = new uint[(side - 1) * (side - 1) * 6];
        int write = 0;

        for (int row = 0; row < side - 1; row++)
        {
            for (int column = 0; column < side - 1; column++)
            {
                uint a = (uint)((row * side) + column);
                uint b = a + 1;
                uint c = a + (uint)side;
                uint d = c + 1;

                indices[write++] = a;
                indices[write++] = c;
                indices[write++] = b;
                indices[write++] = b;
                indices[write++] = c;
                indices[write++] = d;
            }
        }

        _vertices = VulkanBuffer.CreateDeviceLocal<TerrainVertex>(
            _context, vertices, BufferUsageFlags.VertexBufferBit);
        _indices = VulkanBuffer.CreateDeviceLocal<uint>(
            _context, indices, BufferUsageFlags.IndexBufferBit);
        _indexCount = (uint)indices.Length;
    }

    /// <summary>
    /// The shapes a distant wood is made of, in the order the offline placement numbers
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four silhouettes rather than one. A hillside of identical cones is the tell that
    /// gave the reconstructed horizon away from across a valley: a real wood is conifers
    /// and broadleaves mixed, with scrub where it thins out, and at a kilometre the only
    /// thing that survives of a tree <em>is</em> its silhouette — so the silhouette is the
    /// one thing worth spending geometry on.
    /// </para>
    /// <para>
    /// Sixteen to twenty-four triangles apiece, which is what an impostor can afford when
    /// there are twenty thousand of them. The measurements are metres at a scale of one,
    /// and the offline placement varies that per tree.
    /// </para>
    /// </remarks>
    private static readonly (int Sides, float Height, (float At, float Radius)[] Rings)[]
        Impostors =
    [
        // A spruce: widest at the foot, drawn straight to a point.
        (8, 14f, [(0.00f, 3.6f), (0.45f, 2.2f)]),

        // A broadleaf: a round crown carried clear of the ground, widest a third of the
        // way up and closed underneath. Three rings, because two make a diamond and a
        // diamond at this range is a cone with a pointed bottom.
        (10, 11f, [(0.24f, 2.9f), (0.44f, 4.5f), (0.74f, 3.5f)]),

        // A cypress: kept narrow and run tall.
        (6, 17f, [(0.00f, 1.7f), (0.55f, 1.4f)]),

        // Scrub, for the fringe of a wood and the open ground beyond it.
        (6, 3.6f, [(0.10f, 2.6f), (0.45f, 3.0f)]),
    ];

    /// <summary>Where each shape's geometry sits in the shared buffers.</summary>
    private (uint FirstIndex, int VertexOffset, uint IndexCount)[] _impostors = [];

    /// <summary>How many instances of each shape there are, and where they start.</summary>
    private (uint First, uint Count)[] _stands = [];

    private void BuildTrees(TerrainBackdrop backdrop)
    {
        float[] trees = backdrop.Trees;

        if (trees.Length < Stride)
        {
            return;
        }

        List<TerrainVertex> mesh = [];
        List<ushort> shapes = [];
        var ranges = new (uint, int, uint)[Impostors.Length];

        for (int kind = 0; kind < Impostors.Length; kind++)
        {
            (int sides, float height, (float At, float Radius)[] rings) = Impostors[kind];
            int vertexOffset = mesh.Count;
            uint firstIndex = (uint)shapes.Count;

            foreach ((float at, float radius) in rings)
            {
                for (int i = 0; i < sides; i++)
                {
                    float angle = i * (2f * MathF.PI / sides);
                    var outward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));

                    mesh.Add(new TerrainVertex(
                        (outward * radius) + new Vector3(0f, at * height, 0f),
                        Vector3.Normalize(outward + new Vector3(0f, radius / height, 0f))));
                }
            }

            // Relative to this shape's first vertex, because the draw adds the shape's
            // vertex offset for us. Absolute here and it would be added twice, and every
            // shape but the first would be built from another shape's corners.
            int tip = mesh.Count - vertexOffset;
            mesh.Add(new TerrainVertex(new Vector3(0f, height, 0f), Vector3.UnitY));

            // A crown lifted off the ground is closed underneath; one standing on it is
            // not, because nothing ever sees the bottom of a fir.
            bool skirted = rings[0].At > 0.001f;
            int foot = mesh.Count - vertexOffset;

            if (skirted)
            {
                mesh.Add(new TerrainVertex(
                    new Vector3(0f, rings[0].At * height * 0.35f, 0f), -Vector3.UnitY));
            }

            void Band(int lower, int upper)
            {
                for (int i = 0; i < sides; i++)
                {
                    int next = (i + 1) % sides;

                    shapes.Add((ushort)(lower + i));
                    shapes.Add((ushort)(lower + next));
                    shapes.Add((ushort)(upper + next));

                    shapes.Add((ushort)(lower + i));
                    shapes.Add((ushort)(upper + next));
                    shapes.Add((ushort)(upper + i));
                }
            }

            for (int ring = 0; ring + 1 < rings.Length; ring++)
            {
                Band(ring * sides, (ring + 1) * sides);
            }

            int last = (rings.Length - 1) * sides;

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;

                shapes.Add((ushort)(last + i));
                shapes.Add((ushort)(last + next));
                shapes.Add((ushort)tip);

                if (skirted)
                {
                    shapes.Add((ushort)next);
                    shapes.Add((ushort)i);
                    shapes.Add((ushort)foot);
                }
            }

            ranges[kind] = (firstIndex, vertexOffset, (uint)shapes.Count - firstIndex);
        }

        // The instances, straight from the offline placement but gathered by shape, so
        // that one wood is four draws over four slices of one buffer rather than four
        // buffers or a branch in the vertex shader. A cap far above any real set, purely
        // so a malformed file cannot ask for the moon.
        uint count = Math.Min((uint)(trees.Length / Stride), 800_000u);
        var placed = new float[count * Stride];
        var stands = new (uint First, uint Count)[Impostors.Length];
        uint written = 0;

        for (int kind = 0; kind < Impostors.Length; kind++)
        {
            uint first = written;

            for (uint at = 0; at < count; at++)
            {
                // Anything the file numbers past the shapes that exist falls to the first,
                // which is a conifer: an unknown species is still a tree.
                int wanted = (int)trees[(at * Stride) + 5];

                if (wanted != kind && !(kind == 0 && (wanted < 0 || wanted >= Impostors.Length)))
                {
                    continue;
                }

                trees.AsSpan((int)(at * Stride), Stride)
                    .CopyTo(placed.AsSpan((int)(written * Stride), Stride));

                written++;
            }

            stands[kind] = (first, written - first);
        }

        _treeVertices = VulkanBuffer.CreateDeviceLocal<TerrainVertex>(
            _context, mesh.ToArray(), BufferUsageFlags.VertexBufferBit);
        _treeIndices = VulkanBuffer.CreateDeviceLocal<ushort>(
            _context, shapes.ToArray(), BufferUsageFlags.IndexBufferBit);
        _treeInstances = VulkanBuffer.CreateDeviceLocal<float>(
            _context, placed, BufferUsageFlags.VertexBufferBit);

        _impostors = ranges;
        _stands = stands;
        _treeCount = written;
    }

    /// <summary>How far the modelled trees may reach, in metres from the camera.</summary>
    /// <remarks>
    /// Past this the impostors have it, whatever the budget would allow. Three hundred
    /// metres is where a fourteen-metre tree is about forty pixels tall on a 720-line
    /// screen — small enough that a cone with the right silhouette is honestly as good,
    /// and small enough that the alpha-tested cards start to shimmer rather than resolve.
    /// </remarks>
    public float ModelReachMeters { get; set; } = 460f;

    /// <summary>
    /// How many triangles a frame may spend on the near forest.
    /// </summary>
    /// <remarks>
    /// The budget rather than a count of trees, because the two levels of detail differ
    /// by five times: a full tree is twenty thousand triangles and the cheap one four,
    /// so "the nearest two hundred" means something very different depending on which is
    /// drawn. Spending it nearest-first means the trees the player is looking at get the
    /// full model and the rest get whatever is left.
    /// </remarks>
    public int ModelTriangleBudget { get; set; } = 3_000_000;

    /// <summary>How many of the nearest may be the full model rather than the cheap one.</summary>
    /// <remarks>
    /// Both a count and a distance, and the distance is what stops the count being silly.
    /// A full broadleaf is twenty-two thousand triangles against the cheap one's four, and
    /// spending the first forty of those on trees a quarter of a kilometre out — where the
    /// two are indistinguishable — is most of the budget gone before the band that can
    /// actually use it. Seventy metres is about where the difference stops showing.
    /// </remarks>
    public int FullDetailTrees { get; set; } = 48;

    /// <summary>How near a tree must be to be worth the full model.</summary>
    public float FullDetailMeters { get; set; } = 70f;

    /// <summary>Where one model's geometry sits, and what it is painted with.</summary>
    private readonly record struct TreeModelDraw(
        int Kind,
        int Detail,
        int Triangles,
        uint FirstIndex,
        int VertexOffset,
        (int Sheet, uint FirstIndex, uint IndexCount)[] Parts);

    private TreeModelDraw[] _models = [];

    /// <summary>How many instances of each model there are this frame, and where.</summary>
    private (uint First, uint Count)[] _modelStands = [];

    /// <summary>The placements, kept on the host so the near ones can be picked out.</summary>
    private float[] _placements = [];

    /// <summary>Scratch for the selection, so a camera move allocates nothing.</summary>
    private (float Away, int At)[] _candidates = [];

    private float[] _modelInstanceData = [];

    /// <summary>Where the camera was when the selection was last made.</summary>
    private Vector3 _selectedAt = new(float.MaxValue);

    /// <summary>How far the models actually reached, and which species have one.</summary>
    private float _modelReach;
    private float _modelKinds;

    /// <summary>
    /// Builds the modelled trees, their textures and the pipeline that draws them.
    /// </summary>
    /// <param name="backdrop">The backdrop, which carries the models the loader read.</param>
    private void BuildTreeModels(TerrainBackdrop backdrop)
    {
        if (backdrop.TreeModels.Count == 0 || backdrop.Trees.Length < Stride)
        {
            return;
        }

        // One texture and one descriptor set apiece. There are four of them at most — a
        // trunk and three sprays — so a set each is simpler than an array of samplers and
        // asks nothing of the device that a 1.0 driver does not already offer.
        foreach (Formats.Bitmaps.DecodedImage image in backdrop.TreeTextures)
        {
            _sheets.Add(VulkanTexture.Create(_context, image));
        }

        if (_sheets.Count > 0)
        {
            CreateSheetSets();
        }

        var corners = new List<TerrainTreeVertex>();
        var indices = new List<uint>();
        var draws = new List<TreeModelDraw>();

        foreach (TerrainTreeModel model in backdrop.TreeModels)
        {
            if (model.Kind is < 0 or >= 4 || model.Vertices.Length == 0)
            {
                continue;
            }

            int vertexOffset = corners.Count;
            uint firstIndex = (uint)indices.Count;

            corners.AddRange(model.Vertices);
            indices.AddRange(model.Indices);

            var parts = new List<(int, uint, uint)>();

            foreach (TerrainTreePart part in model.Parts)
            {
                if (part.Texture >= 0 && part.Texture < _sheets.Count && part.IndexCount > 0)
                {
                    parts.Add((part.Texture, firstIndex + part.FirstIndex, part.IndexCount));
                }
            }

            if (parts.Count == 0)
            {
                continue;
            }

            draws.Add(new TreeModelDraw(
                model.Kind, model.Detail, model.Triangles, firstIndex, vertexOffset, [.. parts]));

            _modelKinds = (int)_modelKinds | (1 << model.Kind);
        }

        if (draws.Count == 0)
        {
            return;
        }

        _models = [.. draws];
        _modelStands = new (uint, uint)[_models.Length];
        _placements = backdrop.Trees;

        _modelVertices = VulkanBuffer.CreateDeviceLocal<TerrainTreeVertex>(
            _context, [.. corners], BufferUsageFlags.VertexBufferBit);
        _modelIndices = VulkanBuffer.CreateDeviceLocal<uint>(
            _context, [.. indices], BufferUsageFlags.IndexBufferBit);

        // Room for every tree the budget could ever reach at the cheapest model, so the
        // selection never has to grow it and a frame never allocates.
        int cheapest = _models.Min(m => Math.Max(1, m.Triangles));
        int capacity = Math.Clamp(ModelTriangleBudget / cheapest, 64, 20_000);

        _modelInstanceData = new float[capacity * Stride];
        _modelInstances = VulkanBuffer.CreateHostVisible(
            _context,
            (ulong)(_modelInstanceData.Length * sizeof(float)),
            BufferUsageFlags.VertexBufferBit);
    }

    /// <summary>
    /// Picks which trees are near enough to be drawn as models, and where.
    /// </summary>
    /// <param name="eye">The camera, in backdrop metres.</param>
    /// <remarks>
    /// <para>
    /// Nearest first, spending a triangle budget: the closest handful get the full model,
    /// the next few hundred get the cheap one, and the budget stops wherever it stops.
    /// What that leaves is <see cref="_modelReach"/> — how far the models actually got —
    /// and the impostors are told to start there rather than at a constant, so a dense
    /// wood and a thin one both hand over exactly where the models ran out.
    /// </para>
    /// <para>
    /// Only when the camera has moved. A room camera is a fixed viewpoint and the trees
    /// do not move, so the answer is the same frame after frame; recomputing it would
    /// sort twenty thousand distances sixty times a second to arrive back where it was.
    /// </para>
    /// </remarks>
    private void SelectTreeModels(Vector3 eye)
    {
        if (_models.Length == 0 || _modelInstances is null)
        {
            return;
        }

        // Eight metres. Small enough that the band never visibly lags the camera, large
        // enough that a glide is a handful of rebuilds rather than one a frame.
        if ((eye - _selectedAt).LengthSquared() < 64f)
        {
            return;
        }

        _selectedAt = eye;

        int trees = _placements.Length / Stride;
        float reach = ModelReachMeters;
        float reachSquared = reach * reach;

        if (_candidates.Length < trees)
        {
            _candidates = new (float, int)[trees];
        }

        int found = 0;

        for (int i = 0; i < trees; i++)
        {
            int at = i * Stride;
            int kind = (int)_placements[at + 5];

            if (kind is < 0 or >= 4 || ((int)_modelKinds & (1 << kind)) == 0)
            {
                continue;
            }

            float dx = _placements[at] - eye.X;
            float dy = _placements[at + 1] - eye.Y;
            float dz = _placements[at + 2] - eye.Z;
            float away = (dx * dx) + (dy * dy) + (dz * dz);

            if (away < reachSquared)
            {
                _candidates[found++] = (away, at);
            }
        }

        Array.Sort(_candidates, 0, found, CandidateOrder.Instance);

        // Grouped by model, because a draw is one model and one slice of the buffer. The
        // pass below decides each tree's detail from its rank, and the pass after gathers
        // them: two cheap walks rather than a sort inside every group.
        int capacity = _modelInstanceData.Length / Stride;
        float full = FullDetailMeters * FullDetailMeters;
        var wanted = new int[_models.Length];
        int spent = 0;
        int taken = 0;
        float last = 0;

        var detail = new byte[found];

        for (int rank = 0; rank < found && taken < capacity; rank++)
        {
            int at = _candidates[rank].At;
            int kind = (int)_placements[at + 5];
            int want = rank < FullDetailTrees && _candidates[rank].Away < full ? 0 : 1;
            int model = Model(kind, want);

            // A species with only one of the two levels grown uses it for both bands.
            if (model < 0)
            {
                model = Model(kind, 1 - want);
            }

            if (model < 0 || spent + _models[model].Triangles > ModelTriangleBudget)
            {
                break;
            }

            detail[rank] = (byte)model;
            wanted[model]++;
            spent += _models[model].Triangles;
            last = _candidates[rank].Away;
            taken++;
        }

        uint first = 0;

        for (int model = 0; model < _models.Length; model++)
        {
            _modelStands[model] = (first, (uint)wanted[model]);
            first += (uint)wanted[model];
            wanted[model] = 0;
        }

        for (int rank = 0; rank < taken; rank++)
        {
            int model = detail[rank];
            uint slot = _modelStands[model].First + (uint)wanted[model]++;

            _placements.AsSpan(_candidates[rank].At, Stride)
                .CopyTo(_modelInstanceData.AsSpan((int)slot * Stride, Stride));
        }

        _modelCount = (uint)taken;

        // Where the impostors are told to take over. The farthest tree the budget reached
        // when it ran out, and the full reach when it did not: a wood that fits entirely
        // inside the budget should hand over at the distance, not at its own last tree.
        _modelReach = taken == 0
            ? 0f
            : (taken < found ? MathF.Sqrt(last) : reach);

        _modelInstances.Write<float>(_modelInstanceData.AsSpan(0, (int)_modelCount * Stride));
    }

    /// <summary>
    /// The height of the backdrop's ground at a point, in its own metres.
    /// </summary>
    /// <param name="x">Where, east.</param>
    /// <param name="z">Where, north.</param>
    /// <returns>The height, or nought when there is no grid to ask.</returns>
    /// <remarks>
    /// Bilinear, and off the full-resolution grid rather than the drawn mesh: this is
    /// asked once a frame and what it is for is keeping the camera out of the hill, so
    /// it should agree with the ground rather than with the stride the ground is drawn
    /// at.
    /// </remarks>
    private float Ground(float x, float z)
    {
        if (_grid < 2 || _heights.Length != _grid * _grid || _extent <= 0f)
        {
            return 0f;
        }

        float at = ((x / _extent) + 1f) * 0.5f * (_grid - 1);
        float down = ((z / _extent) + 1f) * 0.5f * (_grid - 1);

        int left = Math.Clamp((int)MathF.Floor(at), 0, _grid - 2);
        int top = Math.Clamp((int)MathF.Floor(down), 0, _grid - 2);
        float acrossFraction = Math.Clamp(at - left, 0f, 1f);
        float downFraction = Math.Clamp(down - top, 0f, 1f);

        float upper = float.Lerp(
            _heights[(top * _grid) + left], _heights[(top * _grid) + left + 1], acrossFraction);
        float lower = float.Lerp(
            _heights[((top + 1) * _grid) + left],
            _heights[((top + 1) * _grid) + left + 1],
            acrossFraction);

        return float.Lerp(upper, lower, downFraction);
    }

    /// <summary>Which model draws a given species at a given level of detail.</summary>
    private int Model(int kind, int detail)
    {
        for (int i = 0; i < _models.Length; i++)
        {
            if (_models[i].Kind == kind && _models[i].Detail == detail)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Nearest first.</summary>
    private sealed class CandidateOrder : IComparer<(float Away, int At)>
    {
        public static readonly CandidateOrder Instance = new();

        public int Compare((float Away, int At) a, (float Away, int At) b) =>
            a.Away.CompareTo(b.Away);
    }

    /// <summary>A descriptor set for each of the trees' own textures.</summary>
    private void CreateSheetSets()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };

        if (_vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _sheetLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the tree texture descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)_sheets.Count,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)_sheets.Count,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _sheetPool)
            != Result.Success)
        {
            throw new VulkanException("Could not create the tree texture descriptor pool.");
        }

        foreach (VulkanTexture texture in _sheets)
        {
            DescriptorSetLayout layout = _sheetLayout;
            var allocate = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _sheetPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };

            if (_vk.AllocateDescriptorSets(_context.Device, in allocate, out DescriptorSet set)
                != Result.Success)
            {
                throw new VulkanException("Could not allocate a tree texture descriptor set.");
            }

            var image = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = texture.View,
                Sampler = texture.Sampler,
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &image,
            };

            _vk.UpdateDescriptorSets(_context.Device, 1, in write, 0, null);
            _sheetSets.Add(set);
        }
    }

    private ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            if (_vk.CreateShaderModule(_context.Device, in createInfo, null, out ShaderModule module)
                != Result.Success)
            {
                throw new VulkanException("Could not create a terrain shader module.");
            }

            return module;
        }
    }

    private void CreateDescriptors()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[6];

        for (uint i = 0; i < 6; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 6,
            PBindings = bindings,
        };

        if (_vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _setLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the terrain descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 6,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the terrain descriptor pool.");
        }

        DescriptorSetLayout setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        if (_vk.AllocateDescriptorSets(_context.Device, in allocate, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the terrain descriptor set.");
        }

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[6];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];

        for (int i = 0; i < 6; i++)
        {
            VulkanTexture texture = _textures[i]!;
            images[i] = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = texture.View,
                Sampler = texture.Sampler,
            };

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = images + i,
            };
        }

        _vk.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    private void BuildPipelines(Format colorFormat, Format depthFormat)
    {
        DescriptorSetLayout setLayout = _setLayout;

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<TerrainPush>(),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in layoutInfo, null, out _layout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the terrain pipeline layout.");
        }

        var skyPush = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<SkyPush>(),
        };

        var skyLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &skyPush,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in skyLayoutInfo, null, out _skyLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the horizon sky pipeline layout.");
        }

        // Terrain: one 24-byte stream of position and normal.
        var terrainBinding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)Marshal.SizeOf<TerrainVertex>(),
            InputRate = VertexInputRate.Vertex,
        };

        VertexInputAttributeDescription* terrainAttributes =
            stackalloc VertexInputAttributeDescription[2]
            {
                new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
            };

        _pipeline = BuildOne(
            colorFormat, depthFormat, _vertexModule, _fragmentModule, _layout,
            1, &terrainBinding, 2, terrainAttributes, depthWrite: true);

        // Trees: every impostor shape in stream 0, one 24-byte placement per instance in
        // stream 1. The shapes share a buffer and are drawn as ranges of it, so a hillside
        // of four species is four draws rather than four pipelines.
        VertexInputBindingDescription* treeBindings =
            stackalloc VertexInputBindingDescription[2]
            {
                new()
                {
                    Binding = 0,
                    Stride = (uint)Marshal.SizeOf<TerrainVertex>(),
                    InputRate = VertexInputRate.Vertex,
                },
                new()
                {
                    Binding = 1,
                    Stride = 6 * sizeof(float),
                    InputRate = VertexInputRate.Instance,
                },
            };

        VertexInputAttributeDescription* treeAttributes =
            stackalloc VertexInputAttributeDescription[5]
            {
                new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
                new() { Location = 2, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
                new() { Location = 3, Binding = 1, Format = Format.R32Sfloat, Offset = 16 },
                new() { Location = 4, Binding = 1, Format = Format.R32Sfloat, Offset = 20 },
            };

        _treePipeline = BuildOne(
            colorFormat, depthFormat, _treeVertexModule, _treeFragmentModule, _layout,
            2, treeBindings, 5, treeAttributes, depthWrite: true);

        // The modelled trees of the near band. Two descriptor sets rather than one: the
        // splat and the tint it shares with the ground, and the one sheet it is painted
        // with, which changes per part.
        if (_models.Length > 0 && _sheetLayout.Handle != 0)
        {
            DescriptorSetLayout* modelSets = stackalloc DescriptorSetLayout[2]
            {
                _setLayout,
                _sheetLayout,
            };

            var modelLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = modelSets,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstants,
            };

            if (_vk.CreatePipelineLayout(_context.Device, in modelLayoutInfo, null, out _modelLayout)
                != Result.Success)
            {
                throw new VulkanException("Could not create the horizon tree pipeline layout.");
            }

            VertexInputBindingDescription* modelBindings =
                stackalloc VertexInputBindingDescription[2]
                {
                    new()
                    {
                        Binding = 0,
                        Stride = (uint)Marshal.SizeOf<TerrainTreeVertex>(),
                        InputRate = VertexInputRate.Vertex,
                    },
                    new()
                    {
                        Binding = 1,
                        Stride = Stride * sizeof(float),
                        InputRate = VertexInputRate.Instance,
                    },
                };

            VertexInputAttributeDescription* modelAttributes =
                stackalloc VertexInputAttributeDescription[6]
                {
                    new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                    new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
                    new() { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 },
                    new() { Location = 3, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
                    new() { Location = 4, Binding = 1, Format = Format.R32Sfloat, Offset = 16 },
                    new() { Location = 5, Binding = 1, Format = Format.R32Sfloat, Offset = 20 },
                };

            _modelPipeline = BuildOne(
                colorFormat, depthFormat, _modelVertexModule, _modelFragmentModule, _modelLayout,
                2, modelBindings, 6, modelAttributes, depthWrite: true);
        }

        // The sky: no vertex input at all, and no depth writes — it must lose to
        // everything and stop nothing.
        _skyPipeline = BuildOne(
            colorFormat, depthFormat, _skyVertexModule, _skyFragmentModule, _skyLayout,
            0, null, 0, null, depthWrite: false);
    }

    private Pipeline BuildOne(
        Format colorFormat,
        Format depthFormat,
        ShaderModule vertex,
        ShaderModule fragment,
        PipelineLayout layout,
        uint bindingCount,
        VertexInputBindingDescription* bindings,
        uint attributeCount,
        VertexInputAttributeDescription* attributes,
        bool depthWrite)
    {
        nint entryPoint = SilkMarshal.StringToPtr("main");

        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = (byte*)entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = (byte*)entryPoint,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = bindingCount,
                PVertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = attributeCount,
                PVertexAttributeDescriptions = attributes,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            DynamicState* dynamicStates = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
            };

            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            // No culling: whether a grid's winding survives the world's handedness is
            // exactly the kind of thing that would otherwise be diagnosed as a black
            // screen, and a heightfield seen from above has almost no back faces anyway.
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1f,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = depthWrite,
                DepthCompareOp = CompareOp.LessOrEqual,
            };

            PipelineColorBlendAttachmentState* blendAttachments =
                stackalloc PipelineColorBlendAttachmentState[(int)GBuffer.Targets];

            blendAttachments[GBuffer.Colour] = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };

            for (int i = 1; i < (int)GBuffer.Targets; i++)
            {
                blendAttachments[i] = default;
            }

            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = GBuffer.Targets,
                PAttachments = blendAttachments,
            };

            Format* colors = stackalloc Format[(int)GBuffer.Targets]
            {
                colorFormat,
                GBuffer.NormalFormat,
                GBuffer.MotionFormat,
                GBuffer.LightFormat,
            };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = GBuffer.Targets,
                PColorAttachmentFormats = colors,
                DepthAttachmentFormat = depthFormat,
            };

            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout,
            };

            Result created = _vk.CreateGraphicsPipelines(
                _context.Device, default, 1, in createInfo, null, out Pipeline pipeline);

            if (created != Result.Success)
            {
                throw new VulkanException($"Could not create a terrain pipeline: {created}.");
            }

            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
