using GK3Reborn.Content;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game;

/// <summary>
/// <c>INVENTORYSPRITES.TXT</c> — the picture that belongs to each thing in the bag.
/// </summary>
/// <remarks>
/// <para>
/// One unnamed section of 133 lines, each naming an item and the <em>stem</em> of its
/// art rather than a file: <c>CANDY = candy</c>, <c>GILT_GLOVE = Gauntlet</c>. What is on
/// disk is that stem with a number after it, and the number says which size — <c>3</c> is
/// the pointer-sized icon, <c>9</c> the one the original's inventory screen lists, and
/// <c>6</c> the item held up close. Without this file there is no way from
/// <c>GILT_GLOVE</c> to <c>GAUNTLET9.BMP</c>: the stems are what the artists called
/// things, and a third of them share no letters with the item's name.
/// </para>
/// <para>
/// <b>The list picture is the one worth having.</b> It is 94 by 94 and comes with its own
/// transparency in a second file, so it sits on a panel as a cut-out object; the icon at
/// <c>3</c> is 30 by 32 with nothing but a black square behind it. That is also the
/// original's own choice for a list of items, which is what this is drawn beside.
/// </para>
/// <para>
/// <b>Names are tried two ways.</b> Most stems take the number straight —
/// <c>CANDY9.BMP</c> — but a handful put an underscore in first, <c>MOSELYPRINT_9.BMP</c>,
/// and there is no rule that tells the two apart. The original tries both in this order
/// and so does this.
/// </para>
/// </remarks>
public sealed class InventoryArt
{
    private readonly Dictionary<string, string> _stems = new(StringComparer.OrdinalIgnoreCase);

    private InventoryArt()
    {
    }

    /// <summary>How many items the file gave a picture.</summary>
    public int Count => _stems.Count;

