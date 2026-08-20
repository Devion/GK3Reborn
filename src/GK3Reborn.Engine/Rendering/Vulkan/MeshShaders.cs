namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// The mesh shaders, in GLSL.
/// </summary>
/// <remarks>
/// <para>
/// GLSL rather than the HLSL the plan chose, and for exactly one reason: glslang — which
/// is what shaderc uses for both languages — implements ray query only in its GLSL front
/// end. Its HLSL front end does not know <c>RaytracingAccelerationStructure</c> and fails
/// at the declaration. The alternative is DXC, which would reintroduce the Vulkan SDK
/// prerequisite that shaderc was chosen to avoid. See ADR 0008.
/// </para>
/// <para>
/// One source serves both the raster and the ray-traced pipeline; the ray-tracing paths
/// are behind <c>RAY_TRACING</c>, defined by <see cref="Compose"/>. A device without the
/// extensions gets a shader that cannot reference an acceleration structure at all, which
/// matters because Vulkan requires every statically used binding to be valid whether the
/// branch runs or not.
/// </para>
/// </remarks>
internal static class MeshShaders
{
    /// <summary>Declarations both stages share.</summary>
    private const string Common = """
        struct Light
        {
            // xyz position, w the distance at which falloff begins
            vec4 positionAndStart;

            // rgb colour, a the multiplier on it
            vec4 colorAndIntensity;

            // xyz direction for spots, w the distance at which it reaches zero
            vec4 directionAndEnd;

            // cosine of the lit core, cosine of the outer edge, spot flag, emitter radius
            vec4 cone;
        };

        layout(set = 0, binding = 0) uniform Frame
        {
            mat4 viewProjection;
            mat4 previousViewProjection;
            vec4 lightDirection;
            vec4 cameraPosition;

            // shadowed lights, occlusion rays, rays per shadow, how much the bake counts
            vec4 rays;

            // occlusion radius, unused, and the viewport in pixels
            vec4 tuning;
        } frame;

        layout(push_constant) uniform Draw
        {
            mat4 model;
            mat4 previousModel;

            // x selects the lightmap over the rig, y scales the lightmap,
            // z marks a surface that carries its own brightness
            vec4 shading;
        } draw;
        """;

    private const string Vertex = """
        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec2 inTexCoord;
        layout(location = 3) in vec2 inLightmapCoord;

        // The same vertex a pose ago. A rigid batch binds the same buffer to both streams,
        // so this is its own position and the motion comes out as the transform's alone.
        layout(location = 4) in vec3 inPreviousPosition;

        layout(location = 0) out vec3 outNormal;
        layout(location = 1) out vec2 outTexCoord;
        layout(location = 2) out vec2 outLightmapCoord;
        layout(location = 3) out vec3 outWorld;

        // Where this vertex is now and where it was, both in clip space. The fragment
        // stage divides them and takes the difference, which it cannot do from an
        // interpolated screen position: perspective division does not survive
        // interpolation, so the two clip positions have to travel and be divided there.
        layout(location = 4) out vec4 outClip;
        layout(location = 5) out vec4 outPreviousClip;

        void main()
        {
            vec4 world = draw.model * vec4(inPosition, 1.0);
            vec4 clip = frame.viewProjection * world;

            gl_Position = clip;
            outNormal = normalize(mat3(draw.model) * inNormal);
            outTexCoord = inTexCoord;
            outLightmapCoord = inLightmapCoord;
            outWorld = world.xyz;
            outClip = clip;

            outPreviousClip =
                frame.previousViewProjection *
                (draw.previousModel * vec4(inPreviousPosition, 1.0));
        }
        """;

