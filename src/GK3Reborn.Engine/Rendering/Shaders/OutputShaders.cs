namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The last thing that happens to a frame: a tone curve, a sharpen, and an encode.
/// </summary>
/// <remarks>
/// Here rather than beside the pipeline that builds it, because both backends compile it.
/// The Vulkan pipeline and the Direct3D one are two ways of loading the same text; see
/// <see cref="ShaderCompiler"/>, which takes it to SPIR-V for one and on to DXIL for the
/// other.
/// </remarks>
public static class OutputShaders
{
    /// <summary>One triangle covering the frame, from the vertex index alone.</summary>
    public const string Vertex = """
        #version 460

        layout(location = 0) out vec2 outUv;

        void main()
        {
            // One triangle covering the frame, from nothing but the vertex index.
            outUv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((outUv * 2.0) - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>Linear light in, a picture a display will accept out.</summary>
    public const string Fragment = """
        #version 460

        layout(location = 0) in vec2 inUv;
        layout(location = 0) out vec4 outColor;

        layout(set = 0, binding = 0) uniform sampler2D picture;

        layout(push_constant) uniform Display
        {
            // x transfer function, y paper white, z headroom above it, w tone curve
            vec4 tuning;

            // x sharpness, yz one source pixel in texture coordinates
            vec4 sharpen;
        } display;

        // Rec.709 to Rec.2020, which is what ST.2084 signalling is carried in. Written out
        // rather than looked up: it is nine constants and a matrix constructor, and a
        // texture lookup for a fixed 3x3 would be worse in every way.
        const mat3 kRec709ToRec2020 = mat3(
            0.6274040, 0.0690970, 0.0163916,
            0.3292820, 0.9195400, 0.0880132,
            0.0433136, 0.0113612, 0.8955950);

        // scRGB is defined with 1.0 at 80 candelas, which is the sRGB reference white the
        // standard was written against.
        const float kScRgbReferenceNits = 80.0;

        // ST.2084 is defined against a peak of ten thousand candelas.
        const float kPqPeakNits = 10000.0;

        float Luminance(vec3 c)
        {
            return dot(c, vec3(0.2126, 0.7152, 0.0722));
        }

        // Reinhard, applied to luminance rather than to each channel. Per channel it
        // desaturates everything bright towards white, which is exactly what a fire or a
        // stained-glass window must not do.
        vec3 Reinhard(vec3 c)
        {
            float l = Luminance(c);
            return l > 0.0001 ? c * ((l / (1.0 + l)) / l) : c;
        }

        // The filmic shoulder, in the form Hable published: a rational curve with a toe and
        // a shoulder, normalised so that the white point comes out at one.
        vec3 FilmicCurve(vec3 x)
        {
            const float a = 0.15, b = 0.50, c = 0.10, d = 0.20, e = 0.02, f = 0.30;
            return (((x * ((a * x) + (c * b))) + (d * e)) / ((x * ((a * x) + b)) + (d * f))) -
                   (e / f);
        }

        vec3 Filmic(vec3 colour)
        {
            const float white = 11.2;
            return FilmicCurve(colour * 2.0) / FilmicCurve(vec3(white));
        }

        float PerceptualQuantiser(float nits)
        {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;

            float y = clamp(nits / kPqPeakNits, 0.0, 1.0);
            float p = pow(y, m1);

            return pow((c1 + (c2 * p)) / (1.0 + (c3 * p)), m2);
        }

        // Contrast-adaptive sharpening over the five-tap cross. The amount is derived from
        // how much room the neighbourhood has left in both directions, so a pixel already
        // near black or near white is sharpened hardly at all and cannot be pushed past
        // either end — which is the whole difference between this and an unsharp mask.
        vec3 Sharpened(vec2 uv)
        {
            vec2 step = display.sharpen.yz;

            vec3 c = texture(picture, uv).rgb;

            if (display.sharpen.x <= 0.0)
            {
                return c;
            }

            vec3 n = texture(picture, uv + vec2(0.0, -step.y)).rgb;
            vec3 s = texture(picture, uv + vec2(0.0, step.y)).rgb;
            vec3 w = texture(picture, uv + vec2(-step.x, 0.0)).rgb;
            vec3 e = texture(picture, uv + vec2(step.x, 0.0)).rgb;

            vec3 lowest = min(min(min(n, s), min(w, e)), c);
            vec3 highest = max(max(max(n, s), max(w, e)), c);

            // How much headroom there is either way, whichever is the smaller: how far the
            // darkest tap is above black, and how far the brightest is below white. A pixel
            // with nothing left in either direction is sharpened by nothing, which is what
            // stops the filter from ringing. The square root makes the response perceptual.
            //
            // Above white there is no answer to "how far below white", so the second term
            // falls to nought and highlights are left alone. That is the right behaviour in
            // high dynamic range as well as the only defined one: a lamp at four times
            // paper white has no detail in it to recover.
            vec3 room = sqrt(clamp(
                min(lowest, max(vec3(0.0), vec3(2.0) - highest)) / max(highest, vec3(1e-4)),
                0.0, 1.0));

            // The strongest ratio the filter will use, interpolated by the setting. Eight
            // is barely visible and five is as far as this can go without the ring it
            // exists to avoid.
            float peak = -1.0 / mix(8.0, 5.0, clamp(display.sharpen.x, 0.0, 1.0));

            vec3 weight = room * peak;
            vec3 sum = ((n + s + w + e) * weight) + c;

            return clamp(sum / ((4.0 * weight) + 1.0), lowest, highest);
        }

        void main()
        {
            vec3 colour = max(Sharpened(inUv), vec3(0.0));

            float transfer = display.tuning.x;

            if (transfer < 0.5)
            {
                // Standard range. The target is an sRGB format and the hardware does the
                // encode on write, so all that is left is the tone curve — and the default
                // curve is the clip this game has always had, because every reference
                // image in the corpus was taken through it.
                float curve = display.tuning.w;

                if (curve > 1.5)
                {
                    colour = Filmic(colour);
                }
                else if (curve > 0.5)
                {
                    colour = Reinhard(colour);
                }

                outColor = vec4(clamp(colour, 0.0, 1.0), 1.0);
                return;
            }

            // High dynamic range. No tone curve at all below the headroom the display
            // actually has: the point of the exercise is that a lamp is brighter than a
            // wall, and a curve that pulls it back down is the thing being escaped from.
            // Above the headroom there is nowhere left to go and it is clamped, which is
            // what the display would do anyway and at least keeps hue.
            float paperWhite = max(display.tuning.y, 1.0);
            float headroom = max(display.tuning.z, 1.0);

            float luminance = Luminance(colour);

            if (luminance > headroom)
            {
                colour *= headroom / luminance;
            }

            if (transfer > 1.5)
            {
                // scRGB. Linear light, sRGB primaries, and one unit is 80 candelas.
                outColor = vec4(colour * (paperWhite / kScRgbReferenceNits), 1.0);
                return;
            }

            vec3 wide = kRec709ToRec2020 * (colour * paperWhite);

            outColor = vec4(
                PerceptualQuantiser(wide.r),
                PerceptualQuantiser(wide.g),
                PerceptualQuantiser(wide.b),
                1.0);
        }
        """;
}
