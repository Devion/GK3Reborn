namespace GK3Reborn.Formats.Rebarn;

/// <summary>
/// CRC-32, so a pack can say whether an entry survived the disk.
/// </summary>
/// <remarks>
/// The ordinary IEEE polynomial, the one zip and PNG use. It is here rather than taken
/// from <c>System.IO.Hashing</c> because that is a package this project does not otherwise
/// need, and this is thirty lines. It guards a pack's contents; the index has its own
/// checksum, in the header, and that one is checked on every open.
/// </remarks>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint value = i;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB8_8320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    /// <summary>Computes the CRC-32 of a block of bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>Their CRC-32.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Continue(0xFFFF_FFFFu, data) ^ 0xFFFF_FFFFu;

    /// <summary>Starts a running CRC-32.</summary>
    /// <returns>The state a first <see cref="Continue"/> call takes.</returns>
    public static uint Begin() => 0xFFFF_FFFFu;

    /// <summary>Folds more bytes into a running CRC-32.</summary>
    /// <param name="state">The state so far, from <see cref="Begin"/>.</param>
    /// <param name="data">The next bytes.</param>
    /// <returns>The new state.</returns>
    public static uint Continue(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    /// <summary>Finishes a running CRC-32.</summary>
    /// <param name="state">The state after the last <see cref="Continue"/>.</param>
    /// <returns>The CRC-32.</returns>
    public static uint End(uint state) => state ^ 0xFFFF_FFFFu;
}
