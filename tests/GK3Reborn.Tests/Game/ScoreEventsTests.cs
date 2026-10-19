using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class ScoreEventsTests
{
    [Fact]
    public void Shipped_maximum_is_not_the_sum_of_all_possible_outcomes()
    {
        ScoreEvents scores = ScoreEvents.Open();

        Assert.Equal(965, scores.Maximum);
        Assert.True(scores.Names.Sum(name => scores.Worth(name) ?? 0) > scores.Maximum);
        Assert.Equal(35, scores.Worth("e_temple_radio_grace_about_demon") +
            scores.Worth("e_temple_get_on_sarcophagus") + scores.Worth("e_temple_kill_demon"));
        Assert.Equal(965, new Gk3SheepApi(new GameState()).Invoke("GetMaxScore", []).AsInt());
    }

    [Fact]
    public void Custom_tables_still_sum_their_events()
    {
        Assert.Equal(7, ScoreEvents.Parse("first = 2, second = 5").Maximum);
    }
}
