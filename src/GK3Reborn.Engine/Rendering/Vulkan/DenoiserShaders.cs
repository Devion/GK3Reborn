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

        layout(set = 0, binding = 5) uniform Rig
        {
            vec4 counts;
            Light lights[64];
        } rig;

        layout(push_constant) uniform Trace
        {
            mat4 viewProjectionInverse;
            ivec2 size;
            float radius;
            float seed;
        } trace;

        const float kRayBias = 0.75;
        const float kNormalBias = 6.0;
        const float kShadowFloor = 0.004;

        shared uint gShadow;
        shared uint gOcclusion;

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

        // What this light gives this pixel before anything blocks it, as a single number.
        // The same falloff, cone and lambert term the raster pass uses, so the weights
        // this samples by are the weights the result is multiplied back into.
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

            if (attenuation <= 0.0)
            {
                return 0.0;
            }

            float cone = 1.0;

            if (light.cone.z > 0.5)
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
        bool ShadowRay(vec3 position, vec3 normal, vec2 pixel)
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

            float pick = Random(pixel, 5.0) * total;
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

                float angle = 6.2831853 * Random(pixel, 6.0);
                float offset = rig.lights[i].cone.w * sqrt(Random(pixel, 7.0));

                vec3 target = toLight +
                              (tangent * cos(angle) * offset) +
                              (bitangent * sin(angle) * offset);

                float reach = length(target);
                vec3 start = position + (normal * kNormalBias);

                return !Occluded(start, target / reach, reach);
            }

            return true;
        }

        bool OcclusionRay(vec3 position, vec3 normal, vec2 pixel)
        {
            vec3 tangent;
            vec3 bitangent;
            Basis(normal, tangent, bitangent);

            float u = Random(pixel, 8.0);
            float angle = 6.2831853 * Random(pixel, 9.0);
            float radial = sqrt(u);

            vec3 direction = normalize(
                (tangent * radial * cos(angle)) +
                (bitangent * radial * sin(angle)) +
                (normal * sqrt(max(0.0, 1.0 - u))));

            return !Occluded(position + (normal * kNormalBias), direction, trace.radius);
        }

        void main()
        {
            if (gl_LocalInvocationIndex == 0u)
            {
                gShadow = 0u;
                gOcclusion = 0u;
            }

            barrier();

            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            bool inside = pixel.x < trace.size.x && pixel.y < trace.size.y;
            float depth = inside ? texelFetch(depthTarget, pixel, 0).x : 1.0;

            // Sky, or nothing drawn. Lit and unoccluded, so an empty tile stays uniform.
            bool lit = true;
            bool open = true;

            if (inside && depth > 0.0 && depth < 1.0)
            {
                vec2 uv = (vec2(pixel) + 0.5) / vec2(trace.size);
                vec4 homogeneous =
                    trace.viewProjectionInverse * vec4((uv * 2.0) - 1.0, depth, 1.0);

                vec3 position = homogeneous.xyz / homogeneous.w;
                vec3 normal = normalize(texelFetch(normalTarget, pixel, 0).xyz);

                lit = ShadowRay(position, normal, vec2(pixel));
                open = OcclusionRay(position, normal, vec2(pixel));
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

            barrier();

            if (gl_LocalInvocationIndex == 0u)
            {
                uint tile = LinearTile(uvec2(gl_WorkGroupID.xy), uint(trace.size.x));

                shadowMask.data[tile] = gShadow;
                occlusionMask.data[tile] = gOcclusion;
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

        #define KERNEL_RADIUS 8

        shared int gDissent;
        shared float gNeighbourhood[8][24];
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

        float KernelWeight(int i)
        {
            float sum = exp(0.0);

            for (int c = 1; c <= KERNEL_RADIUS; c++)
            {
                sum += 2.0 * exp(-3.0 * float(c * c) /
                                 ((KERNEL_RADIUS + 1.0) * (KERNEL_RADIUS + 1.0)));
            }

            return exp(-3.0 * float(i * i) /
                       ((KERNEL_RADIUS + 1.0) * (KERNEL_RADIUS + 1.0))) / sum;
        }

        // The horizontal half of a seventeen by seventeen neighbourhood, read straight out
        // of the bitmask three tiles at a time.
        float HorizontalNeighbourhood(ivec2 pixel)
        {
            if (pixel.y < 0 || pixel.y >= Size().y)
            {
                return 0.0;
            }

            uvec2 tile = TileOf(uvec2(pixel));
            uint centreIndex = LinearTile(tile, uint(Size().x));
            uint lastInRow = RoundedDivide(uint(Size().x), TILE_WIDTH) - 1u;

            uint left = tile.x == 0u ? 0u : mask.data[centreIndex - 1u];
            uint centre = mask.data[centreIndex];
            uint right = tile.x == lastInRow ? 0u : mask.data[centreIndex + 1u];

            uint row = uint(pixel.y % TILE_HEIGHT) * TILE_WIDTH;
            uint neighbourhood = ((left >> row) & 0xFFu) |
                                 (((centre >> row) & 0xFFu) << 8) |
                                 (((right >> row) & 0xFFu) << 16);

            // Shifted so this pixel lands on bit eight, where the kernel peaks.
            neighbourhood >>= uint(pixel.x % TILE_WIDTH);

            float moment = 0.0;

            for (int i = 0; i < 8; i++)
            {
                moment += ((1u << uint(i)) & neighbourhood) != 0u ? KernelWeight(8 - i) : 0.0;
            }

            moment += ((1u << 8) & neighbourhood) != 0u ? KernelWeight(0) : 0.0;

            for (int i = 1; i <= 8; i++)
            {
                moment += ((1u << uint(8 + i)) & neighbourhood) != 0u ? KernelWeight(i) : 0.0;
            }

            return moment;
        }

        float LocalNeighbourhood(ivec2 pixel, ivec2 local)
        {
            float upper = HorizontalNeighbourhood(ivec2(pixel.x, pixel.y - 8));
            float centre = HorizontalNeighbourhood(pixel);
            float lower = HorizontalNeighbourhood(ivec2(pixel.x, pixel.y + 8));

            gNeighbourhood[local.x][local.y] = upper;
            gNeighbourhood[local.x][local.y + 8] = centre;
            gNeighbourhood[local.x][local.y + 16] = lower;

            barrier();

            float total = (centre * KernelWeight(0)) +
                          ((upper + lower) * KernelWeight(KERNEL_RADIUS));

            for (int i = 1; i < KERNEL_RADIUS; i++)
            {
                float weight = KernelWeight(i);

                total += gNeighbourhood[local.x][8 + local.y - i] * weight;
                total += gNeighbourhood[local.x][8 + local.y + i] * weight;
            }

            barrier();

            return total;
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

            if (AllTrue(allLit || allShadowed))
            {
                ClearTargets(pixel, local, group, allLit ? 1.0 : 0.0, receiver, allLit);
                return;
            }

            WriteMetadata(group, local, false, false);

            float depth = LoadDepth(pixel);
            vec2 velocity = ClosestVelocity(pixel, local, depth);
            float neighbourhood = LocalNeighbourhood(pixel, ivec2(local));

            vec2 uv = (vec2(pixel) + 0.5) * InverseSize();
            vec2 historyUv = uv + velocity;
            ivec2 historyPixel = ivec2(historyUv * vec2(Size()));

            uint tile = mask.data[LinearTile(TileOf(uvec2(pixel)), uint(Size().x))];

            vec3 moments = vec3(0.0);
            float variance = 0.0;
            float blended = 0.0;

            if (receiver)
            {
                float current = (tile & BitInTile(uvec2(pixel))) != 0u ? 1.0 : 0.0;

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

                float spatial = max(neighbourhood - (neighbourhood * neighbourhood), 0.0);
                float deviation = sqrt(spatial);

                float previous = current;

                if (denoise.eyeAndFirst.w < 0.5)
                {
                    previous = textureLod(
                        sampler2D(history, clampedSampler), historyUv, 0.0).x;
                }

                blended = clamp(
                    previous,
                    neighbourhood - (0.5 * deviation),
                    neighbourhood + (0.5 * deviation));

                // A history that disagrees with where it landed is worth less. Rather
                // than dropping it outright the sample count is damped, which lets the
                // next few frames rebuild it.
                float discontinuity =
                    (previous - neighbourhood) / max(0.5 * deviation, 0.001);

                moments.z *= exp(-discontinuity * discontinuity / 20.0);

                if (moments.z < 16.0)
                {
                    variance = max(variance, spatial) * max(16.0 - moments.z, 1.0);
                }

                float weight = sqrt(max(8.0 - moments.z, 0.0) / 8.0);
                blended = mix(blended, current, mix(0.05, 1.0, weight));
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
                    w *= exp(-abs(depthCentre - linear) / denoise.sigma.x);
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
                // Some of the contrast the blur took out, put back where the estimate is
                // confident enough to deserve it.
                float remap = max(1.2 - results.y, 1.0);

                imageStore(result, pixel, vec4(pow(abs(results.x), remap), 0.0, 0.0, 0.0));
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
