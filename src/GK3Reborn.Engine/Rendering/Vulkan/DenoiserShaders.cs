// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>The compute stages that trace occlusion and then denoise it.</summary>
/// <remarks>
/// <para>
/// The three filtering stages are a port of AMD's FidelityFX denoiser, from the SDK's
/// 1.1.4 release, which is MIT licensed. The algorithm is theirs and is followed closely
/// enough that their source reads as a commentary on this one; what has changed is
/// mechanical:
/// </para>
/// <list type="bullet">
/// <item>
/// The wave intrinsics are gone. Their reductions are done through shared memory instead,
/// which their own code already carries a path for, so the shaders need no subgroup
/// extensions and behave the same whatever width the device runs them at.
/// </item>
/// <item>
/// Clip space is Vulkan's, so the vertical flip their normalised coordinates carry —
/// they are written against Direct3D — is not applied.
/// </item>
/// <item>
/// The pass that packs a ray tracer's output into a bitmask is not here. Ours writes the
/// bitmask itself, so there is nothing to convert.
/// </item>
/// <item>
/// Normals arrive already signed and normalised, so the unpacking multiply and add are
/// dropped rather than passed as one and zero.
/// </item>
/// </list>
/// <para>
/// What is denoised is a *fraction*, not one light's shadow. A GK3 room is lit by a rig of
/// point and spot lights and any of them may be behind a wall, so there is no single sun
/// whose visibility could be tracked. Each pixel instead picks one light with a
/// probability proportional to what that light contributes to it, and traces one ray at
/// it. The answer is one bit, which is what the denoiser wants, and its expected value
/// over many frames is exactly the fraction of the direct light that reaches the pixel —
/// so the denoised result can simply multiply the unshadowed direct term.
/// </para>
/// <para>
/// Ambient occlusion rides the same machinery: one cosine-weighted ray a pixel, one bit,
/// and a second copy of the filter chain.
/// </para>
/// </remarks>
internal static class DenoiserShaders
{
    /// <summary>Bindings and helpers every stage shares.</summary>
    private const string Common = """
        #define TILE_WIDTH 8
        #define TILE_HEIGHT 4

        uint RoundedDivide(uint value, uint divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        uvec2 TileOf(uvec2 pixel)
        {
            return uvec2(pixel.x / TILE_WIDTH, pixel.y / TILE_HEIGHT);
        }

        uint LinearTile(uvec2 tile, uint width)
        {
            return (tile.y * RoundedDivide(width, TILE_WIDTH)) + tile.x;
        }

        uint BitInTile(uvec2 pixel)
        {
            return 1u << (((pixel.y % TILE_HEIGHT) * TILE_WIDTH) + (pixel.x % TILE_WIDTH));
        }

        const uint kTileCleared = 1u;
        const uint kTileLit = 2u;
        """;

