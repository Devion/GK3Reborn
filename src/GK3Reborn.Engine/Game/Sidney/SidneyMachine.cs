// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>One of the things Sidney's analyze screen can be asked to do.</summary>
/// <remarks>
/// The four menus of <c>ESIDNEY.TXT</c>'s analyze screen, minus the ones that are about
/// drawing on a map. What is here is what the story runs through.
/// </remarks>
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
/// <remarks>
/// <para>
/// The words are the game's — see <see cref="SidneyLibrary"/> — and this is the machine
/// underneath them: which file is open, which operations apply to it, what each one says,
/// and which of them the story is allowed to notice afterwards.
/// </para>
/// <para>
/// <b>What the story notices.</b> Only two things reach the game's own conditions:
/// <c>DoesSidneyFileExist</c>, which is answered from <see cref="GameState.SidneyFiles"/>,
/// and the flags an analysis sets when it completes. Everything else here — the text, the
/// order the menus come in, which operation is offered — is presentation over that, and a
/// player who never opens the analyze screen is only blocked where the story says so.
/// </para>
/// <para>
/// <b>Operations are offered when they apply.</b> The original left every menu item enabled
/// and answered "Not implemented yet" or an unhelpful note for the ones that did not fit the
/// open file. Here a file offers what it can do, which is the same information without
/// making the player find it by exhaustion — <c>docs/screens.md</c>'s rule that the
/// interface should never have to be learned.
/// </para>
/// </remarks>
public sealed class SidneyMachine
{
    private readonly SidneyLibrary _library;
    private readonly GameState _state;
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

    private SidneyTranslator? _translator;

