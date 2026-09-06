using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using GK3Reborn.Foundation;
using GK3Reborn.Rendering;
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
/// The two checks that matter here are not about the words. One is that <b>every file
/// holds exactly the same keys</b>: a key present in all but one of them is a row that
/// quietly reads English in one language and in no other, which nobody sees until they
/// are playing in it. The other is that <b>the English in the file is the
/// English in the source</b>, because the call sites carry their own fallback so that the
/// code can be read — and a duplicated string that nothing compares is a duplicated string
/// that drifts.
/// </para>
/// </remarks>
public sealed partial class UiTextTests
{
    /// <summary>The languages the port carries words for.</summary>
    private static readonly string[] Carried = ["en", "cs", "de", "es", "fr", "it", "pl", "pt"];

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

            // Not a rule about every row — plenty of words are shared across these
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
    public void Every_thing_a_character_can_be_asked_about_has_a_word()
    {
        // The other eighty-nine entries of VERBS.TXT: the conversation topics, which are
        // verbs of type Topic and which this port puts on the verb bar directly rather than
        // behind a second click on Talk. They were the half of that file nobody wrote down,
        // so a French game hovering over Emilio read "Ask About Introduce" — the English
        // frame the tidier makes up, wrapped around a bare identifier. Reported as such.
        Dictionary<string, string> english = Words("en");

        Assert.True(
            english.Keys.Count(k => k.StartsWith("verb.T_", StringComparison.Ordinal)) >= 89,
            "the eighty-nine topics are not all there");

        foreach (string topic in new[]
        {
            // Two of the four that a right click on the hotel's receptionist offers, one
            // that is not a question at all, and the one whose subject is a French title
            // no language translates.
            "verb.T_RENNES_L_C", "verb.T_TWO_MEN_TRUNK", "verb.T_INTRODUCE",
            "verb.T_LE_SERPENT_ROUGE",
        })
        {
            Assert.True(english.ContainsKey(topic), $"{topic} has no word");
        }

        // And no language may leave one reading as its identifier: the fallback is what
        // was wrong, so a table that still contains it has not been filled in.
        foreach (string code in Carried)
        {
            foreach ((string key, string said) in Words(code))
            {
                Assert.False(
                    key.StartsWith("verb.T_", StringComparison.Ordinal) &&
                    said.Contains("T_", StringComparison.Ordinal),
                    $"{code}: {key} still reads as its identifier");
            }
        }
    }

