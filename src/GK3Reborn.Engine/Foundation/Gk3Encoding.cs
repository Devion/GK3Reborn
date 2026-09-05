using System.Text;

namespace GK3Reborn.Foundation;

/// <summary>
/// The single-byte code pages GK3's text assets were authored in.
/// </summary>
/// <remarks>
/// <para>
/// Every text file in the game — the string table, Sidney's documents, the screen layouts,
/// the font definitions — is one byte a character in whatever code page the localisation
/// was made on. Decoding one as UTF-8 turns every accented character into a replacement
/// and can throw on a file that is otherwise perfectly valid; decoding French as Latin-1
/// is nearly right and wrong in the one place it matters, because Windows-1252 puts the
/// curly apostrophe at 0x92 and Latin-1 puts a control character there. <c>L’Empereur</c>
/// then arrives with a hole in it.
/// </para>
/// <para>
/// <b>Sierra's own eight are written out here rather than taken from the platform.</b> .NET
/// carries only UTF-8, UTF-16, ASCII and Latin-1 in the box, and three code pages are three
/// tables of 128 characters — smaller than a package reference, identical on every platform
/// the game runs on, and with no registration call at startup for anybody to forget. Only
/// the high half is tabulated: 0x00 to 0x7F is ASCII in all three.
/// </para>
/// <para>
/// <b>Anything else goes to the platform, and that is where the line is.</b> A localisation
/// outside Western Europe is not one byte a character — GBK, which Simplified Chinese uses,
/// is twenty-two thousand mappings in which a byte above 0x80 begins a pair. That is not a
/// table anybody hand-writes, and getting it wrong is not a visible failure: the text
/// decodes, into the wrong characters, silently. So the platform's own code-page provider
/// is registered on first use and asked for any page there is no table for. It ships with
/// .NET 10 and needs no package reference; it does need the registration call, which is
/// why the call lives here rather than at startup where somebody would move it.
/// </para>
/// <para>
/// A page the platform does not have either falls back to Windows-1252 rather than
/// throwing. It is the wrong text, but it is the wrong text in a game that started, which
/// is the same trade every other content layer here makes.
/// </para>
/// </remarks>
public static class Gk3Encoding
{
    /// <summary>
    /// Windows-1252, for English, French, German, Italian, Spanish and Portuguese.
    /// </summary>
    /// <remarks>
    /// Latin-1 with the C1 control block replaced by punctuation and a few letters. The
    /// eight positions Windows never assigned decode to U+FFFD, which is what any decoder
    /// does with them and what makes a wrongly-tagged file visible rather than silent.
    /// </remarks>
    private const string Latin1Supplement =
        "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021" +
        "\u02C6\u2030\u0160\u2039\u0152\uFFFD\u017D\uFFFD" +
        "\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014" +
        "\u02DC\u2122\u0161\u203A\u0153\uFFFD\u017E\u0178" +
        "\u00A0\u00A1\u00A2\u00A3\u00A4\u00A5\u00A6\u00A7" +
        "\u00A8\u00A9\u00AA\u00AB\u00AC\u00AD\u00AE\u00AF" +
        "\u00B0\u00B1\u00B2\u00B3\u00B4\u00B5\u00B6\u00B7" +
        "\u00B8\u00B9\u00BA\u00BB\u00BC\u00BD\u00BE\u00BF" +
        "\u00C0\u00C1\u00C2\u00C3\u00C4\u00C5\u00C6\u00C7" +
        "\u00C8\u00C9\u00CA\u00CB\u00CC\u00CD\u00CE\u00CF" +
        "\u00D0\u00D1\u00D2\u00D3\u00D4\u00D5\u00D6\u00D7" +
        "\u00D8\u00D9\u00DA\u00DB\u00DC\u00DD\u00DE\u00DF" +
        "\u00E0\u00E1\u00E2\u00E3\u00E4\u00E5\u00E6\u00E7" +
        "\u00E8\u00E9\u00EA\u00EB\u00EC\u00ED\u00EE\u00EF" +
        "\u00F0\u00F1\u00F2\u00F3\u00F4\u00F5\u00F6\u00F7" +
        "\u00F8\u00F9\u00FA\u00FB\u00FC\u00FD\u00FE\u00FF";

    /// <summary>Windows-1250, for Polish.</summary>
    private const string CentralEuropeanSupplement =
        "\u20AC\uFFFD\u201A\uFFFD\u201E\u2026\u2020\u2021" +
        "\uFFFD\u2030\u0160\u2039\u015A\u0164\u017D\u0179" +
        "\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014" +
        "\uFFFD\u2122\u0161\u203A\u015B\u0165\u017E\u017A" +
        "\u00A0\u02C7\u02D8\u0141\u00A4\u0104\u00A6\u00A7" +
        "\u00A8\u00A9\u015E\u00AB\u00AC\u00AD\u00AE\u017B" +
        "\u00B0\u00B1\u02DB\u0142\u00B4\u00B5\u00B6\u00B7" +
        "\u00B8\u0105\u015F\u00BB\u013D\u02DD\u013E\u017C" +
        "\u0154\u00C1\u00C2\u0102\u00C4\u0139\u0106\u00C7" +
        "\u010C\u00C9\u0118\u00CB\u011A\u00CD\u00CE\u010E" +
        "\u0110\u0143\u0147\u00D3\u00D4\u0150\u00D6\u00D7" +
        "\u0158\u016E\u00DA\u0170\u00DC\u00DD\u0162\u00DF" +
        "\u0155\u00E1\u00E2\u0103\u00E4\u013A\u0107\u00E7" +
        "\u010D\u00E9\u0119\u00EB\u011B\u00ED\u00EE\u010F" +
        "\u0111\u0144\u0148\u00F3\u00F4\u0151\u00F6\u00F7" +
        "\u0159\u016F\u00FA\u0171\u00FC\u00FD\u0163\u02D9";

