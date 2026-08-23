using GK3Reborn.Game;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for how long the script host thinks a line of dialogue takes.
/// </summary>
/// <remarks>
/// Reported as voices being cut off, and worse the longer the recording. The four dialogue
/// calls are marked waitable and were worth nothing, so a waited block containing one
/// finished in the frame it began: the script ran straight on to the next line, and starting
/// a line abandons whatever is being said. Every exchange in the game was talking over
/// itself.
/// </remarks>
public sealed class DialogueTimingTests
{
    private static Gk3SheepApi Api() => new(new GameState());

    private static IReadOnlyList<SheepValue> Args(params object[] values) =>
    [
        .. values.Select(v => v is int i ? SheepValue.FromInt(i) : SheepValue.FromString((string)v)),
    ];

    /// <summary>Every dialogue call is one a wait block can wait on.</summary>
    [Theory]
    [InlineData("StartDialogue")]
    [InlineData("StartDialogueNoFidgets")]
    [InlineData("ContinueDialogue")]
    [InlineData("ContinueDialogueNoFidgets")]
    [InlineData("StartVoiceOver")]
    public void A_dialogue_call_is_waitable(string name) => Assert.True(Api().IsWaitable(name));

    /// <summary>
    /// A continuation is worth as long as the lines it continues, and asks whatever is
    /// speaking.
    /// </summary>
    /// <remarks>
    /// It names no licence plate — only how many more lines — because the run it belongs to
    /// was named once, several statements ago. So it is the one duration the host cannot
    /// work out for itself.
    /// </remarks>
    [Fact]
    public void A_continuation_is_worth_as_long_as_the_lines_it_continues()
    {
        Gk3SheepApi api = Api();

        int asked = 0;

        api.ContinuedSeconds = lines =>
        {
            asked = lines;
            return 4.5;
        };

        Assert.Equal(4.5, api.SecondsFor("ContinueDialogue", Args(3)), 3);
        Assert.Equal(3, asked);
        Assert.Equal(4.5, api.SecondsFor("ContinueDialogueNoFidgets", Args(3)), 3);
    }

    /// <summary>A continuation of nothing takes no time rather than hanging the script.</summary>
    [Fact]
    public void A_continuation_of_nothing_takes_no_time()
    {
        Assert.Equal(0, Api().SecondsFor("ContinueDialogue", Args(2)), 3);
    }

    /// <summary>
    /// Starting a conversation goes through the same reckoning a voice-over does.
    /// </summary>
    /// <remarks>
    /// Both take a licence plate and a line count, and both are a run of the same recordings
    /// — the two calls differ in whether the speakers play their idles, which is not a matter
    /// of timing. Without an animation library neither can answer, and answering nought there
    /// is right: nothing has been read, so there is nothing to wait for.
    /// </remarks>
    [Theory]
    [InlineData("StartDialogue")]
    [InlineData("StartDialogueNoFidgets")]
    [InlineData("StartVoiceOver")]
    public void Starting_a_conversation_is_reckoned_like_a_voice_over(string name)
    {
        Gk3SheepApi api = Api();

        // No library attached, so all three agree on nought — the point being that they
        // agree, and that the dialogue calls no longer fall through to the default.
        Assert.Equal(
            api.SecondsFor("StartVoiceOver", Args("1E4CU4OCZ1", 2)),
            api.SecondsFor(name, Args("1E4CU4OCZ1", 2)),
            3);
    }
}
