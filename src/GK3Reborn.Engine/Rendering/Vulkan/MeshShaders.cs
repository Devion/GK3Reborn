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

            // xyz where the light grid starts, w how wide one of its cells is
            vec4 gridOrigin;

            // xyz how many cells along each axis, w how many lights the rig holds
            vec4 gridCounts;
        } frame;

        layout(push_constant) uniform Draw
        {
            mat4 model;
            mat4 previousModel;

            // x selects the lightmap over the rig, y scales the lightmap,
            // z is two flags added together — 1 for a surface that carries its own
            // brightness, 2 for a model standing in the room rather than the room itself —
            // and w is how deep its height map goes in *world* units, zero where it has
            // none. World rather than texture coordinates because the same texture is
            // tiled at wildly different densities across the game — one road texture
            // covers 232 units of street and one lobby floor 32 — and a depth in texture
            // coordinates is seven times deeper on the second for no reason anybody chose.
            vec4 shading;

            // The surface's finish where no map says otherwise: x roughness, y metalness,
            // z specular reflectance at normal incidence, w how much of the normal map to
            // believe. A map multiplies the first two rather than replacing them.
            vec4 material;
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

        #ifdef RAY_TRACING
        // The rig's light with nothing in the way. Kept apart from the rest so the
        // denoised shadow can multiply it after the geometry is gone; a fragment shader
        // cannot wait for a filter that has not run yet.
        layout(location = 3) out vec4 outDirect;
        #endif

        // The rig, and which of it reaches where.
        //
        // Storage buffers rather than uniform blocks, and that is the whole of why there is
        // no longer a limit of sixty-four lights on a scene: a uniform block has to be
        // sized at compile time and the standard guarantees only 16 KB of one. A storage
        // buffer is unsized on both sides.
        //
        // What made the limit bearable before was truncating the rig to its brightest few,
        // which is the wrong failure — it drops the lamp beside the player because a
        // streetlight three rooms away is brighter. Now nothing iterates the whole rig: a
        // fragment reads the cell it stands in and loops the handful of lights that
        // actually reach it. See SceneLightGrid.
        layout(std430, set = 0, binding = 1) readonly buffer Rig
        {
            // x is how many of the array are in use
            vec4 counts;
            Light lights[];
        } rig;

        layout(std430, set = 0, binding = 2) readonly buffer Cells
        {
            // Where each cell's list starts, with one more on the end for the last cell.
            int at[];
        } cells;

        layout(std430, set = 0, binding = 3) readonly buffer Reaching
        {
            int lights[];
        } reaching;

        #ifdef RAY_TRACING
        layout(set = 0, binding = 4) uniform accelerationStructureEXT scene;
        #endif

        layout(set = 1, binding = 0) uniform sampler2D baseColor;
        layout(set = 1, binding = 1) uniform sampler2D lightmapTexture;
        layout(set = 1, binding = 2) uniform sampler2D normalTexture;

        // Occlusion in red, roughness in green, metalness in blue: the glTF packing. A
        // surface with no map binds a neutral one — unoccluded, fully rough, not a metal —
        // which multiplies out to the surface the renderer drew before any of this existed.
        layout(set = 1, binding = 3) uniform sampler2D ormTexture;

        // Mid grey is the modelled surface. Only read where the height scale is non-zero,
        // which it is only for a surface that actually has a map.
        layout(set = 1, binding = 4) uniform sampler2D heightTexture;

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
        float Unblocked(
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

        // What this surface is made of, at this pixel. Filled once per fragment and passed
        // around rather than re-sampled, because the ORM texture is read in three places.
        struct Surface
        {
            vec3 albedo;
            vec3 f0;
            float roughness;
            float metalness;
            float occlusion;

            // Whether this surface has a measured finish to shade with: one where a
            // generated ORM map is bound, zero everywhere else. It scales the whole
            // specular term and the energy the diffuse gives up to it, so zero is exactly
            // Lambert rather than nearly Lambert. See Shade.
            float sheen;
        };

        // GGX, the microfacet distribution everything modern uses. The specular highlight's
        // shape: narrow and bright on something smooth, broad and dim on something rough.
        float Distribution(float nDotH, float roughness)
        {
            // Squared once for perceptual roughness, which is what an artist and a texture
            // both mean by the word, and again for the distribution itself.
            float a = roughness * roughness;
            float aa = a * a;
            float d = (nDotH * nDotH * (aa - 1.0)) + 1.0;

            return aa / max(3.14159265 * d * d, 1e-7);
        }

        // Smith's height-correlated visibility term, which is the geometric shadowing and
        // the BRDF's denominator folded together. Written this way because the two cancel
        // partially, and multiplying them out separately loses precision where it is least
        // affordable: at grazing angles, which is where a specular term is most visible.
        float Visibility(float nDotV, float nDotL, float roughness)
        {
            float a = roughness * roughness;
            float aa = a * a;

            float v = nDotL * sqrt((nDotV * nDotV * (1.0 - aa)) + aa);
            float l = nDotV * sqrt((nDotL * nDotL * (1.0 - aa)) + aa);

            return 0.5 / max(v + l, 1e-7);
        }

        // Schlick's approximation to Fresnel: how much more a surface reflects the further
        // from head-on it is looked at. It is what makes a matte floor go bright at the far
        // end of a corridor, and it applies to metals and dielectrics alike.
        vec3 Fresnel(float vDotH, vec3 f0)
        {
            float f = pow(clamp(1.0 - vDotH, 0.0, 1.0), 5.0);

            return f0 + ((vec3(1.0) - f0) * f);
        }

        // One light's contribution: Lambert diffuse plus a Cook-Torrance specular lobe.
        //
        // A metal has no diffuse term at all — it has no subsurface for light to scatter
        // out of — and tints its reflection with its own base colour, which is why metalness
        // is a switch between two shading models rather than a slider between two numbers.
        // A classifier that calls a stone wall metal produces a picture nobody could mistake
        // for correct, which is the argument for reporting the count.
        // How rough a surface has to behave for a light that is not a point.
        //
        // The rig's lamps have a radius — a bulb, a shade, a window — and treating one as a
        // point puts a pinpoint mirror highlight on anything smooth, because all of its
        // energy is arriving from a single direction that it never really arrives from. The
        // standard correction widens the microfacet lobe by the light's apparent size and
        // renormalises so the total energy is unchanged: a lamp a foot across seen from
        // across a room is still nearly a point, and the same lamp a hand's width away is
        // a soft sheen rather than a star.
        //
        // Without it, GK3's hair — 0.44 in its generated map, which is a perfectly
        // reasonable number for hair — came out as a bright plastic sweep across the crown
        // under a lobby's forty-one lamps.
        float Widened(float roughness, float radius, float distance, out float energy)
        {
            float alpha = roughness * roughness;
            float widened = clamp(alpha + (radius / max(2.0 * distance, 1e-4)), alpha, 1.0);

            // Energy in the lobe is proportional to the square of its width, so a lobe made
            // wider has to be made proportionally dimmer or a big lamp brightens the room
            // by being big.
            float ratio = alpha / max(widened, 1e-4);
            energy = ratio * ratio;

            return sqrt(widened);
        }

        vec3 Shade(
            Surface surface, vec3 normal, vec3 toEye, vec3 toLight,
            float radius, float distance)
        {
            float nDotL = max(dot(normal, toLight), 0.0);

            if (nDotL <= 0.0)
            {
                return vec3(0.0);
            }

            // Not "half": GLSL reserves the word.
            vec3 halfway = normalize(toLight + toEye);
            float nDotV = max(dot(normal, toEye), 1e-4);
            float nDotH = max(dot(normal, halfway), 0.0);
            float vDotH = max(dot(toEye, halfway), 0.0);

            float energy;
            float roughness = Widened(surface.roughness, radius, distance, energy);

            vec3 fresnel = Fresnel(vDotH, surface.f0) * surface.sheen;

            // Times pi, and the pi is not decoration.
            //
            // A textbook BRDF divides the diffuse by pi and leaves the light's radiance
            // alone. This rig's intensities were authored in 3ds Max in 1999 and tuned
            // here against a plain Lambert with no pi anywhere, so introducing the division
            // darkened every rig-lit surface to a third of what it was while leaving the
            // specular at full strength. Multiplying both terms by pi instead is the same
            // BRDF with the light's radiance scaled by pi, which is the convention the
            // authored numbers are already in — and it leaves the lightmapped and ambient
            // paths untouched.
            vec3 specular = fresnel *
                Distribution(nDotH, roughness) *
                Visibility(nDotV, nDotL, roughness) * 3.14159265 * energy;

            // Energy that was not reflected is what is left for the diffuse term, and a
            // metal keeps none of it.
            //
            // The sheen is inside the Fresnel rather than only on the specular, and that is
            // the whole of it: Schlick's approximation returns *one* at grazing incidence
            // whatever f0 is, so switching the lobe off by zeroing the reflectance leaves a
            // hard white rim around every silhouette and takes the diffuse away underneath
            // it. Gabriel came out looking like a mannequin lit from behind, which is a
            // very good description of a rim light with nothing else.
            vec3 diffuse = (vec3(1.0) - fresnel) * (1.0 - surface.metalness) * surface.albedo;

            return (diffuse + specular) * nDotL;
        }

        // Which lights reach a point, as a range into the grid's index list.
        //
        // Clamped rather than refused at the edges. A character standing a hair outside the
        // room's own bounding box — which happens, because the box is the geometry's and a
        // walk cycle swings an arm past it — is lit by the cell next to them rather than by
        // nothing at all, which is the difference between a seam and a black silhouette.
        void CellAt(vec3 position, out int first, out int last)
        {
            vec3 local = (position - frame.gridOrigin.xyz) / max(frame.gridOrigin.w, 1e-4);
            ivec3 counts = ivec3(frame.gridCounts.xyz);

            ivec3 at = clamp(ivec3(floor(local)), ivec3(0), max(counts - 1, ivec3(0)));
            int index = ((at.z * counts.y) + at.y) * counts.x + at.x;

            first = cells.at[index];
            last = cells.at[index + 1];
        }

        // The rig the artists authored, evaluated directly. Falloff is linear between the
        // light's start and end distances: that is what 3ds Max's linear decay did and what
        // these numbers were tuned against, and inverse-square would darken every room.
        vec3 EvaluateRig(
            Surface surface, vec3 position, vec3 normal, vec3 toEye,
            int shadowed, int shadowSamples)
        {
            vec3 total = vec3(0.0);
            int traced = 0;

            // The lights that reach where this fragment is, rather than every light in the
            // room. The cell is a lookup on the position, the list inside it is ordered
            // brightest first, and both of those matter: the loop is short, and the few
            // lights that can afford a shadow ray are the ones worth tracing.
            int first = 0;
            int last = 0;
            CellAt(position, first, last);

            for (int slot = first; slot < last; slot++)
            {
                Light light = rig.lights[reaching.lights[slot]];

                vec3 toLight = light.positionAndStart.xyz - position;
                float distance = max(length(toLight), 0.0001);
                vec3 direction = toLight / distance;

                vec3 response =
                    Shade(surface, normal, toEye, direction, light.cone.w, distance);
                if (response == vec3(0.0))
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

                // A distant key — the sun, the moon, the sky fill — has no falloff at
                // all. Its stored range is leftover data that cannot reach the scene, so
                // applying it does not dim the light, it deletes it. The room and the
                // people standing in it read this the same way, which is the point: a
                // character lit by a different set of lights from the wall behind them
                // never looks like they are in the room. See GpuLight.IsDistantKey.
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

                vec3 contribution = light.colorAndIntensity.rgb * light.colorAndIntensity.w *
                                    attenuation * cone * response;

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
                        Unblocked(position, normal, light, toLight, distance, shadowSamples);
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
        //
        // Built once per fragment and handed to both the normal and the parallax. It used
        // to be built twice per fragment, identically, from six derivatives each.
        struct SurfaceFrame
        {
            // Normalised, along +U and +V, with the interpolated normal completing them.
            vec3 tangent;
            vec3 bitangent;
            vec3 normal;

            // How much world one unit of texture coordinate is worth, along U and along V.
            // This is the length the Gram-Schmidt step throws away, and keeping it is what
            // lets a depth authored in world units mean the same thing on every surface.
            vec2 world;

            // False where the surface is edge-on or its coordinates do not vary, which
            // happens at silhouettes and on the flat-shaded helpers. Dividing by zero
            // there is a NaN across a whole triangle; the geometric normal is invisible.
            bool valid;
        };

        SurfaceFrame FrameAt(vec3 geometric)
        {
            SurfaceFrame surface;
            surface.tangent = vec3(0.0);
            surface.bitangent = vec3(0.0);
            surface.normal = geometric;
            surface.world = vec2(0.0);
            surface.valid = false;

            vec3 dpx = dFdx(inWorld);
            vec3 dpy = dFdy(inWorld);
            vec2 dtx = dFdx(inTexCoord);
            vec2 dty = dFdy(inTexCoord);

            float area = (dtx.x * dty.y) - (dty.x * dtx.y);

            if (abs(area) < 1e-12)
            {
                return surface;
            }

            vec3 alongU = ((dpx * dty.y) - (dpy * dtx.y)) / area;
            vec3 alongV = ((dpy * dtx.x) - (dpx * dty.x)) / area;

            if (any(isnan(alongU)) || any(isnan(alongV)))
            {
                return surface;
            }

            surface.world = vec2(length(alongU), length(alongV));

            if (min(surface.world.x, surface.world.y) < 1e-6)
            {
                return surface;
            }

            // Gram-Schmidt against the interpolated normal, so the frame stays square even
            // where the derivatives are noisy.
            surface.tangent = normalize(alongU - (geometric * dot(geometric, alongU)));

            if (any(isnan(surface.tangent)))
            {
                return surface;
            }

            surface.bitangent = cross(geometric, surface.tangent);
            surface.valid = true;

            return surface;
        }

        vec3 PerturbedNormal(SurfaceFrame surface, vec2 uv, float strength)
        {
            // Two channels, not three. BC5 keeps only X and Y — it has no third channel to
            // keep — and Z is recovered from them, which is exact for a unit vector in
            // tangent space because Z is never negative there. An uncompressed map stores a
            // Z as well, and reconstructing it rather than reading it gives the same answer
            // to within a rounding step, so both sources take this one path.
            vec2 mapped = texture(normalTexture, uv).xy;

            // Scaled in the tangent plane, which is the right place: pulling X and Y towards
            // zero and letting Z follow tilts the normal back towards the surface without
            // ever unnormalising it. Everything in a generated map is invented, so how much
            // of it to believe is a per-material decision rather than a constant.
            vec2 tangentXY = ((mapped * 2.0) - 1.0) * strength;

            vec3 tangentNormal = vec3(
                tangentXY, sqrt(max(0.0, 1.0 - dot(tangentXY, tangentXY))));

            // Surfaces with no map are given a flat one, and eight bits cannot encode
            // exactly a half: 128 decodes to 0.0039 rather than 0. The tolerance is one
            // step of an eight-bit channel, not an epsilon — a tighter test never fires,
            // which is how the first attempt at this ran the derivative maths on all 6,400
            // textures that have no map at all.
            if (max(abs(tangentNormal.x), abs(tangentNormal.y)) <= (1.0 / 255.0))
            {
                return surface.normal;
            }

            if (!surface.valid)
            {
                return surface.normal;
            }

            // The frame is built from the *interpolated* coordinate, not the parallax-offset
            // one. A derivative taken across an offset that itself varies per pixel measures
            // the offset as well as the surface, and the frame comes out skewed wherever the
            // relief is steepest — which after a march is everywhere the relief is.
            return normalize(
                mat3(surface.tangent, surface.bitangent, surface.normal) * tangentNormal);
        }

        // How many steps the march takes looking straight at a surface, and how many at
        // grazing incidence.
        //
        // The ray crosses more of the field per unit of depth the further from head-on it
        // arrives, so a fixed count that is generous looking down at a floor stairsteps
        // visibly looking along one — and looking along a floor is how a street is seen.
        // The cost is a BC4 tap per step, which is the cheapest tap there is.
        const int kParallaxNear = 8;
        const int kParallaxFar = 24;

        // Where to sample a surface so that its relief looks like relief.
        //
        // Parallax occlusion mapping: march along the view ray through the height field and
        // sample where the ray first meets it. The cheaper single step — offset once by the
        // height at the coordinate the ray entered on — is exact only where the field is
        // flat, and wrong by more the further from head-on the surface is looked at. That is
        // precisely the case a floor is: a cobbled street seen along its length is where the
        // single step failed hardest, and it is what this is for.
        //
        // The march is in the field's own parameter. <c>s</c> is the sampled value, a half
        // is the modelled surface, and the view ray crosses the polygon at s = 0.5 by
        // construction, so the coordinate at any s is the entry coordinate offset by
        // (0.5 - s) spans. Walking s down from one — above everything the field can be —
        // the hit is the first step at which the field has risen to meet the ray.
        //
        // Zero depth returns the coordinate untouched, which is every surface with no map.
        vec2 ParallaxCoord(SurfaceFrame surface, vec3 toEye, float depth)
        {
            if (depth <= 0.0 || !surface.valid)
            {
                return inTexCoord;
            }

            // The eye in tangent space. Its Z is how head-on the surface is being looked at,
            // and dividing by it is what makes the offset grow towards grazing incidence —
            // clamped, because at the horizon it grows without bound and the surface tears.
            vec3 eye = vec3(
                dot(toEye, surface.tangent),
                dot(toEye, surface.bitangent),
                dot(toEye, surface.normal));

            float facing = max(abs(eye.z), 0.35);

            // How far the coordinate travels for one whole unit of the field: a depth in
            // world units, converted through this surface's own tiling into the texture
            // coordinates the march steps in.
            vec2 span = (eye.xy / facing) * (depth / surface.world);

            if (dot(span, span) < 1e-14)
            {
                return inTexCoord;
            }

            int count = int(mix(float(kParallaxFar), float(kParallaxNear), abs(eye.z)));

            // Not "step": GLSL has a function by that name and shadowing it here would
            // compile and read as the function everywhere else.
            float stride = 1.0 / float(count);

            // Explicit derivatives, because the march samples at coordinates that vary per
            // pixel inside a loop and an implicit level of detail is undefined in control
            // flow that is not uniform across the quad. The entered coordinate's
            // derivatives are the right ones in any case: the footprint being filtered is
            // the surface's, not the march's.
            vec2 ddx = dFdx(inTexCoord);
            vec2 ddy = dFdy(inTexCoord);

            float s = 1.0;
            vec2 uv = inTexCoord - (span * 0.5);
            float sampled = textureGrad(heightTexture, uv, ddx, ddy).r;

            float previous = sampled;
            vec2 wasAt = uv;

            for (int i = 0; i < count && sampled < s; i++)
            {
                previous = sampled;
                wasAt = uv;

                s -= stride;
                uv += span * stride;
                sampled = textureGrad(heightTexture, uv, ddx, ddy).r;
            }

            // Refine between the last two steps. The field is above the ray at one and
            // below it at the other, so the crossing is a linear solve — which is what
            // takes the march from visibly stepped to smooth without more taps.
            float after = sampled - s;
            float before = previous - (s + stride);
            float weight = clamp(after / max(after - before, 1e-6), 0.0, 1.0);

            return mix(uv, wasAt, weight);
        }

        void main()
        {
            vec3 geometric = normalize(inNormal);
            vec3 toEye = normalize(frame.cameraPosition.xyz - inWorld);

            // A character or a prop rather than the room. It decides which instances a
            // ray leaving this pixel may hit, and nothing else: the lighting itself is the
            // same for both, deliberately.
            bool isModel = draw.shading.z >= 1.5;

            // One frame for both the march and the normal, built from the coordinate the
            // fragment arrived with.
            SurfaceFrame basis = FrameAt(geometric);

            vec2 uv = ParallaxCoord(basis, toEye, draw.shading.w);

            vec4 sampled = texture(baseColor, uv);
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
            vec3 normal = PerturbedNormal(basis, uv, draw.material.w);

            // The ORM map, and the material's own numbers under it. The map multiplies
            // rather than replaces, which is what keeps a roughness corrected in the edit
            // layer meaningful once a generated map arrives for the same surface; the
            // neutral map is one in both channels, so a surface without one gets its
            // measured finish unchanged.
            vec3 orm = texture(ormTexture, uv).rgb;

            // Zero reflectance means no ORM map, which means no measured finish and so no
            // specular lobe at all. Not a fudge: without a map the roughness is a
            // classifier's guess at median confidence 0.32, and GK3's diffuse textures
            // already have their highlights painted into them, so a physical lobe over a
            // painted one counts the same light twice.
            float sheen = draw.material.z > 0.0001 ? 1.0 : 0.0;

            Surface surface;
            surface.albedo = albedo;
            surface.occlusion = orm.r;
            surface.sheen = sheen;

            // The map where there is one, the material's own number where there is not —
            // and not the two multiplied together.
            //
            // Multiplying is the glTF convention, where the material's roughness is a
            // *factor* that defaults to one and the map carries the value. Here it is not a
            // factor: `material-library.json` holds a classifier's estimate of the same
            // quantity the map estimates, so multiplying two independent answers to one
            // question squares the glossiness. Gabriel's skin is 0.55 in the library and
            // 0.56 in his map, and 0.31 is polished plastic.
            //
            // A *negative* roughness outranks both: it says a person corrected this surface
            // after looking at the room, and a correction has to beat a measurement or the
            // edit layer cannot fix a generated map that is wrong about what the surface is.
            bool corrected = draw.material.x < 0.0;

            surface.roughness = clamp(
                corrected ? -draw.material.x : mix(draw.material.x, orm.g, sheen), 0.03, 1.0);

            surface.metalness = clamp(
                corrected ? draw.material.y : mix(draw.material.y, orm.b, sheen), 0.0, 1.0);

            // A dielectric reflects a few per cent of what hits it head-on, the same in
            // every channel; a metal reflects most of it, tinted by its own colour. 0.08
            // times the material's reflectance puts the usual 0.5 at 0.04, which is glass,
            // water and most everything else that is not a conductor.
            //
            // The reflectance arrives as zero for a surface with no ORM map, which switches
            // the specular lobe off entirely. That is deliberate and it is the difference
            // between an enhancement and a regression: without a map the roughness is a
            // classifier's guess at median confidence 0.32, and GK3's diffuse textures
            // already have their highlights painted into them, so a physical lobe on top
            // of a painted one counts the same light twice. Gabriel's skin is guessed at
            // 0.55 — glossy enough to give a face a plastic sheen from every one of a
            // room's sixty-three lamps.
            surface.f0 = mix(
                vec3(0.08 * draw.material.z), albedo, surface.metalness);

            // Alpha is how rough this surface is, which is what decides whether a
            // reflection is worth tracing off it and how tightly to gather one. The
            // shaded roughness, so a map that smooths a surface is a surface the
            // reflection pass will consider.
            //
            // <b>Negative on a model.</b> Roughness is clamped to at least 0.03, so the
            // sign is free and it is the one thing the tracing pass needs to know that
            // nothing else in the frame carries: a shadow ray leaving a character has to
            // skip characters, because GK3's people are a stack of overlapping shells and
            // a ray leaving the shirt hits the arm inside it. Whoever reads this channel
            // for a roughness takes its absolute value; see RayTracingScene.MaskFor.
            outNormalTarget = vec4(
                normal,
                isModel ? -surface.roughness : surface.roughness);
            outMotion = vec2(0.0);

            #ifdef RAY_TRACING
            outDirect = vec4(0.0);
            #endif

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
            if (mod(draw.shading.z, 2.0) > 0.5)
            {
                // Alpha says how much occlusion applies to this pixel: none, here. A bulb
                // does not get darker for being in a corner.
                outColor = vec4(albedo, 0.0);
                return;
            }

            float useLightmap = draw.shading.x;
            vec3 baked = texture(lightmapTexture, inLightmapCoord).rgb * draw.shading.y;

            // Ambient occlusion applies to the ambient term and to nothing else. It is a
            // statement about light arriving from everywhere at once, and multiplying a
            // direct light by it darkens a surface the lamp can plainly see.
            vec3 ambient = kAmbient * surface.occlusion;

            int shadowed = int(frame.rays.x);
            int occlusionRays = int(frame.rays.y);
            int shadowSamples = max(int(frame.rays.z), 1);
            float bakedWeight = frame.rays.w;

            bool tracing = shadowed > 0 || occlusionRays > 0;

            if (!tracing)
            {
                // No rays: scene geometry is exactly what the 1999 renderer showed, and
                // anything without a lightmap is lit by the rig with no shadows.
                // The rig's term already carries the albedo, because a metal's specular is
                // tinted and its diffuse does not exist — which is exactly the distinction
                // multiplying afterwards would throw away. The fallback has no rig to shade
                // against and stays the Lambert it always was.
                vec3 direct = rig.counts.x > 0.5
                    ? EvaluateRig(surface, inWorld, normal, toEye, 0, 1)
                    : albedo *
                      (vec3(0.35) + (0.65 * max(dot(normal, -frame.lightDirection.xyz), 0.0)));

                outColor = vec4(
                    mix((albedo * ambient) + direct, albedo * baked, useLightmap), 1.0);

                return;
            }

            #ifdef RAY_TRACING
            // The bake at full strength, and the rig beside it. It is the compositing
            // pass that reconciles them, by subtracting from the bake the light the rig
            // has just accounted for rather than scaling the whole thing down — see
            // there for why the difference matters at a window.
            //
            // Neither term is occluded here. Both occlusions are traced once a pixel and
            // filtered over many frames, and neither is available until this pass has
            // finished.
            // Alpha says what this indirect term *is*, in three states, because the
            // compositing pass has to treat them differently: zero for a surface that
            // carries its own brightness, a half for the ambient floor, one for a bake.
            //
            // Only a bake is a second copy of the light the rig is computing afresh, so
            // only a bake may be subtracted against. The ambient floor is not double
            // counting anything — subtracting the rig from it drove it to nothing wherever
            // a lamp reached, which took the ambient occlusion with it, since occlusion
            // multiplies the residual and there was no residual left. A character, who has
            // no lightmap and is therefore all ambient, lost it everywhere.
            // The bake at full strength, not scaled by the tier's bakedWeight the way the
            // rasterised path scales it. Measured, on RC1: scaling it deepens a cast
            // shadow from 19% to 28% and costs the whole frame 22% of its brightness,
            // because the rig does not deliver what the bake was carrying. Turning the
            // lightmap down is the right lever for "ray trace it properly" and it is one
            // line — but the rig's 1999 intensities have to be calibrated against it first,
            // across 111 scenes, or it just makes the game darker.
            outColor = vec4(
                albedo * mix(ambient, baked, useLightmap), useLightmap > 0.5 ? 1.0 : 0.5);
            outDirect = vec4(EvaluateRig(surface, inWorld, normal, toEye, 0, 1), 1.0);
            #else
            // Indirect light. There is no gathered bounce, so the bake stands in for it,
            // scaled down because it also contains the direct light computed afresh.
            vec3 indirect = mix(ambient, baked * useLightmap, bakedWeight * useLightmap);

            outColor = vec4(
                (albedo * indirect) + EvaluateRig(surface, inWorld, normal, toEye, 0, 1), 1.0);
            #endif
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