    /// <summary>Windows-1251, for Russian.</summary>
    private const string CyrillicSupplement =
        "\u0402\u0403\u201A\u0453\u201E\u2026\u2020\u2021" +
        "\u20AC\u2030\u0409\u2039\u040A\u040C\u040B\u040F" +
        "\u0452\u2018\u2019\u201C\u201D\u2022\u2013\u2014" +
        "\uFFFD\u2122\u0459\u203A\u045A\u045C\u045B\u045F" +
        "\u00A0\u040E\u045E\u0408\u00A4\u0490\u00A6\u00A7" +
        "\u0401\u00A9\u0404\u00AB\u00AC\u00AD\u00AE\u0407" +
        "\u00B0\u00B1\u0406\u0456\u0491\u00B5\u00B6\u00B7" +
        "\u0451\u2116\u0454\u00BB\u0458\u0405\u0455\u0457" +
        "\u0410\u0411\u0412\u0413\u0414\u0415\u0416\u0417" +
        "\u0418\u0419\u041A\u041B\u041C\u041D\u041E\u041F" +
        "\u0420\u0421\u0422\u0423\u0424\u0425\u0426\u0427" +
        "\u0428\u0429\u042A\u042B\u042C\u042D\u042E\u042F" +
        "\u0430\u0431\u0432\u0433\u0434\u0435\u0436\u0437" +
        "\u0438\u0439\u043A\u043B\u043C\u043D\u043E\u043F" +
        "\u0440\u0441\u0442\u0443\u0444\u0445\u0446\u0447" +
        "\u0448\u0449\u044A\u044B\u044C\u044D\u044E\u044F";

    /// <summary>Decodes bytes in a code page.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="codePage">
    /// 1250, 1251 or 1252, which are tabulated here; or any other the platform has, such as
    /// 936 for Simplified Chinese. One nobody has is read as 1252.
    /// </param>
    /// <returns>The text.</returns>
    public static string GetString(ReadOnlySpan<byte> bytes, int codePage)
    {
        if (Elsewhere(codePage) is { } platform)
        {
            return platform.GetString(bytes);
        }

        string high = Supplement(codePage);
        var text = new StringBuilder(bytes.Length);

        foreach (byte b in bytes)
        {
            text.Append(b < 0x80 ? (char)b : high[b - 0x80]);
        }

        return text.ToString();
    }

    /// <summary>Encodes text back into a code page.</summary>
    /// <param name="text">The text.</param>
    /// <param name="codePage">1250, 1251, 1252, or any the platform has.</param>
    /// <returns>The bytes.</returns>
    /// <remarks>
    /// For writing a file the 1999 game will read back — a save's comment, an extracted
    /// asset put back into <c>overrides/</c>. A character the code page has no byte for
    /// becomes a question mark, which is what every single-byte encoder does and what makes
    /// the loss visible in the file rather than at the point somebody reads it.
    /// </remarks>
    public static byte[] GetBytes(string text, int codePage)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Elsewhere(codePage) is { } platform)
        {
            return platform.GetBytes(text);
        }

        string high = Supplement(codePage);
        byte[] bytes = new byte[text.Length];

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c < 0x80)
            {
                bytes[i] = (byte)c;
                continue;
            }

            // Never U+FFFD: it stands for the positions the code page leaves unassigned,
            // and letting it match one would turn a character that failed to decode into a
            // byte that decodes to something else next time round.
            int at = c == '�' ? -1 : high.IndexOf(c, StringComparison.Ordinal);
            bytes[i] = at >= 0 ? (byte)(at + 0x80) : (byte)'?';
        }

        return bytes;
    }

    private static string Supplement(int codePage) => codePage switch
    {
        1250 => CentralEuropeanSupplement,
        1251 => CyrillicSupplement,
        _ => Latin1Supplement,
    };

    /// <summary>
    /// The platform's encoding for a code page there is no table here for.
    /// </summary>
    /// <param name="codePage">The page.</param>
    /// <returns>The encoding, or null to use a table.</returns>
    /// <remarks>
    /// <para>
    /// Null for the three that are tabulated, so the ordinary case — every localisation
    /// Sierra published — costs one integer comparison and reaches no provider at all.
    /// </para>
    /// <para>
    /// Null again when the platform has no such page: the fallback is then Windows-1252,
    /// which is the wrong text in a game that started rather than an exception at the first
    /// line of dialogue.
    /// </para>
    /// </remarks>
    private static Encoding? Elsewhere(int codePage)
    {
        if (codePage is 1250 or 1251 or 1252)
        {
            return null;
        }

        // Registered here rather than at startup, because this is the only thing that needs
        // it and a registration call somewhere else is one somebody moves, reorders or
        // deletes without ever seeing what it was for. It is idempotent and this runs once.
        if (!_registered)
        {
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            _registered = true;
        }

        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static volatile bool _registered;
}
