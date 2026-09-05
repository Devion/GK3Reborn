using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GK3Reborn.Content;
using GK3Reborn.Game;
using GK3Reborn.Rendering.Upscaling;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the port's own interface in a language other than English.
/// </summary>
/// <remarks>
/// <para>
/// GK3's own strings are read out of the archives through the language pack and were
/// already right. Everything the port added — the main menu, the five settings sections,
/// the toolbar, the journal, the way out of every screen, and the ninety verbs the
/// original drew as icons rather than words — had no 1999 counterpart to read, so it was
/// English in every language.
/// </para>
/// <para>
/// The two checks that matter here are not about the words. One is that <b>the six files
/// hold exactly the same keys</b>: a key present in five of them and absent from the sixth
/// is a row that quietly reads English in one language and in no other, which nobody sees
/// until they are playing in it. The other is that <b>the English in the file is the
/// English in the source</b>, because the call sites carry their own fallback so that the
/// code can be read — and a duplicated string that nothing compares is a duplicated string
/// that drifts.
/// </para>
/// </remarks>
public sealed partial class UiTextTests
{
    /// <summary>The languages the port carries words for.</summary>
    private static readonly string[] Carried = ["en", "de", "es", "fr", "it", "pt"];

    private static Dictionary<string, string> Words(string code)
    {
        byte[]? bytes = UiText.CarriedBytes(code);

        Assert.NotNull(bytes);

        Dictionary<string, string>? read =
            JsonSerializer.Deserialize<Dictionary<string, string>>(bytes!);

        Assert.NotNull(read);

        return read!;
    }

    [Fact]
    public void The_port_carries_a_file_for_every_language_it_claims_to()
    {
        foreach (string code in Carried)
        {
            Assert.True(
                Words(code).Count > 200, $"interface-{code}.json is too small to be whole");
        }
    }

    [Fact]
    public void Every_language_holds_exactly_the_keys_English_does()
    {
        Dictionary<string, string> english = Words("en");

        foreach (string code in Carried.Where(c => c != "en"))
        {
            Dictionary<string, string> other = Words(code);

            Assert.Equal(
                english.Keys.OrderBy(k => k, StringComparer.Ordinal),
                other.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Nothing_is_blank_and_nothing_but_a_name_is_still_English()
    {
        // A blank value falls back to the English the call site carries, which is the right
        // behaviour and the wrong thing to ship: it looks translated in the file and is not.
        Dictionary<string, string> english = Words("en");

        // What may legitimately be the same word in another language: names, numbers,
        // formats made of nothing but a placeholder, and the handful of words European
        // languages share.
        var borrowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "controls.gamepad", "general.eggs", "picture.upscaling", "picture.nrstyle",
            "sound.speakers.surround", "sound.speakers.stereo", "screen.journal",
            "hud.journal", "value.on", "value.off", "list.comma", "display.nits",
            "journal.tally", "save.slot", "menu.intro", "verb.MOSELY", "verb.RADIO",
            "verb.GUN", "verb.EGG", "verb.ZOOM", "verb.TALISMAN_TE6", "verb.PLAY",
            "picture.ratio.performance", "picture.ratio.balanced", "verb.CLICK",
            "picture.latency.boost", "display.backend.automatic", "menu.title.paused",
            "verb.SELECT", "verb.TOUCH", "verb.PET", "verb.TURN_ON", "verb.WRITE",
            "verb.PULL", "verb.PUSH", "verb.EXIT", "verb.EXIT_ARROW", "verb.USE",
        };

        foreach (string code in Carried.Where(c => c != "en"))
        {
            Dictionary<string, string> other = Words(code);

            foreach ((string key, string said) in other)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(said), $"{key} is blank in {code}");
            }

            // Not a rule about every row — plenty of words are shared across these six
            // languages — but a whole file that matched English would be a file nobody
            // translated, and that is what this counts.
            int same = other.Count(pair =>
                !borrowed.Contains(pair.Key) &&
                string.Equals(pair.Value, english[pair.Key], StringComparison.Ordinal));

            Assert.True(
                same < other.Count / 8,
                $"{same} of {other.Count} phrases in {code} are still the English");
        }
    }