    /// <summary>
    /// One ray a pixel, at one light chosen by how much it contributes, plus one for
    /// occlusion.
    /// </summary>
    /// <remarks>
    /// Both answers are single bits packed into a tile's worth of them, which is the shape
    /// the filter chain reads. The seed varies with the frame — the whole reason the
    /// grain was previously pinned to the pixel was that nothing averaged it, and now
    /// something does.
    /// </remarks>
    private const string Trace = """
        layout(local_size_x = TILE_WIDTH, local_size_y = TILE_HEIGHT, local_size_z = 1) in;

        struct Light
        {
            vec4 positionAndStart;
            vec4 colorAndIntensity;
            vec4 directionAndEnd;
            vec4 cone;
        };

        layout(set = 0, binding = 0) uniform texture2D depthTarget;
        layout(set = 0, binding = 1) uniform texture2D normalTarget;
        layout(set = 0, binding = 2) uniform accelerationStructureEXT scene;

        layout(set = 0, binding = 3, std430) writeonly buffer ShadowMask
        {
            uint data[];
        } shadowMask;

        layout(set = 0, binding = 4, std430) writeonly buffer OcclusionMask
        {
            uint data[];
        } occlusionMask;

        // What each pixel actually measured, as a number rather than a coin flip. The
        // bitmask above is still written, because the tile classification is built on it
        // and reads a whole tile as one word; this is what the estimate itself uses.
        layout(set = 0, binding = 6, r16f) writeonly uniform image2D shadowFraction;
        layout(set = 0, binding = 7, r16f) writeonly uniform image2D occlusionFraction;

        // Unsized, and a storage buffer, because the rig outgrew a uniform block when the
        // limit of sixty-four lights went. This pass still walks the whole rig rather than
        // a grid cell: it runs once a pixel to decide *which* light is worth a shadow ray,
        // and that is a question about the brightest few of the room rather than about the
        // handful reaching one point. The rig is ordered brightest first for it.
        layout(std430, set = 0, binding = 5) readonly buffer Rig
        {
            vec4 counts;
            Light lights[];
        } rig;

        // The third channel: how much of the rig's light a *moving* thing takes away.
        //
        // It is kept apart from the shadow channel rather than folded into it because the
        // composite needs the two separately. The bake already contains every shadow the
        // room casts on itself, so what the rig has to be credited with — and subtracted
        // from the bake, or it is counted twice — is the light that arrives past the room
        // alone. A character was never in the bake, so what it blocks has to come off the
        // result after that subtraction. Fold them together and the residual grows by
        // exactly what the character removed and the shadow disappears, which is what used
        // to happen: nothing a person walked past ever darkened.
        layout(set = 0, binding = 8, std430) writeonly buffer DynamicMask
        {
            uint data[];
        } dynamicMask;

        layout(set = 0, binding = 9, r16f) writeonly uniform image2D dynamicFraction;

        layout(push_constant) uniform Trace
        {
            mat4 viewProjectionInverse;
            ivec2 size;
            float radius;
            float seed;
            int samples;
            int padding;
        } trace;

        const float kRayBias = 0.75;
        const float kNormalBias = 6.0;
        const float kShadowFloor = 0.004;

        // The reciprocal of the golden ratio. Advancing an angle by this fraction of a
        // turn spaces successive samples about as evenly as a sequence can.
        const float kGolden = 0.6180339887;

        shared uint gShadow;
        shared uint gOcclusion;
        shared uint gDynamic;

        float Random(vec2 pixel, float salt)
        {
            vec3 seed = vec3(pixel, salt + trace.seed);

            return fract(sin(dot(seed, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
        }

        void Basis(vec3 normal, out vec3 tangent, out vec3 bitangent)
        {
            vec3 up = abs(normal.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);

            tangent = normalize(cross(up, normal));
            bitangent = cross(normal, tangent);
        }

        // The two halves of the acceleration structure. See RayTracingScene.MaskFor.
        const uint kRoomOnly = 0x01u;
        const uint kModelsOnly = 0x02u;
        const uint kEverything = 0xFFu;

        // What an *occlusion* ray leaving this pixel is allowed to hit.
        //
        // Everything, unless the pixel is on a model — a character or a prop — in which
        // case the room and nothing else. The mesh pass writes a negative roughness into
        // the normal target to say so, which is the whole of the signal.
        //
        // A hemisphere of rays is a harsher test of the shell stack than a shadow ray is,
        // and the reason is the cosine weighting: most of an occlusion ray's samples leave
        // nearly along the normal and the shell above is exactly there, so a torso inside a
        // shirt occludes itself over most of its hemisphere however the faces are wound.
        // Shadow rays leave toward one light and are handled below.
        //
        // Shadow rays no longer come through here. They are traced twice against the two
        // halves separately, because the composite needs to tell a shadow the bake already
        // holds from one a character is casting now.
        uint OcclusionMaskFor(float roughness)
        {
            return roughness < 0.0 ? kRoomOnly : kEverything;
        }

        bool Occluded(vec3 origin, vec3 direction, float reach, uint mask, uint flags, float from)
        {
            rayQueryEXT query;

            rayQueryInitializeEXT(
                query,
                scene,
                gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsOpaqueEXT | flags,
                mask,
                origin,
                from,
                direction,
                reach);

            while (rayQueryProceedEXT(query)) { }

            return rayQueryGetIntersectionTypeEXT(query, true) !=
                   gl_RayQueryCommittedIntersectionNoneEXT;
        }

        bool Occluded(vec3 origin, vec3 direction, float reach, uint mask)
        {
            return Occluded(origin, direction, reach, mask, 0u, kRayBias);
        }

        // What a shadow ray leaving a *model* is allowed to hit, when the thing it is
        // testing against is the models.
        //
        // <b>Back faces are skipped, and that is what makes a character able to shadow
        // itself at all.</b> GK3's people are not solid bodies: a character is a dozen
        // separate meshes with a shirt shell around a whole torso, sleeves around whole
        // arms, a collar around a whole neck. The surface a ray starts from is very often
        // *inside* another mesh of the same person, so the ray hits that mesh immediately
        // and no bias fixes it — the geometry really is there. That is why every character
        // used to come out with a hard dark patch across the chest and the small of the
        // back, and why models were made to skip models outright.
        //
        // The two cases are told apart by which side of a triangle the ray meets. Leaving a
        // surface that is inside a shell, the ray meets that shell from within and hits its
        // back face: an artefact of how the model is built, and skipped. Blocked by
        // something genuinely in the way — an arm across a chest, a hat brim over a face,
        // another person standing between this one and the lamp — the ray meets that
        // surface from outside and hits its front face, which is a real shadow and is kept.
        //
        // It costs the shadow a shell casts on whatever is directly inside it, which is a
        // shadow nobody can see: the thing inside is not drawn where the shell covers it.
        const uint kSkipShells = gl_RayFlagsCullBackFacingTrianglesEXT;

        // How far along a self-shadow ray to start looking, in scene units — about four
        // centimetres. Small, because the winding is doing the work: this is only here for
        // the seam where two shells meet edge-on and neither face is clearly the far side.
        const float kSelfBias = 1.5;

        // What this light gives this pixel before anything blocks it, as a single number.
        // The same falloff, cone and lambert term the raster pass uses, so the weights
        // this samples by are the weights the result is multiplied back into.
        // This must agree with EvaluateRig's falloff exactly, or the light a pixel is most
        // likely to trace towards is not the light that is actually lighting it — the
        // estimate stays unbiased but its variance goes up, and a pixel would sample the
        // ground bounce while the sun did the shading.
        float Contribution(Light light, vec3 position, vec3 normal, out vec3 toLight)
        {
            toLight = light.positionAndStart.xyz - position;

            float distance = max(length(toLight), 0.0001);
            vec3 direction = toLight / distance;
            float lambert = max(dot(normal, direction), 0.0);

            if (lambert <= 0.0)
            {
                return 0.0;
            }

            float start = light.positionAndStart.w;
            float end = light.directionAndEnd.w;
            float reach = clamp((end - distance) / max(end - start, 0.001), 0.0, 1.0);
            float attenuation = reach * reach;

            if (light.cone.z >= 1.5)
            {
                attenuation = 1.0;
            }

            if (attenuation <= 0.0)
            {
                return 0.0;
            }

            float cone = 1.0;

            if (mod(light.cone.z, 2.0) > 0.5)
            {
                float aligned = dot(-direction, light.directionAndEnd.xyz);
                cone = smoothstep(light.cone.y, light.cone.x, aligned);
            }

            vec3 colour = light.colorAndIntensity.rgb * light.colorAndIntensity.w *
                          attenuation * cone * lambert;

            return max(colour.r, max(colour.g, colour.b));
        }

        // One light, drawn with probability proportional to what it contributes. Sampling
        // this way is what makes a single bit an unbiased estimate of the fraction of the
        // direct light that arrives: a light worth twice as much is picked twice as often,
        // so the average over frames weights each light exactly as the shading does.
        bool ShadowRay(
            vec3 position, vec3 normal, vec2 pixel, int index, int samples, uint mask,
            uint flags, float from)
        {
            int count = int(rig.counts.x);
            float total = 0.0;

            for (int i = 0; i < count; i++)
            {
                vec3 ignored;
                total += Contribution(rig.lights[i], position, normal, ignored);
            }

            // Nothing here is lit, so nothing here can be shadowed. Reporting it as lit
            // keeps whole tiles uniform, which is what lets the filter skip them.
            if (total <= kShadowFloor)
            {
                return true;
            }

            // Stratified, not drawn afresh each time. Eight independent draws over the
            // rig's cumulative brightness clump and leave gaps, and the gaps move every
            // frame, which is a blotch on a wall that will not sit still. Walking the
            // distribution in equal steps from one shared random offset visits every part
            // of it exactly once and leaves only the offset to vary.
            float pick = ((float(index) + Random(pixel, 5.0)) / float(samples)) * total;
            float running = 0.0;

            for (int i = 0; i < count; i++)
            {
                vec3 toLight;
                float weight = Contribution(rig.lights[i], position, normal, toLight);

                running += weight;

                if (running < pick || weight <= 0.0)
                {
                    continue;
                }

                // Across the emitter rather than at its centre, so a lamp with a radius
                // softens its own edge instead of drawing one hard line and relying on
                // the filter to blur it.
                float distance = max(length(toLight), 0.0001);
                vec3 tangent;
                vec3 bitangent;
                Basis(toLight / distance, tangent, bitangent);

                // The emitter's own disc, walked by the golden angle so that successive
                // samples spread rather than cluster.
                float angle = 6.2831853 *
                    fract(Random(pixel, 6.0) + (float(index) * kGolden));

                float offset = rig.lights[i].cone.w *
                    sqrt((float(index) + Random(pixel, 7.0)) / float(samples));

                vec3 target = toLight +
                              (tangent * cos(angle) * offset) +
                              (bitangent * sin(angle) * offset);

                float reach = length(target);
                vec3 start = position + (normal * kNormalBias);

                return !Occluded(start, target / reach, reach, mask, flags, from);
            }

            return true;
        }

        bool ShadowRay(
            vec3 position, vec3 normal, vec2 pixel, int index, int samples, uint mask)
        {
            return ShadowRay(position, normal, pixel, index, samples, mask, 0u, kRayBias);
        }

        bool OcclusionRay(
            vec3 position, vec3 normal, vec2 pixel, int index, int samples, uint mask)
        {
            vec3 tangent;
            vec3 bitangent;
            Basis(normal, tangent, bitangent);

            // Cosine-weighted and stratified: the elevation steps once through the
            // hemisphere and the azimuth advances by the golden angle, so the rays cover
            // it evenly instead of clumping. This is what the mesh shader used to do, and
            // losing it when the tracing moved into a pass of its own is most of why the
            // lobby's walls would not settle.
            float u = (float(index) + Random(pixel, 8.0)) / float(samples);
            float angle = 6.2831853 *
                fract(Random(pixel, 9.0) + (float(index) * kGolden));

            float radial = sqrt(u);

            vec3 direction = normalize(
                (tangent * radial * cos(angle)) +
                (bitangent * radial * sin(angle)) +
                (normal * sqrt(max(0.0, 1.0 - u))));

            return !Occluded(position + (normal * kNormalBias), direction, trace.radius, mask);
        }

        void main()
        {
            if (gl_LocalInvocationIndex == 0u)
            {
                gShadow = 0u;
                gOcclusion = 0u;
                gDynamic = 0u;
            }

            barrier();

            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            bool inside = pixel.x < trace.size.x && pixel.y < trace.size.y;
            float depth = inside ? texelFetch(depthTarget, pixel, 0).x : 1.0;

            // Sky, or nothing drawn. Lit and unoccluded, so an empty tile stays uniform.
            int samples = max(trace.samples, 1);
            int litCount = samples;
            int openCount = samples;
            int clearCount = samples;

            if (inside && depth > 0.0 && depth < 1.0)
            {
                vec2 uv = (vec2(pixel) + 0.5) / vec2(trace.size);
                vec4 homogeneous =
                    trace.viewProjectionInverse * vec4((uv * 2.0) - 1.0, depth, 1.0);

                vec3 position = homogeneous.xyz / homogeneous.w;
                vec4 surface = texelFetch(normalTarget, pixel, 0);
                vec3 normal = normalize(surface.xyz);
                bool onModel = surface.w < 0.0;
                uint mask = OcclusionMaskFor(surface.w);

                litCount = 0;
                openCount = 0;
                clearCount = 0;

                // Several rays rather than one. A single ray is an unbiased estimate of
                // the fraction and a terrible one — its error is half, every frame — and
                // only a long history hides that. A camera that is moving, or anything
                // that is, has no long history, so what the filter is handed has to be
                // worth something on its own.
                for (int i = 0; i < samples; i++)
                {
                    // The same ray twice, against one half of the structure each time.
                    // ShadowRay is deterministic in the pixel and the sample index, so
                    // both calls pick the same light and the same point on its emitter —
                    // the two answers are about one ray, which is what makes it sound to
                    // treat them as the static and moving halves of its visibility.
                    litCount += ShadowRay(
                        position, normal, vec2(pixel), i, samples, kRoomOnly) ? 1 : 0;

                    // The same ray against the models. From a model this is the person's
                    // own arm across their own chest and the person standing between them
                    // and the lamp, which needs the shells skipped; from the room it is
                    // whoever is standing there, and every face of them counts.
                    clearCount += (onModel
                        ? ShadowRay(
                            position, normal, vec2(pixel), i, samples,
                            kModelsOnly, kSkipShells, kSelfBias)
                        : ShadowRay(
                            position, normal, vec2(pixel), i, samples, kModelsOnly))
                        ? 1 : 0;

                    openCount +=
                        OcclusionRay(position, normal, vec2(pixel), i, samples, mask) ? 1 : 0;
                }
            }

            // Set only where every ray got through. The bit is what the tile
            // classification reads, and the one thing it does with it is decide that a
            // whole tile is fully lit and can be written as such — which is only true if
            // it is true of every ray. A majority would let a tile that is six-tenths lit
            // be written as ten-tenths.
            bool lit = litCount == samples;
            bool open = openCount == samples;
            bool clear = clearCount == samples;

            if (inside)
            {
                imageStore(shadowFraction, pixel,
                    vec4(float(litCount) / float(samples), 0.0, 0.0, 0.0));

                imageStore(occlusionFraction, pixel,
                    vec4(float(openCount) / float(samples), 0.0, 0.0, 0.0));

                imageStore(dynamicFraction, pixel,
                    vec4(float(clearCount) / float(samples), 0.0, 0.0, 0.0));
            }

            uint bit = BitInTile(uvec2(gl_LocalInvocationID.xy));

            if (lit)
            {
                atomicOr(gShadow, bit);
            }

            if (open)
            {
                atomicOr(gOcclusion, bit);
            }

            if (clear)
            {
                atomicOr(gDynamic, bit);
            }

            barrier();

            if (gl_LocalInvocationIndex == 0u)
            {
                uint tile = LinearTile(uvec2(gl_WorkGroupID.xy), uint(trace.size.x));

                shadowMask.data[tile] = gShadow;
                occlusionMask.data[tile] = gOcclusion;
                dynamicMask.data[tile] = gDynamic;
            }
        }
        """;

