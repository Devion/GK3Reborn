using System.Globalization;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// The four angels in the church, and the shape drawn between them.
/// </summary>
/// <remarks>
/// <para>
/// Four angels stand at the corners of the nave. Touching them in turn lights a dot on
/// each and a line between each pair, and touching all four round the outside draws the
/// tilted square that Le Serpent Rouge asks for. Touching them in the wrong order draws
/// one of the two diagonals instead, and the player has to erase and start again.
/// </para>
/// <para>
/// <b>The shape is ten props the scene already ships.</b> <c>CHU.SIF</c> declares
/// <c>chu_laserdot01</c> to <c>04</c> and <c>chu_laser01</c> to <c>06</c>, every one of
/// them <c>hidden</c>: four dots and six lines, being the four sides of the square and its
/// two diagonals. Nothing here draws anything — it decides which of the ten to show.
/// </para>
/// <para>
/// <b>What the scripts read back is a noun-verb count.</b> Laying a dot sets
/// <c>Four_Angels ERASE</c> to one, which is what <c>CHU_ALL.NVC</c>'s
/// <c>VALID_TO_ERASE</c> asks about and the only reason the erase action appears; closing
/// the square sets <c>Four_Angels TRACE</c> to one, which is how the rest of the story
/// learns the shape was found. Both are counts rather than flags because the action files
/// only know how to ask about counts.
/// </para>
/// <para>
/// Adapted from G-Engine's <c>SceneFunctions.cpp</c> under GPL-3, attributed in NOTICE.
/// </para>
/// </remarks>
public sealed class AngelTracing : SceneMechanism
{
    /// <summary>The four dots, in the order the scripts number the angels.</summary>
    /// <remarks>Top, right, bottom, left — which is what makes the edge table below read.</remarks>
    private readonly PlacedModel?[] _dots = new PlacedModel?[4];

    /// <summary>
    /// The six lines that can be drawn between them.
    /// </summary>
    /// <remarks>
    /// In the order the scene file declares them: left-to-top, top-to-right,
    /// right-to-bottom, bottom-to-left, then the two diagonals, top-to-bottom and
    /// left-to-right. The first four are the square; the last two are the mistake.
    /// </remarks>
    private readonly PlacedModel?[] _edges = new PlacedModel?[6];

    /// <summary>Which angel was touched last, or -1 before any of them.</summary>
    private int _last = -1;

    /// <summary>
    /// Which of the six lines have been drawn.
    /// </summary>
    /// <remarks>
    /// Kept here rather than read back off the models. The shape is the puzzle and the
    /// props are how it is shown; a room missing one of its ten pieces of art would
    /// otherwise be a room where the square can never be closed, and the failure would look
    /// like the player tracing it wrong.
    /// </remarks>
    private readonly bool[] _drawn = new bool[6];

    /// <summary>
    /// Whether Grace has already said the line she says on laying the first dot.
    /// </summary>
    /// <remarks>
    /// Not a story flag, and deliberately: the original says it again every time the player
    /// walks back into the church, so it is forgotten with the room rather than remembered
    /// with the game.
    /// </remarks>
    private bool _spoken;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public AngelTracing(SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
    }

    /// <inheritdoc/>
    public override string Name => "Angels";

    /// <summary>The noun both counts are kept under.</summary>
    private const string Noun = "Four_Angels";

    /// <inheritdoc/>
    public override void Begin()
    {
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = World.ModelNamed(
                string.Create(CultureInfo.InvariantCulture, $"chu_laserdot0{i + 1}"));
        }

        for (int i = 0; i < _edges.Length; i++)
        {
            _edges[i] = World.ModelNamed(
                string.Create(CultureInfo.InvariantCulture, $"chu_laser0{i + 1}"));
        }

        _spoken = false;

        Erase();
    }

    /// <inheritdoc/>
    public override string Report() =>
        $"{_dots.Count(d => d is not null)} of 4 dots, " +
        $"{_edges.Count(e => e is not null)} of 6 lines";

    /// <inheritdoc/>
    public override bool Perform(string asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (asked.Equals("Erase", StringComparison.OrdinalIgnoreCase))
        {
            Erase();

            return true;
        }

        if (asked.Length == 6 &&
            asked.StartsWith("Angel", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(asked[5..], CultureInfo.InvariantCulture, out int angel) &&
            angel is >= 1 and <= 4)
        {
            Touch(angel - 1);

            return true;
        }

        return false;
    }

    /// <summary>Rubs the whole shape out.</summary>
    private void Erase()
    {
        _last = -1;
        Array.Clear(_drawn);

        foreach (PlacedModel? part in _dots.Concat(_edges))
        {
            if (part is not null)
            {
                World.Show(part, false);
            }
        }

        Story.SetNounVerbCount(Noun, "ERASE", 0);
    }

    /// <summary>Lights one angel, and the line back to the one before it.</summary>
    private void Touch(int angel)
    {
        if (_dots[angel] is { } dot)
        {
            World.Show(dot, true);
        }

        if (Between(_last, angel) is { } edge)
        {
            _drawn[edge] = true;

            if (_edges[edge] is { } line)
            {
                World.Show(line, true);
            }
        }

        _last = angel;

        // There is now something to rub out, which is the whole of what the erase action's
        // case asks about.
        Story.SetNounVerbCount(Noun, "ERASE", 1);

        if (!_spoken)
        {
            _spoken = true;

            if (Story.Ego.StartsWith("GRA", StringComparison.OrdinalIgnoreCase))
            {
                Say("18P1F0M021");
            }
        }

        Finished();
    }

    /// <summary>Whether the four sides are lit and neither diagonal is.</summary>
    private void Finished()
    {
        if (!Drawn(0) || !Drawn(1) || !Drawn(2) || !Drawn(3) || Drawn(4) || Drawn(5))
        {
            return;
        }

        // What the rest of the story reads to know the shape was found.
        Story.SetNounVerbCount(Noun, "Trace", 1);

        Say(Story.Ego.StartsWith("GAB", StringComparison.OrdinalIgnoreCase)
            ? "18L9M0MZ81"
            : "18P9M0MCE1");

        // And then it is rubbed out again, a moment later, so the player sees the shape
        // they made before the church goes back to being a church. The original holds it
        // for two seconds and so does this.
        Then(HoldSeconds, () => Erase());
    }

    /// <summary>How long the finished square stays up.</summary>
    private const double HoldSeconds = 2.0;

    /// <summary>Whether one of the six lines is drawn.</summary>
    private bool Drawn(int edge) => _drawn[edge];

    /// <summary>
    /// Which line joins two angels, or null when there is no line to draw.
    /// </summary>
    /// <remarks>
    /// Symmetric, and the two diagonals are the pairs that face each other: top to bottom
    /// is edge four and left to right is edge five. Those are the two the player must not
    /// draw, and the reason the puzzle can be got wrong at all.
    /// </remarks>
    private static int? Between(int from, int to)
    {
        if (from < 0 || from == to)
        {
            return null;
        }

        (int, int) pair = from < to ? (from, to) : (to, from);

        return pair switch
        {
            (0, 1) => 1,
            (1, 2) => 2,
            (2, 3) => 3,
            (0, 3) => 0,
            (0, 2) => 4,
            (1, 3) => 5,
            _ => null,
        };
    }
}
