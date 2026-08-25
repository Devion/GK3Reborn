// Scalefactor band offsets, TNS coefficient maps and TNS band limits transcribed from
// ISO/IEC 14496-3 (Tables 4.129 .. 4.145) as carried by JAADec (public domain) and
// cross-checked against FFmpeg's copy of the same tables. Everything else here is
// computed at start-up from the formulas in the standard.

namespace GK3Reborn.Formats.Video.Aac;

/// <summary>
/// The constant tables of AAC-LC: sampling rates, scalefactor band layouts, TNS
/// coefficient maps, the inverse-quantisation curve and the two window shapes.
/// </summary>
/// <remarks>
/// The windows and the x^(4/3) curve are computed rather than transcribed: they are
/// closed-form, the computation costs microseconds once, and a formula cannot carry a
/// transcription slip. The band tables have no formula and are copied from the standard.
/// </remarks>
internal static class AacTables
{
    /// <summary>Sampling rates by samplingFrequencyIndex (indices 13-14 are reserved).</summary>
    public static readonly int[] SampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350,
    ];

    /// <summary>Number of scalefactor bands in a long window, by sampling-frequency index.</summary>
    public static readonly int[] NumSwbLong = [41, 41, 47, 49, 49, 51, 47, 47, 43, 43, 43, 40];

    /// <summary>Number of scalefactor bands in a short window, by sampling-frequency index.</summary>
    public static readonly int[] NumSwbShort = [12, 12, 12, 14, 14, 14, 15, 15, 15, 15, 15, 15];

    private static readonly int[] SwbLong96 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56,
        64, 72, 80, 88, 96, 108, 120, 132, 144, 156, 172, 188, 212, 240,
        276, 320, 384, 448, 512, 576, 640, 704, 768, 832, 896, 960, 1024,
    ];

    private static readonly int[] SwbLong64 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56,
        64, 72, 80, 88, 100, 112, 124, 140, 156, 172, 192, 216, 240, 268,
        304, 344, 384, 424, 464, 504, 544, 584, 624, 664, 704, 744, 784, 824,
        864, 904, 944, 984, 1024,
    ];

    private static readonly int[] SwbLong48 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72,
        80, 88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264, 292,
        320, 352, 384, 416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736,
        768, 800, 832, 864, 896, 928, 1024,
    ];

    private static readonly int[] SwbLong32 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72,
        80, 88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264, 292,
        320, 352, 384, 416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736,
        768, 800, 832, 864, 896, 928, 960, 992, 1024,
    ];

    private static readonly int[] SwbLong24 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68,
        76, 84, 92, 100, 108, 116, 124, 136, 148, 160, 172, 188, 204, 220,
        240, 260, 284, 308, 336, 364, 396, 432, 468, 508, 552, 600, 652, 704,
        768, 832, 896, 960, 1024,
    ];

    private static readonly int[] SwbLong16 =
    [
        0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 100, 112, 124,
        136, 148, 160, 172, 184, 196, 212, 228, 244, 260, 280, 300, 320, 344,
        368, 396, 424, 456, 492, 532, 572, 616, 664, 716, 772, 832, 896, 960, 1024,
    ];

    private static readonly int[] SwbLong8 =
    [
        0, 12, 24, 36, 48, 60, 72, 84, 96, 108, 120, 132, 144, 156, 172,
        188, 204, 220, 236, 252, 268, 288, 308, 328, 348, 372, 396, 420, 448,
        476, 508, 544, 580, 620, 664, 712, 764, 820, 880, 944, 1024,
    ];

    private static readonly int[] SwbShort96 = [0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64, 92, 128];
    private static readonly int[] SwbShort48 = [0, 4, 8, 12, 16, 20, 28, 36, 44, 56, 68, 80, 96, 112, 128];
    private static readonly int[] SwbShort24 = [0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 64, 76, 92, 108, 128];
    private static readonly int[] SwbShort16 = [0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 60, 72, 88, 108, 128];
    private static readonly int[] SwbShort8 = [0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 60, 72, 88, 108, 128];

    /// <summary>Long-window scalefactor band offsets (NumSwbLong+1 entries) by sampling-frequency index.</summary>
    public static readonly int[][] SwbOffsetLong =
    [
        SwbLong96, SwbLong96, SwbLong64, SwbLong48, SwbLong48, SwbLong32,
        SwbLong24, SwbLong24, SwbLong16, SwbLong16, SwbLong16, SwbLong8,
    ];

    /// <summary>Short-window scalefactor band offsets (NumSwbShort+1 entries) by sampling-frequency index.</summary>
    public static readonly int[][] SwbOffsetShort =
    [
        SwbShort96, SwbShort96, SwbShort96, SwbShort48, SwbShort48, SwbShort48,
        SwbShort24, SwbShort24, SwbShort16, SwbShort16, SwbShort16, SwbShort8,
    ];

    /// <summary>Highest scalefactor band TNS may filter in a long window, by sampling-frequency index.</summary>
    public static readonly int[] TnsMaxBandsLong = [31, 31, 34, 40, 42, 51, 46, 46, 42, 42, 42, 39];

    /// <summary>Highest scalefactor band TNS may filter in a short window, by sampling-frequency index.</summary>
    public static readonly int[] TnsMaxBandsShort = [9, 9, 10, 14, 14, 14, 14, 14, 14, 14, 14, 14];

    /// <summary>
    /// TNS reflection coefficients indexed by (coef_compress * 2 + coef_res - 3) and then by
    /// the coded value; the maps for coef_compress = 1 are the halves of the wider maps.
    /// </summary>
    /// <remarks>
    /// These carry the standard's signs (sin(coef / iqfac) for small codes). FFmpeg and
    /// JAAD store them negated and undo that inside their step-up recursion; combining
    /// their table with the standard's recursion inverts the filter, which shows up as a
    /// time-reversed transient envelope rather than a parse error.
    /// </remarks>
    public static readonly float[][] TnsCoefficients =
    [
        // coef_compress 0, coef_res 3 (3-bit values)
        [0.00000000f, 0.43388373f, 0.78183150f, 0.97492790f, -0.98480773f, -0.86602539f, -0.64278758f, -0.34202015f],
        // coef_compress 0, coef_res 4 (4-bit values)
        [
            0.00000000f, 0.20791170f, 0.40673664f, 0.58778524f, 0.74314481f, 0.86602539f, 0.95105654f, 0.99452192f,
            -0.99573416f, -0.96182561f, -0.89516330f, -0.79801720f, -0.67369562f, -0.52643216f, -0.36124167f, -0.18374951f,
        ],
        // coef_compress 1, coef_res 3 (2-bit values)
        [0.00000000f, 0.43388373f, -0.64278758f, -0.34202015f],
        // coef_compress 1, coef_res 4 (3-bit values)
        [0.00000000f, 0.20791170f, 0.40673664f, 0.58778524f, -0.67369562f, -0.52643216f, -0.36124167f, -0.18374951f],
    ];

    /// <summary>|q|^(4/3) for the quantised magnitudes 0..8191 (larger escapes are computed directly).</summary>
    public static readonly float[] Pow43 = BuildPow43();

    /// <summary>2^(0.25 * (sf - 100)) for scalefactors 0..255.</summary>
    public static readonly float[] ScaleFactorGain = BuildScaleFactorGain();

    /// <summary>Rising half of the 2048-point sine window.</summary>
    public static readonly float[] SineLong = BuildSine(1024);

    /// <summary>Rising half of the 256-point sine window.</summary>
    public static readonly float[] SineShort = BuildSine(128);

    /// <summary>Rising half of the 2048-point Kaiser-Bessel-derived window (alpha 4).</summary>
    public static readonly float[] KbdLong = BuildKbd(1024, 4.0);

    /// <summary>Rising half of the 256-point Kaiser-Bessel-derived window (alpha 6).</summary>
    public static readonly float[] KbdShort = BuildKbd(128, 6.0);

    /// <summary>Maps an explicit sampling rate to the index whose band tables the standard assigns it.</summary>
    public static int SampleRateToIndex(int rate)
    {
        // Lower bounds of the ranges in ISO 14496-3 Table 4.5 (nearest standard rate).
        ReadOnlySpan<int> lowerBounds = [92017, 75132, 55426, 46009, 37566, 27713, 23004, 18783, 13856, 9391, 7350];
        for (int i = 0; i < lowerBounds.Length; i++)
        {
            if (rate >= lowerBounds[i])
            {
                return i;
            }
        }

        return 11;
    }

    private static float[] BuildPow43()
    {
        float[] table = new float[8192];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)Math.Pow(i, 4.0 / 3.0);
        }

        return table;
    }

    private static float[] BuildScaleFactorGain()
    {
        float[] table = new float[256];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)Math.Pow(2.0, 0.25 * (i - 100));
        }

        return table;
    }

    private static float[] BuildSine(int half)
    {
        float[] w = new float[half];
        for (int n = 0; n < half; n++)
        {
            w[n] = (float)Math.Sin(Math.PI * (n + 0.5) / (2 * half));
        }

        return w;
    }

    private static float[] BuildKbd(int half, double alpha)
    {
        // Kaiser window of half+1 points, cumulated and normalised (ISO 14496-3 4.6.11.3.2).
        double[] kaiser = new double[half + 1];
        double total = 0;
        for (int n = 0; n <= half; n++)
        {
            double t = 2.0 * n / half - 1.0;
            kaiser[n] = BesselI0(Math.PI * alpha * Math.Sqrt(Math.Max(0.0, 1.0 - t * t)));
            total += kaiser[n];
        }

        float[] w = new float[half];
        double running = 0;
        for (int n = 0; n < half; n++)
        {
            running += kaiser[n];
            w[n] = (float)Math.Sqrt(running / total);
        }

        return w;
    }

    private static double BesselI0(double x)
    {
        // Power series; converges quickly for the arguments the windows use (x <= 6*pi).
        double sum = 1.0;
        double term = 1.0;
        double quarter = x * x / 4.0;
        for (int k = 1; k < 200; k++)
        {
            term *= quarter / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