    /// <summary>Bindings the two denoising stages share.</summary>
    private const string Filtering = """
        layout(set = 0, binding = 0) uniform texture2D depthTarget;
        layout(set = 0, binding = 1) uniform texture2D normalTarget;
        layout(set = 0, binding = 2) uniform texture2D motionTarget;
        layout(set = 0, binding = 3) uniform texture2D previousDepth;
        layout(set = 0, binding = 4) uniform texture2D previousMoments;
        layout(set = 0, binding = 5) uniform texture2D history;
        layout(set = 0, binding = 6) uniform texture2D filterInput;
        layout(set = 0, binding = 7) uniform sampler clampedSampler;

        layout(set = 0, binding = 8, std430) readonly buffer Mask
        {
            uint data[];
        } mask;

        layout(set = 0, binding = 9, std430) buffer Metadata
        {
            uint data[];
        } metadata;

        // Every format here is one Vulkan requires of storage images, so nothing depends
        // on the extended set being present.
        layout(set = 0, binding = 10, rgba16f) uniform image2D reprojection;
        layout(set = 0, binding = 11, rgba32f) uniform image2D currentMoments;
        layout(set = 0, binding = 12, rgba16f) uniform image2D historyOut;
        layout(set = 0, binding = 13, r32f) uniform image2D result;

        // Three matrices will not fit in the guaranteed hundred and twenty-eight bytes of
        // push constants, so only what changes between the three blurring stages is
        // pushed.
        // What this pixel measured this frame, as a fraction of its rays.
        layout(set = 0, binding = 15) uniform texture2D fractionTarget;

        layout(set = 0, binding = 14) uniform Denoise
        {
            mat4 projectionInverse;
            mat4 reprojectionMatrix;
            mat4 viewProjectionInverse;
            vec4 eyeAndFirst;
            ivec2 size;
            vec2 inverseSize;
            vec4 sigma;
        } denoise;

        layout(push_constant) uniform Stage
        {
            int stepSize;
            int index;
        } stage;

        vec2 InverseSize() { return denoise.inverseSize; }
        ivec2 Size() { return denoise.size; }

        float LoadDepth(ivec2 p)
        {
            return texelFetch(depthTarget, p, 0).x;
        }

        bool IsShadowReceiver(ivec2 p)
        {
            float depth = LoadDepth(p);

            return depth > 0.0 && depth < 1.0;
        }

        vec3 LoadNormal(ivec2 p)
        {
            return normalize(texelFetch(normalTarget, p, 0).xyz);
        }

        // In normalised coordinates, and already pointing at the previous frame, which is
        // how the reprojection reads it.
        vec2 LoadVelocity(ivec2 p)
        {
            return texelFetch(motionTarget, p, 0).xy * denoise.inverseSize;
        }

        float LinearDepth(ivec2 pixel, float depth)
        {
            vec2 uv = (vec2(pixel) + 0.5) * InverseSize();
            vec4 projected =
                denoise.projectionInverse * vec4((uv * 2.0) - 1.0, depth, 1.0);

            return abs(projected.z / projected.w);
        }
        """;

