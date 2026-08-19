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
            vec4 lightDirection;
            vec4 cameraPosition;

            // shadowed lights, occlusion rays, rays per shadow, how much the bake counts
            vec4 rays;

            // occlusion radius, frame counter, unused, unused
            vec4 tuning;
        } frame;

        layout(push_constant) uniform Draw
        {
            mat4 model;

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

        layout(location = 0) out vec3 outNormal;
        layout(location = 1) out vec2 outTexCoord;
        layout(location = 2) out vec2 outLightmapCoord;
        layout(location = 3) out vec3 outWorld;

        void main()
        {
            vec4 world = draw.model * vec4(inPosition, 1.0);

            gl_Position = frame.viewProjection * world;
            outNormal = normalize(mat3(draw.model) * inNormal);
            outTexCoord = inTexCoord;
            outLightmapCoord = inLightmapCoord;
            outWorld = world.xyz;
        }
        """;

    private const string Fragment = """
        layout(location = 0) in vec3 inNormal;
        layout(location = 1) in vec2 inTexCoord;
        layout(location = 2) in vec2 inLightmapCoord;
        layout(location = 3) in vec3 inWorld;

        layout(location = 0) out vec4 outColor;

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

        // The original's ambient floor, so a surface no light reaches is dim, not black.
        const vec3 kAmbient = vec3(0.06, 0.08, 0.06);

        // A GK3 unit is roughly two and a half centimetres — a character stands about
        // seventy tall — so this offsets a ray start by under two centimetres. Enough to
        // clear the surface it came from without floating visibly above it.
        const float kRayBias = 0.75;

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
        float PixelNoise(float salt)
        {
            vec3 seed = vec3(gl_FragCoord.xy, frame.tuning.y + salt);

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
        float Visibility(vec3 position, Light light, vec3 toLight, float distance, int samples)
        {
            if (samples <= 1)
            {
                return Occluded(position, toLight / distance, distance) ? 0.0 : 1.0;
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
                visible += Occluded(position, target / reach, reach) ? 0.0 : 1.0;
            }

            return visible / float(samples);
        }

        float Occlusion(vec3 position, vec3 normal, int samples, float radius)
        {
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

                open += Occluded(position, direction, radius) ? 0.0 : 1.0;
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
                    visibility = Visibility(position, light, toLight, distance, shadowSamples);
                }
                #endif

                total += contribution * visibility;
            }

            return total;
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

            // A surface the bake never lit: a bulb, a shade with a lamp inside it, the
            // painted view through a window. The original binds a white lightmap and a
            // multiplier of one for these, which comes out as the texture untouched, and
            // no amount of ray tracing should dim something that is its own light source.
            if (draw.shading.z > 0.5)
            {
                outColor = vec4(albedo, 1.0);
                return;
            }

            vec3 normal = normalize(inNormal);
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
