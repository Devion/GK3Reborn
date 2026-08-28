using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;
using GK3Reborn.UI;

namespace GK3Reborn.Game;

/// <summary>How an actor gets to the thing they are about to act on.</summary>
/// <remarks>
/// An action file writes this beside the script as <c>approach=</c>, and it is not part of
/// the script: it is what has to be true before the script runs. 3,617 of them across the
/// corpus, of which 2,120 are <c>WalkToSee</c> and 394 are turns.
/// </remarks>
public enum Approaching
{
    /// <summary>Go to a named spot on the floor and face the way it says.</summary>
    Walk,

    /// <summary>Go to a thing and end up looking at it.</summary>
    WalkToSee,

    /// <summary>Face a thing without going anywhere.</summary>
    Turn,
}

/// <summary>A presentation call the script made, recorded rather than performed.</summary>
/// <param name="Name">Function name.</param>
/// <param name="Arguments">Arguments rendered as text.</param>
public readonly record struct RecordedEvent(string Name, IReadOnlyList<string> Arguments);

/// <summary>
/// The GK3 API surface, bound to game state.
/// </summary>
/// <remarks>
/// <para>
/// The specification documents 359 functions, of which 174 are development-only, leaving
/// around 130 that gameplay needs. Those divide cleanly by what they do rather than by
/// how common they are.
/// </para>
/// <para>
/// **State functions are implemented.** Flags, game variables, noun/verb counts, topic
/// counts, score, timeblock and location are what determines whether the story can
/// progress, and they are what a differential comparison between engines has to agree
/// on.
/// </para>
/// <para>
/// **Presentation functions are recorded.** <c>CutToCameraAngle</c> is called 2,235 times
/// across the corpus and <c>StartAnimation</c> 2,067, but neither changes what the game
/// permits — and neither can be performed before the renderer exists. Recording them
/// keeps the trace complete and honest: the call happened, in that order, with those
/// arguments, and nothing was faked.
/// </para>
/// <para>
/// Anything not registered is reported once. Silence there would let a missing function
/// look like a working one.
/// </para>
/// </remarks>
public sealed class Gk3SheepApi : ISheepApi
{
    private readonly Dictionary<string, Func<IReadOnlyList<SheepValue>, SheepValue>> _functions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _waitable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedUnknown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the API over a game state.</summary>
    /// <param name="state">State the functions read and write.</param>
    public Gk3SheepApi(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;

        RegisterStateFunctions();
        RegisterRecordedFunctions();
    }

    /// <summary>
    /// Grace's computer, when there is one.
    /// </summary>
    /// <remarks>
    /// Held here so that the switches which set a scene up for a screenshot can reach it;
    /// the game itself reaches it through the screen it draws.
    /// </remarks>
    public Sidney.SidneyMachine? Sidney { get; set; }

    /// <summary>Where saved games are kept, or null when nothing may be saved.</summary>
    /// <remarks>
    /// Null for a headless run. A corpus sweep loads five hundred rooms and has neither a
    /// profile directory to write to nor a reason to want one.
    /// </remarks>
    public SaveStore? Saves { get; set; }

    /// <summary>
    /// Where the camera stands in the room a request is moving to, or null for its own.
    /// </summary>
    /// <remarks>
    /// The binoculars set this. Leaning in on somewhere across the valley cuts to a camera
    /// the binoculars data names, and that camera is inside a room which has not been
    /// loaded yet — so it travels with the request rather than being applied here.
    /// </remarks>
    public (System.Numerics.Vector3 Position, System.Numerics.Vector2 Angle)? WantedCamera { get; set; }

    /// <summary>
    /// A room the game has to move to, put here by loading a save.
    /// </summary>
    /// <remarks>
    /// Read and cleared by whatever owns the loop. A script function cannot load a scene —
    /// that needs archives, a device and a renderer — so it says where the game now is and
    /// something with those things takes it there.
    /// </remarks>
    public string? Wanted { get; set; }

    /// <summary>The state these functions operate on.</summary>
    public GameState State { get; }

    /// <summary>Puts a saved game back, and drops what the load has orphaned.</summary>
    /// <param name="save">The save.</param>
    /// <remarks>
    /// <para>
    /// <see cref="GameState.Restore"/> and everything a load has to clear that is not the
    /// story's to keep. The action clock is the one thing here that is not state: it
    /// belongs to whatever action was playing when the player loaded, and that action is
    /// gone with the room it ran in. Left standing it keeps <c>SceneUpdate.Occupied</c>
    /// true in the restored room, which holds the camera away from the player for as long
    /// as the abandoned action had left to run — the same complaint as a save loaded
    /// during a cutscene, arriving from the other side.
    /// </para>
    /// <para>
    /// Every load goes through here: the console and the interface both, because a camera
    /// that answers to nobody is the sort of fault that comes back through whichever path
    /// was not fixed.
    /// </para>
    /// </remarks>
    public void RestoreGame(SaveGame save)
    {
        ArgumentNullException.ThrowIfNull(save);

        State.Restore(save);

        ActionSeconds = 0;
        ActingOn = string.Empty;
    }

    /// <summary>Presentation calls, in the order they were made.</summary>
    public List<RecordedEvent> Events { get; } = [];

    /// <summary>Functions that were called but are not registered.</summary>
    public IReadOnlyCollection<string> UnknownFunctions => _reportedUnknown;

    /// <summary>What each score event is worth.</summary>
    /// <remarks>
    /// The engine's own table rather than the game's: the original compiled it in, and no
    /// barn holds it. See <see cref="ScoreEvents"/>.
    /// </remarks>
    public ScoreEvents Scores { get; set; } = ScoreEvents.Open();

