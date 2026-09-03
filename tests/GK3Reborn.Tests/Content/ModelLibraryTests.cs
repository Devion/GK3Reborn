using GK3Reborn.Content;
using GK3Reborn.Formats.Rebarn;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for the library that supplies prop geometry the game never had.
/// </summary>
/// <remarks>
/// One property carries the whole design and the rest follow from it: this may only answer
/// for names the 1999 archives have no <c>.MOD</c> for. A library that could stand in front
/// of the game's own props would replace every chair and lamp in the game the moment a
/// content workspace happened to hold a mesh of the same name — and the workspace does hold
/// several hundred of exactly those, because the mesh-enhancement pass writes them into the
/// same directory.
/// </remarks>
public sealed class ModelLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gk3reborn-models-" + Guid.NewGuid().ToString("N"));

    public ModelLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_directory_that_is_not_there_costs_nothing_and_answers_nothing()
    {
        ModelLibrary library = ModelLibrary.Open(Path.Combine(_root, "nowhere"));

        Assert.True(library.IsEmpty);
        Assert.Equal(0, library.Count);
        Assert.False(library.Has("r29_madsuitcase"));
        Assert.Null(library.Describe());
    }

    [Fact]
    public void Only_gltf_is_indexed()
    {
        File.WriteAllBytes(Path.Combine(_root, "r29_madsuitcase.glb"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "notes.txt"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "R29_MADSUITCASE.png"), [1]);

        ModelLibrary library = ModelLibrary.Open(_root);

        Assert.Equal(1, library.Count);
        Assert.True(library.Has("r29_madsuitcase"));

        // Named without regard to case, like every other asset in this game.
        Assert.True(library.Has("R29_MadSuitcase"));
        Assert.False(library.Has("notes"));
    }

    [Fact]
    public void A_file_that_will_not_parse_places_nothing_rather_than_throwing()
    {
        File.WriteAllBytes(Path.Combine(_root, "broken.glb"), [0, 1, 2, 3, 4]);

        ModelLibrary library = ModelLibrary.Open(_root);

        Assert.True(library.Has("broken"));
        Assert.Null(library.Read("broken"));

        // And it is not re-read on the next room: the answer is kept either way.
        Assert.Null(library.Read("broken"));
    }

    [Fact]
    public void The_count_is_of_loose_files_and_says_where_they_came_from()
    {
        File.WriteAllBytes(Path.Combine(_root, "one.glb"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "two.gltf"), [1]);

        ModelLibrary library = ModelLibrary.Open(_root);

        Assert.Equal(2, library.Count);
        Assert.False(library.IsEmpty);
        Assert.Equal("2 loose", library.Describe());
    }

    [Fact]
    public void Empty_is_shared_and_never_takes_a_players_overrides()
    {
        // Open returns the shared Empty when there is nowhere to look. Anything that then
        // set overrides on it would be setting them on every other library in the process,
        // so the one it hands back has to have nowhere for them to go.
        ModelLibrary first = ModelLibrary.Open(string.Empty);
        ModelLibrary second = ModelLibrary.Open(string.Empty);

        Assert.Same(first, second);
        Assert.Same(ModelLibrary.Empty, first);
        Assert.Null(ModelLibrary.Empty.Overrides);
    }

    [Fact]
    public void Overrides_outrank_the_loose_directory()
    {
        File.WriteAllBytes(Path.Combine(_root, "r31_wenletter.glb"), [0, 1, 2, 3]);

        string elsewhere = Path.Combine(_root, "over");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "r31_wenletter.glb"), [9, 9, 9, 9]);

        ModelLibrary library = ModelLibrary.Open(_root);
        library.Overrides = ContentOverrides.Open(elsewhere);

        Assert.True(library.Overrides!.Has(RebarnKind.Model, "r31_wenletter"));
        Assert.True(library.Has("r31_wenletter"));

        // Neither parses; what is being pinned is that the override is the one consulted,
        // which its own count reports.
        Assert.Equal(1, library.Overrides.CountOf(RebarnKind.Model));
        Assert.Contains("overridden", library.Describe(), StringComparison.Ordinal);
    }
}
