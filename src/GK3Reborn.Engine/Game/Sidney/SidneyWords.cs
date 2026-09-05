// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>
/// Every word Sidney draws, in the language the game is being played in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Almost all of it is already translated and was being ignored.</b> <c>ESIDNEY.TXT</c>
/// is one of the ten text assets Sierra re-cut for every localisation, and it carries the
/// menus, the screen names, the buttons and the refusals in each of them. The port read it
/// for the long paragraphs — a parchment's analysis, a suspect's refusal — and then wrote
/// its buttons out in English beside them, so a German game had a German analysis under an
/// English <c>START ANALYSIS</c>.
/// </para>
/// <para>
/// <b>The keys are the same in every release and only the values change.</b> The file says
/// so in its own header — "Only translate English to the right of = sign" — and every
/// release here obeys it. So everything on this screen is asked for by the 1999 key, which
/// is also why the *choices* the machine offers carry a key beside their label: the French
/// release labels the wrong answer to the parchment question <c>OCCITAN</c>, and an engine
/// that matched on the word FRENCH would refuse the right one for ever. See
/// <see cref="SidneyChoice"/>.
/// </para>
/// <para>
/// <b>What is left is the port's own.</b> A dozen sentences the 1999 game never had a place
/// for — the empty lists, the scanner with nothing to take, the aid that finishes the map —
/// and those are in <see cref="Table"/>, one row a phrase and one column a language. They
/// are translations rather than extractions, which is the thing <c>docs/localization.md</c>
/// says the rest of the port's interface still needs; Sidney has them because Sidney is a
/// screen the story cannot be finished without.
/// </para>
/// </remarks>
public sealed class SidneyWords
{
    /// <summary>The order the columns of <see cref="Table"/> are in.</summary>
    /// <remarks>
    /// English first because it is what a run with no language pack gets, then the five
    /// Sierra releases that have been built, alphabetically by code. A language not here
    /// reads the first column, which is the same rule the rest of the port follows: what a
    /// player loses by not having a translation is that translation, not the screen.
    /// </remarks>
    private static readonly string[] Codes = ["en", "de", "es", "fr", "it", "pt"];

    /// <summary>
    /// The sentences Sidney says that the 1999 game has no string for.
    /// </summary>
    /// <remarks>
    /// One row a phrase, one column a language, in <see cref="Codes"/> order. Kept as a
    /// table rather than as six files because it is sixteen phrases: a file format, a
    /// loader and a pack entry for sixteen phrases would cost more to read than the phrases
    /// do. <c>SidneyLanguageTests</c> checks every row is as wide as the header and that no
    /// column of it is quietly English.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        ["NotOn"] =
        [
            "Sidney is not switched on.",
            "Sidney ist nicht eingeschaltet.",
            "Sidney no está encendido.",
            "Sidney n'est pas allumé.",
            "Sidney non è acceso.",
            "O Sidney não está ligado.",
        ],
        ["NothingToShow"] =
        [
            "Nothing to show.",
            "Nichts anzuzeigen.",
            "No hay nada que mostrar.",
            "Rien à afficher.",
            "Niente da mostrare.",
            "Nada a mostrar.",
        ],

