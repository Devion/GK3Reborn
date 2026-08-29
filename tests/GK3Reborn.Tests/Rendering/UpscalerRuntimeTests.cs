using GK3Reborn.Rendering.Upscaling;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for finding the vendors' runtimes, which the game ships none of.
/// </summary>
/// <remarks>
/// The important behaviour is what happens when they are <em>not</em> there, because that
/// is what every machine without them looks like and because the one thing this must never
/// do is stop the game starting. Written against a directory made for the test rather than
/// against whatever happens to be installed on the machine running it.
/// </remarks>
public sealed class UpscalerRuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "gk3reborn-libs-" + Guid.NewGuid().ToString("N"));

    public UpscalerRuntimeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test that cannot tidy up is not a test that failed.
        }
    }

    private void Put(params string[] names)
    {
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(_directory, name), "not really a library");
        }
    }

    [Fact]
    public void Nothing_installed_is_an_ordinary_answer_rather_than_an_error()
    {
        UpscalerRuntimes found = UpscalerRuntimes.Find(_directory);

        Assert.False(found.Fsr.Present);
        Assert.False(found.Dlss.Present);

        // And it says which file it wanted, because "not installed" without a file name is
        // a support question rather than an answer.
        Assert.Contains(UpscalerRuntimes.FidelityFx, found.Fsr.Missing);
        Assert.Contains(UpscalerRuntimes.StreamlineInterposer, found.Dlss.Missing);
    }

    [Fact]
    public void The_two_the_engine_carries_itself_are_always_present()
    {
        UpscalerRuntimes found = UpscalerRuntimes.Find(_directory);

        Assert.True(found.For(UpscalerKind.Off).Present);
        Assert.True(found.For(UpscalerKind.Spatial).Present);
    }

    [Fact]
    public void One_file_is_enough_for_fidelityfx_and_three_are_needed_for_dlss()
    {
        Put(UpscalerRuntimes.FidelityFx);

        UpscalerRuntimes found = UpscalerRuntimes.Find(_directory);

        Assert.True(found.Fsr.Present);
        Assert.False(found.Dlss.Present);

        // The loader alone is not DLSS: without the plugin and the network it starts and
        // then has nothing to run.
        Put(UpscalerRuntimes.StreamlineInterposer);
        Assert.False(UpscalerRuntimes.Find(_directory).Dlss.Present);

        Put(UpscalerRuntimes.StreamlineSuperResolution, UpscalerRuntimes.NgxSuperResolution);
        Assert.True(UpscalerRuntimes.Find(_directory).Dlss.Present);
    }

    [Fact]
    public void Frame_generation_and_ray_reconstruction_are_asked_after_separately()
    {
        Put(
            UpscalerRuntimes.StreamlineInterposer,
            UpscalerRuntimes.StreamlineSuperResolution,
            UpscalerRuntimes.NgxSuperResolution);

        UpscalerRuntimes found = UpscalerRuntimes.Find(_directory);

        Assert.True(found.Dlss.Present);
        Assert.False(found.DlssFrameGeneration.Present);
        Assert.False(found.DlssRayReconstruction.Present);

        Put(UpscalerRuntimes.StreamlineFrameGeneration, UpscalerRuntimes.NgxFrameGeneration);

        Assert.True(UpscalerRuntimes.Find(_directory).DlssFrameGeneration.Present);
    }

    [Fact]
    public void A_file_can_be_found_by_name_for_whoever_has_to_open_it()
    {
        Put(UpscalerRuntimes.FidelityFx);

        UpscalerRuntimes found = UpscalerRuntimes.Find(_directory);

        Assert.Equal(
            Path.Combine(_directory, UpscalerRuntimes.FidelityFx),
            found.Locate(UpscalerRuntimes.FidelityFx));

        Assert.Null(found.Locate("something-nobody-installed.dll"));
    }

    [Fact]
    public void Where_it_looked_is_reported_whether_or_not_it_found_anything()
    {
        // A player who copied the files into the wrong directory has no other way to find
        // out: the settings row would say "not installed" and they would already believe
        // they had installed it.
        UpscalerRuntimes found = UpscalerRuntimes.Find(_directory);

        Assert.Contains(_directory, found.Searched);
        Assert.Contains(
            Path.Combine(AppContext.BaseDirectory, UpscalerRuntimes.LibraryDirectory),
            found.Searched);

        // And NVIDIA's own download unpacks to a nested directory, which is looked in too
        // rather than being a thing the player has to flatten first.
        Assert.Contains(
            Path.Combine(AppContext.BaseDirectory, UpscalerRuntimes.LibraryDirectory, "streamline"),
            found.Searched);
    }

    [Fact]
    public void The_startup_line_names_every_runtime()
    {
        string line = UpscalerRuntimes.Find(_directory).ToString();

        Assert.Contains("FSR", line, StringComparison.Ordinal);
        Assert.Contains("DLSS", line, StringComparison.Ordinal);
        Assert.Contains("frame generation", line, StringComparison.Ordinal);
        Assert.Contains("ray reconstruction", line, StringComparison.Ordinal);
    }
}
