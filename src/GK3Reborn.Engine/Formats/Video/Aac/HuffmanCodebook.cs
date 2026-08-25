namespace GK3Reborn.Formats.Video.Aac;

/// <summary>
/// One AAC Huffman codebook prepared for fast decoding.
/// </summary>
/// <remarks>
/// The standard lists each book as (length, codeword, values) rows. Scanning those
/// rows per symbol, as the reference decoder does, costs tens of comparisons for
/// every pair of coefficients. Instead a ten-bit direct lookup resolves the common
/// short codes in one array read, and the rare longer codes (up to 19 bits in the
/// scalefactor book) fall through to a binary tree walk.
/// </remarks>
internal sealed class HuffmanCodebook
{
    private const int PrimaryBits = 10;

    private readonly int[] _primary;   // (length << 16) | row, or -1 when the code is longer than PrimaryBits
    private readonly int[] _tree;      // node*2+bit -> child index, or -(row+1) for a leaf, 0 for none
    private readonly int[] _values;    // row values, Dimension per row

    /// <summary>Values per codeword: 4 for the quad books, 2 for the pair books, 1 for scalefactors.</summary>
    public int Dimension { get; }

    /// <summary>True when the book stores magnitudes and the signs follow the codeword.</summary>
    public bool Unsigned { get; }

    public HuffmanCodebook(int[][] rows, int dimension, bool unsigned)
    {
        Dimension = dimension;
        Unsigned = unsigned;
        _values = new int[rows.Length * dimension];
        _primary = new int[1 << PrimaryBits];
        Array.Fill(_primary, -1);

        List<int> tree = [0, 0];
        for (int row = 0; row < rows.Length; row++)
        {
            int[] r = rows[row];
            int length = r[0];
            int code = r[1];
            for (int i = 0; i < dimension; i++)
            {
                _values[row * dimension + i] = r[2 + i];
            }

            if (length <= PrimaryBits)
            {
                int first = code << (PrimaryBits - length);
                int count = 1 << (PrimaryBits - length);
                Array.Fill(_primary, (length << 16) | row, first, count);
            }

            int node = 0;
            for (int bit = length - 1; bit >= 0; bit--)
            {
                int index = node * 2 + ((code >> bit) & 1);
                if (bit == 0)
                {
                    tree[index] = -(row + 1);
                }
                else
                {
                    if (tree[index] == 0)
                    {
                        tree[index] = tree.Count / 2;
                        tree.Add(0);
                        tree.Add(0);
                    }

                    node = tree[index];
                }
            }
        }

        _tree = [.. tree];
    }

    /// <summary>Decodes one codeword and returns its row index (use <see cref="Value"/> to read the row).</summary>
    public int Decode(ref AacBitReader reader)
    {
        int entry = _primary[(int)reader.Peek(PrimaryBits)];
        if (entry >= 0)
        {
            reader.Skip(entry >> 16);
            return entry & 0xFFFF;
        }

        int node = 0;
        for (int depth = 0; depth < 24; depth++)
        {
            int child = _tree[node * 2 + (int)reader.ReadBits(1)];
            if (child < 0)
            {
                return -child - 1;
            }

            if (child == 0)
            {
                break;
            }

            node = child;
        }

        throw new FormatParseException("AAC: invalid Huffman codeword");
    }

    /// <summary>Reads the <paramref name="index"/>th value of a decoded row.</summary>
    public int Value(int row, int index) => _values[row * Dimension + index];

    /// <summary>The spectral books 1..11 (index 0 is unused).</summary>
    public static readonly HuffmanCodebook[] Spectral = BuildSpectral();

    /// <summary>The scalefactor book; its value is the DPCM delta plus 60.</summary>
    public static readonly HuffmanCodebook ScaleFactor = new(AacCodebookTables.ScaleFactor, 1, false);

    private static HuffmanCodebook[] BuildSpectral()
    {
        // Books 1-4 code quads, 5-11 code pairs; 3, 4 and 7-11 carry magnitudes only.
        bool[] unsigned = [false, false, false, true, true, false, false, true, true, true, true, true];
        HuffmanCodebook[] books = new HuffmanCodebook[12];
        for (int i = 1; i <= 11; i++)
        {
            books[i] = new HuffmanCodebook(AacCodebookTables.Spectral[i], i < 5 ? 4 : 2, unsigned[i]);
        }

        return books;
    }
}
