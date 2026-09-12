// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Text;
using GK3Reborn.Content;

namespace GK3Reborn.Game.Sidney;

/// <summary>One line of a search result, once its markup has been read.</summary>
/// <param name="Text">What it says.</param>
/// <param name="Heading">Whether it is a heading rather than body text.</param>
/// <param name="Rule">Whether it is a horizontal rule, in which case the text is empty.</param>
/// <param name="Link">The page it leads to, or null.</param>
public sealed record SearchLine(string Text, bool Heading = false, bool Rule = false, string? Link = null);

/// <summary>A page of Sidney's encyclopedia.</summary>
/// <param name="Id">Its file name, which is how links refer to it.</param>
/// <param name="Title">Its title.</param>
/// <param name="Lines">Its content, in order.</param>
public sealed record SearchPage(string Id, string Title, IReadOnlyList<SearchLine> Lines);

/// <summary>
/// A line Grace says the first time she reads one of the encyclopedia's pages, and what
/// has to be true for her to say it.
/// </summary>
/// <param name="Plates">The dialogue licence plates, in order.</param>
/// <param name="Flag">The flag set once it has been said, which is also what stops it twice.</param>
/// <param name="Requires">Flags that must hold, each optionally prefixed <c>!</c> for must not.</param>
/// <param name="LeastStep">The earliest verse of Le Serpent Rouge it fits, or -1 for any.</param>
/// <param name="MostStep">The latest verse it fits, inclusive, or -1 for any.</param>
public sealed record SearchRemark(
    IReadOnlyList<string> Plates,
    string Flag,
    IReadOnlyList<string> Requires,
    int LeastStep,
    int MostStep);

/// <summary>
/// Sidney's search: 391 pages of encyclopedia and the words that reach them.
/// </summary>
public sealed class SidneySearch
{
    private readonly Dictionary<string, string> _subjects;
    private readonly Dictionary<string, SearchRemark> _remarks;
    private readonly Func<string, string?> _pages;

    private SidneySearch(
        Dictionary<string, string> subjects,
        Dictionary<string, SearchRemark> remarks,
        Func<string, string?> pages)
    {
        _subjects = subjects;
        _remarks = remarks;
        _pages = pages;
    }

    /// <summary>Nothing to search, for a run with no game data.</summary>
    public static SidneySearch Empty { get; } = new([], [], _ => null);

    /// <summary>How many spellings reach a page.</summary>
    public int Count => _subjects.Count;