    /// <summary>Reads the file out of the archives.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The table, empty when there is no such file.</returns>
    public static InventoryArt Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return archives.ReadText("INVENTORYSPRITES.TXT") is { } text ? Parse(text) : new InventoryArt();
    }

    /// <summary>Reads the file's text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>The table.</returns>
    /// <remarks>
    /// The one file in the game that writes its comments <c>; //</c> rather than <c>//</c>,
    /// so the half the reader does not recognise arrives as a line whose key is a
    /// semicolon. Dropped here rather than taught to the reader, which is what G-Engine
    /// does with it too.
    /// </remarks>
    public static InventoryArt Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var art = new InventoryArt();

        foreach (IniLine line in IniDocument.Parse(text, "INVENTORYSPRITES.TXT").LinesOf(string.Empty))
        {
            if (line.Head is { Key: { Length: > 0 } item, Value: { Length: > 0 } stem } &&
                item[0] != ';')
            {
                art._stems[item] = stem;
            }
        }

        return art;
    }

    /// <summary>What an item's art is called, before a size is put on the end.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The stem, or null when the file does not name the item.</returns>
    public string? StemOf(string item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return _stems.GetValueOrDefault(item.Trim());
    }

    /// <summary>What an item's list picture may be called, in the order to try.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The file names, or nothing when the file does not name the item.</returns>
    public IReadOnlyList<string> IconNames(string item) =>
        StemOf(item) is { } stem ? [stem + "9.BMP", stem + "_9.BMP"] : [];

    /// <summary>What an item's close-up may be called, in the order to try.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The file names, or nothing when the file does not name the item.</returns>
    /// <remarks>
    /// <c>6</c> is the picture of the thing itself rather than an icon of it — the book of
    /// the immortals is 606 by 314 and readable, where its <c>9</c> is a 94-pixel square
    /// that could be any piece of paper in the game. Most stems take the number straight,
    /// a handful spell it <c>6_ALPHA</c>, and <c>MOSELYPRINT_6_ALPHA.BMP</c> alone puts an
    /// underscore in first. G-Engine tries the three in this order and so does this.
    /// </remarks>
    public IReadOnlyList<string> CloseUpNames(string item) =>
        StemOf(item) is { } stem
            ? [stem + "6.BMP", stem + "6_ALPHA.BMP", stem + "_6_ALPHA.BMP"]
            : [];

    /// <summary>What the transparency for a picture is called.</summary>
    /// <param name="icon">The picture's file name.</param>
    /// <returns>The mask's file name.</returns>
    public static string MaskOf(string icon)
    {
        ArgumentNullException.ThrowIfNull(icon);

        int dot = icon.LastIndexOf('.');

        return string.Concat(dot > 0 ? icon[..dot] : icon, "_OP.BMP");
    }

    /// <summary>An item's list picture, with its transparency already applied.</summary>
    /// <param name="archives">Where the art is.</param>
    /// <param name="item">The item.</param>
    /// <returns>The picture, or null when the item has none.</returns>
    /// <remarks>
    /// <para>
    /// Twenty of the items the file names have no list picture at all — nineteen of them
    /// are the panels of Le Serpent Rouge, which are pages of a document rather than things
    /// carried — so a null here is ordinary and means the item is shown by its name alone.
    /// </para>
    /// <para>
    /// <b>The archives' own art, not the enhanced set.</b> Everywhere else an upscale is
    /// preferred, but this is drawn at a fraction of the size the original was painted at,
    /// and the enhanced set has no <c>9</c> pictures in it to prefer.
    /// </para>
    /// </remarks>
    public DecodedImage? Icon(GameArchives archives, string item)
    {
        ArgumentNullException.ThrowIfNull(archives);

        foreach (string name in IconNames(item))
        {
            if (archives.Read(name) is not { } bytes)
            {
                continue;
            }

            try
            {
                DecodedImage icon = BitmapDecoder.Decode(bytes, name);

                return archives.Read(MaskOf(name)) is { } mask
                    ? Masked(icon, BitmapDecoder.Decode(mask, MaskOf(name)))
                    : icon;
            }
            catch (FormatParseException)
            {
                // A picture that will not decode is an item drawn by its name, which is
                // what an item with no picture at all gets.
                return null;
            }
        }

        return null;
    }

    /// <summary>An item's close-up picture, the size it was painted at.</summary>
    /// <param name="archives">Where the art is.</param>
    /// <param name="item">The item.</param>
    /// <returns>The picture, or null when the item has none.</returns>
    /// <remarks>
    /// <para>
    /// No transparency is applied. The close-ups are whole pictures with their own
    /// backgrounds — a book open on a table, a passport lying flat — and there is no
    /// <c>6_OP</c> anywhere in the game to cut one out with.
    /// </para>
    /// <para>
    /// A null here is ordinary: an item nobody drew a close-up of is shown by its list
    /// picture instead, which is what the screen did for every item before this existed.
    /// </para>
    /// </remarks>
    public DecodedImage? CloseUp(GameArchives archives, string item)
    {
        ArgumentNullException.ThrowIfNull(archives);

        foreach (string name in CloseUpNames(item))
        {
            if (archives.Read(name) is not { } bytes)
            {
                continue;
            }

            try
            {
                return BitmapDecoder.Decode(bytes, name);
            }
            catch (FormatParseException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Puts a mask's brightness into a picture's transparency.</summary>
    /// <param name="icon">The picture.</param>
    /// <param name="mask">The mask, white where the picture is to be seen.</param>
    /// <returns>The picture, cut out.</returns>
    /// <remarks>
    /// The mask is a palettised greyscale bitmap whose colours carry the value — its own
    /// alpha is nought throughout — so the red channel is what is read, exactly as
    /// G-Engine's <c>ApplyAlphaChannel</c> does. A mask of a different size is ignored
    /// rather than stretched: none in the game is, and a stretched one would eat the edges
    /// of the thing it is cutting out.
    /// </remarks>
    private static DecodedImage Masked(DecodedImage icon, DecodedImage mask)
    {
        if (mask.Width != icon.Width || mask.Height != icon.Height)
        {
            return icon;
        }

        for (int i = 0; i < icon.Width * icon.Height; i++)
        {
            icon.Pixels[(i * 4) + 3] = mask.Pixels[i * 4];
        }

        return icon with { HasAlpha = true };
    }
}
