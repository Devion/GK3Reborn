// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The fog pass, in GLSL.
/// </summary>
/// <remarks>
/// <para>
/// One triangle over the finished room, a ray marched through the layer for every pixel of
/// it, and what the room's own lamps put into each step gathered along the way. It is the
/// same rig the walls are lit by, read out of the same three buffers by the same grid
/// lookup, which is the whole reason a lamp standing in the mist has a halo round it that is
/// the colour the artists gave the lamp.
/// </para>
/// <para>
/// <b>The march is clipped to the layer rather than run over the ray.</b> A cellar's damp is
/// a metre deep in a room forty metres long, so a march spread evenly over the ray would put
/// one sample in the fog and thirty-one in clear air above it. Both ends of the interval are
/// found first — the near one where the ray drops below the height there is no longer enough
/// fog to matter at, the far one at whatever the room put in front of the pixel — and the
/// steps are spread over that. Thirty-two of them then resolve the layer rather than the
/// room.
/// </para>
/// <para>
/// <b>Nothing here varies with the frame.</b> The dither that hides the banding is a
/// function of the pixel and not of the clock, and the noise drifts on the same seconds the
/// flames flicker on — which a headless render leaves at nought. Two renders of one room are
/// the same picture, which is the basis on which everything in this project is compared.
/// </para>
/// </remarks>
public static class FogShaders
{
    /// <summary>The march, and what it gathers.</summary>
    public const string Fragment = """
        #version 460

        layout(location = 0) out vec4 outColor;

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

            // how far a flame swings, what it settles at, how fast, and its own spread
            vec4 flicker;
        };

        layout(std430, set = 0, binding = 0) readonly buffer Rig
        {
            // x is how many of the array are in use
            vec4 counts;
            Light lights[];
        } rig;

        layout(std430, set = 0, binding = 1) readonly buffer Cells
        {
            // Where each cell's list starts, with one more on the end for the last cell.
            int at[];
        } cells;

        layout(std430, set = 0, binding = 2) readonly buffer Reaching
        {
            int lights[];
        } reaching;

        // What the room drew, in the depth it drew it at. Fetched rather than sampled: this
        // is read once across its own extent at its own resolution, and a filtered depth
        // halfway between a near surface and a far one is a distance nothing is at.
        layout(set = 0, binding = 3) uniform sampler2D depthTarget;

        layout(push_constant) uniform Fog
        {
            mat4 inverseViewProjection;

            // xyz where the camera is, w the clock in seconds
            vec4 eyeAndTime;

            // xyz where the light grid starts, w how wide one of its cells is
            vec4 gridOrigin;

            // xyz how many cells along each axis
            vec4 gridCounts;

            // rgb what a scattering event returns, w the density per world unit
            vec4 tint;

            // x the top of the layer, y how fast it thins above that, z the phase's g,
            // w how much of the ambient floor the fog scatters
            vec4 layer;

            // x the noise's cell size, y how fast it drifts, z how far it takes the
            // density either side of its mean, w how many steps the march takes
            vec4 grain;

            // rgb the room's ambient floor
            vec4 ambient;

            // xy the viewport in pixels
            vec4 screen;
        } fog;

        // How far above the top of the layer there is no longer enough fog to march.
        //
        // Six falloffs, where a quarter of a percent of the density is left. Everything the
        // clip throws away is worth less than the error the dither already hides.
        const float kCeiling = 6.0;

        // Where a flame's light stands at this instant, as a multiplier on its intensity.
        //
        // The same four sines the room is lit by, and it has to be the same: a fire that
        // moves the wall behind it and not the mist in front of it is two fires. See
        // MeshShaders, which explains why the amplitudes total one and why the phases all
        // start at nought.
        float Flicker(Light light)
        {
            if (light.flicker.x <= 0.0)
            {
                return light.flicker.y;
            }

            float t = fog.eyeAndTime.w * light.flicker.z *
                      (0.75 + (0.5 * light.flicker.w)) * 6.2831853;

            float wave = (0.50 * sin(t)) +
                         (0.27 * sin(t * 2.37)) +
                         (0.15 * sin(t * 5.11)) +
                         (0.08 * sin(t * 9.73));

            return light.flicker.y + (light.flicker.x * wave);
        }

        // Which lights reach a point, as a range into the grid's index list. The mesh
        // shader's lookup, against the same grid, clamped at the edges for the same reason.
        void CellAt(vec3 position, out int first, out int last)
        {
            vec3 local = (position - fog.gridOrigin.xyz) / max(fog.gridOrigin.w, 1e-4);
            ivec3 counts = ivec3(fog.gridCounts.xyz);

            ivec3 at = clamp(ivec3(floor(local)), ivec3(0), max(counts - 1, ivec3(0)));
            int index = ((at.z * counts.y) + at.y) * counts.x + at.x;

            first = cells.at[index];
            last = cells.at[index + 1];
        }

        // The Henyey-Greenstein phase, normalised so that scattering in every direction
        // equally is one rather than a quarter of pi.
        //
        // That is deliberate and it is not physics. These lights are the artists' own,
        // authored in 1999 against a linear-decay renderer and tuned by what the walls
        // looked like; a phase carrying its own 1/4pi would put the fog two orders of
        // magnitude below the surfaces beside it and the density needed to see it at all
        // would be soup. One at g = 0 makes a lit step of fog comparable to a lit surface,
        // and the layer's own density then says how much of it there is.
        float Phase(float cosine, float g)
        {
            float gg = g * g;
            float d = 1.0 + gg - (2.0 * g * cosine);

            return (1.0 - gg) / max(pow(max(d, 1e-4), 1.5), 1e-4);
        }

        // One number in [0,1) per cell of the noise lattice.
        //
        // Integer rather than the sine-fract hash the coat uses, because this is asked eight
        // times a step and thirty-two steps a pixel: at 1080p that is half a billion of
        // them, and half a billion transcendentals is a pass nobody can afford. It is also
        // exactly reproducible, which the sine one is not — its accuracy is the driver's
        // business and two cards disagree about the last bits.
        float Hash(vec3 cell)
        {
            uvec3 c = uvec3(ivec3(cell)) *
                      uvec3(1597334673u, 3812015801u, 2798796415u);

            uint h = (c.x ^ c.y ^ c.z) * 1597334677u;

            return float(h) * (1.0 / 4294967296.0);
        }

        // Value noise: eight corners of a lattice cell, smoothed and mixed.
        float Noise(vec3 at)
        {
            vec3 cell = floor(at);
            vec3 f = at - cell;

            // Smoothstep on each axis, so the lattice's own edges do not show as creases.
            f = f * f * (3.0 - (2.0 * f));

            float n000 = Hash(cell + vec3(0.0, 0.0, 0.0));
            float n100 = Hash(cell + vec3(1.0, 0.0, 0.0));
            float n010 = Hash(cell + vec3(0.0, 1.0, 0.0));
            float n110 = Hash(cell + vec3(1.0, 1.0, 0.0));
            float n001 = Hash(cell + vec3(0.0, 0.0, 1.0));
            float n101 = Hash(cell + vec3(1.0, 0.0, 1.0));
            float n011 = Hash(cell + vec3(0.0, 1.0, 1.0));
            float n111 = Hash(cell + vec3(1.0, 1.0, 1.0));

            return mix(
                mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
                mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y),
                f.z);
        }

        // How much fog stands between two heights, as a length of it at full density.
        //
        // The height profile integrates in closed form, which is what makes the self-
        // shadowing below affordable: everything under the top of the layer counts for its
        // own length, and everything above it counts for one falloff's worth of what is
        // left. Two exponentials, no march.
        float Column(float lower, float upper)
        {
            float top = fog.layer.x;
            float falloff = fog.layer.y;

            float below = max(min(upper, top) - min(lower, top), 0.0);

            float from = max(lower, top);
            float to = max(upper, top);
            float above = falloff *
                (exp(-(from - top) / falloff) - exp(-(to - top) / falloff));

            return below + above;
        }

        // How much of a light survives the fog between it and a point in the layer.
        //
        // <b>This is what makes a deep layer dark at the bottom.</b> Without it every lamp
        // in the room reaches every sample unimpeded — and a distant key, which is exempt
        // from falloff by design, then lights the floor of a chasm exactly as brightly as
        // its lip. What that draws is not fog but a lit cloud: the temple's pit came out
        // white to the bottom, brighter than the hall around it.
        //
        // The path is approximated by the height it crosses. Density here is a function of
        // height alone, so the only thing a slanted ray changes is how much of each layer it
        // spends in — which is its length over its rise, and is exact for a ray through a
        // stratified medium. What it does not know about is the walls: a lantern on the far
        // side of a pier still reaches through it. That is a shadow ray's job and this pass
        // does not trace one; the layer's own extinction is by far the larger term wherever
        // there is enough fog for the difference to show.
        float Survives(vec3 at, vec3 light)
        {
            float rise = light.y - at.y;
            float span = distance(at, light);

            // Level with the lamp, or as near as makes no difference: the whole path is at
            // this sample's own density and there is no column to integrate.
            if (abs(rise) < 1e-3)
            {
                return exp(-fog.tint.w * exp(-max(at.y - fog.layer.x, 0.0) / fog.layer.y) * span);
            }

            float column = Column(min(at.y, light.y), max(at.y, light.y));

            return exp(-fog.tint.w * column * (span / abs(rise)));
        }

        // How much fog there is at a point, as a fraction of the layer's own density.
        float Density(vec3 at)
        {
            float above = max(at.y - fog.layer.x, 0.0);
            float thickness = exp(-above / fog.layer.y);

            if (fog.grain.x > 0.0 && fog.grain.z > 0.0)
            {
                // The field moves rather than the fog: a layer of mist does not travel
                // across a cellar, it churns where it lies. Sideways only, and slower along
                // one axis than the other, so it does not read as a texture sliding.
                float drift = fog.eyeAndTime.w * fog.grain.y;
                vec3 moved = at + vec3(drift, 0.0, drift * 0.6);

                float n = Noise(moved / fog.grain.x);
                thickness *= 1.0 + (fog.grain.z * ((n * 2.0) - 1.0));
            }

            return max(thickness, 0.0);
        }

        // What the room's lamps put into a step of fog, looking along `view`.
        //
        // The rig as the walls read it — the same cell lookup, the same linear range squared,
        // the same cone, the same flicker — with the surface's part of it replaced by the
        // phase. A step of fog has no normal to face a light with, so what decides how much
        // it returns is which way the light is going relative to the eye, which is the whole
        // of what a phase function says.
        vec3 InScatter(vec3 at, vec3 view)
        {
            // The ambient floor, less whatever the layer above this point takes out of it.
            // Ambient is light from everywhere, and in a room the everywhere it comes from
            // is mostly the open space overhead — so the fog above a sample stands in its
            // way exactly as it stands in a lamp's, and the bottom of a deep layer is dark
            // for the same reason it is dark under water.
            float overhead = exp(
                -fog.tint.w * Column(at.y, fog.layer.x + (kCeiling * fog.layer.y)));

            vec3 total = fog.ambient.rgb * fog.layer.w * overhead;

            int first = 0;
            int last = 0;
            CellAt(at, first, last);

            for (int slot = first; slot < last; slot++)
            {
                Light light = rig.lights[reaching.lights[slot]];

                vec3 toLight = light.positionAndStart.xyz - at;
                float distance = max(length(toLight), 0.0001);
                vec3 direction = toLight / distance;

                float start = light.positionAndStart.w;
                float end = light.directionAndEnd.w;
                float reach = clamp((end - distance) / max(end - start, 0.001), 0.0, 1.0);

                // Squared, and a distant key exempt from falloff entirely. Both are the
                // mesh shader's rules, and they have to be: a fog lit by a different set of
                // lights from the wall behind it never looks like it is in the room.
                float attenuation = reach * reach;

                if (light.cone.z >= 1.5)
                {
                    attenuation = 1.0;
                }

                if (attenuation <= 0.0)
                {
                    continue;
                }

                float cone = 1.0;
                if (mod(light.cone.z, 2.0) > 0.5)
                {
                    float aligned = dot(-direction, light.directionAndEnd.xyz);
                    cone = smoothstep(light.cone.y, light.cone.x, aligned);
                }

                // The light travels from the lamp to here and leaves toward the eye, so the
                // angle the phase wants is between those two — which comes out as the dot of
                // the view direction with the direction to the light. Forward scattering is
                // then a halo when the lamp is ahead and nothing when it is behind, which is
                // what mist round a lamp actually does.
                float phase = Phase(dot(view, direction), fog.layer.z);

                total += light.colorAndIntensity.rgb * light.colorAndIntensity.w *
                         attenuation * cone * phase * Flicker(light) *
                         Survives(at, light.positionAndStart.xyz);
            }

            return max(total, vec3(0.0));
        }

        // Where inside its step this pixel samples, from the pixel and nothing else.
        //
        // Interleaved gradient noise. Without it thirty-two steps across a deep layer band
        // the picture into thirty-two shells, which the eye finds instantly because they
        // move with the camera. With it the banding becomes a dither the resolve cannot tell
        // from grain — and because it is a function of the pixel, a still rendered twice is
        // still the same still.
        float Dither(vec2 pixel)
        {
            return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
        }

        void main()
        {
            ivec2 pixel = ivec2(gl_FragCoord.xy);
            float depth = texelFetch(depthTarget, pixel, 0).x;

            // Back to the world, from the same matrix the room was drawn with. A pixel the
            // room never covered reads the cleared depth and comes back on the far plane,
            // which is the right answer: there is nothing behind it to stop the fog.
            vec2 uv = gl_FragCoord.xy / fog.screen.xy;
            vec4 homogeneous =
                fog.inverseViewProjection * vec4((uv * 2.0) - 1.0, depth, 1.0);

            vec3 eye = fog.eyeAndTime.xyz;
            vec3 target = homogeneous.xyz / homogeneous.w;
            vec3 along = target - eye;

            float far = length(along);

            if (far <= 0.0 || any(isnan(along)))
            {
                outColor = vec4(0.0);
                return;
            }

            vec3 view = along / far;

            // The part of the ray that is in the fog at all. Above the ceiling there is
            // none, so the interval is where the ray is below it: a ray going up leaves at
            // the crossing, a ray going down arrives there, and a level ray is either
            // wholly in or wholly out.
            float ceiling = fog.layer.x + (kCeiling * fog.layer.y);
            float near = 0.0;

            if (abs(view.y) < 1e-5)
            {
                if (eye.y > ceiling)
                {
                    outColor = vec4(0.0);
                    return;
                }
            }
            else
            {
                float crossing = (ceiling - eye.y) / view.y;

                if (view.y < 0.0)
                {
                    near = max(near, crossing);
                }
                else
                {
                    far = min(far, crossing);
                }
            }

            if (far <= near)
            {
                outColor = vec4(0.0);
                return;
            }

            int steps = max(int(fog.grain.w), 1);
            float span = (far - near) / float(steps);
            float offset = Dither(gl_FragCoord.xy);

            vec3 scattered = vec3(0.0);
            float transmittance = 1.0;

            for (int i = 0; i < steps; i++)
            {
                vec3 at = eye + (view * (near + ((float(i) + offset) * span)));

                float sigma = fog.tint.w * Density(at);

                if (sigma <= 0.0)
                {
                    continue;
                }

                // The step integrated as a slab of its own rather than as a point sample.
                // What a slab returns is one minus what it lets through, which makes the
                // answer very nearly independent of how many steps there are — halve them
                // and the picture dims by a fraction of a percent instead of by half.
                float through = exp(-sigma * span);
                float gathered = 1.0 - through;

                scattered += transmittance * gathered * fog.tint.rgb * InScatter(at, view);
                transmittance *= through;

                // Nothing behind two hundred and fifty to one is going to show.
                if (transmittance < 0.004)
                {
                    break;
                }
            }

            // Premultiplied: the pass adds what the fog put in and takes away what it
            // stopped, which is one blend and no second target to read the picture from.
            outColor = vec4(scattered, 1.0 - transmittance);
        }
        """;
}
