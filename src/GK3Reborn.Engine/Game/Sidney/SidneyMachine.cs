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

    /// <summary>The map, its points and whatever has been laid over it.</summary>
    public SidneyMap Map { get; } = new();

    /// <summary>Whether the analyze screen is waiting for a point to be marked.</summary>
    public bool Marking { get; private set; }

    /// <summary>The file the analyze screen has open, or null.</summary>
    public SidneyFile? Open { get; private set; }

    /// <summary>The message the mail screen has open, or null.</summary>
    public SidneyMail? Reading { get; set; }

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
                actions.Add(SidneyAction.ClearPoints);
                actions.Add(SidneyAction.DrawGrid);
                actions.Add(SidneyAction.EraseGrid);

                // A shape can only be laid once one has been found in a picture, which is
                // what makes the geometry analyses worth running.
                if (Shapes.Count > 0)
                {
                    actions.Add(SidneyAction.UseShape);
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
        Screen = SidneyScreen.Main;
        Showing = null;
        Reading = null;
        Page = null;
        Suspect = null;
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
            _state.SetFlag($"SidneyMatched:{suspect.Index}");
        }

        return Showing;
    }

    /// <summary>Whether a print's name is this suspect's.</summary>
    /// <remarks>
    /// An item's name leads with its owner — BUTHANES_FINGERPRINT, WILKES_FINGERPRINT —
    /// and a suspect's surname is the last word of their name. A label somebody stuck on
    /// afterwards comes later in the item's name and is deliberately not consulted.
    /// </remarks>
    private static bool Belongs(string item, SidneySuspect suspect)
    {
        string[] words = suspect.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return false;
        }

        string surname = words[^1].ToUpperInvariant();
        string leading = item.Split('_')[0].ToUpperInvariant().TrimEnd('S');

        return leading.Length > 2 &&
               (surname.StartsWith(leading, StringComparison.Ordinal) ||
                leading.StartsWith(surname, StringComparison.Ordinal));
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
            List<MapShape> found = [];

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
        Map.UseShape(shape);

        Showing = new SidneyResult(
            Map.Locked
                ? Say("MapShapeLockNote")
                : Map.Points.Count == 0
                    ? Say("CirclePointsNote")
                    : Say("MapIndeterminateNote"));

        if (Map.Locked)
        {
            _state.SetFlag($"SidneyShape:{SidneyMap.NameOf(shape)}");
        }

        return Showing;
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

        if (locked)
        {
            _state.SetFlag($"SidneyShape:{SidneyMap.NameOf(Map.Shape)}");
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

        // A shape already laid is re-fitted, because the places it has to pass through
        // have just changed.
        if (Map.Shape != MapShape.None)
        {
            Map.UseShape(Map.Shape);

            if (Map.Locked)
            {
                _state.SetFlag($"SidneyShape:{SidneyMap.NameOf(Map.Shape)}");
            }
        }

        string said = Say("MapEnterPointNote").Replace(
            "%s", SidneyMap.Coordinates(at), StringComparison.Ordinal);

        if (Map.Points.Count > 2 || found.Finding is MapFinding.Circle or MapFinding.Rectangle)
        {
            said = said + "\n\n" + Verdict(found);
        }

        Showing = new SidneyResult(said);

        return Showing;
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
                return Map.Points.Count > 2 ? Say("MapLine3Note") : Say("MapLineDisallow");

            case MapFinding.Several:
                return Say("MapSeveralPossNote");

            case MapFinding.TooFew:
                return Say("EnterPointsNote");

            default:
                return Say("MapIndeterminateNote");
        }
    }

    private SidneyResult Marked()
    {
        Marking = true;

        return new SidneyResult(Say("EnterPointsNote"));
    }

    private SidneyResult Cleared()
    {
        Marking = false;
        Map.ClearPoints();

        return new SidneyResult(Say("EnterPointsNote"));
    }

    private SidneyResult Ruled()
    {
        if (Map.Grid > 0)
        {
            return new SidneyResult(Say("GridDispNote"));
        }

        // Eight by eight. The file offers a list — two, four, eight, twelve, sixteen — and
        // eight is the one the puzzle is drawn against.
        Map.DrawGrid(8);

        return new SidneyResult(Say("MapGridPointsNote"));
    }

    private SidneyResult Unruled()
    {
        if (Map.Grid == 0)
        {
            return new SidneyResult(Say("NoGridEraseNote"));
        }

        Map.EraseGrid();

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
    }

    /// <summary>
    /// The flag an operation sets when it has been run.
    /// </summary>
    /// <remarks>
    /// Named so it cannot collide with the story's own flags, and set on the story rather
    /// than kept here so it survives a save. What Sidney has been asked to do is part of
    /// the game, not part of the screen.
    /// </remarks>
    private static string Flag(SidneyFile file, SidneyAction action) =>
        $"SidneyDid:{file.Id}:{action}";
}