    /// <summary>Reprojects the last frame onto this one, tile by tile.</summary>
    private const string Classify = """
        layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

        // The scale at which a disagreement between a pixel's history and its
        // neighbourhood counts as evidence that the history is stale, rather than as the
        // two having drawn different rays. Eight Bernoulli draws give a fraction whose
        // standard error is a fifth; this is set well above that, so that only a wholesale
        // change in what a pixel is looking at discards what it has learned.
        const float kSamplingError = 0.40;

        shared int gDissent;
        shared float gDepth[8][8];
        shared vec2 gVelocity[8][8];

        bool AllTrue(bool value)
        {
            barrier();

            if (gl_LocalInvocationIndex == 0u)
            {
                gDissent = 0;
            }

            barrier();

            if (!value)
            {
                gDissent = 1;
            }

            barrier();

            return gDissent == 0;
        }

        void SearchSpatialRegion(uvec2 group, out bool allLit, out bool allShadowed)
        {
            // The filter stages reach seven pixels around each block, and the masks are
            // eight by four, so the vertical span has to be the taller one.
            ivec2 base = ivec2(TileOf(group * 8u));
            uint combinedOr = 0u;
            uint combinedAnd = 0xFFFFFFFFu;

            ivec2 last = ivec2(
                RoundedDivide(uint(Size().x), TILE_WIDTH),
                RoundedDivide(uint(Size().y), TILE_HEIGHT)) - 1;

            for (int j = -2; j <= 3; j++)
            {
                for (int i = -1; i <= 1; i++)
                {
                    ivec2 tile = clamp(base + ivec2(i, j), ivec2(0), last);
                    uint value = mask.data[LinearTile(uvec2(tile), uint(Size().x))];

                    combinedOr |= value;
                    combinedAnd &= value;
                }
            }

            allLit = combinedAnd == 0xFFFFFFFFu;
            allShadowed = combinedOr == 0u;
        }

        bool IsDisoccluded(ivec2 pixel, float depth, vec2 velocity)
        {
            vec2 uv = (vec2(pixel) + 0.5) * InverseSize();
            vec2 ndc = (uv * 2.0) - 1.0;
            vec2 previousUv = uv + velocity;

            if (any(lessThanEqual(previousUv, vec2(0.0))) ||
                any(greaterThanEqual(previousUv, vec2(1.0))))
            {
                return true;
            }

            vec3 normal = LoadNormal(pixel);
            vec4 clip = denoise.reprojectionMatrix * vec4(ndc, depth, 1.0);

            clip.z /= clip.w;

            // How square-on this surface is to the eye. Depth is least reliable where the
            // surface runs away from the camera, so the tolerance opens up there.
            vec4 homogeneous = denoise.viewProjectionInverse * vec4(ndc, depth, 1.0);
            vec3 world = homogeneous.xyz / homogeneous.w;
            vec3 toEye = normalize(denoise.eyeAndFirst.xyz - world);
            float alignment = pow(1.0 - dot(toEye, normal), 8.0);

            float linear = LinearDepth(pixel, clip.z);
            ivec2 previousPixel = ivec2(previousUv * vec2(Size()));
            float before = LinearDepth(
                previousPixel, texelFetch(previousDepth, previousPixel, 0).x);

            float difference = abs(before - linear) / max(linear, 1e-6);

            return difference >= mix(1e-2, 1e-1, alignment);
        }

        // The nearest of a two-by-two block, so a pixel on a silhouette follows the
        // surface in front rather than the one behind it.
        vec2 ClosestVelocity(ivec2 pixel, uvec2 local, float depth)
        {
            gDepth[local.x][local.y] = depth;
            gVelocity[local.x][local.y] = LoadVelocity(pixel);

            barrier();

            uvec2 corner = local & ~1u;
            float best = 1.0e30;
            vec2 chosen = vec2(0.0);

            for (uint j = 0u; j < 2u; j++)
            {
                for (uint i = 0u; i < 2u; i++)
                {
                    float candidate = gDepth[corner.x + i][corner.y + j];

                    if (candidate < best)
                    {
                        best = candidate;
                        chosen = gVelocity[corner.x + i][corner.y + j];
                    }
                }
            }

            barrier();

            return chosen;
        }

        // The mean and spread of what the neighbours measured.
        //
        // AMD build this out of the bitmask, with a seventeen-by-seventeen Gaussian and a
        // pair of shared-memory passes, because a bitmask is all they have: their estimate
        // is one bit and a tile of them is one word. Ours is a fraction of eight rays, and
        // mixing the two representations was a real defect rather than an inefficiency —
        // the clamp below is what keeps a reprojected history honest, and clamping a
        // fraction against bounds derived from a majority vote pinned every uniform region
        // to nought or one. A wall at six-tenths lit read as fully lit once its history
        // settled and as six-tenths while the camera moved, so it changed brightness
        // whenever anything moved.
        //
        // Forty-nine fetches of the fraction, and a variance that is the real one.
        void Neighbourhood(ivec2 pixel, out float mean, out float spread)
        {
            float total = 0.0;
            float squares = 0.0;

            for (int y = -3; y <= 3; y++)
            {
                for (int x = -3; x <= 3; x++)
                {
                    ivec2 at = clamp(pixel + ivec2(x, y), ivec2(0), Size() - 1);
                    float value = texelFetch(fractionTarget, at, 0).x;

                    total += value;
                    squares += value * value;
                }
            }

            mean = total / 49.0;
            spread = max((squares / 49.0) - (mean * mean), 0.0);
        }

        void WriteMetadata(uvec2 group, uvec2 local, bool cleared, bool allLit)
        {
            if (local.x == 0u && local.y == 0u)
            {
                uint value = (allLit ? kTileLit : 0u) | (cleared ? kTileCleared : 0u);

                metadata.data[(group.y * RoundedDivide(uint(Size().x), TILE_WIDTH)) + group.x] =
                    value;
            }
        }

        // A tile the filter will skip still has to hold values its neighbours can read.
        void ClearTargets(ivec2 pixel, uvec2 local, uvec2 group, float value, bool receiver, bool allLit)
        {
            WriteMetadata(group, local, true, allLit);
            imageStore(reprojection, pixel, vec4(value, 0.0, 0.0, 0.0));
            imageStore(currentMoments, pixel, vec4(value, 0.0, receiver ? 1.0 : 0.0, 0.0));
        }

        void main()
        {
            uvec2 group = gl_WorkGroupID.xy;
            uvec2 local = uvec2(gl_LocalInvocationIndex % 8u, gl_LocalInvocationIndex / 8u);
            ivec2 pixel = ivec2((group * 8u) + local);

            bool receiver = IsShadowReceiver(pixel);

            if (AllTrue(!receiver))
            {
                ClearTargets(pixel, local, group, 0.0, receiver, false);
                return;
            }

            bool allLit = false;
            bool allShadowed = false;
            SearchSpatialRegion(group, allLit, allShadowed);

            // AMD short-circuit a tile whose every bit is set: it is fully lit, so write
            // one and skip the filtering. That shortcut cannot survive a fractional
            // estimate. Whether a tile qualifies is decided afresh from this frame's rays,
            // so a tile sitting at nineteen-twentieths lit qualifies on some frames and
            // not others, and on the frames it does it is written as fully lit with no
            // temporal blending at all — a whole eight-by-eight block stepping five per
            // cent brighter and back, every frame, which is what a hallway of them looks
            // like when the lights appear to fight.
            //
            // The tile metadata is still written, because the filtering stages read it,
            // and a tile that is genuinely uniform costs them almost nothing anyway.
            WriteMetadata(group, local, false, false);

            float depth = LoadDepth(pixel);
            vec2 velocity = ClosestVelocity(pixel, local, depth);
            float neighbourhood;
            float spatial;
            Neighbourhood(pixel, neighbourhood, spatial);

            vec2 uv = (vec2(pixel) + 0.5) * InverseSize();
            vec2 historyUv = uv + velocity;
            ivec2 historyPixel = ivec2(historyUv * vec2(Size()));

            vec3 moments = vec3(0.0);
            float variance = 0.0;
            float blended = 0.0;

            if (receiver)
            {
                // The fraction, not the bit. The bitmask is what the tile classification
                // and the neighbourhood are built on — both read a tile as one word —
                // but the estimate itself should use everything the rays found.
                float current = texelFetch(fractionTarget, pixel, 0).x;

                vec3 before = IsDisoccluded(pixel, depth, velocity)
                    ? vec3(0.0)
                    : texelFetch(previousMoments, historyPixel, 0).xyz;

                float oldMean = before.x;
                float oldSum = before.y;
                float samples = before.z + 1.0;
                float mean = oldMean + ((current - oldMean) / samples);
                float sum = oldSum + ((current - oldMean) * (current - mean));

                variance = samples > 1.0 ? sum / (samples - 1.0) : 1.0;
                moments = vec3(mean, sum, samples);

                float deviation = sqrt(spatial);

                float previous = current;

                if (denoise.eyeAndFirst.w < 0.5)
                {
                    previous = textureLod(
                        sampler2D(history, clampedSampler), historyUv, 0.0).x;
                }

                // The window a reprojected history is allowed to sit in. Widened by the
                // same sampling error the damper uses, and for the same reason: both ends
                // of it are computed from this frame's rays and so move by that much on
                // their own. Left at half the spatial deviation, a smooth wall gives a
                // window a couple of hundredths wide that jitters by a couple of
                // hundredths, and the history is dragged along by it rather than
                // converging — which is most of what was left of the noise on the lobby's
                // walls once the damper stopped firing.
                float window = max(0.5 * deviation, kSamplingError);

                blended = clamp(previous, neighbourhood - window, neighbourhood + window);

                // A history that disagrees with where it landed is worth less. Rather
                // than dropping it outright the sample count is damped, which lets the
                // next few frames rebuild it.
                //
                // The floor under the deviation is a thousand times AMD's. Theirs is
                // there to stop a divide by zero; at that size it does not stop the
                // divide by *nearly* zero, and a neighbourhood that happens to be
                // uniform — which most of a character is — sends the discontinuity into
                // the hundreds and the exponential straight to zero. The count then
                // resets every frame, the blend takes the current sample whole, and
                // anything that moves shows the single bit it drew rather than an
                // average of anything.
                // The floor under the deviation is the estimator's own standard error.
                // Eight Bernoulli draws give a fraction whose error is at most a fifth,
                // so a history that differs from the neighbourhood by less than that has
                // not disagreed about anything — it has drawn different rays. AMD's floor
                // is a thousandth, which is there to stop a divide by zero and which, on
                // a signal as smooth as eight rays make, is the number this divides by
                // nearly everywhere. Measured in HAL, it multiplied the sample count by
                // 0.62 every frame: the count settled at 1.6, the blend took almost the
                // whole fresh sample every frame, and the whole picture fizzed.
                float discontinuity =
                    (previous - neighbourhood) / max(0.5 * deviation, kSamplingError);

                // And never below one sample, because this pixel did take one.
                moments.z = max(moments.z * exp(-discontinuity * discontinuity / 20.0), 1.0);

                if (moments.z < 16.0)
                {
                    variance = max(variance, spatial) * max(16.0 - moments.z, 1.0);
                }

                // Capped, because the filter divides by its square root to decide how
                // much a neighbour of a different value is worth. Boosted sixteenfold on
                // a signal that only ever runs from zero to one, that division turns every
                // weight into one and the blur stops caring what it is blurring: a door
                // and the wall it sits in have the same normal and very nearly the same
                // depth, so nothing else in the filter tells them apart, and the bright
                // one bleeds into the other for as long as the camera keeps moving.
                variance = min(variance, 1.0);

                // A pixel with no history has only what its own rays found, and with
                // eight of them that is worth something. What it must *not* fall back on
                // is the seventeen-by-seventeen neighbourhood: that is a flat Gaussian
                // over the bitmask with no regard for depth or normal, so beside a window
                // it mixes wall with the fully-lit pixels an undrawn sky is written as,
                // and every camera movement painted a glow around the frame that faded
                // once the camera stopped. The three filtering stages that follow blur
                // this with edges in mind, which is the whole reason they exist.
                // How much of this frame's estimate to take. AMD hold this at a
                // twentieth once a pixel has eight frames behind it, and hold it there
                // for ever — so the picture never actually settles. It is an average that
                // keeps a twentieth of a fresh sample every frame, and with a seed that
                // moves each frame that is a fifth of a per cent of jitter that never goes
                // away. In a hallway with a bright fitting and dark panelling that reads
                // as the lights faintly fighting, and a moving camera, which resets these
                // counts, makes it far worse.
                //
                // One over the sample count is what a converging average actually uses:
                // the hundredth frame is worth a hundredth. The count is reset by
                // disocclusion and damped by disagreement, so this still follows a room
                // whose lighting changes — it simply stops fidgeting when nothing does.
                float steady = 1.0 / clamp(moments.z, 1.0, 400.0);
                float weight = sqrt(max(8.0 - moments.z, 0.0) / 8.0);

                blended = mix(blended, current, mix(steady, 1.0, weight));
            }

            imageStore(reprojection, pixel, vec4(blended, variance, 0.0, 0.0));
            imageStore(currentMoments, pixel, vec4(moments, 0.0));
        }
        """;

