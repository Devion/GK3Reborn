// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Shaders;

/// <summary>The compute stages that reflect the frame in its own smooth surfaces.</summary>
/// <remarks>
/// <para>
/// The marching is AMD's, from FidelityFX SSSR in the SDK's 1.1.4 release, which is MIT
/// licensed: the hierarchical walk over a min-depth pyramid, the plane intersections that
/// advance it, the visible-normal sampling that gives a rough surface a wider cone than a
/// polished one, and the checks that decide a hit is real rather than the back of
/// something or the edge of the screen.
/// </para>
/// <para>
/// What is not ported is the scaffolding around it: their tile classification, their
/// indirect dispatch, their blue-noise sampler, and their own reflection denoiser. This
/// dispatches over the whole frame and leaves early where a surface is too rough to be
/// worth a ray, takes its randomness from a hash rather than from sampler tables, and
/// accumulates over time with the motion vectors already in the frame. Their scaffolding
/// buys throughput on scenes far heavier than a 1999 adventure game's rooms; leaving it
/// out costs image quality nothing.
/// </para>
/// <para>
/// Reflections read the previous frame's finished picture. Reading this one is not
/// possible — it is what the reflection is being added to — and the alternative, a
/// second lighting pass at every hit, would need material data at a point the
/// acceleration structure does not carry. A frame of lag in a reflection is not
/// something anybody has ever seen.
/// </para>
/// </remarks>
public static class ReflectionShaders
{
    /// <summary>What every stage shares.</summary>
    private const string Common = """
        layout(set = 0, binding = 0) uniform texture2D depthTarget;
        layout(set = 0, binding = 1) uniform texture2D normalTarget;
        layout(set = 0, binding = 2) uniform texture2D motionTarget;
        layout(set = 0, binding = 3) uniform texture2D litTarget;
        layout(set = 0, binding = 4) uniform texture2D pyramid;
        layout(set = 0, binding = 5) uniform texture2D historyTarget;
        layout(set = 0, binding = 6) uniform sampler clampedSampler;

        layout(set = 0, binding = 7, rgba16f) uniform image2D reflection;
        layout(set = 0, binding = 8, r32f) writeonly uniform image2D pyramidOut;

        layout(set = 0, binding = 9) uniform Reflect
        {
            mat4 projection;
            mat4 invProjection;
            mat4 view;
            mat4 invViewProjection;
            vec4 eyeAndSeed;
            ivec2 size;
            vec2 inverseSize;

            // thickness, roughest surface worth a ray, mip count, unused
            vec4 tuning;
        } settings;
        """;

    /// <summary>One level of the min-depth pyramid the march walks.</summary>
    /// <remarks>
    /// AMD build theirs in a single pass with atomics over the whole chain. This does one
    /// dispatch a level, which is a handful of dispatches over a picture this size and
    /// needs no atomics to be correct.
    /// </remarks>
    private const string Downsample = """
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

        layout(push_constant) uniform Level
        {
            ivec2 size;
            int level;
        } level;

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);

            if (pixel.x >= level.size.x || pixel.y >= level.size.y)
            {
                return;
            }

            float nearest;

            if (level.level == 0)
            {
                nearest = texelFetch(depthTarget, pixel, 0).x;
            }
            else
            {
                // The nearest of the four below, because a ray that clears the nearest
                // thing in a tile clears the whole tile.
                ivec2 from = pixel * 2;
                ivec2 last = (level.size * 2) - 1;

                nearest = min(
                    min(texelFetch(pyramid, min(from, last), level.level - 1).x,
                        texelFetch(pyramid, min(from + ivec2(1, 0), last), level.level - 1).x),
                    min(texelFetch(pyramid, min(from + ivec2(0, 1), last), level.level - 1).x,
                        texelFetch(pyramid, min(from + ivec2(1, 1), last), level.level - 1).x));
            }

            imageStore(pyramidOut, pixel, vec4(nearest, 0.0, 0.0, 0.0));
        }
        """;

