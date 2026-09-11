// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>One of the things Sidney's analyze screen can be asked to do.</summary>
public enum SidneyAction
{
    /// <summary>Say what this file is.</summary>
    Analyse,

    /// <summary>Pull the raised letters out of a parchment.</summary>
    ExtractAnomalies,

    /// <summary>Look the text up and find what is inserted in it.</summary>
    AnalyseText,

    /// <summary>Turn it into English.</summary>
    Translate,

    /// <summary>Find the shape hidden in an image.</summary>
    ViewGeometry,

    /// <summary>Turn a symbolic device and read it again.</summary>
    RotateShape,

    /// <summary>Enlarge part of an image until the words in it can be read.</summary>
    ZoomAndClarify,

    /// <summary>Mark places on the map.</summary>
    EnterPoints,

    /// <summary>Take the marks off again.</summary>
    ClearPoints,

    /// <summary>Take back the place marked last, which the original cannot do.</summary>
    UndoPoint,

    /// <summary>Rule the map into squares.</summary>
    DrawGrid,

    /// <summary>Take the ruling off again.</summary>
    EraseGrid,

    /// <summary>Lay one of the saved shapes over the map.</summary>
    UseShape,

    /// <summary>Take it off again.</summary>
    EraseShape,
}

/// <summary>
/// Sidney, running.
/// </summary>
public sealed class SidneyMachine
{
    private SidneyLibrary _library;
    private readonly GameState _state;
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

    private SidneyTranslator? _translator;
    private SidneyWords? _words;
    private string _language = Content.GameLanguage.Default.Code;

    /// <summary>The rulings the game offers, in the order it lists them.</summary>
    private static readonly int[] GridSizes = [2, 4, 8, 12, 16];
    private readonly SidneyMap _map = new();
    private SavedMap? _mapWas;
    private readonly Queue<SidneySpeech> _cues = new();

    /// <summary>
    /// The score sheet, for what each step of Le Serpent Rouge is worth. Null awards
    /// nothing, which is what a test wants and what nothing else does.
    /// </summary>
    public ScoreEvents? Scores { get; init; }

    /// <summary>Creates the machine.</summary>
    /// <param name="library">The game's own Sidney text.</param>
    /// <param name="state">The story, which owns which files exist.</param>
    public SidneyMachine(SidneyLibrary library, GameState state)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(state);

