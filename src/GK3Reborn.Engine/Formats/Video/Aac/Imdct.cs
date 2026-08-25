namespace GK3Reborn.Formats.Video.Aac;

/// <summary>
/// Inverse MDCT of 2048 or 256 points, evaluated through an N/4-point complex FFT.
/// </summary>
/// <remarks>
/// <para>
/// The direct definition, x[n] = (2/N) sum X[k] cos(2pi/N (n + n0)(k + 1/2)), is an
/// O(N^2) sum that would cost more than everything else in the decoder combined. The
/// same values come out of a DCT-IV of N/2 points, which folds into an N/4-point FFT
/// with a complex twiddle before and after, and the DCT-IV's symmetries give the four
/// quarters of the IMDCT output.
/// </para>
/// <para>
/// The FFT is a plain radix-2 decimation-in-time on split real/imaginary arrays with
/// precomputed twiddles; at 512 and 64 points it needs no cleverer scheme.
/// </para>
/// </remarks>
internal sealed class Imdct
{
    private readonly int _n;        // window length: 2048 or 256
    private readonly int _half;     // N/2: number of spectral coefficients
    private readonly int _quarter;  // N/4: FFT size
    private readonly float _scale;

    private readonly float[] _preCos, _preSin;   // exp(-i pi (k + 1/4) / (N/2))
    private readonly float[] _postCos, _postSin; // exp(-i pi k / (N/2))
    private readonly float[] _fftCos, _fftSin;   // exp(-2 pi i j / (N/4)) for j < N/8
    private readonly int[] _bitReverse;
    private readonly float[] _re, _im, _dct;

    public Imdct(int n)
    {
        _n = n;
        _half = n / 2;
        _quarter = n / 4;
        _scale = 2.0f / n;

        _preCos = new float[_quarter];
        _preSin = new float[_quarter];
        _postCos = new float[_quarter];
        _postSin = new float[_quarter];
        for (int k = 0; k < _quarter; k++)
        {
            double pre = Math.PI * (k + 0.25) / _half;
            _preCos[k] = (float)Math.Cos(pre);
            _preSin[k] = (float)Math.Sin(pre);
            double post = Math.PI * k / _half;
            _postCos[k] = (float)Math.Cos(post);
            _postSin[k] = (float)Math.Sin(post);
        }

        _fftCos = new float[_quarter / 2];
        _fftSin = new float[_quarter / 2];
        for (int j = 0; j < _quarter / 2; j++)
        {
            double a = 2.0 * Math.PI * j / _quarter;
            _fftCos[j] = (float)Math.Cos(a);
            _fftSin[j] = (float)Math.Sin(a);
        }

        int bits = int.TrailingZeroCount(_quarter);
        _bitReverse = new int[_quarter];
        for (int i = 0; i < _quarter; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _bitReverse[i] = r;
        }

        _re = new float[_quarter];
        _im = new float[_quarter];
        _dct = new float[_half];
    }

    /// <summary>Transforms N/2 coefficients into N time samples (spec scaling, 2/N).</summary>
    public void Compute(ReadOnlySpan<float> spectrum, Span<float> output)
    {
        int half = _half;
        int quarter = _quarter;
        float[] re = _re;
        float[] im = _im;

        // Pre-twiddle: pair the even coefficients from the front with the odd ones from the back.
        for (int k = 0; k < quarter; k++)
        {
            float a = spectrum[2 * k];
            float b = spectrum[half - 1 - 2 * k];
            float c = _preCos[k];
            float s = _preSin[k];
            int dest = _bitReverse[k];
            re[dest] = a * c + b * s;
            im[dest] = b * c - a * s;
        }

        Fft(re, im);

        // Post-twiddle and unfold into the DCT-IV.
        float[] dct = _dct;
        for (int k = 0; k < quarter; k++)
        {
            float c = _postCos[k];
            float s = _postSin[k];
            float ur = re[k] * c + im[k] * s;
            float ui = im[k] * c - re[k] * s;
            dct[2 * k] = ur;
            dct[half - 1 - 2 * k] = -ui;
        }

        // The IMDCT is the DCT-IV shifted by N/4 and extended by its symmetries.
        float scale = _scale;
        int n4 = quarter;
        int n34 = 3 * quarter;
        for (int n = 0; n < n4; n++)
        {
            output[n] = dct[n + n4] * scale;
        }

        for (int n = n4; n < n34; n++)
        {
            output[n] = -dct[n34 - 1 - n] * scale;
        }

        for (int n = n34; n < _n; n++)
        {
            output[n] = -dct[n - n34] * scale;
        }
    }

    /// <summary>In-place forward FFT of bit-reversed input.</summary>
    private void Fft(float[] re, float[] im)
    {
        int n = _quarter;
        for (int size = 2; size <= n; size <<= 1)
        {
            int halfSize = size >> 1;
            int twiddleStep = n / size;
            for (int start = 0; start < n; start += size)
            {
                int t = 0;
                for (int j = 0; j < halfSize; j++, t += twiddleStep)
                {
                    float wr = _fftCos[t];
                    float wi = -_fftSin[t];
                    int a = start + j;
                    int b = a + halfSize;
                    float br = re[b] * wr - im[b] * wi;
                    float bi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - br;
                    im[b] = im[a] - bi;
                    re[a] += br;
                    im[a] += bi;
                }
            }
        }
    }
}