    private const string Fragment = """
        layout(location = 0) in vec3 inNormal;
        layout(location = 1) in vec2 inTexCoord;
        layout(location = 2) in vec2 inLightmapCoord;
        layout(location = 3) in vec3 inWorld;
        layout(location = 4) in vec4 inClip;
        layout(location = 5) in vec4 inPreviousClip;

        layout(location = 0) out vec4 outColor;

        // The surface, and how far it moved. Nothing in the picture uses either; they are
        // what lets anything filter over time.
        layout(location = 1) out vec4 outNormalTarget;
        layout(location = 2) out vec2 outMotion;

        layout(set = 0, binding = 1) uniform Rig
        {
            // x is how many of the array are in use
            vec4 counts;
            Light lights[64];
        } rig;

        #ifdef RAY_TRACING
        layout(set = 0, binding = 2) uniform accelerationStructureEXT scene;
        #endif

        layout(set = 1, binding = 0) uniform sampler2D baseColor;
        layout(set = 1, binding = 1) uniform sampler2D lightmapTexture;
        layout(set = 1, binding = 2) uniform sampler2D normalTexture;

        // The original's ambient floor, so a surface no light reaches is dim, not black.
        const vec3 kAmbient = vec3(0.06, 0.08, 0.06);

        // A GK3 unit is roughly two and a half centimetres — a character stands about
        // seventy tall — so this offsets a ray start by under two centimetres. Enough to
        // clear the surface it came from without floating visibly above it.
        const float kRayBias = 0.75;

        // And this lifts the start off the surface along its normal, which is a different
        // thing and the one that matters. A minimum distance is measured *along the ray*,
        // so a ray leaving at a grazing angle — which is every ray on a curved surface
        // facing away from the light — is still within a hair of the surface after it and
        // hits the surface it started on. On a wall there are few such angles; on a face
        // there is little else, which is why the room was clean and Gabriel was covered in
        // black speckle.
        //
        // Two and a half units, about six centimetres. It has to clear not only the
        // surface but the gap between the smooth normal a low-polygon character is shaded
        // with and the flat triangle the ray actually starts on, which on a face is most
        // of the error.
        const float kNormalBias = 6.0;

        // Where a ray should start: off the surface, along its normal.
        vec3 RayStart(vec3 position, vec3 normal)
        {
            return position + (normal * kNormalBias);
        }

        // How much a light has to contribute before its shadow is worth a ray. Below this
        // the shadow it casts is under a step of an eight-bit channel, so tracing it costs
        // a ray and changes no pixel.
        const float kShadowFloor = 0.004;

        // The reciprocal of the golden ratio. Advancing an angle by this fraction of a
        // turn spaces successive samples about as evenly as a sequence can.
        const float kGolden = 0.6180339887;

        // A random value for this pixel, taken from the pixel rather than from the point
        // it shades: world coordinates run into the hundreds, and a sine of a number that
        // large loses enough precision to band into visible patterns across a wall.
        //
        // And from the pixel *only* — the frame number used to go in here as well. Varying
        // the seed each frame is what lets a temporal filter average the noise away, and
        // there is no temporal filter: the grain simply changed every frame, which reads as
        // a pattern crawling across the picture and is far more distracting than grain that
        // sits still. One frame to the next, 15% of the picture used to move by more than a
        // step of an eight-bit channel with nothing in the room moving at all.
        //
        // Fixed to the pixel, the noise becomes a dither pattern locked to the screen. That
        // is the right trade until something accumulates frames, and it is why High showed
        // this worse than Medium: High leans less on the bake, so more of what you see comes
        // from the sampled terms.
        float PixelNoise(float salt)
        {
            vec3 seed = vec3(gl_FragCoord.xy, salt);

            return fract(sin(dot(seed, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
        }

        void Basis(vec3 normal, out vec3 tangent, out vec3 bitangent)
        {
            // Frisvad's method, with the branch that avoids its singularity at the pole.
            vec3 up = abs(normal.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);

            tangent = normalize(cross(up, normal));
            bitangent = cross(normal, tangent);
        }

        #ifdef RAY_TRACING
        bool Occluded(vec3 origin, vec3 direction, float reach)
        {
            rayQueryEXT query;

            rayQueryInitializeEXT(
                query,
                scene,
                gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsOpaqueEXT,
                0xFF,
                origin,
                kRayBias,
                direction,
                reach);

            while (rayQueryProceedEXT(query)) { }

            return rayQueryGetIntersectionTypeEXT(query, true) !=
                   gl_RayQueryCommittedIntersectionNoneEXT;
        }

        // How much of a light reaches a point: one ray for a hard shadow, several jittered
        // across the emitter's own radius for a soft one.
        float Visibility(
            vec3 position, vec3 normal, Light light, vec3 toLight, float distance, int samples)
        {
            vec3 start = RayStart(position, normal);

            if (samples <= 1)
            {
                return Occluded(start, toLight / distance, distance) ? 0.0 : 1.0;
            }

            vec3 tangent;
            vec3 bitangent;
            Basis(toLight / distance, tangent, bitangent);

            float radius = light.cone.w;
            float jitter = PixelNoise(3.0);
            float turn = PixelNoise(4.0);
            float visible = 0.0;

            for (int i = 0; i < samples; i++)
            {
                float angle = 6.2831853 * fract(turn + (float(i) * kGolden));
                float offset = radius * sqrt((float(i) + jitter) / float(samples));

                vec3 target = toLight +
                              (tangent * cos(angle) * offset) +
                              (bitangent * sin(angle) * offset);

                float reach = length(target);
                visible += Occluded(start, target / reach, reach) ? 0.0 : 1.0;
            }

            return visible / float(samples);
        }

        float Occlusion(vec3 position, vec3 normal, int samples, float radius)
        {
            vec3 start = RayStart(position, normal);
            vec3 tangent;
            vec3 bitangent;
            Basis(normal, tangent, bitangent);

            // Stratified rather than drawn independently: the elevation steps once
            // through the hemisphere and the azimuth advances by the golden angle, so
            // eight rays cover the hemisphere evenly instead of clumping and leaving
            // gaps. At this sample count that is most of the difference between a smooth
            // occlusion term and the grain the same eight rays used to produce.
            float jitter = PixelNoise(1.0);
            float turn = PixelNoise(2.0);
            float open = 0.0;

            for (int i = 0; i < samples; i++)
            {
                // Cosine-weighted, so the samples cluster where they matter most.
                float u = (float(i) + jitter) / float(samples);
                float radial = sqrt(u);
                float angle = 6.2831853 * fract(turn + (float(i) * kGolden));

                vec3 direction = normalize(
                    (tangent * radial * cos(angle)) +
                    (bitangent * radial * sin(angle)) +
                    (normal * sqrt(max(0.0, 1.0 - u))));

                open += Occluded(start, direction, radius) ? 0.0 : 1.0;
            }

            return open / float(samples);
        }
        #endif

        // The rig the artists authored, evaluated directly. Falloff is linear between the
        // light's start and end distances: that is what 3ds Max's linear decay did and what
        // these numbers were tuned against, and inverse-square would darken every room.
        vec3 EvaluateRig(vec3 position, vec3 normal, int shadowed, int shadowSamples)
        {
            vec3 total = vec3(0.0);
            int count = int(rig.counts.x);
            int traced = 0;

            for (int i = 0; i < count; i++)
            {
                Light light = rig.lights[i];

                vec3 toLight = light.positionAndStart.xyz - position;
                float distance = max(length(toLight), 0.0001);
                vec3 direction = toLight / distance;

                float lambert = max(dot(normal, direction), 0.0);
                if (lambert <= 0.0)
                {
                    continue;
                }

                float start = light.positionAndStart.w;
                float end = light.directionAndEnd.w;
                float reach = clamp((end - distance) / max(end - start, 0.001), 0.0, 1.0);

                // Squared, not linear. The authored range is respected either way, but a
                // linear ramp spreads a lamp's light evenly across a whole room and the
                // result is flatter than the bake it replaces. Squaring concentrates it
                // near the source, which is both closer to how light behaves and closer
                // to what the artists' own bakes look like.
                float attenuation = reach * reach;

                if (attenuation <= 0.0)
                {
                    continue;
                }

                float cone = 1.0;
                if (light.cone.z > 0.5)
                {
                    float aligned = dot(-direction, light.directionAndEnd.xyz);
                    cone = smoothstep(light.cone.y, light.cone.x, aligned);
                }

                vec3 contribution = light.colorAndIntensity.rgb * light.colorAndIntensity.w *
                                    attenuation * cone * lambert;

                float visibility = 1.0;

                #ifdef RAY_TRACING
                // Spend the ray budget on the lights that are actually lighting this
                // pixel, not on the first few of the array. A scene's rig is sorted by
                // brightness times reach, which puts the sun and the streetlights at the
                // front — and from inside a hotel room every one of them is behind a wall,
                // so the budget went entirely on rays that returned "occluded" for the
                // whole image while the lamp overhead, further down the array, was never
                // tested at all. That is why nothing in a room cast a shadow.
                if (traced < shadowed && max(contribution.r, max(contribution.g, contribution.b)) > kShadowFloor)
                {
                    traced++;
                    visibility =
                        Visibility(position, normal, light, toLight, distance, shadowSamples);
                }
                #endif

                total += contribution * visibility;
            }

            return total;
        }

        // A tangent frame, built from the screen-space derivatives of position and texture
        // coordinate rather than stored on the vertex.
        //
        // Stored tangents would have to be rebuilt every frame: GK3's characters have no
        // skeleton, so an .ACT clip rewrites their vertex positions on every frame of every
        // animation, and a tangent computed at load would be stale the moment anybody moved.
        // A derivative frame is correct for free, on deforming and rigid geometry alike.
        vec3 PerturbedNormal(vec3 geometric)
        {
            // Two channels, not three. BC5 keeps only X and Y — it has no third channel to
            // keep — and Z is recovered from them, which is exact for a unit vector in
            // tangent space because Z is never negative there. An uncompressed map stores a
            // Z as well, and reconstructing it rather than reading it gives the same answer
            // to within a rounding step, so both sources take this one path.
            vec2 mapped = texture(normalTexture, inTexCoord).xy;

            vec2 tangentXY = (mapped * 2.0) - 1.0;

            vec3 tangentNormal = vec3(
                tangentXY, sqrt(max(0.0, 1.0 - dot(tangentXY, tangentXY))));

            // Surfaces with no map are given a flat one, and eight bits cannot encode
            // exactly a half: 128 decodes to 0.0039 rather than 0. The tolerance is one
            // step of an eight-bit channel, not an epsilon — a tighter test never fires,
            // which is how the first attempt at this ran the derivative maths on all 6,400
            // textures that have no map at all.
            if (max(abs(tangentNormal.x), abs(tangentNormal.y)) <= (1.0 / 255.0))
            {
                return geometric;
            }

            vec3 dpx = dFdx(inWorld);
            vec3 dpy = dFdy(inWorld);
            vec2 dtx = dFdx(inTexCoord);
            vec2 dty = dFdy(inTexCoord);

            // Degenerate where a surface is edge-on or its coordinates do not vary, which
            // happens at silhouettes and on the flat-shaded helpers. Falling back to the
            // geometric normal there is invisible; dividing by zero is not.
            float area = (dtx.x * dty.y) - (dty.x * dtx.y);

            if (abs(area) < 1e-12)
            {
                return geometric;
            }

            vec3 tangent = ((dpx * dty.y) - (dpy * dtx.y)) / area;

            // Gram-Schmidt against the interpolated normal, so the frame stays square even
            // where the derivatives are noisy.
            tangent = normalize(tangent - (geometric * dot(geometric, tangent)));

            if (any(isnan(tangent)))
            {
                return geometric;
            }

            vec3 bitangent = cross(geometric, tangent);

            return normalize(mat3(tangent, bitangent, geometric) * tangentNormal);
        }

        void main()
        {
            vec4 sampled = texture(baseColor, inTexCoord);
            vec3 albedo = sampled.rgb;

            // GK3 keys transparency on magenta. It is converted to alpha before upload, so
            // the test here is on alpha — which filters and mips gracefully — with the
            // colour test kept as a backstop for anything the conversion missed.
            if (sampled.a < 0.5 || distance(albedo, vec3(1.0, 0.0, 1.0)) < 0.1)
            {
                discard;
            }

            // Both extra targets, written before anything can return. A fragment that
            // leaves an output alone does not leave it cleared — it leaves it undefined,
            // and the self-lit return below used to hand a filter whatever was in the
            // register: every lamp, and the painted street through the hotel window,
            // claiming to have crossed the screen since the last frame.
            vec3 normal = PerturbedNormal(normalize(inNormal));

            outNormalTarget = vec4(normal, 0.0);
            outMotion = vec2(0.0);

            // In pixels, and from this frame back to the last, which is the direction a
            // filter reads it in: "the pixel I want from the last frame is this far away".
            //
            // Where the fragment is now comes from gl_FragCoord rather than from its own
            // interpolated clip position. The two agree to within a rounding error, but
            // clip positions on distant geometry are large enough that subtracting two of
            // them leaves nothing but that error. A surface that was behind the previous
            // camera keeps the zero: it had no previous pixel to point at.
            if (inPreviousClip.w > 1e-4)
            {
                vec2 there = inPreviousClip.xy / inPreviousClip.w;
                outMotion = (there * 0.5 + 0.5) * frame.tuning.zw - gl_FragCoord.xy;
            }

            // A surface the bake never lit: a bulb, a shade with a lamp inside it, the
            // painted view through a window. The original binds a white lightmap and a
            // multiplier of one for these, which comes out as the texture untouched, and
            // no amount of ray tracing should dim something that is its own light source.
            if (draw.shading.z > 0.5)
            {
                outColor = vec4(albedo, 1.0);
                return;
            }

            float useLightmap = draw.shading.x;
            vec3 baked = texture(lightmapTexture, inLightmapCoord).rgb * draw.shading.y;

            int shadowed = int(frame.rays.x);
            int occlusionRays = int(frame.rays.y);
            int shadowSamples = max(int(frame.rays.z), 1);
            float bakedWeight = frame.rays.w;

            bool tracing = shadowed > 0 || occlusionRays > 0;

            if (!tracing)
            {
                // No rays: scene geometry is exactly what the 1999 renderer showed, and
                // anything without a lightmap is lit by the rig with no shadows.
                vec3 direct = rig.counts.x > 0.5
                    ? EvaluateRig(inWorld, normal, 0, 1)
                    : vec3(0.35) + (0.65 * max(dot(normal, -frame.lightDirection.xyz), 0.0));

                outColor = vec4(
                    mix(albedo * (kAmbient + direct), albedo * baked, useLightmap), 1.0);

                return;
            }

            vec3 direct = EvaluateRig(inWorld, normal, shadowed, shadowSamples);

            float occlusion = 1.0;

            #ifdef RAY_TRACING
            if (occlusionRays > 0)
            {
                occlusion = Occlusion(inWorld, normal, occlusionRays, frame.tuning.x);
            }
            #endif

            // Indirect light. There is no gathered bounce yet, so the bake stands in for
            // it: scaled down, because it also contains the direct light being computed
            // afresh above, and weighted less the higher the quality goes.
            vec3 indirect = mix(kAmbient, baked * useLightmap, bakedWeight * useLightmap) * occlusion;

            outColor = vec4(albedo * (indirect + direct), 1.0);
        }
        """;

    /// <summary>Builds a stage's source.</summary>
    /// <param name="fragment">True for the fragment stage, false for the vertex stage.</param>
    /// <param name="rayTracing">Whether the ray-tracing paths are compiled in.</param>
    /// <returns>Complete GLSL.</returns>
    public static string Compose(bool fragment, bool rayTracing)
    {
        string header = rayTracing
            ? "#version 460\n#extension GL_EXT_ray_query : require\n#define RAY_TRACING 1\n"
            : "#version 460\n";

        return header + Common + "\n" + (fragment ? Fragment : Vertex);
    }
}
