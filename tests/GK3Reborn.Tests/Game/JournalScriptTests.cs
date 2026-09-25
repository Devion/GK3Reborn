using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>Runs original award scripts with their actual dialogue and animation clocks.
/// Requires a local extraction; no game assets are included in the test assembly.</summary>
public sealed class JournalScriptTests
{
    [Theory]
    [InlineData("MS3", "GabListenLHE", "e_110a_ms3_hoverhear_howard_estelle")]
    [InlineData("MS3", "GabLHEIntro", "e_110a_ms3_talk_howard_estelle_introduce")]
    [InlineData("MS3", "GabLHETresure", "e_110a_ms3_talk_howard_estelle_treasure")]
    [InlineData("MS2110A", "T_INTRODUCE", "e_110a_ms2_talk_girard_introduce")]
    [InlineData("MS2110A", "T_HOLY_GRAIL", "e_110a_ms2_talk_girard_grail")]
    [InlineData("MS2110A", "T_TREASURE2", "e_110a_ms2_talk_girard_treasure_2nd_time")]
    [InlineData("LBY110A", "IntroEmilio1", "e_110a_lby_talk_emilio_introduce")]
    [InlineData("LBY110A", "TalkEmilio2", "e_110a_lby_talk_emilio_checkin")]
    [InlineData("LBY_ALL", "T_TWO_M_T2", "e_110a_lby_talk_jean_two_men")]
    [InlineData("LBY_ALL", "seeRegister1", "e_110a_lby_read_register")]
    [InlineData("LBY110A", "TalkJean2", "e_110a_lby_talk_jean_tour")]
    [InlineData("RC1110A", "T_CHECK_IN2", "e_110a_rc1_talk_madeline_checkin")]
    [InlineData("RC1110A", "T_TOUR_GROUP1A", "e_110a_rc1_talk_madeline_tour")]
    [InlineData("RC1110A", "SanGrealWords", "e_110a_rc1_look_at_grail_book")]
    [InlineData("DIN110A", "GabCoffee", "e_110a_din_enter")]
    [InlineData("PHO110A", "DoPhoneScene1", "e_110a_pho_phone_prince_james")]
    [InlineData("R25_ALL", "Hanger", "e_110a_r25_hanger")]
    [InlineData("R25_ALL", "TakeTape", "e_110a_r25_tape")]
    public void Original_scripts_reach_their_journal_awards_with_and_without_skipping(string script, string function, string score)
    {
        string? root = FindContent();
        Assert.SkipUnless(root is not null, "needs ContentWorkspace/normalized or GK3_NORMALIZED_CONTENT");
        foreach (bool skipping in new[] { false, true })
        {
            string? Read(string name)
            {
                foreach (string directory in new[] { "animation-scripts", "dialogue" })
                {
                    string path = Path.Combine(root, directory, name);
                    if (File.Exists(path))
                    {
                        return File.ReadAllText(path);
                    }
                }
                return null;
            }

            var state = new GameState { Timeblock = new Timeblock(1, 10, false) };
            var animations = new AnimationLibrary(Read);
            var api = new Gk3SheepApi(state) { Animations = animations };
            var host = new ScriptHost(api);
            var scheduler = new SheepScheduler(host.Machine);
            host.Scheduler = scheduler;
            host.Add(SheepScriptFile.Parse(File.ReadAllBytes(Path.Combine(root, "scripts", script + ".SHP")), script + ".SHP"));
            var scene = new LoadedScene("TEST", new SceneDefinition(SceneInitFile.Parse("[GENERAL]", "TEST.SIF")), null, null, 0);
            var world = new SceneUpdate(scene, api, new Glances(), new HeadlessSceneSink(), scripts: scheduler);
            using var device = new SilentDevice();
            var audio = new SceneAudio(new SoundLibrary(_ => null), animations, device);
            SceneScripting.Attach(api, scene, audio: audio, world: world);
            // Exercise the successful eavesdropping branch. Geometry and navigation
            // are intentionally outside this test; speech and VM waits are not stubbed.
            api.Register("IsActorNear", _ => SheepValue.FromInt(1));
            SheepThread? thread = host.Run(script, function);
            Assert.NotNull(thread);
            for (int frame = 0; frame < 36_000 && scheduler.Count > 0; frame++)
            {
                world.Advance(1.0 / 60);
                if (skipping)
                {
                    audio.Skip();
                }
                audio.Update(1.0 / 60);
            }
            Assert.Equal(SheepThreadState.Completed, thread.State);
            Assert.True(state.HasScored(score), $"{script}:{function}, skipping={skipping}, awards: {string.Join(", ", state.Scored)}");
        }
    }

    private static string? FindContent()
    {
        if (Environment.GetEnvironmentVariable("GK3_NORMALIZED_CONTENT") is { Length: > 0 } configured && Directory.Exists(configured))
        {
            return configured;
        }
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "ContentWorkspace", "normalized");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private sealed class SilentDevice : IAudioBackend
    {
        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;
        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;
        public int Playing => 0;
        public AudioVoice Play(WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null) => AudioVoice.None;
        public bool IsPlaying(AudioVoice voice) => false;
        public void SetBusGain(AudioBus bus, float gain) { }
        public void SetVoiceGain(AudioVoice voice, float gain) { }
        public void Move(AudioVoice voice, Vector3 position) { }
        public void Listen(Vector3 position, Vector3 forward, Vector3 up) { }
        public void Silence(AudioVoice voice) { }
        public void StopBus(AudioBus bus) { }
        public void Update() { }
        public void Dispose() { }
    }
}
