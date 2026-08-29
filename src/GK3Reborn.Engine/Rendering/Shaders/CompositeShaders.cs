namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The pass that adds the traced light to the raster picture.
/// </summary>
/// <remarks>
/// Here rather than beside the pipeline that builds it, because both backends compile it.
/// The Vulkan pipeline and the Direct3D one are two ways of loading the same text; see
/// <see cref="ShaderCompiler"/>, which takes it to SPIR-V for one and on to DXIL for the
/// other.
/// </remarks>
public static class CompositeShaders
{
    /// <summary>One triangle covering the frame, from the vertex index alone.</summary>
    public const string Vertex = """
        #version 460

        void main()
        {
            // One triangle covering the frame, from nothing but the vertex index.
            //
            // A local rather than an output: the fragment stage reads its targets with
            // texelFetch at gl_FragCoord, so an interpolated coordinate would be computed,
            // interpolated and thrown away. Direct3D also refuses to link two stages that
            // disagree about a varying, and a varying only one of them declares is exactly
            // that disagreement.
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((corner * 2.0) - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>The six targets of a frame, brought together into one picture.</summary>
    public const string Fragment = """
        #version 460

        layout(location = 0) out vec4 outColor;

        layout(set = 0, binding = 0) uniform sampler2D indirectTarget;
        layout(set = 0, binding = 1) uniform sampler2D directTarget;
        layout(set = 0, binding = 2) uniform sampler2D shadowTarget;
        layout(set = 0, binding = 3) uniform sampler2D occlusionTarget;
        layout(set = 0, binding = 4) uniform sampler2D reflectionTarget;

        // How much of the rig's light survives the things standing in the room, as opposed
        // to the room itself. One where nobody is in the way.
        layout(set = 0, binding = 5) uniform sampler2D dynamicTarget;

        // How much of the traced occlusion to believe, which is a decision the tier makes.
        //
        // Never all of it. Whole, it drives a surface to black outright — enough of the
        // hemisphere above a shoulder is that person's own head that the shoulder
        // disappears, which is not a shadow anybody would draw. What is worth having is the
        // near contact nothing else holds: the seam where an arm meets a body, the line
        // under a table, the ground a chair leg stands on.
        //
        // Where a bake is still in play there is a second reason to hold it back, which is
        // that these rooms' lightmaps were baked with occlusion already in them, so a
        // hemisphere of rays is measuring something the bake has largely accounted for.
        // Medium and High have no bake to count twice against and believe a good deal more
        // of it — which is the whole of why a chair leg meets the floor there and floated on
        // it before.
        layout(push_constant) uniform Tier
        {
            float occlusionStrength;
        } tier;

        // Reflections arrive already weighted: by how much of the ray the marcher could
        // follow, by how much the surface reflects at the angle it is seen from, and by
        // how rough it is. There is nothing left to scale here.
        const float kReflectionStrength = 1.0;

