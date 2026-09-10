// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The sun's rays, in GLSL.
/// </summary>
/// <remarks>
/// <para>
/// Outdoors, a screen-space pass. Every pixel walks the line from itself toward the sun's
/// place on the screen and gathers how much of that line is sky — where the sun's light gets
/// in — less what the clouds hold back; a wall, a tree or a hillside on the line is where it
/// does not. Weighted toward the near end and toward the sun, that is the shafts between the
/// trees and the glow over a roofline. See <see cref="SunRays"/> for why it is done this way.
/// </para>
/// <para>
/// Indoors, a volume. Each shaft of daylight at a window is a box of lit air, and every
/// pixel's ray is cut against each box and marched through the part of it that lies in front
/// of whatever the room drew at that pixel — so a pillar standing in a shaft cuts it, a shaft
/// behind a wall is not seen, and the eye can stand inside one. See <see cref="LightShaft"/>.
/// </para>
/// </remarks>
public static class SunRayShaders
{
    /// <summary>
    /// The cloud field, exactly as the generated sky draws it. It has to be the same
    /// function: a ray that breaks through a gap the sky does not show is a ray from
    /// nowhere. <c>SunRayTests</c> holds the two together.
    /// </summary>
    public const string Clouds = """
        float cloudWaves(vec3 p)
        {
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
        """;