    [Fact]
    public void Every_noun_the_original_never_named_has_a_word()
    {
        // The other family GK3 never wrote down. Its string table names the 293 things the
        // player carries and nothing else in a room — DRESSER is scenery, and no release in
        // any language has a word for it — so the label under the cursor read the tidied
        // identifier, and a French game pointed at "Front Door" beside a French verb. The
        // corpus declares 875 nouns the picker can return; they are all here.
        Dictionary<string, string> english = Words("en");

        Assert.True(
            english.Keys.Count(k => k.StartsWith("noun.", StringComparison.Ordinal)) >= 875,
            "the room's nouns are not all there");

        foreach (string noun in new[]
        {
            // One the string table does name, one it does not, the two the engine writes
            // itself, and the format behind a hotel door's number.
            "noun.FRONT_DOOR", "noun.DRESSER", "noun.BATHROOM_DOOR", "noun.CHESSBOARD",
            "noun.WOMAN", "noun.MAN", "noun.ROOM",
        })
        {
            Assert.True(english.ContainsKey(noun), $"{noun} has no word");
        }

        // The room number goes into the label rather than beside it, because languages put
        // it in different places — "Room 27", "Chambre 27", "Pokój 27".
        foreach (string code in Carried)
        {
            Assert.Contains("{0}", Words(code)["noun.ROOM"], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_line_of_the_journal_has_a_word_and_it_is_the_line_itself()
    {
        // The journal's objectives and the walkthrough behind its hint button are the port's
        // own prose — the 1999 game had no journal, so there is nothing to extract — and they
        // are keyed in here beside the menus rather than copied per language. The tables stay
        // the one place the English is written; this is what stops the file drifting from
        // them, in either direction.
        Dictionary<string, string> english = Words("en");
        Quests table = Quests.Open();
        Walkthrough guide = Walkthrough.Open();
        List<string> wrong = [];

        foreach (Quest quest in table.All)
        {
            if (!english.TryGetValue(quest.TitleKey, out string? said))
            {
                wrong.Add($"{quest.TitleKey} has no word for \"{quest.Title}\"");
            }
            else if (!string.Equals(said, quest.Title, StringComparison.Ordinal))
            {
                wrong.Add(
                    $"{quest.TitleKey}: Quests.txt has \"{quest.Title}\", the file has \"{said}\"");
            }
        }

        foreach (WalkthroughStep step in guide.Steps)
        {
            if (!english.TryGetValue(step.TextKey, out string? said))
            {
                wrong.Add($"{step.TextKey} has no word for \"{Walkthrough.Shorten(step.Text)}\"");
            }
            else if (!string.Equals(said, step.Text, StringComparison.Ordinal))
            {
                wrong.Add($"{step.TextKey} is not the line Walkthrough.txt has");
            }
        }

        // And nothing left behind by a line that was deleted or renumbered, which would sit
        // in seven translated files answering for an objective that no longer exists.
        var live = new HashSet<string>(
            table.All.Select(q => q.TitleKey).Concat(guide.Steps.Select(s => s.TextKey)),
            StringComparer.Ordinal);

        wrong.AddRange(english.Keys
            .Where(k =>
                (k.StartsWith("quest.", StringComparison.Ordinal) ||
                 k.StartsWith("hint.", StringComparison.Ordinal)) &&
                !live.Contains(k))
            .Select(k => $"{k} is a line neither table has any more"));

        Assert.Empty(wrong);
        Assert.True(live.Count > 400, $"only {live.Count} journal lines were found");
    }

    [Fact]
    public void A_thing_in_the_room_reads_in_the_players_own_language()
    {
        // The whole point, as the hud asks it. GameStrings has no v_front_door — no release
        // does — so this is the port's own table answering, and it has to answer for the
        // room as well as for the pocket.
        var hud = new GameHud(new Overlay(Atlas())) { Text = UiText.Carried("fr") };

        Assert.Equal("Porte d'entrée", Label(hud, "FRONT_DOOR"));
        Assert.Equal("Commode", Label(hud, "DRESSER"));

        // A noun nobody has written stays the tidied identifier rather than becoming blank.
        Assert.Equal("Mod Gadget", Label(hud, "MOD_GADGET"));
    }

    /// <summary>A sheet with letters on it, because a hud has to be given an overlay.</summary>
    private static OverlayAtlas Atlas()
    {
        var image = new DecodedImage(
            64, 16, [.. Enumerable.Repeat<byte>(255, 64 * 16 * 4)], HasAlpha: false, "sheet");

        return OverlayAtlas.Build(
            FontFile.Parse("Font=ABCDEFGH\n", image, "TEST", new DiagnosticBag()));
    }

    /// <summary>What the hud would draw under the pointer for a noun.</summary>
    private static string Label(GameHud hud, string noun) =>
        (string)typeof(GameHud)
            .GetMethod("Thing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hud, [noun])!;

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
    public void Every_letter_the_interface_uses_can_actually_be_drawn()
    {
        // The atlas rasterises a list of characters, not a font: Noto Serif carries two
        // thousand and the sheet carried the hundred and thirty of Latin-1, so a phrase
        // could name a letter no glyph existed for and the letter simply was not there.
        // Nothing looked broken -- "Dzien 1" reads as a typo, not as a renderer fault --
        // which is why this is a test and not a code review.
        //
        // Two live cases when it was written: Polish n-acute, and the OE of the French
        // menu's own "Oeufs de Paques", which is in Windows-1252 and not in Latin-1.
        foreach (string code in Carried)
        {
            foreach ((string key, string said) in Words(code))
            {
                foreach (char c in said)
                {
                    Assert.True(
                        OverlayAtlas.Everything.Contains(c, StringComparison.Ordinal),
                        $"{code} {key} wants U+{(int)c:X4}, which no atlas draws");
                }
            }
        }
    }

    [Fact]
    public void The_atlas_covers_every_code_page_the_engine_reads()
    {
        // Asked of the tables rather than spelled out, because the tables are what decides
        // which bytes a release's text can hold. A language whose page grew a letter the
        // atlas has not got is the fault this catches.
        foreach (GameLanguage language in GameLanguage.Known)
        {
            foreach (char c in Gk3Encoding.Repertoire(language.CodePage))
            {
                Assert.True(
                    OverlayAtlas.Everything.Contains(c, StringComparison.Ordinal),
                    $"{language.Name} can spell U+{(int)c:X4}, which no atlas draws");
            }
        }

        // Windows-936 is the one that cannot work this way: its repertoire is twenty
        // thousand ideographs, so it says nothing rather than something useless.
        Assert.Empty(Gk3Encoding.Repertoire(936));
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