        // {0} is the add-data screen's own name, which is itself translated: the sentence
        // tells the player where to go and the place has a different name in each release.
        ["NothingScanned"] =
        [
            "Nothing scanned yet. Use {0} to put something in.",
            "Noch nichts eingelesen. Mit {0} etwas hinzufügen.",
            "Aún no se ha escaneado nada. Usa {0} para introducir algo.",
            "Rien n'a encore été numérisé. Utilisez {0} pour y mettre quelque chose.",
            "Non è stato ancora acquisito nulla. Usa {0} per inserire qualcosa.",
            "Nada digitalizado ainda. Use {0} para inserir algo.",
        ],
        ["NoMessages"] =
        [
            "No messages.",
            "Keine Nachrichten.",
            "No hay mensajes.",
            "Aucun message.",
            "Nessun messaggio.",
            "Nenhuma mensagem.",
        ],
        ["PickMessage"] =
        [
            "Select a message.",
            "Wähle eine Nachricht.",
            "Selecciona un mensaje.",
            "Sélectionnez un message.",
            "Seleziona un messaggio.",
            "Selecione uma mensagem.",
        ],
        ["AllScanned"] =
        [
            "Everything you are carrying is already in the machine.",
            "Alles, was du bei dir hast, ist bereits im Gerät.",
            "Todo lo que llevas ya está en la máquina.",
            "Tout ce que vous portez est déjà dans la machine.",
            "Tutto quello che porti con te è già nella macchina.",
            "Tudo o que traz consigo já está na máquina.",
        ],
        ["NothingToScan"] =
        [
            "Nothing here that the scanner will take.",
            "Hier ist nichts, was der Scanner annimmt.",
            "Aquí no hay nada que el escáner acepte.",
            "Rien ici que le scanner puisse prendre.",
            "Qui non c'è nulla che lo scanner possa acquisire.",
            "Não há aqui nada que o scanner aceite.",
        ],
        ["TypeSubject"] =
        [
            "Type a subject...",
            "Suchbegriff eingeben ...",
            "Escribe un tema...",
            "Tapez un sujet...",
            "Digita un argomento...",
            "Digite um assunto...",
        ],
        ["PickSuspect"] =
        [
            "Open a suspect's file.",
            "Öffne die Akte eines Verdächtigen.",
            "Abre el expediente de un sospechoso.",
            "Ouvrez le dossier d'un suspect.",
            "Apri il fascicolo di un sospettato.",
            "Abra o ficheiro de um suspeito.",
        ],
        ["NoFiles"] =
        [
            "No files. Scan something first.",
            "Keine Dateien. Zuerst etwas einlesen.",
            "No hay archivos. Escanea algo primero.",
            "Aucun fichier. Numérisez d'abord quelque chose.",
            "Nessun file. Prima acquisisci qualcosa.",
            "Nenhum ficheiro. Digitalize algo primeiro.",
        ],
        ["NoFigures"] =
        [
            "no figures saved yet",
            "noch keine Figuren gespeichert",
            "aún no hay figuras guardadas",
            "aucune forme sauvegardée",
            "nessuna figura salvata",
            "ainda não há formas guardadas",
        ],

        // The word in front of a file that is not linked to anybody yet. The game's own
        // LINK TO SUSPECT is a menu item and runs to thirty-six characters in German,
        // which is wider than the column the row is drawn in.
        ["LinkWord"] =
        [
            "link",
            "verbinden",
            "vincular",
            "lier",
            "collega",
            "vincular",
        ],

        // The port's own operation: the original offers ENTER POINTS and CLEAR POINTS and
        // nothing between them, so one misplaced click costs every place marked so far.
        ["UndoPoint"] =
        [
            "UNDO POINT",
            "PUNKT ZURÜCK",
            "DESHACER PUNTO",
            "ANNULER POINT",
            "ANNULLA PUNTO",
            "DESFAZER PONTO",
        ],

