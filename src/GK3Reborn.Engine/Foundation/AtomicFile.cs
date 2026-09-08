namespace GK3Reborn.Foundation;

/// <summary>
/// Writes files so that a crash cannot leave a half-written one behind.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes text to <paramref name="path"/>, replacing it atomically.</summary>
    /// <param name="path">Destination path. Its directory is created if needed.</param>
    /// <param name="contents">Text to write.</param>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Writes bytes to <paramref name="path"/>, replacing it atomically.</summary>
    /// <param name="path">Destination path. Its directory is created if needed.</param>
    /// <param name="contents">Bytes to write.</param>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }
}
