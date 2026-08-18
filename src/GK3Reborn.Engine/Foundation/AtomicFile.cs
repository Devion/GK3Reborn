namespace GK3Reborn.Foundation;

/// <summary>
/// Writes files so that a crash cannot leave a half-written one behind.
/// </summary>
/// <remarks>
/// Manifests, saves and authoring documents are all read back by tools and by the
/// game. A truncated one is worse than a missing one: it parses far enough to look
/// real. Writing to a temporary file and moving it into place means a reader sees
/// either the previous content or the new content, never a mixture.
/// </remarks>
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
}