    /// <summary>One edge-aware blur, at whatever spacing the pass was given.</summary>
    private const string Filter = """
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

        shared vec2 gInput[16][16];
        shared float gDepth[16][16];
        shared vec3 gNormal[16][16];

        void Cache(ivec2 pixel, ivec2 local, ivec2 offset)
        {
            ivec2 p = clamp(pixel + offset, ivec2(0), Size() - 1);

            gInput[local.y + offset.y][local.x + offset.x] = texelFetch(filterInput, p, 0).xy;
            gDepth[local.y + offset.y][local.x + offset.x] = LoadDepth(p);
            gNormal[local.y + offset.y][local.x + offset.x] = LoadNormal(p);
        }

        // Sixteen by sixteen of shared memory for an eight by eight group: the widest pass
        // steps four pixels at a time in each direction from a centre that is itself
        // offset by four.
        void Precache(ivec2 pixel, ivec2 local)
        {
            pixel -= 4;

            Cache(pixel, local, ivec2(0, 0));
            Cache(pixel, local, ivec2(8, 0));
            Cache(pixel, local, ivec2(0, 8));
            Cache(pixel, local, ivec2(8, 8));
        }

        float FilteredVariance(ivec2 p)
        {
            const float kernel[2][2] = { { 0.25, 0.125 }, { 0.125, 0.0625 } };
            float variance = 0.0;

            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    variance += kernel[abs(x)][abs(y)] * gInput[p.y + y][p.x + x].y;
                }
            }

            return variance;
        }

        vec2 Blur(ivec2 pixel, ivec2 local, float depth, int step)
        {
            vec2 centre = gInput[local.y][local.x];
            vec3 normal = gNormal[local.y][local.x];

            float weightSum = 1.0;
            vec2 sum = centre;

            float deviation = sqrt(max(FilteredVariance(local) + 1e-9, 0.0));
            float depthCentre = LinearDepth(pixel, depth);

            const float kernel[3] = { 1.0, 2.0 / 3.0, 1.0 / 6.0 };

            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    ivec2 offset = ivec2(x, y) * step;
                    ivec2 near = local + offset;

                    float depthNear = gDepth[near.y][near.x];
                    vec3 normalNear = gNormal[near.y][near.x];
                    vec2 valueNear = gInput[near.y][near.x];

                    // The sky is not a surface and must not bleed into one.
                    float usable =
                        ((x == 0 && y == 0) || depthNear >= 1.0 || depthNear <= 0.0)
                            ? 0.0
                            : 1.0;

                    float linear = LinearDepth(pixel + offset, depthNear);

                    float w = kernel[abs(x)] * kernel[abs(y)];
                    w *= exp(-abs(centre.x - valueNear.x) / deviation);

                    // How far apart two depths may be before they stop being one surface,
                    // <b>as a fraction of how far away they are</b> rather than in scene
                    // units.
                    //
                    // AMD's tolerance is a hundredth and this divided a difference in
                    // *units* by it. GK3 measures in units of about two and a half
                    // centimetres, so a room is hundreds of them across and a wall a pixel
                    // further from the eye than its neighbour is a hundred tolerances away:
                    // the exponential returned nothing, the neighbour counted for nothing,
                    // and the blur only ever ran across surfaces standing square-on to the
                    // camera. Walls are square-on, which is why the room came out clean;
                    // a head is not square-on anywhere, so the eight rays a pixel spends
                    // stayed exactly as noisy as they arrived and read as a stipple over
                    // every face in the game.
                    //
                    // A fraction is the same thing the reprojection already asks — see
                    // IsDisoccluded, which divides by the depth before it compares.
                    w *= exp(-abs(depthCentre - linear) /
                             max(denoise.sigma.x * depthCentre, 1e-4));

                    w *= pow(clamp(dot(normal, normalNear), 0.0, 1.0), 32.0);
                    w *= usable;

                    sum += vec2(w, w * w) * valueNear;
                    weightSum += w;
                }
            }

            return vec2(sum.x / weightSum, sum.y / (weightSum * weightSum));
        }

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            ivec2 local = ivec2(gl_LocalInvocationID.xy);
            uvec2 group = gl_WorkGroupID.xy;

            uint meta =
                metadata.data[(group.y * RoundedDivide(uint(Size().x), TILE_WIDTH)) + group.x];

            bool cleared = (meta & kTileCleared) != 0u;
            bool allLit = (meta & kTileLit) != 0u;

            vec2 results = vec2(0.0);
            bool write = false;

            if (cleared)
            {
                // The middle pass leaves a skipped tile alone: its two neighbours have
                // already written the same constant into the buffer it reads from.
                if (stage.index != 1)
                {
                    results.x = allLit ? 1.0 : 0.0;
                    write = true;
                }
            }
            else
            {
                Precache(pixel, local);

                bool receiver = IsShadowReceiver(pixel);

                barrier();

                if (receiver)
                {
                    results = Blur(pixel, local + 4, LoadDepth(pixel), stage.stepSize);
                }

                write = true;
            }

            if (!write || pixel.x >= Size().x || pixel.y >= Size().y)
            {
                return;
            }

            if (stage.index == 2)
            {
                // AMD put back some of the contrast their blur takes out, by raising the
                // result to a power that depends on how confident the estimate is. That
                // makes a settled pixel darker than a moving one *by construction*: the
                // variance is boosted while a pixel has no history, so the exponent falls
                // to one exactly when the camera is moving and rises again when it stops.
                // Walking a hallway, the whole picture brightened and dimmed as the
                // camera went — which reads as the lights fighting each other.
                //
                // Their blur has more to undo than ours does. It is rescuing one bit a
                // pixel; this is filtering eight rays, and the contrast that recovers is
                // contrast that was never lost.
                imageStore(result, pixel, vec4(results.x, 0.0, 0.0, 0.0));
            }
            else
            {
                imageStore(historyOut, pixel, vec4(results, 0.0, 0.0));
            }
        }
        """;

    /// <summary>Builds the source for the stage that traces.</summary>
    /// <returns>Complete GLSL.</returns>
    public static string ComposeTrace() =>
        Header + "#extension GL_EXT_ray_query : require\n" + Common + "\n" + Trace;

    /// <summary>Builds the source for the stage that reprojects.</summary>
    /// <returns>Complete GLSL.</returns>
    public static string ComposeClassify() =>
        Header + Common + "\n" + Filtering + "\n" + Classify;

    /// <summary>Builds the source for the three blurring stages, which differ only in
    /// their push constants.</summary>
    /// <returns>Complete GLSL.</returns>
    public static string ComposeFilter() =>
        Header + Common + "\n" + Filtering + "\n" + Filter;

    /// <summary>
    /// What every stage begins with. The samplerless extension is what allows a
    /// <c>texelFetch</c> of a plain <c>texture2D</c>: these stages read exact pixels of
    /// the frame rather than filtering between them, so all but one of the images they
    /// read are bound without a sampler at all.
    /// </summary>
    private const string Header =
        "#version 460\n#extension GL_EXT_samplerless_texture_functions : require\n";
}
