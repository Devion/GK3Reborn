using GK3Reborn.Content;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ini;

namespace GK3Reborn.Game;

/// <summary>
/// <c>INVENTORYSPRITES.TXT</c> — the picture that belongs to each thing in the bag.
/// </summary>
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