        // ShapeCircle, ShapeSquare, ShapeHexagram and ShapeTriangle are all in the file and
        // a line is not: the original never offers one as a figure, and this does.
        ["ShapeLine"] =
        [
            "Line",
            "Linie",
            "Línea",
            "Ligne",
            "Linea",
            "Linha",
        ],
        ["AssistSays"] =
        [
            "SCHATGPT will finish the map: the sunrise line, the circle, the square around "
                + "it and the chessboard over that.",
            "SCHATGPT vervollständigt die Karte: die Sonnenaufgangslinie, den Kreis, das "
                + "Quadrat darum herum und das Schachbrett darüber.",
            "SCHATGPT completará el mapa: la línea del amanecer, el círculo, el cuadrado a "
                + "su alrededor y el tablero de ajedrez encima.",
            "SCHATGPT terminera la carte : la ligne du lever du soleil, le cercle, le carré "
                + "autour et l'échiquier par-dessus.",
            "SCHATGPT completerà la mappa: la linea dell'alba, il cerchio, il quadrato "
                + "attorno e la scacchiera sopra.",
            "O SCHATGPT vai terminar o mapa: a linha do nascer do sol, o círculo, o quadrado "
                + "à volta e o tabuleiro de xadrez por cima.",
        ],
        ["AssistAsks"] =
        [
            "Let it?",
            "Fortfahren?",
            "¿Continuar?",
            "Continuer ?",
            "Procedere?",
            "Continuar?",
        ],
    };

    private readonly SidneyLibrary _library;
    private readonly int _column;

    /// <summary>The words for a run with no game data at all, which is English.</summary>
    public static SidneyWords None { get; } =
        new(SidneyLibrary.Empty, Content.GameLanguage.Default.Code);

    /// <summary>Creates the words.</summary>
    /// <param name="library">The game's own Sidney text.</param>
    /// <param name="language">The ISO 639-1 code the game is being played in.</param>
    public SidneyWords(SidneyLibrary library, string language)
    {
        ArgumentNullException.ThrowIfNull(library);

        _library = library;
        _column = Math.Max(0, Array.FindIndex(Codes, code =>
            string.Equals(code, language, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>What the taskbar's way home is called: the machine's own name.</summary>
    public string Home => Game("MenuName", "Main Screen", "SIDNEY");

    /// <summary>What the file store is called.</summary>
    /// <remarks>
    /// The original's main menu has no row for it — its file list lives on the front screen
    /// — so there is no <c>ScreenName</c> to read. What it does have is the name of the
    /// list itself, in every screen that shows one, and that is set in sentence case where
    /// every other screen's name is capitals: this is a title in a row of titles.
    /// </remarks>
    public string Files => Game("FileList", "AddData Screen", "FILES").ToUpperInvariant();

    /// <summary>The search screen's own button.</summary>
    public string Search => Game("Search", "Search Screen", "SEARCH");

    /// <summary>The button the fingerprint puzzle ends on.</summary>
    public string Match => Game("Menu3Item4", "Suspects Screen", "MATCH ANALYSIS");

    /// <summary>What the game writes where a fact is not known yet.</summary>
    /// <remarks>
    /// The Abbé's vehicle, which nobody ever finds out. Taken from there rather than
    /// written here because it is the same word in the same screen.
    /// </remarks>
    public string Unknown => Game("VehicleID4", "Suspects Screen", "Unknown");

    /// <summary>The light that says something has not been read.</summary>
    public string NewMail => Game("NewEMail", "EMail Screen", "NEW E-MAIL");

    /// <summary>A message's sender line.</summary>
    public string MailFrom => Game("From", "EMail Screen", "From:");

    /// <summary>A message's addressee line.</summary>
    public string MailTo => Game("To", "EMail Screen", "To:");

    /// <summary>A message's copy line.</summary>
    public string MailCc => Game("CC", "EMail Screen", "CC:");

    /// <summary>The button that accepts what has been typed.</summary>
    public string Ok => Game("OKButton", "Analyze Screen", "OK");

    /// <summary>What the translate screen says while nothing is open.</summary>
    public string OpenFile => Game("MenuItem1", SidneyTranslator.Section, "OPEN FILE");

    /// <summary>One of the port's own phrases, in this language.</summary>
    /// <param name="key">Which phrase.</param>
    /// <returns>The phrase, falling back to English.</returns>
    public string Own(string key)
    {
        if (!Table.TryGetValue(key, out string[]? said))
        {
            return key;
        }

        return _column < said.Length && said[_column].Length > 0 ? said[_column] : said[0];
    }

    /// <summary>
    /// What one of the analyze screen's operations is called on its button.
    /// </summary>
    /// <param name="action">The operation.</param>
    /// <returns>Its name, in the game's own words where it has any.</returns>
    /// <remarks>
    /// The keys are the four menus <c>ESIDNEY.TXT</c> groups them into, and the numbering
    /// is the file's own inconsistency: the first menu writes its rows as
    /// <c>MenuItem<i>n</i></c> and the rest as <c>Menu<i>n</i>Item<i>m</i></c>.
    /// </remarks>
    public string Action(SidneyAction action) => action switch
    {
        SidneyAction.Analyse => Menu("MenuItem2", "START ANALYSIS"),
        SidneyAction.ExtractAnomalies => Menu("Menu2Item1", "EXTRACT ANOMALIES"),
        SidneyAction.Translate => Menu("Menu2Item2", "TRANSLATE"),
        SidneyAction.AnalyseText => Menu("Menu2Item4", "ANALYZE TEXT"),
        SidneyAction.ViewGeometry => Menu("Menu3Item1", "VIEW GEOMETRY"),
        SidneyAction.RotateShape => Menu("Menu3Item2", "ROTATE SHAPE"),
        SidneyAction.ZoomAndClarify => Menu("Menu3Item3", "ZOOM & CLARIFY"),
        SidneyAction.UseShape => Menu("Menu3Item5", "USE SHAPE"),
        SidneyAction.EraseShape => Menu("Menu3Item7", "ERASE SHAPE"),
        SidneyAction.EnterPoints => Menu("Menu4Item1", "ENTER POINTS"),
        SidneyAction.ClearPoints => Menu("Menu4Item2", "CLEAR POINTS"),
        SidneyAction.DrawGrid => Menu("Menu4Item4", "DRAW GRID"),
        SidneyAction.EraseGrid => Menu("Menu4Item5", "ERASE GRID"),

        // The one the original does not have.
        _ => Own("UndoPoint"),
    };

    /// <summary>
    /// What the machine takes a file to be.
    /// </summary>
    /// <param name="kind">What the file is.</param>
    /// <returns>Its category, in the game's own words.</returns>
    /// <remarks>
    /// <b>The original's own six directories</b>, which is what its file list sorts into:
    /// images, fingerprints, audio, text, licences and shapes. The port used to write
    /// "parchment", "painting" and "licence plate" here, which is a taxonomy nobody
    /// translated because nobody but the port has ever had one — and it says little the
    /// file's own name does not already say now that the name is the game's.
    /// </remarks>
    public string Kind(SidneyKind kind) => kind switch
    {
        SidneyKind.KnownPrint or SidneyKind.UnknownPrint =>
            Game("FingerDir", "Main Screen", "Fingerprints"),
        SidneyKind.Tape => Game("AudioDir", "Main Screen", "Audio"),
        SidneyKind.Note => Game("TextDir", "Main Screen", "Text"),
        SidneyKind.Licence => Game("LicenseDir", "Main Screen", "Licenses"),
        SidneyKind.Unknown => string.Empty,
        _ => Game("ImageDir", "Main Screen", "Images"),
    };

    /// <summary>
    /// What one of the figures is called.
    /// </summary>
    /// <param name="shape">The figure.</param>
    /// <returns>Its name, in the game's own words where it has one.</returns>
    /// <remarks>
    /// <b>Not <see cref="SidneyMap.NameOf"/>, which must not move.</b> That one is what a
    /// save writes and what a click on a figure's button answers to, so it is English in
    /// every language on purpose; this is the word a player reads.
    /// </remarks>
    public string Shape(MapShape shape) => shape switch
    {
        MapShape.Circle => Menu("ShapeCircle", "Circle"),
        MapShape.Square => Menu("ShapeSquare", "Square"),
        MapShape.Hexagram => Menu("ShapeHexagram", "Hexagram"),
        MapShape.Triangle => Menu("ShapeTriangle", "Triangle"),
        MapShape.Line => Own("ShapeLine"),
        _ => string.Empty,
    };

    /// <summary>One of the analyze screen's strings, or the English it was written in.</summary>
    private string Menu(string key, string english) => Game(key, "Analyze Screen", english);

    /// <summary>One of the game's own strings, or the English it was written in.</summary>
    /// <param name="key">The 1999 key, which is the same in every release.</param>
    /// <param name="section">Which of the file's screens it is under.</param>
    /// <param name="english">What to say when there is no game data at all.</param>
    /// <returns>The string.</returns>
    private string Game(string key, string section, string english) =>
        _library.Say(key, section) is { Length: > 0 } said ? said : english;
}