    /// <summary>Reads the subject index out of the archives.</summary>
    /// <param name="archives">The game's data.</param>
    /// <returns>The search.</returns>
    public static SidneySearch Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return new SidneySearch(
            Index(archives.ReadText("SIDSEARCH.TXT")),
            Remarks(archives.ReadText("SIDNEYDIALOG.TXT")),
            name => archives.ReadText(name));
    }

    /// <summary>Reads the index from a string, for tests.</summary>
    /// <param name="index">The contents of <c>SIDSEARCH.TXT</c>.</param>
    /// <param name="pages">Where a page's markup comes from.</param>
    /// <param name="dialog">The contents of <c>SIDNEYDIALOG.TXT</c>, or null for none.</param>
    /// <returns>The search.</returns>
    public static SidneySearch From(
        string index, Func<string, string?> pages, string? dialog = null) =>
        new(Index(index), Remarks(dialog), pages);

    /// <summary>
    /// What Grace says on reading a page, when the file names one.
    /// </summary>
    /// <param name="page">The page's name without its extension.</param>
    /// <returns>The remark, or null when that page has none.</returns>
    public SearchRemark? RemarkOn(string page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return _remarks.GetValueOrDefault(page);
    }

    /// <summary>
    /// Looks something up.
    /// </summary>
    /// <param name="typed">What the player typed.</param>
    /// <returns>The page, or null when nothing matches.</returns>
    public SearchPage? Look(string? typed)
    {
        if (typed is not { Length: > 0 })
        {
            return null;
        }

        string wanted = typed.Trim();

        if (!_subjects.TryGetValue(wanted, out string? file))
        {
            return null;
        }

        return Read(file);
    }

    /// <summary>Reads one page by its file name, which is how links name them.</summary>
    /// <param name="file">The page's file name.</param>
    /// <returns>The page, or null when the archives do not have it.</returns>
    public SearchPage? Read(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (_pages(file) is not { Length: > 0 } markup)
        {
            return null;
        }

        return Parse(file, markup);
    }

    /// <summary>Every spelling that finds something, for a hint or a test.</summary>
    public IReadOnlyList<string> Subjects => [.. _subjects.Keys.OrderBy(s => s, StringComparer.Ordinal)];

    /// <summary>
    /// Reads <c>SIDNEYDIALOG.TXT</c>: which pages Grace has something to say about.
    /// </summary>
    private static Dictionary<string, SearchRemark> Remarks(string? text)
    {
        var remarks = new Dictionary<string, SearchRemark>(StringComparer.OrdinalIgnoreCase);

        if (text is not { Length: > 0 })
        {
            return remarks;
        }

        string? page = null;
        List<string> plates = [];
        string flag = string.Empty;
        List<string> requires = [];
        int least = -1;
        int most = -1;

        void Flush()
        {
            if (page is { Length: > 0 } && plates.Count > 0 && flag.Length > 0)
            {
                remarks[page] = new SearchRemark([.. plates], flag, [.. requires], least, most);
            }

            plates = [];
            flag = string.Empty;
            requires = [];
            least = -1;
            most = -1;
        }

        foreach (string raw in text.Split('\n'))
        {
            // Every one of these lines may carry a trailing comment, and half of them do.
            string line = raw.Split("//")[0].Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                Flush();

                int close = line.IndexOf(']');
                page = close > 1 ? PageKey(line[1..close].Trim()) : null;

                continue;
            }

            if (page is null || line.IndexOf('=') is not (> 0 and { } equals))
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();

            switch (key.ToUpperInvariant())
            {
                case "ME":
                    plates = [.. value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)];
                    break;

                case "FLAG":
                    flag = value;
                    break;

                case "FLAGS":
                    requires = [.. value.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0)];
                    break;

                case "LSR":
                    least = Number(value);
                    break;

                case "LSRMAX":
                    most = Number(value);
                    break;

                default:
                    break;
            }
        }

        Flush();

        return remarks;
    }

    private static int Number(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : -1;

    /// <summary>A page's name without its path or any of its extensions.</summary>
    /// <param name="file">The file name, as a section or a link spells it.</param>
    /// <returns>The name.</returns>
    public static string PageKey(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        string name = file;

        if (name.LastIndexOfAny(['/', '\\']) is var slash and >= 0)
        {
            name = name[(slash + 1)..];
        }

        // Some links carry two extensions, and HTM and HTML are both used.
        while (Path.GetExtension(name) is { Length: > 0 })
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        return name;
    }

    /// <summary>The spelling-to-page index.</summary>
    private static Dictionary<string, string> Index(string? text)
    {
        var subjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (text is not { Length: > 0 })
        {
            return subjects;
        }

        string? file = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                file = close > 1 ? line[1..close].Trim() : null;

                continue;
            }

            if (file is null || !line.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Commas only, and no trimming of inner spaces: the file says so, because a
            // subject may be several words.
            foreach (string spelling in line[5..].Split(','))
            {
                string key = spelling.Trim();

                if (key.Length > 0)
                {
                    subjects[key] = file;
                }
            }
        }

        return subjects;
    }

    /// <summary>Turns a page's markup into lines the interface can draw.</summary>
    private static SearchPage Parse(string file, string markup)
    {
        string title = Between(markup, "<TITLE>", "</TITLE>")?.Trim() ?? file;

        List<SearchLine> lines = [];
        var run = new StringBuilder();
        string? link = null;
        bool heading = false;

        void Flush()
        {
            string text = Collapse(run.ToString());
            run.Clear();

            if (text.Length > 0)
            {
                lines.Add(new SearchLine(text, heading, Rule: false, link));
            }

            link = null;
            heading = false;
        }

        for (int i = 0; i < markup.Length; i++)
        {
            if (markup[i] != '<')
            {
                run.Append(markup[i]);

                continue;
            }

            int close = markup.IndexOf('>', i);

            if (close < 0)
            {
                break;
            }

            string tag = markup[(i + 1)..close];
            i = close;

            string name = tag.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                is [string first, ..] ? first.ToUpperInvariant() : string.Empty;

            switch (name)
            {
                case "P":
                case "BR":
                case "/P":
                    Flush();
                    break;

                case "HR":
                    Flush();
                    lines.Add(new SearchLine(string.Empty, Rule: true));
                    break;

                case "A":
                    // The page a link leads to, which is a file name in the same set.
                    Flush();
                    link = Between(tag, "HREF=\"", "\"") ?? Between(tag, "href=\"", "\"");
                    break;

                case "/A":
                    Flush();
                    break;

                case "FONT":
                    // The only thing these pages use a font size for is a heading.
                    if (tag.Contains("+2", StringComparison.Ordinal) ||
                        tag.Contains("+3", StringComparison.Ordinal))
                    {
                        Flush();
                        heading = true;
                    }

                    break;

                case "/FONT":
                    if (heading)
                    {
                        Flush();
                    }

                    break;

                default:
                    // Everything else — HTML, HEAD, BODY, I, B — changes nothing this
                    // interface can show, and dropping it is better than printing it.
                    break;
            }
        }

        Flush();

        // The title is repeated as the first heading in every one of these pages, so the
        // one in the document is enough and the tag's copy is not shown twice.
        return new SearchPage(file, title, lines);
    }

    private static string? Between(string text, string open, string close)
    {
        int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);

        return end < 0 ? null : text[start..end];
    }

    /// <summary>One space between words, and none at either end.</summary>
    private static string Collapse(string text)
    {
        var built = new StringBuilder(text.Length);
        bool space = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = built.Length > 0;

                continue;
            }

            if (space)
            {
                built.Append(' ');
                space = false;
            }

            built.Append(c);
        }

        return built.ToString();
    }
}
