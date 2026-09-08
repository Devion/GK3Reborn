using GK3Reborn.Formats.Video.H264;

namespace GK3Reborn.Formats.Video;

/// <summary>
/// Turns a decoded Y'CbCr picture into the RGBA a texture upload wants.
/// </summary>
public static class YuvConverter
{
    // 16.16 fixed-point coefficients for the two matrices, limited range then full.
    private static readonly int[] Ytab601 = BuildLuma(false);
    private static readonly int[] Ytab709 = Ytab601;
    private static readonly int[] YtabFull = BuildLuma(true);

    private static int[] BuildLuma(bool fullRange)
    {
        var table = new int[256];

        for (int i = 0; i < 256; i++)
        {
            table[i] = fullRange ? i << 16 : (int)Math.Round((i - 16) * 1.164383 * 65536);
        }

        return table;
    }

    /// <summary>Converts one decoded frame, cropped, into tightly packed RGBA rows.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="rgba">At least <c>Width * Height * 4</c> bytes.</param>
    public static void ToRgba(DecodedFrame frame, Span<byte> rgba)
    {
        ArgumentNullException.ThrowIfNull(frame);

        int width = frame.Width;
        int height = frame.Height;

        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("The RGBA buffer is too small for the frame.", nameof(rgba));
        }

        bool bt709 = frame.ColourMatrix == 1;
        bool full = frame.FullRange;
        int[] ytab = full ? YtabFull : bt709 ? Ytab709 : Ytab601;

        // Cr → R, Cb/Cr → G, Cb → B, scaled by 65536; limited-range chroma spans 224 codes.
        double scale = full ? 1.0 : 255.0 / 224.0;
        int crR = (int)Math.Round((bt709 ? 1.5748 : 1.402) * scale * 65536);
        int cbG = (int)Math.Round((bt709 ? 0.1873 : 0.344136) * scale * 65536);
        int crG = (int)Math.Round((bt709 ? 0.4681 : 0.714136) * scale * 65536);
        int cbB = (int)Math.Round((bt709 ? 1.8556 : 1.772) * scale * 65536);

        byte[] y = frame.Y;
        byte[] cb = frame.Cb;
        byte[] cr = frame.Cr;
        int stride = frame.Stride;
        int chromaStride = frame.ChromaStride;
        bool subsampled = frame.ChromaFormat == 1;
        bool mono = frame.ChromaFormat == 0;
        int cropLeft = frame.CropLeft;
        int cropTop = frame.CropTop;

        for (int row = 0; row < height; row++)
        {
            int yPos = (cropTop + row) * stride + cropLeft;
            int cRow = subsampled ? (cropTop + row) >> 1 : cropTop + row;
            int cPos = cRow * chromaStride + (subsampled ? cropLeft >> 1 : cropLeft);
            int outPos = row * width * 4;

            for (int col = 0; col < width; col++)
            {
                int luma = ytab[y[yPos + col]];
                int r;
                int g;
                int b;

                if (mono)
                {
                    r = g = b = luma;
                }
                else
                {
                    int c = subsampled ? cPos + (col >> 1) : cPos + col;
                    int u = cb[c] - 128;
                    int v = cr[c] - 128;
                    r = luma + crR * v;
                    g = luma - cbG * u - crG * v;
                    b = luma + cbB * u;
                }

                rgba[outPos] = Clip(r);
                rgba[outPos + 1] = Clip(g);
                rgba[outPos + 2] = Clip(b);
                rgba[outPos + 3] = 255;
                outPos += 4;
            }
        }
    }

    private static byte Clip(int fixed16)
    {
        int v = (fixed16 + 32768) >> 16;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