    /// <summary>Every <c>Say("key", "English")</c> the engine's own sources carry.</summary>
    [GeneratedRegex(
        """Say\(\s*"(?<key>[A-Za-z0-9_.]+)"\s*,\s*(?<english>"(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)""")]
    private static partial Regex Calls();

    private static string EngineRoot
    {
        get
        {
            string repository = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "RepositoryRoot")
                .Value!;

            return Path.Combine(repository, "src", "GK3Reborn.Engine");
        }
    }

    [Fact]
    public void The_English_in_the_file_is_the_English_in_the_source()
    {
        // The call sites keep their own English so that the code can be read: a line saying
        // Say("picture.trees") tells nobody what the row says. The price of that is two
        // copies of every phrase, and this is what stops them drifting.
        Dictionary<string, string> english = Words("en");
        List<string> wrong = [];
        int found = 0;

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(EngineRoot, "UI"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(EngineRoot, "Application.cs")))
        {
            foreach (Match call in Calls().Matches(File.ReadAllText(file)))
            {
                string key = call.Groups["key"].Value;

                // Sidney's words come out of ESIDNEY.TXT and are not in this file at all.
                if (!english.TryGetValue(key, out string? said))
                {
                    continue;
                }

                found++;

                string wanted = string.Concat(
                    Regex.Matches(call.Groups["english"].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"")
                        .Select(m => m.Groups[1].Value.Replace("\\\"", "\"")));

                if (!string.Equals(said, wanted, StringComparison.Ordinal))
                {
                    wrong.Add($"{key}: source has \"{wanted}\", the file has \"{said}\"");
                }
            }
        }

        Assert.True(found > 100, $"only {found} call sites were found; the scan is broken");
        Assert.Empty(wrong);
    }

    [Fact]
    public void Every_verb_the_original_drew_as_a_picture_has_a_word()
    {
        // VERBS.TXT lists 287 entries and ninety of them are verbs rather than things the
        // player carries. The original never wrote any of them down — it drew icons — so
        // there is no 1999 string to read and the port has to carry them. They are also
        // the text a player reads more often than anything else in the game.
        Dictionary<string, string> english = Words("en");

        Assert.True(
            english.Keys.Count(k => k.StartsWith("verb.", StringComparison.Ordinal)) >= 90,
            "the ninety verbs are not all there");

        foreach (string verb in new[]
        {
            "verb.LOOK", "verb.TALK", "verb.OPEN", "verb.PICKUP", "verb.SCANNER",
            "verb.THINK", "verb.EXAMINE", "verb.Z_CHAT",
        })
        {
            Assert.True(english.ContainsKey(verb), $"{verb} has no word");
        }
    }

    [Fact]
    public void A_language_with_no_words_of_its_own_answers_in_English()
    {
        // Russian has a code page, a prefix and a pack, and nobody has written its
        // interface. What it loses is the interface, not the game.
        UiText russian = UiText.Carried("ru");

        Assert.Equal("Settings", russian.Say("menu.options", "Settings"));
        Assert.Equal("Einstellungen", UiText.Carried("de").Say("menu.options", "Settings"));
    }

    [Fact]
    public void A_key_nobody_wrote_draws_the_English_the_call_site_carries()
    {
        Assert.Equal(
            "Something new", UiText.Carried("fr").Say("nothing.here.yet", "Something new"));
    }

    [Fact]
    public void The_settings_screen_reads_in_the_players_own_language()
    {
        // The whole point, said as a test of the screen rather than of the file: the rows
        // are built from the settings and the words come from beside them.
        var front = new FrontEnd(new Settings()) { Text = UiText.Carried("de") };

        front.Show(FrontEndPage.Gameplay);

        Assert.Equal("Einstellungen", front.Title);
        Assert.Contains(front.Items, row => row.Text == "Sprache");
        Assert.Contains(front.Items, row => row.Text == "Easter Eggs");

        // And the sidebar with it, keyed on the section's identifier so a click still
        // answers to "tab:gameplay" whatever the tab says.
        Assert.Equal("Allgemein", front.Tabs[0].Text);
        Assert.Equal("gameplay", front.Tabs[0].Id);
        Assert.Equal("Bild", front.Tabs[1].Text);
    }

    [Fact]
    public void A_toggle_says_on_and_off_in_the_players_own_language()
    {
        // MenuItem.Toggle writes those two words itself and knows nothing about language,
        // so every toggle on every page goes through the front end's own wrapper. A page
        // reading "Ein" beside "Higher-resolution textures" would be the wrapper missed.
        var front = new FrontEnd(new Settings { EasterEggs = true })
        {
            Text = UiText.Carried("fr"),
        };

        front.Show(FrontEndPage.Gameplay);

        MenuItem eggs = front.Items.First(row => row.Id == "eggs");

        Assert.Equal("Oui", eggs.Value);
        Assert.DoesNotContain(front.Items, row => row.Value == "On" || row.Value == "Off");
    }

    [Fact]
    public void The_main_menu_and_the_save_slots_read_in_it_too()
    {
        var front = new FrontEnd(new Settings(), inGame: true)
        {
            Text = UiText.Carried("es"),
        };

        Assert.Equal("Pausa", front.Title);
        Assert.Contains(front.Items, row => row.Text == "Continuar");

        front.Show(FrontEndPage.Save);

        Assert.Equal("Guardar partida", front.Title);
        Assert.Contains(front.Items, row => row.Text.StartsWith("Ranura 1", StringComparison.Ordinal));
        Assert.Contains(front.Items, row => row.Text == "Atrás");
    }

    [Fact]
    public void The_controls_page_names_the_actions_and_not_only_its_headings()
    {
        // Reported: the headings on the Commandes page read in French and every row under
        // them did not. The rows are built from InputBindings.Name and GamepadButtons, both
        // of which live in Platform and have no idea what language the game is in — so the
        // page asks for each of them by the action's own name and falls back to the English
        // those two already give.
        var front = new FrontEnd(new Settings()) { Text = UiText.Carried("fr"), HasGamepad = true };

        front.Show(FrontEndPage.Controls);

        Assert.Contains(front.Items, row => row.Text == "Inventaire");
        Assert.Contains(front.Items, row => row.Text == "Sauvegarde rapide");
        Assert.Contains(front.Items, row => row.Text == "Caméra en avant");
        Assert.Contains(front.Items, row => row.Text == "Demander ce que ça fait");

        // Nothing on the page is still the English it fell back from.
        Assert.DoesNotContain(front.Items, row => row.Text == "Quick save");
        Assert.DoesNotContain(front.Items, row => row.Text == "Camera forward");
    }

    [Fact]
    public void The_picture_page_values_the_renderer_names_read_in_it_too()
    {
        // The other half of the same fault: a row's label came from the front end and its
        // *value* came from the renderer, so a French page carried "Whatever the network
        // prefers" beside "Réseau".
        var front = new FrontEnd(
            new Settings
            {
                Upscaler = UpscalerKind.Dlss,
                NeuralUplift = true,
                NeuralPreset = 0,
                DlssPreset = 0,
                FrameGeneration = FrameGeneration.Off,
            })
        {
            Text = UiText.Carried("fr"),
            DlssAvailable = true,
            Runtimes = null,
        };

        front.Show(FrontEndPage.Video);

        string[] values = [.. front.Items.Select(row => row.Value)];

        Assert.DoesNotContain("Whatever the network prefers", values);
        Assert.DoesNotContain("Whatever the runtime prefers", values);
        Assert.DoesNotContain("Off", values);
        Assert.Contains("Ce que préfère le runtime", values);
    }

    [Fact]
    public void A_pack_beats_the_copy_inside_the_assembly()
    {
        // Which is what lets somebody correct a translation, or add one for a language the
        // port carries nothing for, by shipping a pack rather than rebuilding the game.
        Assert.Equal(
            "Einstellungen", UiText.Of(GameLanguage.Of("de"), null).Say("menu.options", "S"));

        // And a language nobody has a pack or a file for still answers, in English.
        Assert.Equal("S", UiText.Of(GameLanguage.Of("ru"), null).Say("menu.options", "S"));
    }
}