    /// <summary>One reflection ray a pixel, marched against the pyramid.</summary>
    private const string March = """
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

        const float kFloatMax = 3.402823466e+38;

        // How much a surface reflects when looked at square on. Rough stone is four per
        // cent and would be invisible here; what these rooms are actually made of is
        // polished tile, marble and varnished wood, and they are being added to a picture
        // whose lighting is otherwise a 1999 bake. This is generous for a dielectric and
        // reads as polish rather than as a mirror.
        const float kBaseReflectance = 0.16;

        float Random(vec2 pixel, float salt)
        {
            vec3 seed = vec3(pixel, salt + settings.eyeAndSeed.w);

            return fract(sin(dot(seed, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
        }

        vec3 Unproject(vec3 screen, mat4 inverse)
        {
            vec4 projected = inverse * vec4((screen.xy * 2.0) - 1.0, screen.z, 1.0);

            return projected.xyz / projected.w;
        }

        vec3 ViewSpace(vec3 screen)
        {
            return Unproject(screen, settings.invProjection);
        }

        float LoadPyramid(ivec2 pixel, int mip)
        {
            return texelFetch(pyramid, pixel, mip).x;
        }

        // Heitz, "Sampling the GGX Distribution of Visible Normals", JCGT 7(4).
        vec3 SampleVisibleNormal(vec3 view, float alpha, float u1, float u2)
        {
            vec3 stretched = normalize(vec3(alpha * view.x, alpha * view.y, view.z));

            float lengthSquared = (stretched.x * stretched.x) + (stretched.y * stretched.y);
            vec3 t1 = lengthSquared > 0.0
                ? vec3(-stretched.y, stretched.x, 0.0) * inversesqrt(lengthSquared)
                : vec3(1.0, 0.0, 0.0);
            vec3 t2 = cross(stretched, t1);

            float r = sqrt(u1);
            float phi = 6.2831853 * u2;
            float p1 = r * cos(phi);
            float p2 = r * sin(phi);
            float s = 0.5 * (1.0 + stretched.z);

            p2 = ((1.0 - s) * sqrt(1.0 - (p1 * p1))) + (s * p2);

            vec3 hemisphere = (p1 * t1) + (p2 * t2) +
                (sqrt(max(0.0, 1.0 - (p1 * p1) - (p2 * p2))) * stretched);

            return normalize(vec3(alpha * hemisphere.x, alpha * hemisphere.y,
                max(0.0, hemisphere.z)));
        }

        vec3 ReflectionDirection(vec3 view, vec3 normal, float roughness, vec2 pixel)
        {
            vec3 u;

            if (abs(normal.z) > 0.0)
            {
                float k = sqrt((normal.y * normal.y) + (normal.z * normal.z));
                u = vec3(0.0, -normal.z / k, normal.y / k);
            }
            else
            {
                float k = sqrt((normal.x * normal.x) + (normal.y * normal.y));
                u = vec3(normal.y / k, -normal.x / k, 0.0);
            }

            vec3 row0 = u;
            vec3 row1 = cross(normal, u);
            vec3 row2 = normal;

            vec3 local = vec3(dot(row0, -view), dot(row1, -view), dot(row2, -view));
            vec3 sampled = SampleVisibleNormal(
                local, roughness, Random(pixel, 11.0), Random(pixel, 12.0));

            vec3 bounced = reflect(-local, sampled);

            return vec3(
                dot(vec3(row0.x, row1.x, row2.x), bounced),
                dot(vec3(row0.y, row1.y, row2.y), bounced),
                dot(vec3(row0.z, row1.z, row2.z), bounced));
        }

        vec2 MipSize(int mip)
        {
            return vec2(settings.size) * pow(0.5, float(mip));
        }

        void InitialAdvance(
            vec3 origin, vec3 direction, vec3 inverseDirection, vec2 mipSize,
            vec2 inverseMipSize, vec2 floorOffset, vec2 uvOffset,
            out vec3 position, out float travelled)
        {
            vec2 here = mipSize * origin.xy;
            vec2 plane = ((floor(here) + floorOffset) * inverseMipSize) + uvOffset;
            vec2 t = (plane * inverseDirection.xy) - (origin.xy * inverseDirection.xy);

            travelled = min(t.x, t.y);
            position = origin + (travelled * direction);
        }

        bool Advance(
            vec3 origin, vec3 direction, vec3 inverseDirection, vec2 here,
            vec2 inverseMipSize, vec2 floorOffset, vec2 uvOffset, float surface,
            inout vec3 position, inout float travelled)
        {
            vec2 plane = ((floor(here) + floorOffset) * inverseMipSize) + uvOffset;
            vec3 boundaries = vec3(plane, surface);
            vec3 t = (boundaries * inverseDirection) - (origin * inverseDirection);

            // Never use the depth plane when the ray is heading away from the screen.
            t.z = direction.z > 0.0 ? t.z : kFloatMax;

            float nearest = min(min(t.x, t.y), t.z);
            bool above = surface > position.z;

            // Whether the ray cleared the whole tile rather than being stopped by its
            // surface. Compared as bits, because the question is whether the minimum
            // *was* t.z, not whether it is nearly equal to it.
            bool skipped = floatBitsToUint(nearest) != floatBitsToUint(t.z) && above;

            travelled = above ? nearest : travelled;
            position = origin + (travelled * direction);

            return skipped;
        }

        vec3 March(vec3 origin, vec3 direction, int mostDetailed, int mips, out bool hit)
        {
            vec3 inverseDirection = vec3(
                direction.x != 0.0 ? 1.0 / direction.x : kFloatMax,
                direction.y != 0.0 ? 1.0 / direction.y : kFloatMax,
                direction.z != 0.0 ? 1.0 / direction.z : kFloatMax);

            int mip = mostDetailed;
            vec2 mipSize = MipSize(mip);
            vec2 inverseMipSize = 1.0 / mipSize;

            // Nudged into the next cell, so the ray never stalls on the boundary it is
            // standing on.
            vec2 uvOffset = 0.005 * exp2(float(mostDetailed)) / vec2(settings.size);
            uvOffset.x = direction.x < 0.0 ? -uvOffset.x : uvOffset.x;
            uvOffset.y = direction.y < 0.0 ? -uvOffset.y : uvOffset.y;

            vec2 floorOffset = vec2(
                direction.x < 0.0 ? 0.0 : 1.0,
                direction.y < 0.0 ? 0.0 : 1.0);

            float travelled;
            vec3 position;

            InitialAdvance(
                origin, direction, inverseDirection, mipSize, inverseMipSize,
                floorOffset, uvOffset, position, travelled);

            int steps = 0;

            while (steps < 64 && mip >= mostDetailed)
            {
                vec2 here = mipSize * position.xy;
                float surface = LoadPyramid(ivec2(here), mip);

                bool skipped = Advance(
                    origin, direction, inverseDirection, here, inverseMipSize,
                    floorOffset, uvOffset, surface, position, travelled);

                if (!skipped || mip < mips - 1)
                {
                    mip += skipped ? 1 : -1;
                    mipSize *= skipped ? 0.5 : 2.0;
                    inverseMipSize *= skipped ? 2.0 : 0.5;
                }

                steps++;
            }

            hit = steps < 64;

            return position;
        }

        // How much of the hit to believe: nothing off the screen, nothing that barely
        // moved, nothing behind the surface it hit, and less near the frame's edges where
        // what a reflection would need is not on screen to be found.
        float Confidence(vec3 hit, vec2 uv, vec3 direction)
        {
            if (any(lessThan(hit.xy, vec2(0.0))) || any(greaterThan(hit.xy, vec2(1.0))))
            {
                return 0.0;
            }

            vec2 moved = abs(hit.xy - uv);

            if (moved.x < (2.0 / settings.size.x) && moved.y < (2.0 / settings.size.y))
            {
                return 0.0;
            }

            ivec2 pixel = ivec2(vec2(settings.size) * hit.xy);
            float surface = LoadPyramid(pixel, 0);

            // The sky reflects nothing here: it is drawn after this and has no depth.
            if (surface >= 1.0)
            {
                return 0.0;
            }

            vec3 hitNormal = texelFetch(normalTarget, pixel, 0).xyz;

            if (dot(hitNormal, direction) > 0.0)
            {
                return 0.0;
            }

            vec3 surfaceView = ViewSpace(vec3(hit.xy, surface));
            vec3 hitView = ViewSpace(hit);
            float behind = length(surfaceView - hitView);

            vec2 edge = 0.05 * vec2(float(settings.size.y) / float(settings.size.x), 1.0);
            vec2 border = smoothstep(vec2(0.0), edge, hit.xy) *
                          (1.0 - smoothstep(vec2(1.0) - edge, vec2(1.0), hit.xy));

            float depthFit = 1.0 - smoothstep(0.0, settings.tuning.x, behind);

            return border.x * border.y * depthFit * depthFit;
        }

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);

            if (pixel.x >= settings.size.x || pixel.y >= settings.size.y)
            {
                return;
            }

            vec4 surface = texelFetch(normalTarget, pixel, 0);
            float depth = texelFetch(depthTarget, pixel, 0).x;

            // Absolute, because the sign of this channel is not part of the roughness: the
            // mesh pass negates it to mark a model standing in the room, which is what lets
            // a shadow ray leaving a character skip characters. See
            // RayTracingScene.MaskFor.
            float roughness = abs(surface.w);

            // Too rough to be worth a ray, or nothing drawn here at all. What a wide cone
            // gathers is the ambient term the surface already has.
            if (roughness > settings.tuning.y || depth <= 0.0 || depth >= 1.0)
            {
                imageStore(reflection, pixel, vec4(0.0));
                return;
            }

            vec2 uv = (vec2(pixel) + 0.5) * settings.inverseSize;
            vec3 world = Unproject(vec3(uv, depth), settings.invViewProjection);
            vec3 view = normalize(world - settings.eyeAndSeed.xyz);
            vec3 normal = normalize(surface.xyz);

            // Roughness squared, because roughness is the perceptual number an artist
            // or an inference pass writes down and the distribution wants its square.
            vec3 direction = ReflectionDirection(
                view, normal, roughness * roughness, vec2(pixel));

            // Into screen space, where the march works. A world-space line stays a line
            // under projection — normalised depth varies linearly along it as well — so
            // the ray can be given by any two points on it, and which two matters a great
            // deal. AMD project a point one unit along, which in a game measured in
            // metres is a good part of a room; GK3 measures a hotel room at about a
            // thousand, so one unit projects to a few millionths of the screen and the
            // subtractions that follow are all rounding error. This goes as far along the
            // ray as the camera is away, stopping short of the near plane, which puts the
            // two points most of a screen apart and the arithmetic back in range.
            // The ray starts on the level it will march, at that level's depth rather
            // than at this pixel's. A level of the pyramid holds the nearest of the
            // pixels under it, so starting there puts the ray just in front of its own
            // surface; starting at the pixel's own depth puts it exactly on it, the very
            // first test says it is not above anything, and the march ends where it
            // began.
            int mostDetailed = roughness < 0.05 ? 0 : 1;
            float startZ = LoadPyramid(ivec2(uv * MipSize(mostDetailed)), mostDetailed);

            vec3 originView = ViewSpace(vec3(uv, startZ));
            vec3 directionView = (settings.view * vec4(direction, 0.0)).xyz;

            float reach = length(originView);
            float ahead = max(originView.z * 0.05, 1.0);

            if (directionView.z < 0.0)
            {
                reach = min(reach, (originView.z - ahead) / -directionView.z);
            }

            vec3 origin = vec3(uv, startZ);
            vec4 endClip = settings.projection *
                vec4(originView + (directionView * max(reach, 0.001)), 1.0);

            vec3 end = endClip.xyz / endClip.w;
            end.xy = (end.xy * 0.5) + 0.5;

            bool hit;
            vec3 landed = March(
                origin, end - origin, mostDetailed, int(settings.tuning.z), hit);

            float confidence = hit ? Confidence(landed, uv, direction) : 0.0;

            // Schlick, with the reflectance of an ordinary dielectric. Almost nothing
            // reflects much when looked at square on — four per cent for stone or tile —
            // and almost everything reflects at a glancing angle, which is why a polished
            // floor shows the room ahead of you and not the one under your feet. Without
            // this a reflection is a haze over the whole picture.
            float grazing = pow(1.0 - clamp(dot(normal, -view), 0.0, 1.0), 5.0);
            float fresnel = kBaseReflectance + ((1.0 - kBaseReflectance) * grazing);

            // And fading out as the surface roughens, rather than stopping dead at the
            // threshold, so a floor and the wall beside it do not differ by a hard line.
            // The root rather than the ratio: a mildly glossy surface should keep most of
            // its reflection, not most of it taken away.
            confidence *= fresnel *
                sqrt(max(1.0 - (roughness / max(settings.tuning.y, 0.001)), 0.0));

            vec3 colour = vec3(0.0);

            if (confidence > 0.0)
            {
                // Where that surface was on the frame this picture is from, because the
                // picture being sampled is the previous one.
                ivec2 at = ivec2(vec2(settings.size) * landed.xy);
                vec2 back = texelFetch(motionTarget, at, 0).xy * settings.inverseSize;

                colour = textureLod(
                    sampler2D(litTarget, clampedSampler), landed.xy + back, 0.0).rgb;
            }

            // Where this pixel was, so a reflection can be averaged over frames rather
            // than flickering with whichever direction the sample happened to take.
            vec2 was = uv + (texelFetch(motionTarget, pixel, 0).xy * settings.inverseSize);
            vec4 history = all(greaterThan(was, vec2(0.0))) && all(lessThan(was, vec2(1.0)))
                ? textureLod(sampler2D(historyTarget, clampedSampler), was, 0.0)
                : vec4(0.0);

            // A rough surface gathers over a wide cone and needs many frames to fill it;
            // a polished one is nearly the same ray every frame and can follow the
            // picture more closely.
            float keep = mix(0.75, 0.94, roughness / max(settings.tuning.y, 0.001));

            imageStore(reflection, pixel, mix(vec4(colour, confidence), history, keep));
        }
        """;

    /// <summary>Builds the source for the stage that reduces depth.</summary>
    /// <returns>Complete GLSL.</returns>
    public static string ComposeDownsample() => Header + Common + "\n" + Downsample;

    /// <summary>Builds the source for the stage that marches.</summary>
    /// <returns>Complete GLSL.</returns>
    public static string ComposeMarch() => Header + Common + "\n" + March;

    private const string Header =
        "#version 460\n#extension GL_EXT_samplerless_texture_functions : require\n";
}