    /// <summary>The gather, and what it adds.</summary>
    public const string Fragment = """
        #version 460

        layout(location = 0) out vec4 outColor;

        // What the room drew, in the depth it drew it at. Fetched rather than sampled, for
        // the reason the fog gives: a filtered depth halfway between a near surface and the
        // sky is a distance nothing is at.
        layout(set = 0, binding = 0) uniform sampler2D depthTarget;

        struct Shaft
        {
            // xyz where the pane is, w half the shaft's width
            vec4 originAndHalfWidth;

            // xyz which way the light goes, w half the shaft's height
            vec4 directionAndHalfHeight;

            // rgb the light, w how far the shaft reaches
            vec4 colourAndLength;

            // x how bright it is drawn
            vec4 strength;
        };

        layout(std430, set = 0, binding = 1) readonly buffer Shafts
        {
            // x is how many of the array are in use
            ivec4 counts;
            Shaft shafts[];
        } daylight;

        layout(push_constant) uniform Rays
        {
            // clip space back to the world, from the jittered projection the room used
            mat4 inverseViewProjection;

            // xyz where the camera looks
            vec4 forward;

            // xyz its right; w the tangent of half the horizontal field of view
            vec4 right;

            // xyz its up; w the tangent of half the vertical field of view
            vec4 up;

            // xyz toward the sun, in the world; w how strongly the rays are drawn
            vec4 sun;

            // rgb what the sun's light is; w one when the sky is the generated one, whose
            // clouds hold the light back, and nought when it is a painting
            vec4 colour;

            // x cloud coverage, y cloud scale, zw the sky's own stable offset
            vec4 clouds;

            // xy the viewport in pixels
            vec4 screen;

            // x how many samples the walk takes, y how much each step is worth against the
            // one before, z how far toward the sun the walk goes as a fraction of the
            // distance, w how bright the whole is
            vec4 tuning;

            // x the clock in seconds, y how many shafts there are
            vec4 time;
        } rays;

        // How many samples a ray spends inside one shaft. A shaft is a smooth thing and
        // the dither below turns what banding is left into grain.
        const int ShaftSteps = 12;

        // Where the room ends and the sky begins on the depth buffer. The room is nearer
        // than this by a long way and the reconstructed horizon lives just under it; the sky
        // itself writes no depth and reads the cleared one.
        const float kSky = 0.9999995;

        """ + Clouds + """

        // How much of the sun gets through the cloud in a direction. The sky's own reading
        // of its own cloud, less the fine detail, which a walk across forty samples of it
        // would average away in any case.
        float Through(vec3 ray)
        {
            if (rays.colour.w < 0.5)
            {
                return 1.0;
            }

            vec3 cloudPoint = (ray * (4.2 * rays.clouds.y))
                            + vec3(rays.clouds.z, rays.clouds.w,
                                   (rays.clouds.z * 0.37) - (rays.clouds.w * 0.61));
            float broad = cloudWaves(cloudPoint * 0.78);

            float threshold = mix(0.72, 0.38, clamp(rays.clouds.x, 0.0, 1.0));
            float cloudBody = smoothstep(threshold - 0.09, threshold + 0.14, broad);
            float cloudVeil = smoothstep(threshold - 0.22, threshold + 0.07, broad) * 0.46;
            float cloudAlpha = max(cloudBody, cloudVeil) * smoothstep(0.018, 0.16, ray.y);

            // Thick cloud lets a little through and thin cloud most of it. Never nothing:
            // an overcast sky is still the brightest thing in the picture, and the rays
            // under it are the softer for it rather than absent.
            return mix(1.0, 0.10, clamp(cloudAlpha, 0.0, 1.0));
        }

        // The direction a pixel looks in, in the world.
        vec3 Looking(vec2 pixel)
        {
            vec2 ndc = ((pixel / rays.screen.xy) * 2.0) - 1.0;

            return normalize(rays.forward.xyz
                           + (rays.right.xyz * (ndc.x * rays.right.w))
                           - (rays.up.xyz * (ndc.y * rays.up.w)));
        }

        // Where inside its step this pixel starts, from the pixel and nothing else.
        // Interleaved gradient noise, the fog's dither, for the fog's reason: forty steps
        // along a line band into forty rings without it, and with it the rings become a
        // grain the eye reads as air.
        float Dither(vec2 pixel)
        {
            return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
        }

        // One number in [0,1) per point in space, with no transcendental in it: the
        // particle pass's hash, for the particle pass's reason.
        float Spark(vec3 at)
        {
            vec3 p = fract(at * vec3(0.1031, 0.1030, 0.0973));
            p += dot(p, p.yxz + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        // Value noise in three dimensions.
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

        // The point in the world a pixel's depth came from.
        vec3 Unproject(vec2 pixel, float depth)
        {
            vec2 uv = pixel / rays.screen.xy;
            vec4 homogeneous =
                rays.inverseViewProjection * vec4((uv * 2.0) - 1.0, depth, 1.0);

            return homogeneous.xyz / homogeneous.w;
        }

        // Cuts a ray against one slab of a box: between `low` and `high` along an axis the
        // ray's origin sits at `at` on and moves along at `rate`. False when it misses.
        bool Slab(float at, float rate, float low, float high, inout float near, inout float far)
        {
            if (abs(rate) < 1e-6)
            {
                return at >= low && at <= high;
            }

            float t0 = (low - at) / rate;
            float t1 = (high - at) / rate;

            near = max(near, min(t0, t1));
            far = min(far, max(t0, t1));

            return far > near;
        }

        // What one shaft adds along a ray from the eye to `stop` world units away.
        vec3 Shine(Shaft shaft, vec3 eye, vec3 ray, float stop, float offset)
        {
            vec3 pane = shaft.originAndHalfWidth.xyz;
            vec3 along = shaft.directionAndHalfHeight.xyz;
            float halfWidth = shaft.originAndHalfWidth.w;
            float halfHeight = shaft.directionAndHalfHeight.w;
            float span = shaft.colourAndLength.w;

            // The pane's frame across the light: level along the wall, and up it.
            vec3 across = cross(vec3(0.0, 1.0, 0.0), along);
            across = dot(across, across) > 1e-6 ? normalize(across) : vec3(1.0, 0.0, 0.0);
            vec3 upward = cross(along, across);

            // Where the ray is inside the box, if anywhere, and no further than the room.
            vec3 rel = eye - pane;
            float near = 0.0;
            float far = stop;

            if (!Slab(dot(rel, along), dot(ray, along), 0.0, span, near, far) ||
                !Slab(dot(rel, across), dot(ray, across), -halfWidth * 1.15, halfWidth * 1.15, near, far) ||
                !Slab(dot(rel, upward), dot(ray, upward), -halfHeight * 1.15, halfHeight * 1.15, near, far))
            {
                return vec3(0.0);
            }

            float step = (far - near) / float(ShaftSteps);
            float clock = rays.time.x;
            float gathered = 0.0;

            for (int i = 0; i < ShaftSteps; i++)
            {
                vec3 local = (eye + (ray * (near + ((float(i) + offset) * step)))) - pane;

                float t = dot(local, along);
                float x = dot(local, across) / max(halfWidth, 1e-3);
                float y = dot(local, upward) / max(halfHeight, 1e-3);

                // Soft at the edges of the pane, fading in just inside the glass and dying
                // away with distance from it: light through a window is brightest at the
                // sill and lost in the room's own light by the far wall.
                float edge = max(abs(x), abs(y));
                float body = 1.0 - smoothstep(0.80, 1.12, edge);
                float reach = exp(-t / max(span, 1.0) * 1.9) * smoothstep(-2.0, 12.0, t);

                // Dust hanging in it, which is what the eye reads a shaft by: two octaves
                // drifting slowly down and across the light.
                vec3 drifting = vec3(x * 2.4, (t * 0.05) + (clock * 0.11), y * 2.4)
                              + vec3(clock * 0.03, 0.0, clock * 0.02);
                float dust = 0.55 + (0.45 * Billow(drifting * 3.0))
                           + (0.25 * (Billow(drifting * 9.0) - 0.5));

                gathered += body * reach * dust * step;
            }

            // How much lit air the ray crossed, against the pane's own size, so a window
            // twice as wide is not a shaft twice as bright.
            float thickness = gathered / max(min(halfWidth, halfHeight) * 2.0, 1.0);
            float lit = 1.0 - exp(-thickness * 0.9);

            // Brighter looking into the light than across it: forward scattering, gently,
            // so a shaft is a shaft from anywhere and a glare from in front of its window.
            float toward = max(dot(ray, along), 0.0);
            float phase = 0.55 + (0.45 * toward * toward);

            return shaft.colourAndLength.rgb * shaft.strength.x * lit * phase * 0.36;
        }

        // Everything the room's windows throw along a pixel's ray.
        vec3 Windows(vec2 pixel)
        {
            int count = min(daylight.counts.x, int(rays.time.y));

            if (count <= 0)
            {
                return vec3(0.0);
            }

            float depth = texelFetch(depthTarget, ivec2(pixel), 0).x;
            vec3 eye = Unproject(pixel, 0.0);
            vec3 target = Unproject(pixel, depth);
            vec3 along = target - eye;
            float stop = length(along);

            if (stop <= 0.0 || any(isnan(along)))
            {
                return vec3(0.0);
            }

            vec3 ray = along / stop;
            float offset = Dither(pixel);
            vec3 total = vec3(0.0);

            for (int i = 0; i < count; i++)
            {
                total += Shine(daylight.shafts[i], eye, ray, stop, offset);
            }

            return total;
        }

        void main()
        {
            // The daylight at the windows first, which is there whichever way the camera
            // faces; then the sky's rays, which are only there looking toward the sun.
            vec3 indoors = Windows(gl_FragCoord.xy);

            // Where the sun is on the screen. Behind the camera it is nowhere, and the pass
            // fades out over the last of the way there rather than switching off. A room
            // whose sun is only at its windows has no sun to face at all.
            float ahead = dot(rays.sun.xyz, rays.forward.xyz);
            float facingSun = dot(rays.sun.xyz, rays.sun.xyz) > 0.5
                ? smoothstep(0.02, 0.30, ahead)
                : 0.0;

            if (facingSun <= 0.0)
            {
                outColor = vec4(indoors * rays.sun.w, 0.0);
                return;
            }

            vec2 sunNdc = vec2(
                dot(rays.sun.xyz, rays.right.xyz) / (ahead * rays.right.w),
                -dot(rays.sun.xyz, rays.up.xyz) / (ahead * rays.up.w));

            vec2 sunPixel = ((sunNdc * 0.5) + 0.5) * rays.screen.xy;

            // Toward the sun from here, over the part of the way the walk covers.
            vec2 pixel = gl_FragCoord.xy;
            vec2 toward = (sunPixel - pixel) * rays.tuning.z;

            int steps = max(int(rays.tuning.x), 4);
            vec2 stride = toward / float(steps);
            float offset = Dither(pixel);

            // How much of the sun this pixel's own direction faces. Rays are brightest
            // looking into the light and fall away from it — forward scattering, which is
            // the shape of the phase function and not a decoration.
            vec3 look = Looking(pixel);
            float cosine = clamp(dot(look, rays.sun.xyz), -1.0, 1.0);
            const float g = 0.62;
            float phase = pow((1.0 - g) / sqrt(1.0 + (g * g) - (2.0 * g * cosine)), 3.0);

            float gathered = 0.0;
            float weight = 1.0;
            float total = 0.0;
            ivec2 size = ivec2(rays.screen.xy);

            for (int i = 0; i < steps; i++)
            {
                vec2 at = pixel + (stride * (float(i) + offset));
                ivec2 texel = ivec2(at);

                // Off the picture there is no depth to read. Counted as nothing rather than
                // as sky, so a sun just off the edge draws rays from the sky that is on the
                // screen and not from a guess about the sky that is not.
                if (texel.x < 0 || texel.y < 0 || texel.x >= size.x || texel.y >= size.y)
                {
                    total += weight;
                    weight *= rays.tuning.y;
                    continue;
                }

                float depth = texelFetch(depthTarget, texel, 0).x;

                if (depth >= kSky)
                {
                    gathered += weight * Through(Looking(at));
                }

                total += weight;
                weight *= rays.tuning.y;
            }

            float lit = gathered / max(total, 1e-4);

            vec3 added = ((rays.colour.rgb * lit * phase * facingSun * rays.tuning.w) + indoors)
                       * rays.sun.w;

            if (max(max(added.r, added.g), added.b) <= 0.0005)
            {
                outColor = vec4(0.0);
                return;
            }

            // Light added to the picture and nothing taken away: the air between the eye
            // and the room is brighter for the sun in it, and no less transparent.
            outColor = vec4(added, 0.0);
        }
        """;
}