    /// <summary>Diagnostics raised while running.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <inheritdoc/>
    public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);

        if (_functions.TryGetValue(name, out Func<IReadOnlyList<SheepValue>, SheepValue>? function))
        {
            return function(arguments);
        }

        if (_reportedUnknown.Add(name))
        {
            Diagnostics.Add(new Diagnostic(
                "GK3R3200", DiagnosticSeverity.Warning,
                $"Script called '{name}', which is not implemented.",
                null, null, "a registered API function", name,
                "Implement it, or register it as a recorded presentation call."));
        }

        Events.Add(new RecordedEvent(name, [.. arguments.Select(a => a.AsString())]));
        return SheepValue.FromInt(0);
    }

    /// <inheritdoc/>
    public bool IsWaitable(string name) => _waitable.Contains(name);

    /// <summary>How long the next lines of the conversation in progress take.</summary>
    /// <remarks>
    /// <c>ContinueDialogue(2)</c> means the next two of a run whose licence plate was given
    /// once, several statements ago, so only the thing doing the speaking knows which
    /// recordings those are. Null where nothing is speaking, which answers nought and is
    /// right: a continuation of nothing says nothing and takes no time.
    /// </remarks>
    public Func<int, double>? ContinuedSeconds { get; set; }

    /// <summary>
    /// What sends an actor across the room, when there is a room to cross.
    /// </summary>
    /// <remarks>
    /// Given the actor, the place, how to get there and whether they are in a hurry;
    /// answers how long it will take. Set by <see cref="SceneScripting.Attach"/>, so a tool
    /// with no scene leaves it null and the walking calls stay recorded, as they always
    /// were.
    /// </remarks>
    /// <remarks>
    /// Only the player is ever in a hurry. A script that sends somebody somewhere passes
    /// false, because a script's timings are written against the pace the game walks at and
    /// hurrying one leg of a scripted sequence would arrive an actor before their line.
    /// </remarks>
    public Func<string, string, Approaching, bool, bool, double>? Walks { get; set; }

    /// <summary>
    /// The noun of the action being carried out, or empty.
    /// </summary>
    /// <remarks>
    /// Some functions take no arguments and mean "the thing this action is about".
    /// <c>InspectObject()</c> is the one that matters — <c>REGISTER, INSPECT, ALL,
    /// script={wait InspectObject();}</c> is the whole of that rule, and without a noun it
    /// has nothing to look at.
    /// <para>
    /// Set when an action starts and left there, rather than restored afterwards. An
    /// action's script may be deferred until its approach has finished, so the noun has to
    /// outlive the call that set it; and actions are serialised by their approach anyway,
    /// so the last one started is the one running.
    /// </para>
    /// </remarks>
    public string ActingOn { get; set; } = string.Empty;

    /// <summary>How much longer the action that is running has to run.</summary>
    /// <remarks>
    /// <para>
    /// The reference keeps a whole <c>mCurrentAction</c> for this and asks it
    /// <c>IsActionPlaying</c>; what anything here wants to know is only whether the story
    /// is in the middle of something, so what is kept is the time left rather than the
    /// action. The runner writes the sum of an action's waits here as it performs it and
    /// the room counts it down.
    /// </para>
    /// <para>
    /// It is a floor rather than a promise. A statement whose length the host cannot work
    /// out — <c>wait CallSheep(…)</c>, whose length is another script — contributes
    /// nothing, so an action can still be going on after this has run out. Whoever asks
    /// has to be safe if it says no too early.
    /// </para>
    /// </remarks>
    public double ActionSeconds { get; set; }

    /// <summary>How long a movie runs, asked before it is played.</summary>
    /// <remarks>
    /// A hook rather than a library lookup, because the length lives in the movie's own
    /// container and only the thing that can open one knows it. Null means no movies,
    /// which is a machine with no decoder: the scripts still run and wait for nothing.
    /// </remarks>
    public Func<string, double>? MovieSeconds { get; set; }

    /// <summary>Walks an actor to where an animation begins, and says how long it takes.</summary>
    /// <remarks>
    /// A hook of its own rather than another <see cref="Approaching"/>, because the name it
    /// is given is an animation rather than a place: working out where to go means reading
    /// the clip, and only a standing scene has the clips.
    /// </remarks>
    public Func<string, string, bool, double>? WalksToAnimationStart { get; set; }

    /// <summary>
    /// What holds something back until the player has walked there, if anything can.
    /// </summary>
    /// <remarks>
    /// Given a number of seconds and the work; answers whether it took charge of it. An
    /// action's <c>approach</c> has to finish before its script runs — the original walks
    /// the ego to the target and performs the action from the arrival — and this is where
    /// a runner finds something with a clock to wait against. Null in a tool, where every
    /// action runs the instant it is asked for, exactly as it always did.
    /// </remarks>
    public Func<double, Action, bool>? Defers { get; set; }

    /// <summary>
    /// What plays an animation, when there is a room to play it in.
    /// </summary>
    /// <remarks>
    /// Given the animation's name and whether it repeats; answers how long it will take.
    /// Null in a tool, where the animation calls stay recorded as they always were.
    /// </remarks>
    public Func<string, bool, double>? Plays { get; set; }

    /// <summary>
    /// The animations, for the calls whose length is a frame count.
    /// </summary>
    /// <remarks>
    /// Optional, because a tool sweeping the corpus has no clock and does not want one.
    /// Without it a line of dialogue is over as soon as it starts, which is what every
    /// waited call did before there was anywhere to read a duration from.
    /// </remarks>
    public AnimationLibrary? Animations { get; set; }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Only the calls whose length is knowable from what has been read. A timer is exactly
    /// its argument and a camera glide is as long as a glide, both of which the engine
    /// decides for itself. The rest is asset-shaped: an animation is a frame count at
    /// fifteen frames a second, and a voice-over is one animation per line of dialogue.
    /// </para>
    /// <para>
    /// A call whose asset cannot be found answers zero rather than a plausible guess, so a
    /// missing file makes a line instant instead of inventing a pause that is not in the
    /// game.
    /// </para>
    /// </remarks>
    public double SecondsFor(string name, IReadOnlyList<SheepValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);

        string first = arguments.Count > 0 ? arguments[0].AsString() : string.Empty;

        return name.ToUpperInvariant() switch
        {
            "SETTIMERSECONDS" => arguments.Count > 0 ? arguments[0].AsFloat() : 0,
            "SETTIMERMS" => arguments.Count > 0 ? arguments[0].AsInt() / 1000.0 : 0,
            "GLIDETOCAMERAANGLE" => SceneUpdate.GlideSeconds,

            // A licence plate and a line count, not an asset name. The library works out
            // which animations those are.
            //
            // A conversation is the same thing under four more names, and they were all
            // answering nought. Marked waitable and worth no time, a waited StartDialogue
            // let the block finish in the frame it began — so the script ran straight on to
            // the next line, and starting a line abandons whatever is being said. Every
            // exchange in the game was cut off mid-sentence, and the longer the recording
            // the more of it was lost, which is exactly how it was reported.
            "STARTVOICEOVER" or "STARTDIALOGUE" or "STARTDIALOGUENOFIDGETS" =>
                Animations?.SecondsOfVoiceOver(
                    first, arguments.Count > 1 ? arguments[1].AsInt() : 1) ?? 0,

            // A continuation names no plate at all — only how many more lines — because the
            // run it belongs to is remembered by whatever is speaking. So this is the one
            // duration the API cannot work out on its own, and it asks.
            "CONTINUEDIALOGUE" or "CONTINUEDIALOGUENOFIDGETS" =>
                ContinuedSeconds?.Invoke(arguments.Count > 0 ? arguments[0].AsInt() : 1) ?? 0,

            // StartYak names one animation outright, where StartVoiceOver names a run of
            // them, so the same asset is reached two different ways.
            "STARTYAK" or "STARTANIMATION" or "LOOPANIMATION" or "STARTMOVEANIMATION" or
            "STARTMORPHANIMATION" or "STARTMOM" =>
                Animations?.SecondsOf(first) ?? 0,

            // A walk is as long as the route is, which is not known until it is found —
            // so this asks for one rather than guessing from the distance.
            "WALKTO" or "WALKTOANIMATION" => Length(arguments, Approaching.Walk),
            "WALKTOSEEMODEL" => Length(arguments, Approaching.WalkToSee),
            "TURNTOMODEL" or "TURNTO" => Length(arguments, Approaching.Turn),

            // A movie is as long as the movie is, which only whatever plays it knows.
            // Answering nothing would have a script speak over its own cutscene.
            "PLAYMOVIE" or "PLAYFULLSCREENMOVIE" or "PLAYFULLSCREENMOVIEX" =>
                MovieSeconds?.Invoke(first) ?? 0,

            _ => 0,
        };
    }

    /// <summary>
    /// How long a walking call takes, by asking what the route would be.
    /// </summary>
    /// <remarks>
    /// Answering this <em>starts</em> the walk, because the length of a route is not known
    /// until it has been found and finding it twice would be the same work done twice. That
    /// is fine and deliberate: a host is only asked how long a call takes when the call is
    /// about to be made.
    /// </remarks>
    private double Length(IReadOnlyList<SheepValue> arguments, Approaching how)
    {
        if (Walks is null || arguments.Count == 0)
        {
            return 0;
        }

        string actor = arguments[0].AsString() is { Length: > 0 } named && arguments.Count > 1
            ? named
            : State.Ego;

        string place = arguments.Count > 1 ? arguments[1].AsString() : arguments[0].AsString();

        // A script's walk never runs of its own accord. Its timings were written against
        // the pace the game walks at, and a cutscene that arrives early is a cutscene with
        // a gap in it.
        return Walks(actor, place, how, false, false);
    }

    /// <summary>Whether a function does something rather than being recorded.</summary>
    /// <param name="name">Function name.</param>
    /// <returns>True when it is registered.</returns>
    /// <remarks>
    /// Asked by tools that want to say what a script would really do before running it. An
    /// unregistered call is not an error — the presentation surface is deliberately
    /// recorded rather than performed — but a script whose every call is recorded has not
    /// moved the story, and the difference is worth being able to see.
    /// </remarks>
    public bool Implements(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _functions.ContainsKey(name);
    }

    /// <summary>Every function this host performs, by name.</summary>
    /// <remarks>
    /// What this build can actually do, as against what the 1999 scripts call. The console
    /// completes against this rather than against the archives' import table for exactly
    /// that reason: offering the player a function that would be recorded and not performed
    /// is worse than not offering it.
    /// </remarks>
    public IReadOnlyCollection<string> FunctionNames => _functions.Keys;

    /// <summary>Calls a function by name, as a script would.</summary>
    /// <param name="name">The function.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>What it returned, or null when there is no such function.</returns>
    /// <remarks>
    /// The same path a script takes, deliberately. A console that reached past this into
    /// the game's own objects would be able to put the story into states no script could
    /// reach, and the first thing anybody would do with it is produce a save nothing can
    /// load.
    /// </remarks>
    public SheepValue? Perform(string name, IReadOnlyList<SheepValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);

        return _functions.TryGetValue(name, out Func<IReadOnlyList<SheepValue>, SheepValue>? function)
            ? function(arguments)
            : null;
    }

    /// <summary>Registers a function.</summary>
    /// <param name="name">Function name.</param>
    /// <param name="implementation">What it does.</param>
    /// <param name="waitable">Whether callers may wait on it.</param>
    public void Register(
        string name, Func<IReadOnlyList<SheepValue>, SheepValue> implementation, bool waitable = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(implementation);

        _functions[name] = implementation;
        if (waitable)
        {
            _waitable.Add(name);
        }
    }

    private void RegisterStateFunctions()
    {
        SheepValue Zero(IReadOnlyList<SheepValue> _) => SheepValue.FromInt(0);

        Register("GetGameVariableInt", a => SheepValue.FromInt(State.GetVariable(Arg(a, 0))));
        Register("SetGameVariableInt", a =>
        {
            State.SetVariable(Arg(a, 0), Int(a, 1));
            return SheepValue.FromInt(0);
        });
        Register("IncGameVariableInt", a =>
        {
            State.IncrementVariable(Arg(a, 0), 1);
            return SheepValue.FromInt(0);
        });

        Register("GetFlag", a => SheepValue.FromInt(State.GetFlag(Arg(a, 0)) ? 1 : 0));
        Register("SetFlag", a =>
        {
            State.SetFlag(Arg(a, 0));
            return SheepValue.FromInt(0);
        });
        Register("ClearFlag", a =>
        {
            State.ClearFlag(Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        Register("GetNounVerbCount", a => SheepValue.FromInt(State.GetNounVerbCount(Arg(a, 0), Arg(a, 1))));
        Register("SetNounVerbCount", a =>
        {
            State.SetNounVerbCount(Arg(a, 0), Arg(a, 1), Int(a, 2));
            return SheepValue.FromInt(0);
        });
        Register("IncNounVerbCount", a =>
        {
            State.IncrementNounVerbCount(Arg(a, 0), Arg(a, 1));
            return SheepValue.FromInt(0);
        });

        // Both characters at once. It exists because the counts are per character, and a
        // door that has been opened is open for whoever walks in next.
        Register("SetNounVerbCountBoth", a =>
        {
            State.SetNounVerbCount("GABRIEL", Arg(a, 0), Arg(a, 1), Int(a, 2));
            State.SetNounVerbCount("GRACE", Arg(a, 0), Arg(a, 1), Int(a, 2));
            return SheepValue.FromInt(0);
        });

        Register("IncNounVerbCountBoth", a =>
        {
            foreach (string who in BothCharacters)
            {
                State.SetNounVerbCount(
                    who, Arg(a, 0), Arg(a, 1),
                    State.GetNounVerbCount(who, Arg(a, 0), Arg(a, 1)) + 1);
            }
            return SheepValue.FromInt(0);
        });

        Register("GetTopicCount", a => SheepValue.FromInt(State.GetTopicCount(Arg(a, 0), Arg(a, 1))));
        Register("SetTopicCount", a =>
        {
            State.SetTopicCount(Arg(a, 0), Arg(a, 1), Int(a, 2));
            return SheepValue.FromInt(0);
        });

        // <b>The argument is a name, not a number.</b> ChangeScore("e_110a_lby_read_register")
        // is what the scripts write, 321 times, and reading it as an integer awards zero
        // every time — so the score was permanently nought. What each event is worth is the
        // engine's own table; see ScoreEvents.
        Register("ChangeScore", a =>
        {
            string named = Arg(a, 0);

            if (Scores.Worth(named) is null && named.Length > 0)
            {
                Diagnostics.Add(new Diagnostic(
                    "GK3R3350", DiagnosticSeverity.Info,
                    "A script scored an event the engine's table does not list.",
                    named, null, "an event in Assets/Story/Scores.txt", named,
                    "It scores nothing. Add it to the table if the game does award it."));
            }

            State.AwardScore(named, Scores.Worth(named));

            return SheepValue.FromInt(0);
        });

        Register("IncreaseScore", a =>
        {
            State.ChangeScore(Int(a, 0));
            return SheepValue.FromInt(0);
        });

        Register("GetScore", _ => SheepValue.FromInt(State.Score));
        Register("GetMaxScore", _ => SheepValue.FromInt(Scores.Maximum));

        // Moving the clock on. Nothing in the shipped data calls either of these — the
        // rules that do are the engine's own Game/Story/TimeblockRules.cs, because the
        // original kept them in its executable — so until those rules existed there was no
        // way out of the first two hours of the game. They are still registered here: the
        // rules go through State.ChangeTimeblock as these do, and a script that did call
        // one would be answered the same way.
        Register("SetTime", a =>
        {
            if (Timeblock.TryParse(Arg(a, 0), out Timeblock wanted))
            {
                State.ChangeTimeblock(wanted);
            }

            return SheepValue.FromInt(0);
        }, waitable: true);

        Register("SetLocationTime", a =>
        {
            if (Timeblock.TryParse(Arg(a, 1), out Timeblock wanted))
            {
                State.ChangeTimeblock(wanted, Arg(a, 0));
            }

            return SheepValue.FromInt(0);
        }, waitable: true);

        Register("IsCurrentTime", a =>
            SheepValue.FromInt(string.Equals(Arg(a, 0), State.Timeblock.ToString(), StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        Register("IsCurrentLocation", a =>
            SheepValue.FromInt(string.Equals(Arg(a, 0), State.Location, StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        Register("GetEgoName", _ => SheepValue.FromString(State.Ego));
        Register("IsCurrentEgo", a =>
            SheepValue.FromInt(string.Equals(Arg(a, 0), State.Ego, StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        Register("SetActorLocation", a =>
        {
            State.SetActorLocation(Arg(a, 0), Arg(a, 1));
            return SheepValue.FromInt(0);
        });
        Register("GetActorLocation", a => SheepValue.FromString(State.GetActorLocation(Arg(a, 0))));

        Register("IsActorAtLocation", a => SheepValue.FromInt(
            string.Equals(State.GetActorLocation(Arg(a, 0)), Arg(a, 1), StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0));

        Register("WasLastLocation", a => SheepValue.FromInt(
            string.Equals(State.LastLocation, Arg(a, 0), StringComparison.OrdinalIgnoreCase) ? 1 : 0));

        // Counts are per timeblock and count previous visits; see GameState.EnterLocation
        // for why the current one is not among them.
        Register("GetEgoCurrentLocationCount", _ =>
            SheepValue.FromInt(State.GetLocationCount(State.Ego, State.Location)));

        Register("GetEgoLocationCount", a =>
            SheepValue.FromInt(State.GetLocationCount(State.Ego, Arg(a, 0))));

        Register("SetEgoLocationCount", a =>
        {
            State.SetLocationCount(State.Ego, Arg(a, 0), Int(a, 1));
            return SheepValue.FromInt(0);
        });

        Register("WasEgoEverInLocation", a =>
            SheepValue.FromInt(State.WasEverInLocation(State.Ego, Arg(a, 0)) ? 1 : 0));

        Register("GetChatCount", a => SheepValue.FromInt(State.GetChatCount(Arg(a, 0))));

        // The same question with the noun given as an enumerated value rather than a name.
        // The distinction is the caller's and the answer is the same count.
        Register("GetChatCountInt", a => SheepValue.FromInt(State.GetChatCount(Arg(a, 0))));
        Register("SetChatCount", a =>
        {
            State.SetChatCount(Arg(a, 0), Int(a, 1));
            return SheepValue.FromInt(0);
        });
        Register("IncChatCount", a =>
        {
            State.IncrementChatCount(Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        // Inventory is asked about by name rather than by a parameter, so the two leads
        // get a function each, and ego gets a third that follows whoever is being played.
        Register("DoesGabeHaveInvItem", a =>
            SheepValue.FromInt(State.Inventory.Has("GABRIEL", Arg(a, 0)) ? 1 : 0));
        Register("DoesGraceHaveInvItem", a =>
            SheepValue.FromInt(State.Inventory.Has("GRACE", Arg(a, 0)) ? 1 : 0));
        Register("DoesEgoHaveInvItem", a =>
            SheepValue.FromInt(State.Inventory.Has(State.Ego, Arg(a, 0)) ? 1 : 0));

        // Which of the things in the bag is the one in hand. Using an item on something is
        // written in the action files as a verb named for the item, so this is how a rule
        // asks whether the player is about to use that one.
        Register("IsActiveInvItem", a => SheepValue.FromInt(
            string.Equals(
                State.Inventory.ActiveItemOf(State.Ego),
                Arg(a, 0),
                StringComparison.OrdinalIgnoreCase) ? 1 : 0));
        Register("SetEgoActiveInvItem", a =>
        {
            State.Inventory.SetActive(State.Ego, Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        Register("DoesSidneyFileExist", a =>
            SheepValue.FromInt(State.HasSidneyFile(Arg(a, 0)) ? 1 : 0));

        Register("GetRandomInt", a => SheepValue.FromInt(State.NextRandom(Int(a, 0), Int(a, 1))));

        // The Int forms take the noun and the verb as the identifiers a case's n$ and v$
        // carry. The original numbers those, because its script host can only pass integers
        // between a case and a function; this binds the names themselves, so the two forms
        // ask the same question and the Int suffix is only history.
        Register("GetNounVerbCountInt", a =>
            SheepValue.FromInt(State.GetNounVerbCount(Arg(a, 0), Arg(a, 1))));
        // Not the game's functions. A topic is written as several lines under different
        // conditions and each is said once, so the resolver has to ask which have been —
        // and it reaches the story only through here, the same way it asks for a noun-verb
        // count. Named so that nobody mistakes them for something a script may call.
        Register("EngineHasSaidTopicLine", a => SheepValue.FromInt(
            State.HasSaid(Arg(a, 0), Arg(a, 1), Arg(a, 2)) ? 1 : 0));

        Register("EngineRecordTopicLine", a =>
        {
            State.Said(Arg(a, 0), Arg(a, 1), Arg(a, 2));
            return SheepValue.FromInt(0);
        });

        // Saving and loading, from the console and from the interface. Not the game's own
        // functions — the original saves through its shell and no script asks it to — so
        // they carry the Engine prefix that says nobody may mistake them for the API.
        //
        // The store is optional. A headless corpus sweep has nowhere to write and no reason
        // to, and a missing store answers "no" rather than throwing.
        Register("EngineSaveGame", a =>
        {
            string slot = Arg(a, 0) is { Length: > 0 } named ? named : SaveStore.QuickSlot;

            return SheepValue.FromInt(
                Saves is not null && Saves.Write(slot, State.Capture(Arg(a, 1))) ? 1 : 0);
        });

        Register("EngineLoadGame", a =>
        {
            string slot = Arg(a, 0) is { Length: > 0 } named ? named : SaveStore.QuickSlot;

            if (Saves?.Read(slot, out SaveFault fault) is not { } save || fault != SaveFault.None)
            {
                return SheepValue.FromInt(0);
            }

            RestoreGame(save);

            // The room the save names is not this one, and putting the player in it is the
            // caller's job rather than the state's: loading a scene needs archives, a
            // device and a renderer, none of which belong to a script function. This
            // records that the room has to change and the loop acts on it.
            Wanted = save.Location;

            return SheepValue.FromInt(1);
        });

        // Puts a place on the driving map. The original keeps a flag per marker and sets
        // it from hooks compiled into its own executable; this is the same idea reached
        // through the story's flags, so it survives a save and a script can say it.
        Register("EngineOpenOnMap", a =>
            SheepValue.FromInt(DrivingMap.Reveal(State, Arg(a, 0)) ? 1 : 0));

        Register("SetConversation", a =>
        {
            State.Conversation = Arg(a, 0);
            return SheepValue.FromInt(0);
        });

        Register("EndConversation", _ =>
        {
            State.Conversation = null;
            State.Talking = false;
            return SheepValue.FromInt(0);
        });

        Register("InConversation", _ =>
            SheepValue.FromInt(State.Conversation is { Length: > 0 } ? 1 : 0));

        Register("GetTopicCountInt", a =>
            SheepValue.FromInt(State.GetTopicCount(Arg(a, 0), Arg(a, 1))));

        // The six statuses the original accepts, boiled down to who is holding the thing,
        // which is all any of them ever meant: NotPlaced, Placed and Used all say nobody.
        Register("SetInvItemStatus", a =>
        {
            string item = Arg(a, 0);

            switch (Arg(a, 1).ToUpperInvariant())
            {
                case "GRACEHAS":
                    Give("GRACE", item);
                    Take("GABRIEL", item);
                    break;
                case "GABEHAS":
                    Give("GABRIEL", item);
                    Take("GRACE", item);
                    break;
                case "BOTHHAVE":
                    Give("GABRIEL", item);
                    Give("GRACE", item);
                    break;
                case "NOTPLACED" or "PLACED" or "USED":
                    Take("GABRIEL", item);
                    Take("GRACE", item);
                    break;
                default:
                    Diagnostics.Add(new Diagnostic(
                        "GK3R3201", DiagnosticSeverity.Warning,
                        $"'{Arg(a, 1)}' is not an inventory status.",
                        null, null, "NotPlaced, Placed, Used, GabeHas, GraceHas or BothHave",
                        Arg(a, 1),
                        "The call is ignored, as it is in the original."));
                    break;
            }

            return SheepValue.FromInt(0);

            void Give(string who, string what) => State.Inventory.Add(who, what);
            void Take(string who, string what) => State.Inventory.Remove(who, what);
        });

        // Milliseconds in the file, seconds here, because everything else that measures
        // time in this engine does. A wait of zero or less is due at once, which the
        // original notes happens "from time to time".
        Register("SetGameTimer", a =>
        {
            State.Timers.Set(Arg(a, 0), Arg(a, 1), Int(a, 2) / 1000.0);
            return SheepValue.FromInt(0);
        });

        RegisterScreenFunctions();

        // SetTimerSeconds and SetTimerMs are a script sleeping, and are already declared
        // waitable among the recorded calls. There is nothing for them to do but take the
        // time, which SecondsFor reports and the scheduler spends.

        // Explicitly answered rather than left unknown: scripts poll these constantly and
        // an unregistered warning for each would drown everything else.
        Register("IsActorNear", Zero);
        Register("IsWalkingActorNear", Zero);
    }

    /// <summary>
    /// The screens a script can put in front of the room.
    /// </summary>
    /// <remarks>
    /// Every one of them goes on the same stack and comes off it the same way, which is
    /// what <c>Plan/03</c> section 3 asks for and what the original did not do. A script
    /// showing the binoculars and a player pressing Back are talking about the same object.
    /// </remarks>
    private void RegisterScreenFunctions()
    {
        Register("ShowInventory", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Inventory));
            return SheepValue.FromInt(0);
        });

        Register("HideInventory", _ =>
        {
            // The inspect panel belongs to the inventory that opened it, so it goes with
            // it. The original leaves it behind, which is a bug it shipped with: scanning
            // an item from the inspect screen left the panel over a room it had no
            // business being over.
            State.Screens.Hide(ScreenKind.InventoryInspect);
            State.Screens.Hide(ScreenKind.Inventory);
            return SheepValue.FromInt(0);
        });

        Register("InventoryInspect", a =>
        {
            State.Screens.Show(new Screen(ScreenKind.InventoryInspect, Arg(a, 0)));
            return SheepValue.FromInt(0);
        });

        Register("InventoryUninspect", _ =>
        {
            State.Screens.Hide(ScreenKind.InventoryInspect);
            return SheepValue.FromInt(0);
        });

        // Looking closely at something in the room is a camera and not a screen: the view
        // moves to a close-up of the thing and the room stays where it is. These used to put
        // up a modal panel instead, which was harmless only for as long as nothing drew it —
        // once the panel had a painter, inspecting the museum's H panel covered the screen
        // with something that looked like the inventory.
        //
        // A scene registers the real ones over these, because only a standing scene has the
        // cameras to move to. Without one there is nothing to look closely at, so these
        // record the intent and show nothing.
        Register("InspectObject", a =>
        {
            State.Inspecting = Arg(a, 0) is { Length: > 0 } what ? what : State.Inspecting;
            return SheepValue.FromInt(0);
        });

        Register("InspectModelUsingAngle", a =>
        {
            State.Inspecting = Arg(a, 1) is { Length: > 0 } angle
                ? angle
                : Arg(a, 0) is { Length: > 0 } model ? model : State.Inspecting;

            return SheepValue.FromInt(0);
        });

        Register("UnInspect", _ =>
        {
            State.Inspecting = string.Empty;
            return SheepValue.FromInt(0);
        });

        Register("ShowBinocs", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Binoculars));
            return SheepValue.FromInt(0);
        });

        Register("ShowDrivingInterface", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Driving));
            return SheepValue.FromInt(0);
        });

        Register("FollowOnDrivingMap", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Driving, "follow"));
            return SheepValue.FromInt(0);
        });

        Register("ShowFingerprintInterface", a =>
        {
            State.Screens.Show(new Screen(ScreenKind.Fingerprint, Arg(a, 0)));
            return SheepValue.FromInt(0);
        });

        // Sidney is a screen like the others and was the one of them the story could not
        // open. Two of the game's scripts call this.
        Register("ShowSidney", _ =>
        {
            State.Screens.Show(new Screen(ScreenKind.Sidney));
            return SheepValue.FromInt(0);
        });

        // Every script is loaded when the story starts, so there is nothing to preload.
        // Answering rather than recording is the honest form here: the call's whole
        // purpose is met, it just costs nothing.
        Register("PreloadSheep", _ => SheepValue.FromInt(0));

        // Which camera a conversation opens on. Both halves, because a script that sets one
        // for a chat clears it afterwards and the next conversation would otherwise inherit
        // a shot framed for two other people.
        Register("SetDefaultDialogueCamera", a =>
        {
            State.DefaultDialogueCamera = Arg(a, 0) is { Length: > 0 } named ? named : null;
            return SheepValue.FromInt(0);
        });

        Register("ClearDefaultDialogueCamera", _ =>
        {
            State.DefaultDialogueCamera = null;
            return SheepValue.FromInt(0);
        });

        // Hit tests, on and off. A script switches them off while something plays so that
        // the player cannot click through it.
        Register("DisableHitTestModel", a =>
        {
            if (Arg(a, 0) is { Length: > 0 } blocked)
            {
                State.BlockedHitTests.Add(blocked);
            }

            return SheepValue.FromInt(0);
        });

        Register("EnableHitTestModel", a =>
        {
            State.BlockedHitTests.Remove(Arg(a, 0));
            return SheepValue.FromInt(0);
        });

        Register("SetVerbModal", a =>
        {
            State.MustChooseAnAction = Int(a, 0) != 0;
            return SheepValue.FromInt(0);
        });

        // The other half of the same switch, from the other direction: StartVerbCancel
        // puts the way out back on the action bar and StopVerbCancel takes it away. 14
        // calls, and they are the moments a script insists the player choose something.
        Register("StartVerbCancel", _ =>
        {
            State.MustChooseAnAction = false;
            return SheepValue.FromInt(0);
        });

        Register("StopVerbCancel", _ =>
        {
            State.MustChooseAnAction = true;
            return SheepValue.FromInt(0);
        });

        Register("IsTopLayerInventory", _ => SheepValue.FromInt(
            State.Screens.IsOnTop(ScreenKind.Inventory) ||
            State.Screens.IsOnTop(ScreenKind.InventoryInspect) ? 1 : 0));
    }

    /// <summary>
    /// Registers the presentation surface as recorded calls.
    /// </summary>
    /// <remarks>
    /// Chosen by measured frequency across the 224 shipped scripts rather than by guess,
    /// so the most-used calls are the ones that stop producing warnings first.
    /// </remarks>
    /// <summary>The two people the player controls, for the calls that mean both of them.</summary>
    private static readonly string[] BothCharacters = ["GABRIEL", "GRACE"];

    private void RegisterRecordedFunctions()
    {
        (string Name, bool Waitable)[] recorded =
        [
            ("CutToCameraAngle", false),
            ("SetCameraAngle", false),
            ("GlideToCameraAngle", true),
            ("ForceCutToCameraAngle", false),
            ("StartAnimation", true),
            ("StartMoveAnimation", true),
            ("StartMorphAnimation", true),
            ("StopAnimation", false),
            ("StartDialogue", true),
            ("StartDialogueNoFidgets", true),
            ("ContinueDialogue", true),
            ("ContinueDialogueNoFidgets", true),
            ("StartVoiceOver", true),
            ("WalkTo", true),
            ("WalkToSeeModel", true),
            ("WalkerBoundaryBlockRegion", false),
            ("WalkerBoundaryUnblockRegion", false),
            ("SetIdleGAS", false),
            ("SetTalkGAS", false),
            ("SetListenGAS", false),
            ("StartIdleFidget", false),
            ("StopFidget", false),
            ("TurnHead", false),
            ("TurnToModel", true),
            ("SetActorPosition", false),
            ("InitEgoPosition", false),
            ("SetTimerSeconds", true),
            ("SetTimerMs", true),
            ("PlaySound", true),
            ("PlaySoundTrack", false),
            ("StopSoundTrack", false),

            // Registered over by SceneScripting once there is a device to silence. A
            // machine with no sound card still has to answer them, or a script that
            // hushes the room before speaking stops there.
            ("StopAllSoundTracks", false),
            ("StopAllSounds", false),
            ("StopSound", false),
            ("EnableCinematics", false),
            ("DisableCinematics", false),
            ("Blink", false),
            ("Expression", false),

            // The rest of what the game's own scripts call. Every one of these was being
            // met with nothing at all, which the machine reports as an unimplemented call
            // and the player sees as a moment that does not happen. Recording them is not
            // implementing them — it is the difference between a known gap and a silent
            // one, and it is what lets a sweep say the surface is covered.
            //
            // Camera work: boundaries the camera may not cross, and the angle types the
            // room cameras are classified by.
            ("SetCameraAngleType", false),
            ("SetCameraGlide", false),

            // Registered over by SceneScripting once there is a scene, the same way the
            // sound calls above are: what they do needs a room, and a console or a tool
            // calling one without a room still has to be answered rather than reported as
            // an unimplemented call.
            ("CameraBoundaryBlockModel", false),
            ("CameraBoundaryUnblockModel", false),
            ("EnableCameraBoundaries", false),
            ("DisableCameraBoundaries", false),
            ("GlideToCameraAngleX", true),
            ("ShowSceneModel", false),
            ("HideSceneModel", false),
            ("SetMood", false),
            ("ClearMood", false),
            ("SetWalkAnim", false),
            ("StartMom", true),
            ("ActionWaitClearRegion", true),
            ("StartPropFidget", false),
            ("StopPropFidget", false),

            // Fidgets: the idle, talking and listening loops a character runs between
            // instructions. They are GAS scripts using the branching half of the language,
            // which is not run; see docs/formats/behaviour-scripts.md.
            ("StartListenFidget", true),
            ("StartTalkFidget", true),
            ("ClearPropGas", false),

            // Momentary animations and glances, which need the face and the walker to
            // agree about who is driving a character.
            ("Glance", true),
            ("GlanceX", true),
            ("LookitPoint", false),
            ("LookitNoun", false),
            ("LookitNounQuick", false),
            ("LookitLock", false),
            ("LookitUnlock", false),
            ("BlinkX", false),
            ("EnableEyeJitter", false),
            ("DisableEyeJitter", false),
            ("EyeJitter", false),

            // Presentation: shadows, lighting overrides and the layers a modal screen
            // draws on. See docs/screens.md for what is state and what is drawing.
            ("EnableModelShadow", false),
            ("DisableModelShadow", false),
            ("SetModelShadowTexture", false),
            ("ClearModelShadowTexture", false),
            ("SetModelLighting", false),
            ("ShowDeathLayer", false),
            ("FinishedScreen", false),
            ("ShowInset", false),
            ("HideInset", false),
            ("ShowPlate", false),
            ("HidePlate", false),
            ("SetPamphletPage", false),

            // Video. The corpus is converted and a player exists; what is missing is the
            // pause of the world around one.
            ("PlayMovie", true),
            ("PlayFullScreenMovie", true),
            ("PlayFullScreenMovieX", true),

            // Construction mode, which builds a scene from a script rather than a file.
            // AddModel and the two SetScenes are registered over by SceneScripting once
            // there is a room: what a script builds is staged while the room loads, and
            // what a script relights is the bake the room is already wearing.
            ("AddModel", false),
            ("AddActor", false),
            ("AddPosition", false),
            ("SetScene", false),
            ("SetSceneNoPreloadTextures", false),
            ("SetBoundaryMap", false),
            ("UploadSceneLightmaps", false),
            ("ResetCaseLogic", false),

            // Verb cancelling, which takes the action bar away while something plays.

            // Recorded on purpose, because the original does nothing with them either.
            // Reproducing a no-op faithfully means leaving it a no-op, and a reader of
            // this list should be able to tell those apart from the gaps.
            //
            //   Glance, GlanceX     eye offsets, and nothing here has eyes to offset;
            //                       commented out in the reference too
            //   SetCameraAngleType  logs its arguments and returns
            //   StartMorphAnimation, StopMorphAnimation   commented out in the reference
            //   UploadSceneLightmaps  lightmaps are uploaded with the scene already
            //
            // The rest.
            ("AddCaptionVoiceOver", false),
            ("StartDialogueX", true),
            ("SetActorOffstage", false),
            ("Warp", true),
            ("WalkNear", true),
            ("WalkNearModel", true),
            ("DefaultInspect", true),
            ("StopMorphAnimation", false),
            ("ScreenShot", false),
            ("SetTopSheep", false),
            ("CallDefaultSheep", true),
            ("ClownShoes", false),

            // The last of the specification's gameplay surface. None of these is called by
            // any of the game's own scripts — they are here so the surface is closed rather
            // than nearly closed, and so that a console or a mod calling one is answered.
            ("ShowModelGroup", false),
            ("HideModelGroup", false),
            ("LookitModelX", false),
            ("LookitModelQuickX", false),
        ];

        foreach ((string name, bool waitable) in recorded)
        {
            string captured = name;
            Register(name, a =>
            {
                Events.Add(new RecordedEvent(captured, [.. a.Select(v => v.AsString())]));
                return SheepValue.FromInt(0);
            }, waitable);
        }
    }

    private static string Arg(IReadOnlyList<SheepValue> arguments, int index) =>
        index < arguments.Count ? arguments[index].AsString() : string.Empty;

    private static int Int(IReadOnlyList<SheepValue> arguments, int index) =>
        index < arguments.Count ? arguments[index].AsInt() : 0;
}