    /// <summary>The rulings the game offers, in the order it lists them.</summary>
    private static readonly int[] GridSizes = [2, 4, 8, 12, 16];
    private readonly SidneyMap _map = new();
    private SavedMap? _mapWas;

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
    /// <remarks>
    /// Read back out of the story whenever the story's copy has moved on without it, which
    /// is what loading a save looks like from here. The machine is built once and a save may
    /// be loaded under it at any time, so this cannot be done in the constructor.
    /// </remarks>
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
                    ])),
            ],
            _map.GridInShape ? -_map.Grid : _map.Grid);

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
            Locked: false,
            [.. saved.Points.Select(Place)]);

    /// <summary>Whether the analyze screen is waiting for a point to be marked.</summary>
    public bool Marking { get; private set; }

    /// <summary>The file the analyze screen has open, or null.</summary>
    public SidneyFile? Open { get; private set; }

    /// <summary>The message the mail screen has open, or null.</summary>
    public SidneyMail? Reading { get; set; }

    /// <summary>
    /// When it is, for the clock in the corner of the screen.
    /// </summary>
    /// <remarks>
    /// The story's own timeblock rather than the wall clock. Sidney is a machine inside the
    /// game and a real time of day on it would say the player is not.
    /// </remarks>
    public string Now
    {
        get
        {
            Timeblock when = _state.Timeblock;

            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Day {when.Day}  {when.Hour}:00 {(when.IsAfternoon ? "PM" : "AM")}");
        }
    }

    /// <summary>
    /// Whether a message has been opened.
    /// </summary>
    /// <param name="mail">Which message.</param>
    /// <returns>True when it has been read.</returns>
    /// <remarks>
    /// A flag on the story, like everything else the machine remembers, so that it survives
    /// a save. The original had a "NEW E-MAIL" light in the corner of its screen with
    /// nothing behind it here to turn it off.
    /// </remarks>
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
    public int Unread => _library.Mail().Count(m => !HasRead(m));

    /// <summary>What the last operation said, or null.</summary>
    public SidneyResult? Showing { get; private set; }

    /// <summary>The game's own text, for whatever draws this.</summary>
    public SidneyLibrary Library => _library;

    /// <summary>Every file that has been scanned in.</summary>
    /// <remarks>
    /// Derived from the story rather than kept here, because the story is what a save
    /// records and what <c>DoesSidneyFileExist</c> reads. Sidney holding its own list would
    /// be a second answer to the same question.
    /// </remarks>
    public IReadOnlyList<SidneyFile> Files
    {
        get
        {
            List<SidneyFile> files = [];

            foreach (string item in _state.SidneyScans)
            {
                if (SidneyFiles.For(item) is { } file)
                {
                    files.Add(file);
                }
            }

            return files;
        }
    }

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
    /// <remarks>
    /// The story's own script runs separately — the <c>SCANNER</c> verb on that noun, which
    /// marks the item used and sets <c>SidScanner</c>. What this adds is the file, which is
    /// the half nothing else does: <c>AddSidneyFile</c> had no caller at all before, so
    /// every <c>DoesSidneyFileExist</c> in the game answered no for ever.
    /// </remarks>
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
    /// <param name="language">Which language the player suggested.</param>
    /// <returns>What the machine says.</returns>
    /// <remarks>
    /// Both parchments end by asking what language to break the letters on, and both have
    /// one right answer and two written wrong ones. The wrong answers are not failures —
    /// the text for them is in the file, they say what is wrong, and the player may ask
    /// again — so nothing is lost by picking one.
    /// </remarks>
    public SidneyResult Answer(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        bool second = Open?.Kind == SidneyKind.Parchment2;

        string key = language.Trim().ToUpperInvariant() switch
        {
            "FRENCH" => second ? "Parch2French" : "Parch1French",
            "LATIN" => "ParchLatin",
            _ => "ParchEnglish",
        };

        Showing = new SidneyResult(Say(key));

        if (string.Equals(language.Trim(), "FRENCH", StringComparison.OrdinalIgnoreCase) &&
            Open is { } file)
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

    /// <summary>What the player says that file is written in, or null.</summary>
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
    /// <remarks>
    /// <b>The game's own screen says this is something that gets determined</b> — its
    /// refusal for a second licence reads "Vehicle information has already been determined
    /// for this suspect", which only means anything if there was a point at which it had
    /// not been. The port printed every suspect's registration the moment the screen was
    /// opened, which hands the player the answer to the plates they are collecting.
    /// </remarks>
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
    /// <remarks>
    /// The game's own text carries every refusal — no suspect open, already linked, a
    /// fingerprint where one is linked already. They are honoured rather than simplified,
    /// because they are the rules the puzzle is played against.
    /// </remarks>
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
    /// <remarks>
    /// <b>A known print carries its owner's name, and that is the whole rule.</b>
    /// ABBE_FINGERPRINT is the Abbe's, and BUCHELLIS_FINGERPRINT_LABELED_WILKES is
    /// Buchelli's however it is labelled — which is the story point that pair exists to
    /// make. An <em>unknown</em> print matches nobody, which is exactly what the game's own
    /// analysis says it is for: bringing it here to be matched against a known one.
    /// Gabriel's and Grace's prints have their own answers written in the text.
    /// </remarks>
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
    /// <remarks>
    /// Against the noun the game knows them by, not the surname on the list: the Abbé,
    /// Estelle and Larry are all named after something else, and comparing surnames left
    /// their prints matching nobody. A single trailing "s" is dropped from each side, because
    /// the items are possessive where the nouns are not — <c>BUCHELLIS_FINGERPRINT</c> beside
    /// <c>BUCHELLI</c> — and <c>WILKES</c> is spelt that way in both.
    /// </remarks>
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
    /// <remarks>
    /// Recorded as a flag on the story, because which card Grace is carrying is something
    /// the game's conditions may read and something a save has to keep.
    /// </remarks>
    public SidneyResult PrintIdentity(SidneyIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        Identity = identity;
        _state.SetFlag($"SidneyId:{identity.Title}");

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
    /// <remarks>
    /// <para>
    /// Shapes are earned rather than offered. "The shape has been saved" is what every
    /// geometry analysis ends with, and until one has been run the map's shape list is
    /// empty — which is the whole reason to run them.
    /// </para>
    /// <para>
    /// What each grants is what its own text says it found: parchment 2 names "a perfect
    /// square" and "a circle", Poussin names a triangle and then "hexagram shape", and the
    /// Teniers analysis names a square four times over. Parchment 1's note is the one that
    /// names no shape — it says only that the devices "suggest this image" — and it grants
    /// the circle, which is the figure that locates the site.
    /// </para>
    /// </remarks>
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
        // is what the Aries passage asks of it.
        if (Map.Points.Count == 0 && shape == MapShape.Square)
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

        Showing = new SidneyResult(Note(shape));

        return Showing;
    }

    /// <summary>
    /// Which of the analyze screen's four menus is open, or nought for none.
    /// </summary>
    /// <remarks>
    /// The original's analyze screen is four dropdowns — OPEN, TEXT, GRAPHIC and MAP — and
    /// the port's first pass laid every operation out flat. That is easier to read right up
    /// until the map, which has eight of them and wrapped onto three rows of a screen that
    /// is only 640 pixels wide to begin with. The game's own grouping is both the fix and
    /// what the data describes.
    /// </remarks>
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
    /// <remarks>
    /// <b>The original cannot move a place at all</b> — a misplaced click has to be cleared
    /// and every other place with it. The puzzle is played by clicking villages on a
    /// photograph and getting one a few pixels out is the ordinary case, so a place can be
    /// picked up and put down again here.
    /// </remarks>
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

        Showing = new SidneyResult(Verdict(found));

        return Showing;
    }

    /// <summary>
    /// How far into the map the screen is looking, from one.
    /// </summary>
    /// <remarks>
    /// <b>The map is 1,368 pixels shown in about 450.</b> Marking the church at
    /// Rennes-le-Château means clicking a dot three pixels across, and the original's own
    /// walkthrough says to enter points "on the magnified map" — it has a little map and a
    /// big one for exactly this reason. Zooming is not part of the puzzle and is not saved:
    /// it is where the player happens to be looking.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>An aid to clicking, not an answer to the puzzle.</b> The crosses are three pixels
    /// across on a map shown in about four hundred and fifty, and a player who knows
    /// perfectly well that they want Bugarach can still spend a minute failing to hit it.
    /// This places the next one they have not used; which places matter, and what to make
    /// of them, is still theirs to work out.
    /// </para>
    /// <para>
    /// Skips a cross already marked — by the working set or by any figure — so pressing it
    /// four times gives the four the circle wants and not the same one four times.
    /// </para>
    /// </remarks>
    public SidneyResult Assist()
    {
        // Asked before anything is drawn, because it draws a great deal. The answer comes
        // back through Finish.
        Showing = new SidneyResult(
            "SCHATGPT will finish the map: the sunrise line, the circle, the square around "
            + "it and the chessboard over that.",
            "Let it?",
            [Say("Yes") is { Length: > 0 } yes ? yes : "YES",
             Say("No") is { Length: > 0 } no ? no : "NO"]);

        return Showing;
    }

    /// <summary>
    /// Finishes the map puzzle, as far as what the player has earned allows.
    /// </summary>
    /// <param name="yes">Whether they said to.</param>
    /// <returns>What the machine says.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every step it can, in order, and none it cannot.</b> A figure is offered by a
    /// picture the player has analysed; one they have not earned is one the machine has no
    /// business knowing about, so the square is drawn only if a parchment gave it up and the
    /// hexagram only if Poussin's painting did. What it does draw is drawn properly — the
    /// places are the survey's own crosses and its one ruin — so the flags the story is
    /// waiting on are set by the ordinary path rather than written directly.
    /// </para>
    /// <para>
    /// It exists because this is a puzzle a player can be genuinely stuck in front of, with
    /// a timeblock that will not end until the hexagram locks. Being stuck for good is worse
    /// than being told.
    /// </para>
    /// </remarks>
    public SidneyResult Finish(bool yes)
    {
        if (!yes)
        {
            Showing = new SidneyResult(Say("EnterPointsNote"));

            return Showing;
        }

        List<string> done = [];

        // The sunrise line: the church at Rennes-le-Château over the ruin at Blanchefort.
        if (Draw(MapShape.Line, [SidneyMap.Church, SidneyMap.Blanchefort]))
        {
            done.Add(SidneyMap.NameOf(MapShape.Line));
        }

        // The circle through the four the survey crosses.
        if (Draw(
            MapShape.Circle,
            [.. SidneyMap.Sites.Take(4).Select(site => site.At)]))
        {
            done.Add(SidneyMap.NameOf(MapShape.Circle));
        }

        // The square round it, which takes no places of its own.
        if (Draw(MapShape.Square, []))
        {
            done.Add(SidneyMap.NameOf(MapShape.Square));

            RuleInShape = true;
            Rule(8);
        }

        // <b>The hexagram is not drawn.</b> Poussin's painting gives it up, and the
        // timeblock in R25307A will not end without it, but where it goes on the country is
        // not something the survey says: its places are not the crosses, and a hexagram put
        // somewhere plausible would not lock and would leave a wrong figure on the map
        // looking like an answer. See docs/sidney.md.

        Map.Select(MapShape.None);
        RememberMap();

        Showing = new SidneyResult(
            done.Count == 0
                ? Say("NoShapeNote")
                : string.Join(", ", done) + ".\n\n" + Say("MapShapeLockNote"));

        return Showing;
    }

    /// <summary>Draws one figure over named places, when the player has earned it.</summary>
    /// <param name="shape">The figure.</param>
    /// <param name="places">Where it goes, which may be empty.</param>
    /// <returns>True when it was drawn.</returns>
    private bool Draw(MapShape shape, IReadOnlyList<System.Numerics.Vector2> places)
    {
        if (!Shapes.Contains(shape))
        {
            return false;
        }

        // Cleared before the figure is chosen, not after: choosing it first lets it adopt
        // whatever the previous step left lying about, which gave the square the circle's
        // four places instead of letting it go round the circle.
        Map.Select(MapShape.None);
        Map.ClearPoints();
        Map.Select(shape);

        foreach (System.Numerics.Vector2 at in places)
        {
            Map.Enter(at);
        }

        if (places.Count == 0)
        {
            Map.UseShape(shape);
        }

        Map.Analyse();

        foreach (LaidShape laid in Map.Laid)
        {
            if (laid.Shape == shape && laid.Locked)
            {
                Locked(shape);
            }
        }

        return true;
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
        Map.DrawGrid(cells, RuleInShape && Map.Laid.Count > 0);
        RememberMap();
        Ruling = false;

        Showing = new SidneyResult(Say("MapGridPointsNote"));

        return Showing;
    }

    /// <summary>
    /// What the machine says about the figure being marked.
    /// </summary>
    /// <param name="shape">The figure.</param>
    /// <returns>The line to show.</returns>
    /// <remarks>
    /// <b>How many more places it wants</b> is the one thing the original never says and
    /// the player most needs. Its own notes only ever report what a finished set turned out
    /// to be, which leaves somebody four clicks into a six-place figure with no idea whether
    /// they are nearly there or doing it wrong.
    /// </remarks>
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
        // finding one takes a few presses rather than a hundred.
        bool locked = Map.Rotate(15f);

        RememberMap();

        if (locked)
        {
            Locked(Map.Shape);
        }

        return new SidneyResult(locked ? Say("MapShapeLockNote") : Say("CirclePointsNote"));
    }

    private SidneyResult Unshaped()
    {
        if (Map.Shape == MapShape.None)
        {
            return new SidneyResult(Say("NoShapeNote"));
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
    /// <remarks>
    /// Every mark re-measures the set, because the interesting answer arrives on the fourth
    /// point and making the player ask for it separately is making them guess that there is
    /// something to ask about.
    /// </remarks>
    public SidneyResult Mark(System.Numerics.Vector2 at)
    {
        if (!Map.Enter(at))
        {
            Showing = new SidneyResult(Say("MapIndeterminateNote"));

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

        Showing = new SidneyResult(said);

        return Showing;
    }

    /// <summary>
    /// Which of the game's notes a line between two places earns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ESIDNEY.TXT</c> writes five, and which one applies is geography.
    /// <c>MapLine1Note</c> is the one the whole map puzzle opens on: "A straight line marked
    /// between the two points intersects with meridian and point 'Arques'" — the sunrise
    /// line from the church at Rennes-le-Château over the tower at Blanchefort, which runs
    /// on to Arques. <c>MapLine2Note</c> wants the line tangential to a circle already laid.
    /// </para>
    /// <para>
    /// <b><c>MapLine4Note</c> is not chosen here.</b> "Landmark feature connects points" is
    /// the snake — the railway north of the site — and the engine has no idea where the
    /// railway runs. Saying it on a guess would confirm a passage the player had not solved.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <b>A toggle, and the map only takes a click while it is on.</b> The picture used to
    /// be a target the whole time the map was open, so a click meant to reach a menu behind
    /// the pointer, or a click to dismiss something, put a village on the map — before
    /// ENTER POINTS had ever been chosen. The original's menu item exists precisely because
    /// clicking a map is otherwise ambiguous.
    /// </remarks>
    private SidneyResult Marked()
    {
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
        if (Map.Grid == 0)
        {
            return new SidneyResult(Say("NoGridEraseNote"));
        }

        Map.EraseGrid();
        RememberMap();

        return new SidneyResult(Say("ShapeErasedNote"));
    }

    /// <summary>What the machine says about a file it has just been told to analyse.</summary>
    private SidneyResult Analysed(SidneyFile file) => Finished(file.Kind switch
    {
        SidneyKind.Parchment1 => "AnalyzeParch1",
        SidneyKind.Parchment2 => "AnalyzeParch2",
        SidneyKind.Map => "MapNoPrimitiveNote",
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

    /// <summary>An operation that ends by asking the player something.</summary>
    private SidneyResult Asked(string key, string before = "") => new(
        before.Length > 0 ? before + "\n\n" + Say(key) : Say(key),
        Say("Languages"),
        [Say("French"), Say("English"), Say("Latin")]);

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
    /// <remarks>
    /// Named so it cannot collide with the story's own flags, and set on the story rather
    /// than kept here so it survives a save. What Sidney has been asked to do is part of
    /// the game, not part of the screen.
    /// </remarks>
    /// <summary>
    /// Records that a figure sits on every marked place.
    /// </summary>
    /// <param name="shape">Which figure.</param>
    /// <remarks>
    /// <b>Under the name the game reads.</b> <c>R25307A.NVC</c> will not let its timeblock
    /// end without <c>GetFlag("LockedHexagram")</c>, and seven conditions across the action
    /// files ask about <c>LockedSquare</c>. The machine's own
    /// <c>SidneyShape:Hexagram</c> is kept beside them because the map screen reads it, but
    /// it is not what the story is listening for.
    /// </remarks>
    private void Locked(MapShape shape)
    {
        if (shape == MapShape.None)
        {
            return;
        }

        _state.SetFlag($"SidneyShape:{SidneyMap.NameOf(shape)}");
        _state.SetFlag($"Locked{SidneyMap.NameOf(shape)}");
    }

    private static string Flag(SidneyFile file, SidneyAction action) =>
        $"SidneyDid:{file.Id}:{action}";

    /// <summary>
    /// The name the game's own conditions know a finding by, where they know it at all.
    /// </summary>
    /// <param name="file">The file that was analysed.</param>
    /// <param name="action">What was done to it.</param>
    /// <returns>The flag the story reads, or null where the story does not ask.</returns>
    /// <remarks>
    /// <para>
    /// <b>The story asks for these by name and the machine was setting others.</b>
    /// <c>SidneyDid:fileParchment1:ViewGeometry</c> is the machine's own bookkeeping and
    /// nothing in the game has ever heard of it; what the action files ask is
    /// <c>GetFlag("AnalyzedGeomParchment1")</c>. Every such condition answered no, for ever
    /// — the same fault as <c>AddSidneyFile</c> having had no caller.
    /// </para>
    /// <para>
    /// The four are the two parchments and two of the three paintings, which is exactly the
    /// set the files are numbered as: <c>filePainting1</c> is the Poussin and
    /// <c>filePainting3</c> the Teniers without its temple. The third is not asked about.
    /// </para>
    /// </remarks>
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