        void main()
        {
            ivec2 pixel = ivec2(gl_FragCoord.xy);

            vec4 indirect = texelFetch(indirectTarget, pixel, 0);
            vec4 rig = texelFetch(directTarget, pixel, 0);
            vec3 direct = rig.rgb;
            float shadow = clamp(texelFetch(shadowTarget, pixel, 0).r, 0.0, 1.0);
            float open = clamp(texelFetch(occlusionTarget, pixel, 0).r, 0.0, 1.0);

            // Alpha carries what the indirect term is: zero for a surface that carries
            // its own brightness, a half for the ambient floor, one for a bake.
            //
            // A bulb is not dimmed by the shade around it, so occlusion applies to the
            // other two and not to it.
            float lightmapped = step(0.75, indirect.a);
            float shaded = step(0.25, indirect.a);

            float occlusion = mix(1.0, open, tier.occlusionStrength * shaded);

            // How much of the rig's light a moving thing takes away, kept apart from the
            // room's own shadowing above because the two are subtracted at different
            // points. See below.
            float unblocked = clamp(texelFetch(dynamicTarget, pixel, 0).r, 0.0, 1.0);

            // The rig's light, as much of it as the *room* lets through. This is the term
            // the bake is comparable to, because the bake was made with the room and
            // nothing else in it.
            vec3 accounted = direct * shadow;

            // And what actually arrives, once whoever is standing there is counted too.
            vec3 arrived = accounted * unblocked;

            // And what the bake holds that the rig has not just accounted for.
            //
            // A bake contains two things: the light these same lamps threw in 1999, which
            // is being computed afresh and would otherwise be counted twice, and light
            // from sources the rig has not got — daylight through a window, sky, bounce
            // off a wall. Scaling the whole bake down, which is what this used to do,
            // throws the second away with the first: R25's window fell from a mean of 71
            // to 50 and the room lost the daylight the artists painted into it.
            //
            // Subtracting what the rig delivered keeps the two apart, and needs no weight
            // to be chosen. Where the rig explains the bake this falls to nothing and the
            // picture is ray traced outright; where it explains none of it — a window with
            // no light behind it, a corner lit only by bounce — the bake survives whole.
            // And it is the light that got past the *room* that is subtracted, not the
            // light the rig would give with nothing in the way: a rig this size has lamps
            // in the rooms next door, which contribute on paper and are stopped by a wall
            // in fact.
            //
            // What is emphatically not subtracted is the light a character is standing in
            // front of. Subtracting the fully occluded term is what made characters cast
            // no shadow for as long as this pass has existed: block a light and `arrived`
            // falls, `residual` rises by exactly as much, and the sum is unchanged. The
            // bake refilled every shadow the moment it was drawn. The bake cannot know
            // about somebody who walked into the room after 1999, so its light is credited
            // against the room's occlusion only, and the shadow is taken off the result.
            // Only against the bake. The ambient floor is not a second copy of anything
            // the rig computes, so nothing is taken off it — it is simply light that is
            // there, and it survives to be occluded below.
            vec3 residual = max(indirect.rgb - (accounted * lightmapped), vec3(0.0));

            // And what a moving thing takes off *that*.
            //
            // Not all of it. The mesh pass says in the rig target's spare channel how much
            // of this pixel's indirect term is a plain ambient floor, and that part is
            // light from everywhere at once — nobody standing here blocks it. The rest is
            // the shape of the bake, which is a record of how much light these same lamps
            // put on this spot when nobody was standing on it, and somebody standing on it
            // now is stopping the same share of it that they are stopping of the rig.
            //
            // Without this a character outdoors could not cast a shadow worth the name.
            // Measured on RC1's square: 54% of a lit ground pixel was bake-shaped ambient
            // and untouchable, and of the 46% left the sun was about half — so the deepest
            // shadow anybody could throw was a fifth of the pixel, which reads as a smudge.
            // Room shadows never had this problem, because the bake contains them: where
            // the obelisk stands the artists painted the shadow, so the shape dims with it.
            //
            // A surface that carries its own brightness is exempt, and so is the sky, and
            // neither needs a test: the mesh pass writes nothing into this channel for
            // either of them and the target clears to nought, which already means "none of
            // this is the bake's". The sky is the one that would have shown — its alpha in
            // the *indirect* target clears to one, so every is-this-a-surface test built on
            // that reads the background as a fully baked wall, and the dynamic channel over
            // it is nought because the denoiser writes nought wherever there is nothing to
            // shadow. The background went black.
            residual *= mix(1.0, unblocked, rig.a);

            vec3 lit = (residual * occlusion) + arrived;

            // What the surface reflects, and how much of it was found. Added rather than
            // mixed in: a floor that reflects a lamp is brighter for it, not less itself.
            // Alpha is the marcher's own confidence — off the edge of the screen there is
            // nothing to reflect and it says so.
            vec4 mirrored = texelFetch(reflectionTarget, pixel, 0);

            outColor = vec4(lit + (mirrored.rgb * mirrored.a * kReflectionStrength), 1.0);
        }
        """;
}
