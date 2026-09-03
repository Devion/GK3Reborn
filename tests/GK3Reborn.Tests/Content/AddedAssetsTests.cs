using System.Text;
using GK3Reborn.Content;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for the assets the remake adds, which no barn has.
/// </summary>
/// <remarks>
/// This is the layer most able to do harm and so the one with the strictest rule: it is
/// consulted after every archive, so it can only ever answer for a name the game does not
/// know, and it is empty unless the player asked for cut content. A replaced <c>.SIF</c>
/// is a replaced room.
/// </remarks>
public sealed class AddedAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gk3reborn-added-" + Guid.NewGuid().ToString("N"));

    public AddedAssetsTests()
    {
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "TE2.SIF"), "[GENERAL]\nscene=TE2A\n");
        File.WriteAllText(Path.Combine(_root, "TE2309P.NVC"), "X, LOOK, ALL, script={}\n");
        File.WriteAllBytes(Path.Combine(_root, "TE2WLKBNDS.BMP"), [1, 2, 3]);

        // The room's geometry lives in the same directory and is not a 1999 asset. Putting
        // it into the archive listing would name something nothing there can parse.
        File.WriteAllBytes(Path.Combine(_root, "Te2.glb"), [4, 5, 6]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_directory_that_is_not_there_adds_nothing()
    {
        AddedAssets none = AddedAssets.Open(Path.Combine(_root, "nowhere"));

        Assert.True(none.IsEmpty);
        Assert.Equal(0, none.Count);
        Assert.False(none.Has("TE2.SIF"));
        Assert.Null(none.Read("TE2.SIF"));
        Assert.Null(none.Describe());
    }

    [Fact]
    public void A_room_is_carried_whole()
    {
        // A room needs all three: something to load, something to walk on, and something to
        // do. Any one of them missing is a room that opens and cannot be played.
        AddedAssets added = AddedAssets.Open(_root);

        Assert.True(added.Has("TE2.SIF"));
        Assert.True(added.Has("TE2309P.NVC"));
        Assert.True(added.Has("TE2WLKBNDS.BMP"));
        Assert.Equal(3, added.Count);
    }

    [Fact]
    public void The_geometry_beside_them_is_not_one_of_them()
    {
        // glTF in this directory is the room library's business. Naming it here would put
        // it in the archive listing, where every name is a 1999 asset.
        AddedAssets added = AddedAssets.Open(_root);

        Assert.False(added.Has("Te2.glb"));
        Assert.DoesNotContain("Te2.glb", added.Names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void What_is_added_is_what_is_read_back()
    {
        AddedAssets added = AddedAssets.Open(_root);

        Assert.Equal(
            "[GENERAL]\nscene=TE2A\n",
            Encoding.Latin1.GetString(added.Read("TE2.SIF")!));

        // Named without regard to case, like every other asset in this game.
        Assert.NotNull(added.Read("te2.sif"));
    }

    [Fact]
    public void The_names_are_the_whole_file_name_because_that_is_how_an_archive_is_asked()
    {
        AddedAssets added = AddedAssets.Open(_root);

        Assert.Contains("TE2.SIF", added.Names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("TE2", added.Names, StringComparer.OrdinalIgnoreCase);
    }
}