        _library = library;
        _state = state;
    }

    /// <summary>Which of Sidney's screens is showing.</summary>
    public SidneyScreen Screen { get; set; } = SidneyScreen.Main;

    /// <summary>The encyclopedia, when there is one.</summary>
    public SidneySearch Search { get; set; } = SidneySearch.Empty;

    /// <summary>What the player has typed into the search box.</summary>
    public string Typed { get; set; } = string.Empty;

    /// <summary>The page the search screen is showing, or null.</summary>
    public SearchPage? Page { get; private set; }

    /// <summary>The suspect whose file is open, or null.</summary>
    public SidneySuspect? Suspect { get; private set; }

    /// <summary>The identity the make-ID screen has printed, or null.</summary>
    public SidneyIdentity? Identity { get; private set; }

    /// <summary>
    /// The map, its marks and whatever has been laid over it.
    /// </summary>
    public SidneyMap Map
    {
        get
        {
            if (!ReferenceEquals(_mapWas, _state.SidneyMap))
            {
                _mapWas = _state.SidneyMap;

                _map.Restore(
                    _state.SidneyMap.Marks.Select(Place),
                    _state.SidneyMap.Figures.Select(Figure),
                    _state.SidneyMap.Grid);
                _map.RestoreGrid(_state.SidneyMap.Grid, _state.SidneyMap.GridFixed);
            }

            return _map;
        }
    }

    /// <summary>Writes the map back to the story, which is what a save records.</summary>
    private void RememberMap()
    {
        var kept = new SavedMap(
            [
                .. _map.Points.Select(point => string.Create(
                    System.Globalization.CultureInfo.InvariantCulture, $"{point.X},{point.Y}")),
            ],
            [
                .. _map.Laid.Select(laid => new SavedFigure(
                    SidneyMap.NameOf(laid.Shape),
                    laid.At.X,
                    laid.At.Y,
                    laid.Size,
                    laid.Turn,
                    [
                        .. laid.Points.Select(point => string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"{point.X},{point.Y}")),
                    ],
                    laid.Fixed)),
            ],
            _map.GridInShape ? -_map.Grid : _map.Grid,
            _map.GridFixed);

        _state.SidneyMap = kept;
        _mapWas = kept;
    }

    /// <summary>One saved place, back as a point.</summary>
    private static System.Numerics.Vector2 Place(string mark) =>
        mark.Split(',') is [string across, string down] &&
        float.TryParse(across, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
        float.TryParse(down, System.Globalization.CultureInfo.InvariantCulture, out float y)
            ? new System.Numerics.Vector2(x, y)
            : System.Numerics.Vector2.Zero;

    /// <summary>One saved figure, back as a placement.</summary>
    private static LaidShape Figure(SavedFigure saved) =>
        new(
            Enum.TryParse(saved.Shape, ignoreCase: true, out MapShape shape) ? shape : MapShape.None,
            new System.Numerics.Vector2(saved.X, saved.Y),
            saved.Size,
            saved.Turn,
            Locked: saved.Fixed,
            [.. saved.Points.Select(Place)])
        {
            Fixed = saved.Fixed,
        };

    /// <summary>Whether The Site has been marked, so the map shows its label.</summary>
    public bool ShowsSite => _state.GetFlag("MarkedTheSite");

    /// <summary>Whether the red serpent has been marked, so the map shows it.</summary>
    public bool ShowsSerpent => _state.GetFlag("PlacedSerpent");

    /// <summary>Whether the analyze screen is waiting for a point to be marked.</summary>
    public bool Marking { get; private set; }

    /// <summary>The file the analyze screen has open, or null.</summary>
    public SidneyFile? Open { get; private set; }

    /// <summary>The message the mail screen has open, or null.</summary>
    public SidneyMail? Reading { get; set; }

    /// <summary>
    /// When it is, for the clock in the corner of the screen.
    /// </summary>
    public string Now
    {
        get
        {
            Timeblock when = _state.Timeblock;

            return Names.When(when.ToString()) is { Length: > 0 } said
                ? said
                : string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{when.Day}  {when.Hour}:00 {(when.IsAfternoon ? "PM" : "AM")}");
        }
    }

    /// <summary>
    /// Whether a message has been opened.
    /// </summary>
    /// <param name="mail">Which message.</param>
    /// <returns>True when it has been read.</returns>
    public bool HasRead(SidneyMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);

        return _state.GetFlag("SidneyRead:" + mail.Id);
    }

    /// <summary>Opens a message, and marks it read.</summary>
    /// <param name="mail">Which message, or null to close the one open.</param>
    public void ReadMail(SidneyMail? mail)
    {
        Reading = mail;

        if (mail is not null)
        {
            _state.SetFlag("SidneyRead:" + mail.Id);
        }
    }

    /// <summary>How many messages have not been opened yet.</summary>
    public int Unread => Mail().Count(m => !HasRead(m));

    /// <summary>
    /// Grace's inbox as it stands at this point in the story.
    /// </summary>
    /// <remarks>
    /// The file lists every message she will ever get, and the retail engine hands them
    /// over as the days go by: the first three from the start, the Temple of Solomon's
    /// divisions from the third noon, the analysis of the symbols from Serres from six
    /// that evening, and the one about the egg only once the egg has been found. Showing
    /// the whole file from the first morning gave away two of the third day's puzzles on
    /// the second, which is how it was reported.
    /// </remarks>
    /// <returns>The messages received so far, in the order the file lists them.</returns>
    public IReadOnlyList<SidneyMail> Mail()
    {
        Timeblock now = _state.Timeblock;

        return [.. _library.Mail().Where(m => m.Id.ToUpperInvariant() switch
        {
            "EMAIL4" => now >= new Timeblock(3, 12, IsAfternoon: true),
            "EMAIL5" => now >= new Timeblock(3, 6, IsAfternoon: true),
            "EMAIL6" => _state.GetFlag("Egg"),
            _ => true,
        })];
    }

    /// <summary>
    /// The people Sidney has a file on at this point in the story.
    /// </summary>
    /// <remarks>
    /// Eight from the start. Montreaux is added on the second afternoon, once Grace has
    /// met him; Mosely from five that day, and only if his print was lifted that morning
    /// — the retail engine's own two rules, from the function that fills the list — and
    /// his print is linked to him as he is added, the way it does. All ten from the first
    /// morning named two people the story had not yet, which is how it was reported.
    /// </remarks>
    /// <returns>The suspects so far, in the file's order.</returns>
    public IReadOnlyList<SidneySuspect> Suspects()
    {
        Timeblock now = _state.Timeblock;
        List<SidneySuspect> people = [];

        foreach (SidneySuspect person in _library.Suspects())
        {
            switch (person.Index)
            {
                case 9 when now < new Timeblock(2, 2, IsAfternoon: true):
                    continue;

                case 10 when now < new Timeblock(2, 5, IsAfternoon: true) ||
                             !_state.GetFlag("GotPMoselyPrint"):
                    continue;

                case 10:
                    if (SidneyFiles.For("MOSELYS_PRINT") is { } print)
                    {
                        _state.SetFlag(Link(person, print));
                    }

                    break;

                default:
                    break;
            }

            people.Add(person);
        }

        return people;
    }

    /// <summary>What the last operation said, or null.</summary>
    public SidneyResult? Showing { get; private set; }

    /// <summary>
    /// Takes the next thing waiting to happen outside the screen — a line said aloud, a
    /// room left for, Sidney put away — so that it happens once, in order. Sidney's screens
    /// are text, but the retail engine has Grace speak over them and ends two timeblocks
    /// from them; the frame loop takes these one at a time, each once the last is over.
    /// </summary>
    /// <returns>The cue, or null when nothing is waiting.</returns>
    public SidneySpeech? TakeCue() => _cues.Count > 0 ? _cues.Dequeue() : null;

    /// <summary>Whether anything is waiting to happen outside the screen.</summary>
    public bool HasCues => _cues.Count > 0;

    private void Speak(string plate, int lines = 1) =>
        _cues.Enqueue(new SidneySpeech(SerpentRougeCue.Say, plate, lines));

    private void Cue(SerpentRougeOutcome outcome)
    {
        foreach (SidneySpeech cue in outcome.Cues ?? [])
        {
            _cues.Enqueue(cue);
        }
    }

    /// <summary>
    /// Opens one of the screens from the menu.
    /// </summary>
    /// <remarks>
    /// The one screen with a rule of its own is ADD DATA on the second morning: the retail
    /// engine has Grace say she has nothing to scan yet (<c>0264G2ZPF1</c>) instead of
    /// asking for input. The screen still opens here, with its own "nothing to scan".
    /// </remarks>
    /// <param name="screen">Which screen.</param>
    public void Show(SidneyScreen screen)
    {
        Screen = screen;
        OpenFile(null);

        if (screen == SidneyScreen.AddData &&
            _state.Timeblock == new Timeblock(2, 7, IsAfternoon: false))
        {
            Speak("0264G2ZPF1");
        }
    }

    /// <summary>
    /// The game's own text, for whatever draws this.
    /// </summary>
    public SidneyLibrary Library
    {
        get => _library;

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _library = value;
            _translator = null;
            _words = null;
        }
    }

    /// <summary>
    /// The string table, for what the player's own things are called.
    /// </summary>
    public GameStrings Names { get; set; } = GameStrings.None;

    /// <summary>
    /// The language the game is being played in, as an ISO 639-1 code.
    /// </summary>
    public string Language
    {
        get => _language;

        set
        {
            _language = value ?? Content.GameLanguage.Default.Code;
            _words = null;
        }
    }

    /// <summary>Everything the screens draw, in the language they are drawn in.</summary>
    public SidneyWords Words => _words ??= new SidneyWords(_library, _language);

    /// <summary>Every file that has been scanned in.</summary>
    public IReadOnlyList<SidneyFile> Files
    {
        get
        {
            List<SidneyFile> files = [];

            foreach (string item in _state.SidneyScans)
            {
                if (SidneyFiles.For(item) is { } file)
                {
                    files.Add(file with { Label = NameOf(item) });
                }
            }

            return files;
        }
    }

    /// <summary>
    /// What one of the player's things is called.
    /// </summary>
    /// <param name="item">Its noun, as the action files spell it.</param>
    /// <returns>The game's own name for it, or the tidied identifier.</returns>
    public string NameOf(string item) =>
        Names.Item(item) is { Length: > 0 } named ? named : SidneyFiles.Pretty(item);

    /// <summary>Whether an item may be put into the scanner.</summary>
    /// <param name="item">The inventory item.</param>
    /// <returns>True when it produces a file and has not already been scanned.</returns>
    public bool CanScan(string item) =>
        SidneyFiles.For(item) is { } file && !_state.HasSidneyFile(file.Id);

    /// <summary>
    /// Puts an item into the scanner.
    /// </summary>
    /// <param name="item">The inventory item.</param>
    /// <returns>What the machine says, or null when it will not take the item.</returns>
    public SidneyResult? Scan(string item)
    {
        if (SidneyFiles.For(item) is not { } file)
        {
            return null;
        }

        _state.AddSidneyFile(file.Id);
        _state.RecordSidneyScan(file.Item);

        Showing = new SidneyResult($"{file.Label} scanned.", Produced: file.Id);

        return Showing;
    }

    /// <summary>Opens a file on the analyze screen.</summary>
    /// <param name="file">The file.</param>
    public void OpenFile(SidneyFile? file)
    {
        Open = file;
        Showing = null;
    }

    /// <summary>Which operations the open file will answer.</summary>
    /// <returns>The actions, which is empty when nothing is open.</returns>
    public IReadOnlyList<SidneyAction> Available()
    {
        if (Open is not { } file)
        {
            return [];
        }

        List<SidneyAction> actions = [SidneyAction.Analyse];

        switch (file.Kind)
        {
            case SidneyKind.Parchment1:
                actions.Add(SidneyAction.ExtractAnomalies);
                actions.Add(SidneyAction.ViewGeometry);
                break;

            case SidneyKind.Parchment2:
                actions.Add(SidneyAction.AnalyseText);
                actions.Add(SidneyAction.ViewGeometry);
                actions.Add(SidneyAction.RotateShape);
                break;

            case SidneyKind.Poussin:
                actions.Add(SidneyAction.ViewGeometry);
                actions.Add(SidneyAction.ZoomAndClarify);
                break;

            case SidneyKind.Teniers:
                actions.Add(SidneyAction.ViewGeometry);
                break;

            // The map is the one file with a screen of its own rather than a chain of
            // notes: places are marked on it and the analysis measures what they make.
            case SidneyKind.Map:
                actions.Add(SidneyAction.EnterPoints);
                actions.Add(SidneyAction.UndoPoint);
                actions.Add(SidneyAction.ClearPoints);
                actions.Add(SidneyAction.DrawGrid);
                actions.Add(SidneyAction.EraseGrid);

                // A shape can only be laid once one has been found in a picture, which is
                // what makes the geometry analyses worth running.
                // USE SHAPE is not here: the figures that may be laid are drawn beside the
                // map as themselves, and a button that opens a list of their names is two
                // steps and a covered map to do what one look at that row does. What is
                // left is turning whichever was laid last.
                if (Map.Laid.Count > 0)
                {
                    actions.Add(SidneyAction.RotateShape);
                    actions.Add(SidneyAction.EraseShape);
                }

                break;

            case SidneyKind.Tape:
            case SidneyKind.Note:
                actions.Add(SidneyAction.Translate);
                break;

            default:
                break;
        }

        return actions;
    }

    /// <summary>Runs one of the operations on the open file.</summary>
    /// <param name="action">Which operation.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult Perform(SidneyAction action)
    {
        if (Open is not { } file)
        {
            Showing = new SidneyResult(Say("NoShapeNote"));

            return Showing;
        }

        Showing = action switch
        {
            SidneyAction.Analyse => Analysed(file),
            SidneyAction.ExtractAnomalies => Asked("ExtractParch1"),
            SidneyAction.AnalyseText => Asked("Text4Parch2", Progress("Text1Parch2", "Text2Parch2", "Text3Parch2")),
            SidneyAction.ViewGeometry => Finished(file.Kind switch
            {
                SidneyKind.Parchment1 => "GeometryParch1",
                SidneyKind.Parchment2 => "GeometryParch2",
                SidneyKind.Poussin => "GeometryPous",
                SidneyKind.Teniers => "GeometryTenier2",
                _ => "AnalyzeTemp",
            }),
            // Two different things under one menu item, which is what the original has:
            // on a parchment it turns the symbolic device and reads it again, and on the
            // map it turns the template laid over the country.
            SidneyAction.RotateShape => file.Kind == SidneyKind.Map
                ? Turned()
                : Finished("RotateParch2"),
            SidneyAction.ZoomAndClarify => Finished("ArcadiaAnalysis"),
            SidneyAction.Translate => Finished("AnalyzeSUM"),
            SidneyAction.EnterPoints => Marked(),
            SidneyAction.ClearPoints => Cleared(),
            SidneyAction.UndoPoint => Undone(),
            SidneyAction.DrawGrid => Ruled(),
            SidneyAction.EraseGrid => Unruled(),
            SidneyAction.EraseShape => Unshaped(),
            SidneyAction.UseShape => Choose(),
            _ => new SidneyResult(Say("NotImplemented")),
        };

        Record(file, action);

        return Showing;
    }

    /// <summary>
    /// Answers the question an operation asked.
    /// </summary>
    /// <param name="language">
    /// Which language the player suggested, as <c>ESIDNEY.TXT</c> keys it — <c>French</c>,
    /// <c>English</c>, <c>Latin</c> — rather than as the button spelled it.
    /// </param>
    /// <returns>What the machine says.</returns>
    public SidneyResult Answer(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        bool second = Open?.Kind == SidneyKind.Parchment2;
        bool french = string.Equals(language.Trim(), "French", StringComparison.OrdinalIgnoreCase);

        string key = language.Trim().ToUpperInvariant() switch
        {
            "FRENCH" => second ? "Parch2French" : "Parch1French",
            "LATIN" => "ParchLatin",
            _ => "ParchEnglish",
        };

        Showing = new SidneyResult(Say(key));

        if (french && Open is { } file)
        {
            // The one that gets somewhere. Recorded as a flag so the story can read it the
            // way it reads everything else.
            _state.SetFlag(Flag(file, SidneyAction.Translate));
            _done.Add(Flag(file, SidneyAction.Translate));
        }

        return Showing;
    }

    /// <summary>Whether an operation has already been run on a file.</summary>
    /// <param name="file">The file.</param>
    /// <param name="action">The operation.</param>
    /// <returns>True when it has.</returns>
    public bool HasDone(SidneyFile file, SidneyAction action)
    {
        ArgumentNullException.ThrowIfNull(file);

        return _done.Contains(Flag(file, action)) || _state.GetFlag(Flag(file, action));
    }

    /// <summary>Puts the machine back to its front screen.</summary>
    public void Home()
    {
        Menu = 0;
        Marking = false;
        Screen = SidneyScreen.Main;
        Showing = null;
        Reading = null;
        Page = null;
        Suspect = null;
        Appending = false;
    }

    /// <summary>The translate screen's own reading of the game's text.</summary>
    public SidneyTranslator Translator => _translator ??= new SidneyTranslator(_library);

    /// <summary>The file the translate screen has open, or null.</summary>
    public SidneyFile? Translating { get; private set; }

    /// <summary>
    /// What the player says that file is written in, or null.
    /// </summary>
    public string? From { get; set; }

    /// <summary>Whether the machine is waiting for a string to add to a sentence.</summary>
    public bool Appending { get; private set; }

    /// <summary>Opens a file on the translate screen.</summary>
    /// <param name="file">Which file, or null to close the one open.</param>
    public void OpenForTranslation(SidneyFile? file)
    {
        Translating = file;
        Showing = null;
        Appending = false;
        From = null;
    }

    /// <summary>Translates the open file out of the language chosen.</summary>
    /// <returns>What the machine says.</returns>
    public SidneyResult Translate()
    {
        Showing = Translator.Translate(Translating, From);
        Appending = false;

        if (Showing.Choices is { Count: > 0 } && Translating is { } file)
        {
            _state.SetFlag(Flag(file, SidneyAction.Translate));
            _done.Add(Flag(file, SidneyAction.Translate));
        }

        return Showing;
    }

    /// <summary>Says whether to add to an unfinished sentence.</summary>
    /// <param name="yes">True to be asked for a string.</param>
    public void Complete(bool yes)
    {
        Appending = yes;
        Typed = string.Empty;

        if (yes)
        {
            // The game's own name for having started the anagram, which two conditions ask
            // about before the sentence is finished.
            _state.SetFlag("StartArcadiaAnagram");
        }
    }

    /// <summary>Adds whatever has been typed to the unfinished sentence.</summary>
    /// <returns>What the machine says.</returns>
    public SidneyResult Append()
    {
        Showing = Translator.Append(Translating, Typed);

        if (Showing.Produced is { Length: > 0 } made)
        {
            // <b>The names the story asks about.</b> R25307A's timeblock will not end
            // without SavedArcadiaText, and three other conditions read ArcadiaComplete.
            // The machine's own SidneyText: name is kept beside them for the screen.
            _state.SetFlag("SidneyText:" + made);
            _state.SetFlag("SavedArcadiaText");
            _state.SetFlag("ArcadiaComplete");
            Appending = false;
        }

        return Showing;
    }

    /// <summary>Looks up whatever has been typed.</summary>
    /// <returns>What the machine says.</returns>
    public SidneyResult Look()
    {
        Page = Search.Look(Typed);

        Showing = new SidneyResult(
            Page is null ? Ask("NotFound", "Search Screen") : Page.Title);

        return Showing;
    }

    /// <summary>Follows a link out of the page being read.</summary>
    /// <param name="page">The page's file name.</param>
    public void Follow(string page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (Search.Read(page) is { } found)
        {
            Page = found;
            Typed = found.Title;
        }
    }

    /// <summary>Opens somebody's file on the suspects screen.</summary>
    /// <param name="suspect">Which of them.</param>
    public void OpenSuspect(SidneySuspect? suspect)
    {
        Suspect = suspect;
        Showing = null;
    }

    /// <summary>
    /// Whether the vehicle a suspect drives has been worked out yet.
    /// </summary>
    /// <param name="suspect">Which of them.</param>
    /// <returns>True once a licence plate has been linked to them.</returns>
    public bool KnowsVehicle(SidneySuspect suspect)
    {
        ArgumentNullException.ThrowIfNull(suspect);

        foreach (SidneyFile file in LinkedTo(suspect))
        {
            if (file.Kind == SidneyKind.Licence)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The files linked to a suspect.</summary>
    /// <param name="suspect">Which of them.</param>
    /// <returns>The files, in the order the store holds them.</returns>
    public IReadOnlyList<SidneyFile> LinkedTo(SidneySuspect suspect)
    {
        ArgumentNullException.ThrowIfNull(suspect);

        return [.. Files.Where(f => _state.GetFlag(Link(suspect, f)))];
    }

    /// <summary>
    /// Links a file to the open suspect.
    /// </summary>
    /// <param name="file">The file: a fingerprint, a licence.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult LinkToSuspect(SidneyFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (Suspect is not { } suspect)
        {
            Showing = new SidneyResult(Ask("NoSuspect"));

            return Showing;
        }

        if (_state.GetFlag(Link(suspect, file)))
        {
            Showing = new SidneyResult(Ask("AlreadyLinked"));

            return Showing;
        }

        bool print = file.Kind is SidneyKind.KnownPrint or SidneyKind.UnknownPrint;

        if (print &&
            LinkedTo(suspect).Any(f => f.Kind is SidneyKind.KnownPrint or SidneyKind.UnknownPrint))
        {
            Showing = new SidneyResult(Ask("ExistingFP"));

            return Showing;
        }

        if (file.Kind == SidneyKind.Licence &&
            LinkedTo(suspect).Any(f => f.Kind == SidneyKind.Licence))
        {
            Showing = new SidneyResult(Ask("ExistingID"));

            return Showing;
        }

        _state.SetFlag(Link(suspect, file));
        Showing = new SidneyResult($"{file.Label} linked to {suspect.Name}.");

        return Showing;
    }

    /// <summary>Takes a file off a suspect again.</summary>
    /// <param name="file">The file.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult UnlinkFromSuspect(SidneyFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (Suspect is not { } suspect)
        {
            Showing = new SidneyResult(Ask("NoSuspect"));

            return Showing;
        }

        _state.ClearFlag(Link(suspect, file));
        Showing = new SidneyResult($"{file.Label} un-linked.");

        return Showing;
    }

    /// <summary>
    /// Runs the fingerprint match against the open suspect.
    /// </summary>
    /// <returns>What the machine says.</returns>
    public SidneyResult MatchPrint()
    {
        if (Suspect is not { } suspect)
        {
            Showing = new SidneyResult(Ask("NoSuspect"));

            return Showing;
        }

        SidneyFile? print = LinkedTo(suspect)
            .FirstOrDefault(f => f.Kind is SidneyKind.KnownPrint or SidneyKind.UnknownPrint);

        if (print is null)
        {
            Showing = new SidneyResult(Ask("NoFingerprint"));

            return Showing;
        }

        string owner = print.Item;

        if (owner.StartsWith("GAB", StringComparison.OrdinalIgnoreCase))
        {
            Showing = new SidneyResult(Ask("GabesPrint"));

            return Showing;
        }

        if (owner.StartsWith("GRACE", StringComparison.OrdinalIgnoreCase))
        {
            Showing = new SidneyResult(Ask("GracesPrint"));

            return Showing;
        }

        bool matched = print.Kind == SidneyKind.KnownPrint && Belongs(owner, suspect);

        Showing = new SidneyResult(
            $"{Ask("MatchCompare")} {suspect.Name}\n\n" +
            (matched ? Ask("MatchFound") : Ask("MatchNone")));

        if (matched)
        {
            // <b>The flag the game's own scripts read.</b> "SidneyMatched:2" was written and
            // read by nothing; the story is waiting on Matched<i>Noun</i>, and setting
            // MatchedEstelle is what opens the T_LSR topic with her in the lobby and gives
            // Grace something to say over the LSR envelope. The four the scripts name —
            // Buthane, Buchelli, Estelle, Mosely — are spelt exactly this way.
            _state.SetFlag($"Matched{suspect.Noun}");
        }

        return Showing;
    }

    /// <summary>Whether a piece of evidence is this suspect's.</summary>
    /// <param name="item">The item the file was scanned from.</param>
    /// <param name="suspect">Who it is being tested against.</param>
    /// <returns>True when the item is named after them.</returns>
    private static bool Belongs(string item, SidneySuspect suspect)
    {
        if (suspect.Noun.Length == 0)
        {
            return false;
        }

        return Bare(item.Split('_')[0]).Equals(Bare(suspect.Noun), StringComparison.Ordinal);
    }

    /// <summary>A name with its possessive "s", if it has one, taken off.</summary>
    private static string Bare(string name)
    {
        string upper = name.ToUpperInvariant();

        return upper.Length > 3 && upper[^1] == 'S' ? upper[..^1] : upper;
    }

    /// <summary>Prints an identity card.</summary>
    /// <param name="identity">Which one.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult PrintIdentity(SidneyIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        Identity = identity;

        // A card is only ever needed the afternoon Gabriel calls on Montreaux as a
        // journalist; any other time the retail engine prints nothing and has whoever is
        // sitting there say they do not need a fake ID. Printing one regardless left a
        // card in the story that the story never asked for.
        if (_state.Timeblock != new Timeblock(2, 2, IsAfternoon: true))
        {
            Speak(string.Equals(_state.Ego, "GABRIEL", StringComparison.OrdinalIgnoreCase)
                ? "02O8G5FVU1"
                : "02O8G5FZ51");

            Showing = new SidneyResult(string.Empty);

            return Showing;
        }

        // Keyed on the row rather than on the job, because the job is translated: a save
        // made in French would otherwise carry SidneyId:JOURNALISTE and mean nothing to the
        // same game opened in English.
        _state.SetFlag(
            "SidneyId:" + (identity.Key.Length > 0 ? identity.Key : identity.Title));

        Showing = new SidneyResult($"{identity.Category}: {identity.Title}");

        return Showing;
    }

    /// <summary>The flag that says a file is linked to somebody.</summary>
    private static string Link(SidneySuspect suspect, SidneyFile file) =>
        $"SidneyLink:{suspect.Index}:{file.Id}";

    /// <summary>One of the suspects' or the search screen's own strings.</summary>
    private string Ask(string key, string section = "Suspects Screen")
    {
        string said = _library.Say(key, section);

        return said.Length > 0 ? said : _library.Say(key, "Search Screen");
    }

    /// <summary>
    /// The shapes the geometry analyses have found, in the order the game names them.
    /// </summary>
    public IReadOnlyList<MapShape> Shapes
    {
        get
        {
            // The line is always offered. It is the tool the first step of the puzzle is
            // made of — two places joined, before any picture has been analysed — and
            // nothing grants it because nothing has to.
            List<MapShape> found = [MapShape.Line];

            foreach ((SidneyKind kind, MapShape[] shapes) in Granted)
            {
                foreach (SidneyFile file in Files)
                {
                    if (file.Kind != kind || !HasDone(file, SidneyAction.ViewGeometry))
                    {
                        continue;
                    }

                    foreach (MapShape shape in shapes)
                    {
                        if (!found.Contains(shape))
                        {
                            found.Add(shape);
                        }
                    }
                }
            }

            return found;
        }
    }

    /// <summary>Which pictures give up which shapes when their geometry is analysed.</summary>
    private static readonly (SidneyKind Kind, MapShape[] Shapes)[] Granted =
    [
        (SidneyKind.Parchment1, [MapShape.Circle]),
        (SidneyKind.Parchment2, [MapShape.Square, MapShape.Circle]),
        (SidneyKind.Poussin, [MapShape.Triangle, MapShape.Hexagram]),
        (SidneyKind.Teniers, [MapShape.Square]),
    ];

    /// <summary>
    /// Lays one of the saved shapes over the map.
    /// </summary>
    /// <param name="shape">Which shape.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult LayShape(MapShape shape)
    {
        if (!Shapes.Contains(shape))
        {
            Showing = new SidneyResult(Say("NoShapeNote"));

            return Showing;
        }

        Choosing = false;

        // <b>Choosing a figure never throws one away.</b> Pressing the same button again
        // used to take the figure off and its places with it, which is a lot to lose to a
        // stray click on the step that took longest. It picks the figure up to be edited
        // instead, and the map is armed for its places; ERASE SHAPE is how one goes.
        Map.Select(shape);
        Marking = true;

        // A square asked for with nothing marked goes round the circle already laid, which
        // is what the Aries passage asks of it; a hexagram with nothing marked goes inside
        // it, which is what the Libra passage asks.
        if (Map.Points.Count == 0 && shape is MapShape.Square or MapShape.Hexagram)
        {
            Map.UseShape(shape);
        }

        RememberMap();

        foreach (LaidShape laid in Map.Laid)
        {
            if (laid.Shape == shape && laid.Locked)
            {
                Locked(shape);
            }
        }

        Showing = new SidneyResult(Progress() ?? Note(shape));

        return Showing;
    }

    /// <summary>
    /// Asks Le Serpent Rouge whether the figures as they stand have finished a verse, and
    /// records what it says.
    /// </summary>
    /// <returns>The note to show for a verse finished, or null when nothing changed.</returns>
    private string? Progress()
    {
        SerpentRougeOutcome outcome = SerpentRougeAnalysis.Changed(Map, _state, Scores);

        if (!outcome.Handled)
        {
            return null;
        }

        Cue(outcome);
        Marking = false;
        RememberMap();

        return NoteOf(outcome);
    }

    /// <summary>What the analyze screen shows for what a step came to, in the game's words.</summary>
    private string? NoteOf(SerpentRougeOutcome outcome)
    {
        if (outcome.Note is not { Length: > 0 } key)
        {
            return null;
        }

        // The Site is typed on to the map through a little box of its own in the original:
        // title, prompt and the words. Shown here as the three lines they are.
        if (key == "SiteText")
        {
            return Say("SiteTextTitle") + "\n" + Say("SiteTextPrompt") + " " + Say("SiteText");
        }

        string said = Say(key);

        return outcome.Argument is { } argument
            ? said.Replace("%s", argument, StringComparison.Ordinal)
            : said;
    }

    /// <summary>
    /// Which of the analyze screen's four menus is open, or nought for none.
    /// </summary>
    public int Menu { get; set; }

    /// <summary>
    /// Which menu an operation sits under, as <c>ESIDNEY.TXT</c> groups them.
    /// </summary>
    /// <param name="action">The operation.</param>
    /// <returns>One to four.</returns>
    public static int MenuOf(SidneyAction action) => action switch
    {
        SidneyAction.Analyse => 1,
        SidneyAction.ExtractAnomalies or SidneyAction.AnalyseText or SidneyAction.Translate => 2,
        SidneyAction.ViewGeometry or SidneyAction.RotateShape or
            SidneyAction.ZoomAndClarify or SidneyAction.EraseShape => 3,
        _ => 4,
    };

    /// <summary>What the game calls one of those menus.</summary>
    /// <param name="menu">Which of the four.</param>
    /// <returns>Its name, in the game's own words.</returns>
    public string MenuName(int menu) =>
        _library.Say($"Menu{menu}Name", "Analyze Screen") is { Length: > 0 } named
            ? named
            : menu switch { 1 => "OPEN", 2 => "TEXT", 3 => "GRAPHIC", _ => "MAP" };

    /// <summary>
    /// Which marked place is being dragged, or minus one while none is.
    /// </summary>
    public int Dragging { get; private set; } = -1;

    /// <summary>
    /// Which figure the dragged place belongs to, or minus one for the working set.
    /// </summary>
    public int DraggingFigure { get; private set; } = -1;

    /// <summary>Picks up a marked place.</summary>
    /// <param name="figure">Which figure it belongs to, or minus one for the working set.</param>
    /// <param name="which">Which of that figure's places.</param>
    public void StartDrag(int figure, int which)
    {
        DraggingFigure = figure;
        Dragging = which;
    }

    /// <summary>Moves the place being dragged.</summary>
    /// <param name="to">Where the pointer is, in map pixels.</param>
    public void DragTo(System.Numerics.Vector2 to)
    {
        if (Dragging >= 0)
        {
            Map.MovePoint(DraggingFigure, Dragging, to);
        }
    }

    /// <summary>
    /// Puts the dragged place down, and measures everything again.
    /// </summary>
    /// <returns>What the machine says, or null when nothing was being dragged.</returns>
    public SidneyResult? EndDrag()
    {
        if (Dragging < 0)
        {
            return null;
        }

        Dragging = -1;
        DraggingFigure = -1;

        // The same as marking a fresh place: every figure is re-fitted and the set is
        // measured again, because a confirmation cannot outlive the marks that earned it.
        Map.Refit();
        RememberMap();

        foreach (LaidShape laid in Map.Laid)
        {
            if (laid.Locked)
            {
                Locked(laid.Shape);
            }
        }

        MapAnalysis found = Map.Analyse();

        Showing = new SidneyResult(Progress() ?? Verdict(found));

        return Showing;
    }

    /// <summary>
    /// How far into the map the screen is looking, from one.
    /// </summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>What sits in the middle of the view, in map pixels.</summary>
    public System.Numerics.Vector2 Focus { get; private set; } =
        new(SidneyMap.Extent / 2f, SidneyMap.Extent / 2f);

    /// <summary>
    /// Zooms the map about a place, keeping that place under the pointer.
    /// </summary>
    /// <param name="on">Where the pointer is, in map pixels.</param>
    /// <param name="by">How many notches, away from the player being positive.</param>
    public void ZoomOn(System.Numerics.Vector2 on, float by)
    {
        float was = Zoom;

        Zoom = Math.Clamp(Zoom * MathF.Pow(1.2f, by), 1f, 6f);

        if (MathF.Abs(Zoom - was) < 1e-4f)
        {
            return;
        }

        // The place under the pointer stays under it, which is what makes a wheel zoom feel
        // like looking closer rather than like the picture jumping.
        Focus = on + ((Focus - on) * (was / Zoom));
        Clamp();
    }

    /// <summary>Slides the view without changing how close it is.</summary>
    /// <param name="by">How far, in map pixels.</param>
    public void PanBy(System.Numerics.Vector2 by)
    {
        Focus += by;
        Clamp();
    }

    /// <summary>Keeps the view inside the map.</summary>
    private void Clamp()
    {
        float shown = SidneyMap.Extent / Zoom;
        float edge = shown / 2;

        Focus = new System.Numerics.Vector2(
            Math.Clamp(Focus.X, edge, SidneyMap.Extent - edge),
            Math.Clamp(Focus.Y, edge, SidneyMap.Extent - edge));
    }

    /// <summary>
    /// Marks the next place the survey itself has a cross on.
    /// </summary>
    /// <returns>What the machine says.</returns>
    public SidneyResult Assist()
    {
        // Asked before anything is drawn, because it draws a great deal. The answer comes
        // back through Finish.
        Showing = new SidneyResult(
            Words.Own("AssistSays"),
            Words.Own("AssistAsks"),
            [
                new SidneyChoice("Yes", Say("YesButton") is { Length: > 0 } yes ? yes : "YES"),
                new SidneyChoice("No", Say("NoButton") is { Length: > 0 } no ? no : "NO"),
            ]);

        return Showing;
    }

    /// <summary>
    /// Does the next verse of Le Serpent Rouge for the player, as far as what they have
    /// earned allows.
    /// </summary>
    /// <remarks>
    /// One verse a time, by the same road the player would take — the places marked, the
    /// figure laid, ANALYZE pressed — so that everything the verse sets is set by the
    /// ordinary path. A figure the pictures have not given up yet is not laid: the machine
    /// says so instead. Verses that are not on the map (Ophiuchus is the anagram) and steps
    /// that wait on something else (Scorpio waits on a mail) are left to the player.
    /// </remarks>
    /// <param name="yes">Whether they said to.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult Finish(bool yes)
    {
        if (!yes)
        {
            Showing = new SidneyResult(Say("EnterPointsNote"));

            return Showing;
        }

        Showing = SerpentRougeAnalysis.Solved(_state) switch
        {
            0 => MarkAndAnalyse(SerpentRougeAnalysis.Church, SerpentRougeAnalysis.Ruin),
            1 => Lay(
                MapShape.Circle,
                SerpentRougeAnalysis.Coustaussa, SerpentRougeAnalysis.Bezu, SerpentRougeAnalysis.Bugarach),
            2 => Lay(MapShape.Square),
            3 => Align(),
            4 or 5 => Chessboard(),
            6 => MarkAndAnalyse(SerpentRougeAnalysis.Ermitage, SerpentRougeAnalysis.Tomb),
            7 => MarkAndAnalyse(SerpentRougeAnalysis.TempleCorners),
            8 => Lay(MapShape.Hexagram, turn: SerpentRougeAnalysis.HexagramTurn),
            9 => Divide(),
            11 when _state.GetFlag("Ophiuchus") =>
                MarkAndAnalyse(SerpentRougeAnalysis.SerpentTail, SerpentRougeAnalysis.SerpentHead),
            _ => new SidneyResult(Words.Own("AssistStuck")),
        };

        return Showing;
    }

    /// <summary>Marks places and presses ANALYZE, which is most of the verses.</summary>
    private SidneyResult MarkAndAnalyse(params System.Numerics.Vector2[] places)
    {
        Map.Select(MapShape.None);
        Map.ClearPoints();

        foreach (System.Numerics.Vector2 at in places)
        {
            Map.Enter(at);
        }

        return AnalysedMap();
    }

    /// <summary>Lays a figure over places, or over the circle, and turns it where asked.</summary>
    private SidneyResult Lay(MapShape shape, params System.Numerics.Vector2[] places) =>
        Lay(shape, null, places);

    private SidneyResult Lay(MapShape shape, float? turn, params System.Numerics.Vector2[] places)
    {
        if (!Shapes.Contains(shape))
        {
            return new SidneyResult(Say("NoShapeNote"));
        }

        Map.Select(MapShape.None);
        Map.ClearPoints();

        foreach (System.Numerics.Vector2 at in places)
        {
            Map.Enter(at);
        }

        SidneyResult laid = LayShape(shape);

        if (turn is { } to && Map.Working is { } working)
        {
            Map.Rework(working with { Turn = to });
            RememberMap();

            return new SidneyResult(Progress() ?? laid.Text);
        }

        return laid;
    }

    /// <summary>Taurus: the meridian line down, and the square turned to it.</summary>
    private SidneyResult Align()
    {
        if (!_state.GetFlag("PlacedMeridianLine"))
        {
            MarkAndAnalyse(SerpentRougeAnalysis.Serres, SerpentRougeAnalysis.Meridian);
        }

        if (Map.Working is not { Shape: MapShape.Square } square)
        {
            return Lay(MapShape.Square, turn: SerpentRougeAnalysis.SquareTurn);
        }

        Map.Rework(square with { Turn = SerpentRougeAnalysis.SquareTurn });
        RememberMap();

        return new SidneyResult(Progress() ?? Say("MapShapeLockNote"));
    }

    /// <summary>Gemini and Cancer: the eight by eight ruled inside the square.</summary>
    private SidneyResult Chessboard()
    {
        RuleInShape = true;

        return Rule(8);
    }

    /// <summary>Scorpio: the temple's divisions, then The Site — each only when it can be.</summary>
    private SidneyResult Divide()
    {
        if (_state.GetFlag("PlacedTempleDivisions"))
        {
            Map.Select(MapShape.None);

            return Mark(SerpentRougeAnalysis.Site);
        }

        return _state.GetFlag("OpenedTempleDiagram")
            ? MarkAndAnalyse(SerpentRougeAnalysis.TempleDivisions)
            : new SidneyResult(Words.Own("AssistStuck"));
    }

    /// <summary>Whether a place is already marked, by the working set or by any figure.</summary>
    private bool AlreadyThere(System.Numerics.Vector2 at)
    {
        const float Near = 30f;

        foreach (System.Numerics.Vector2 point in Map.Points)
        {
            if (System.Numerics.Vector2.Distance(point, at) <= Near)
            {
                return true;
            }
        }

        foreach (LaidShape laid in Map.Laid)
        {
            foreach (System.Numerics.Vector2 point in laid.Points)
            {
                if (System.Numerics.Vector2.Distance(point, at) <= Near)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether the map is waiting for a grid to be chosen off its list.</summary>
    public bool Ruling { get; set; }

    /// <summary>Whether the grid the list offers will be ruled inside the figure.</summary>
    public bool RuleInShape { get; set; }

    /// <summary>The grid sizes the game offers, as it writes them.</summary>
    public IReadOnlyList<(int Cells, string Label)> Grids =>
    [
        .. GridSizes
            .Select(cells => (cells, _library.Say($"Grid{cells}", "Analyze Screen")))
            .Where(row => row.Item2.Length > 0),
    ];

    /// <summary>Rules the map, or the figure on it, into so many cells.</summary>
    /// <param name="cells">How many each way.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult Rule(int cells)
    {
        // Filling the shape is the player's choice whether or not a figure is down; what
        // is drawn inside is the square, when there is one, and the whole map otherwise.
        bool inShape = RuleInShape;

        Ruling = false;

        // Filling a shape is only ever the answer while Gemini is the verse in hand; any
        // other time Grace says a grid will not help, and nothing is drawn.
        if (inShape && SerpentRougeAnalysis.RefusesShapeGrid(_state) is { } refusal)
        {
            Speak(refusal);
            Showing = new SidneyResult(Say("GridList"));

            return Showing;
        }

        if (Map.GridFixed)
        {
            Showing = new SidneyResult(Say("GridDispNote"));

            return Showing;
        }

        Map.DrawGrid(cells, inShape && Map.Laid.Count > 0);

        SerpentRougeOutcome outcome = SerpentRougeAnalysis.Ruled(Map, _state, Scores, cells, inShape);

        Cue(outcome);
        RememberMap();

        Showing = new SidneyResult(Say("MapGridPointsNote"));

        return Showing;
    }

    /// <summary>
    /// What the machine says about the figure being marked.
    /// </summary>
    /// <param name="shape">The figure.</param>
    /// <returns>The line to show.</returns>
    private string Note(MapShape shape)
    {
        foreach (LaidShape laid in Map.Laid)
        {
            if (laid.Shape == shape && laid.Locked)
            {
                return Say("MapShapeLockNote");
            }
        }

        int needs = SidneyMap.Needs(shape);
        int has = Map.Points.Count;

        return has < needs
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{SidneyMap.NameOf(shape)}: {has} of {needs} places marked.")
            : Say("MapIndeterminateNote");
    }

    /// <summary>Whether the map is waiting for a shape to be picked off the list.</summary>
    public bool Choosing { get; private set; }

    private SidneyResult Choose()
    {
        if (Shapes.Count == 0)
        {
            return new SidneyResult(Say("NoShapeNote"));
        }

        Choosing = true;

        return new SidneyResult(Say("ShapeList"));
    }

    private SidneyResult Turned()
    {
        if (Map.Shape == MapShape.None)
        {
            return new SidneyResult(Say("NoShapeNote"));
        }

        // Fifteen degrees a step, which is fine enough to find a fit and coarse enough that
        // finding one takes a few presses rather than a hundred. A step that sweeps past
        // the turn a verse is waiting for lands on it: see SerpentRougeAnalysis.Snap.
        float from = Map.ShapeTurn;
        bool locked = Map.Rotate(15f);

        if (SerpentRougeAnalysis.Snap(Map, _state, from, Map.ShapeTurn) is { } snapped &&
            Map.Working is { } turned)
        {
            Map.Rework(turned with { Turn = snapped });
        }

        RememberMap();

        if (locked)
        {
            Locked(Map.Shape);
        }

        return new SidneyResult(
            Progress() ?? (locked ? Say("MapShapeLockNote") : Say("CirclePointsNote")));
    }

    private SidneyResult Unshaped()
    {
        if (Map.Shape == MapShape.None)
        {
            return new SidneyResult(Say("NoShapeNote"));
        }

        if (SerpentRougeAnalysis.RefusesErase(Map, _state) is { } refusal)
        {
            // "I think that's right — I don't want to erase it."
            Speak(refusal);

            return new SidneyResult(Say("MapShapeLockNote"));
        }

        Choosing = false;
        Map.EraseShape();
        RememberMap();

        return new SidneyResult(Say("ShapeErasedNote"));
    }

    /// <summary>
    /// Marks a place on the map.
    /// </summary>
    /// <param name="at">Where, in the map's own pixels.</param>
    /// <returns>What the machine says.</returns>
    public SidneyResult Mark(System.Numerics.Vector2 at)
    {
        if (SerpentRougeAnalysis.RefusesMarking(_state) is { } refusal)
        {
            Speak(refusal);
            Marking = false;
            Showing = new SidneyResult(Say("EnterPointsNote"));

            return Showing;
        }

        if (!Map.Enter(at))
        {
            Showing = new SidneyResult(Say("MapIndeterminateNote"));

            return Showing;
        }

        // The one place that answers the moment it is marked.
        SerpentRougeOutcome marked = SerpentRougeAnalysis.Marked(Map, _state, Scores);

        if (marked.Handled)
        {
            Cue(marked);
            Marking = false;
            RememberMap();
            Showing = new SidneyResult(NoteOf(marked) ?? Say("MapEnterPointNote"));

            return Showing;
        }

        MapAnalysis found = Map.Analyse();

        // Every figure already laid is re-fitted, because the places they have to pass
        // through have just changed — and a confirmation cannot be allowed to outlive the
        // marks that earned it.
        Map.Refit();
        RememberMap();

        foreach (LaidShape laid in Map.Laid)
        {
            if (laid.Locked)
            {
                Locked(laid.Shape);
            }
        }

        string said = Say("MapEnterPointNote").Replace(
            "%s", SidneyMap.Coordinates(at), StringComparison.Ordinal);

        // <b>Two places are the interesting case, not the dull one.</b> The verdict used
        // to wait for a third, so the sunrise line — the first step of the whole map
        // puzzle, and a thing made of exactly two points — was marked and never
        // remarked on.
        if (Map.Points.Count > 1 || found.Finding is MapFinding.Circle or MapFinding.Rectangle)
        {
            said = said + "\n\n" + Verdict(found);
        }

        Showing = new SidneyResult(Progress() ?? said);

        return Showing;
    }

    /// <summary>
    /// Which of the game's notes a line between two places earns.
    /// </summary>
    /// <summary>Whether a line was drawn between two named places, either way round.</summary>
    private static bool Between(
        System.Numerics.Vector2 from,
        System.Numerics.Vector2 to,
        System.Numerics.Vector2 one,
        System.Numerics.Vector2 other)
    {
        const float Near = 40f;

        return (System.Numerics.Vector2.Distance(from, one) <= Near &&
                System.Numerics.Vector2.Distance(to, other) <= Near) ||
               (System.Numerics.Vector2.Distance(from, other) <= Near &&
                System.Numerics.Vector2.Distance(to, one) <= Near);
    }

    private string LineNote()
    {
        if (Map.Points.Count < 2)
        {
            return Say("MapLineDisallow");
        }

        System.Numerics.Vector2 from = Map.Points[0];
        System.Numerics.Vector2 to = Map.Points[^1];

        // <b>The sunrise line is which two places, not where the line happens to go.</b>
        // Testing it by geometry — does it cross the meridian and pass through Arques —
        // refuses the right answer: on this survey the line from the church at
        // Rennes-le-Château over the ruin at Blanchefort misses Arques by a hundred and
        // twelve pixels, because the map is drawn rather than surveyed. What the note is
        // about is the two places the player picked.
        if (Between(from, to, SidneyMap.Church, SidneyMap.Blanchefort))
        {
            return Say("MapLine1Note");
        }

        // Tangential to a circle already laid: touching it, rather than cutting across it.
        foreach (LaidShape laid in Map.Laid)
        {
            if (laid.Shape != MapShape.Circle)
            {
                continue;
            }

            if (SidneyMap.Through(from, to, laid.At + new System.Numerics.Vector2(laid.Size, 0)) ||
                SidneyMap.Through(from, to, laid.At - new System.Numerics.Vector2(laid.Size, 0)) ||
                SidneyMap.Through(from, to, laid.At + new System.Numerics.Vector2(0, laid.Size)) ||
                SidneyMap.Through(from, to, laid.At - new System.Numerics.Vector2(0, laid.Size)))
            {
                return Say("MapLine2Note");
            }
        }

        return Map.Points.Count > 2 ? Say("MapLine3Note") : Say("MapLineDisallow");
    }

    /// <summary>What the machine makes of the points as they stand.</summary>
    private string Verdict(MapAnalysis found)
    {
        switch (found.Finding)
        {
            case MapFinding.Circle:
                // The one that gets somewhere, and the story is allowed to know.
                _state.SetFlag("SidneyMapCircle");

                return Say("MapCircleConfirmNote").Replace(
                    "%s", SidneyMap.Coordinates(found.Centre), StringComparison.Ordinal);

            case MapFinding.Rectangle:
                _state.SetFlag("SidneyMapRectangle");

                return Say("MapRectNote");

            case MapFinding.Line:
                return LineNote();

            case MapFinding.Several:
                return Say("MapSeveralPossNote");

            case MapFinding.TooFew:
                return Say("EnterPointsNote");

            default:
                return Say("MapIndeterminateNote");
        }
    }

    /// <summary>
    /// Arms the map for marking, or disarms it.
    /// </summary>
    private SidneyResult Marked()
    {
        if (!Marking && SerpentRougeAnalysis.RefusesMarking(_state) is { } refusal)
        {
            Speak(refusal);

            return new SidneyResult(Say("EnterPointsNote"));
        }

        Marking = !Marking;

        return new SidneyResult(Say("EnterPointsNote"));
    }

    private SidneyResult Cleared()
    {
        Marking = false;
        Map.ClearPoints();
        RememberMap();

        return new SidneyResult(Say("EnterPointsNote"));
    }

    /// <summary>Takes back the place marked last, and measures what is left.</summary>
    private SidneyResult Undone()
    {
        if (!Map.Undo())
        {
            return new SidneyResult(Say("EnterPointsNote"));
        }

        Map.Refit();
        RememberMap();

        foreach (LaidShape laid in Map.Laid)
        {
            if (laid.Locked)
            {
                Locked(laid.Shape);
            }
        }

        // What is left is measured again, because taking a place back changes the answer as
        // surely as adding one does.
        MapAnalysis found = Map.Analyse();

        return new SidneyResult(
            Map.Points.Count == 0
                ? Say("EnterPointsNote")
                : found.Finding == MapFinding.TooFew
                    ? Say("MapIndeterminateNote")
                    : Verdict(found));
    }

    private SidneyResult Ruled()
    {
        // The list of sizes the game offers, rather than one size chosen for the player.
        if (Map.Grid == 0)
        {
            Ruling = true;

            return new SidneyResult(Say("GridList"));
        }

        if (Map.Grid > 0)
        {
            return new SidneyResult(Say("GridDispNote"));
        }

        // Eight by eight. The file offers a list — two, four, eight, twelve, sixteen — and
        // eight is the one the puzzle is drawn against.
        Map.DrawGrid(8);
        RememberMap();

        return new SidneyResult(Say("MapGridPointsNote"));
    }

    private SidneyResult Unruled()
    {
        if (!Map.EraseGrid())
        {
            return new SidneyResult(Say("NoGridEraseNote"));
        }

        RememberMap();

        return new SidneyResult(Say("ShapeErasedNote"));
    }

    /// <summary>What the machine says about a file it has just been told to analyse.</summary>
    private SidneyResult Analysed(SidneyFile file) => file.Kind == SidneyKind.Map
        ? AnalysedMap()
        : Finished(file.Kind switch
    {
        SidneyKind.Parchment1 => "AnalyzeParch1",
        SidneyKind.Parchment2 => "AnalyzeParch2",
        SidneyKind.Poussin => "AnalyzePous",
        SidneyKind.Teniers => "GeometryTenier1",
        SidneyKind.Symbols => "AnalyzeHermNote",
        SidneyKind.Note => "AnalyzeSUM",
        SidneyKind.KnownPrint => "AnalyzeKPrint",
        SidneyKind.UnknownPrint => "AnalyzeUPrint",
        SidneyKind.Tape => "AnalyzeTape",
        SidneyKind.Licence => "AnalyzeLicense",
        _ => "AnalyzeTemp",
    });

    private SidneyResult Finished(string key) => new(Say(key));

    /// <summary>
    /// ANALYZE over the map: Le Serpent Rouge's turn to look at what is marked.
    /// </summary>
    private SidneyResult AnalysedMap()
    {
        SerpentRougeOutcome outcome = SerpentRougeAnalysis.Analyse(Map, _state, Scores);

        Cue(outcome);

        // Analysing always puts point entry and the figure in hand away, as the original does.
        Marking = false;
        Map.Select(MapShape.None);
        RememberMap();

        return new SidneyResult(NoteOf(outcome) ?? Say("MapIndeterminateNote"));
    }

    /// <summary>
    /// An operation that ends by asking the player something.
    /// </summary>
    private SidneyResult Asked(string key, string before = "") => new(
        before.Length > 0 ? before + "\n\n" + Say(key) : Say(key),
        Say("Languages"),
        [
            new SidneyChoice("French", Say("French")),
            new SidneyChoice("English", Say("English")),
            new SidneyChoice("Latin", Say("Latin")),
        ]);

    /// <summary>The machine talking to itself while it works.</summary>
    private string Progress(params string[] keys) =>
        string.Join('\n', keys.Select(Say).Where(s => s.Length > 0));

    private string Say(string key)
    {
        string said = _library.Say(key, "Analyze Screen");

        return said.Length > 0 ? said : _library.Say(key);
    }

    private void Record(SidneyFile file, SidneyAction action)
    {
        string flag = Flag(file, action);

        _done.Add(flag);
        _state.SetFlag(flag);

        // And under the name the game itself asks about, where it asks about one at all.
        if (StoryFlag(file, action) is { Length: > 0 } known)
        {
            _state.SetFlag(known);
        }
    }

    /// <summary>
    /// The flag an operation sets when it has been run.
    /// </summary>
    /// <summary>
    /// Records that a figure sits on every marked place.
    /// </summary>
    /// <param name="shape">Which figure.</param>
    private void Locked(MapShape shape)
    {
        if (shape == MapShape.None)
        {
            return;
        }

        // Only the machine's own note of it. LockedCircle, LockedSquare and LockedHexagram
        // are the story's, set by Pisces, Taurus and Libra and read by the end of the third
        // morning; a hexagram that happens to fit six marks anywhere is not Libra.
        _state.SetFlag($"SidneyShape:{SidneyMap.NameOf(shape)}");
    }

    private static string Flag(SidneyFile file, SidneyAction action) =>
        $"SidneyDid:{file.Id}:{action}";

    /// <summary>
    /// The name the game's own conditions know a finding by, where they know it at all.
    /// </summary>
    /// <param name="file">The file that was analysed.</param>
    /// <param name="action">What was done to it.</param>
    /// <returns>The flag the story reads, or null where the story does not ask.</returns>
    private static string? StoryFlag(SidneyFile file, SidneyAction action)
    {
        if (action != SidneyAction.ViewGeometry)
        {
            return null;
        }

        // fileParchment1 becomes AnalyzedGeomParchment1, which is how the game spells it.
        return file.Id.StartsWith("file", StringComparison.OrdinalIgnoreCase)
            ? "AnalyzedGeom" + file.Id[4..]
            : null;
    }
}
