# Localisation

GK3 was published in eight languages, and Sierra localised it the most awkward way
available: they re-cut every archive. A French disc is not an English disc with a French
patch on it — it is a whole second copy of the game, 657 megabytes of it, in which about
fifteen thousand of the forty thousand assets happen to differ and nothing anywhere says
which fifteen thousand.

The port works that out once and ships the difference. One extra file beside the
executable per language:

```
GK3Reborn/
  GK3Reborn.exe
  Reborn.rebarn                8.4 GB   the shared content
  RebornMaterials.rebarn       5.9 GB   its material channels
  Reborn_EN.rebarn              328 MB  what English says
  Reborn_DE.rebarn              300 MB  what German says
  Reborn_ES.rebarn               63 MB  what Spanish says
  Reborn_FR.rebarn              306 MB  what French says
  Reborn_IT.rebarn              313 MB  what Italian says
  Reborn_PT.rebarn               21 MB  what Portuguese says
```

Reading one of those in front of the installation turns any installation into any language.
An English install with `Reborn_FR.rebarn` plays in French; a French install with
`Reborn_EN.rebarn` plays in English. The player chooses in **Settings → Playing →
Language**, or with `--language fr` for one run.

The game starts in English and needs no pack to do it: every locale Sierra shipped answers
to the English spellings in its own archives, so an installation with no language pack at
all still has one language rather than none.

## What is actually localised

Two different things, and they need different treatment.

**Most assets keep their 1999 name and change their contents.** The same `27KASHAF.BMP`
with different words painted on it; the same `A014ED3S.6J1` with a different actor saying a
different sentence; the same `ISIS.HTML` in Sidney's library, translated.

**A few change their name instead.** Sierra renamed the spoken assets for four
localisations and left the other four with English's spellings:

| | en | fr | de | it | es | pt | ru | pl | zh |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| letter | `E` | `F` | `G` | `I` | `S` | `E` | `E` | `E` | `E` |
| string table | `ESTRINGS` | `FSTRINGS` | `GSTRINGS` | `ISTRINGS` | `SSTRINGS` | `ESTRINGS` | `ESTRINGS` | `ESTRINGS` | `ESTRINGS` |
| code page | 1252 | 1252 | 1252 | 1252 | 1252 | 1252 | 1251 | 1250 | 936 |

Simplified Chinese is not one of Sierra's eight; somebody else translated it. It is in the
table because the arrangement was built so that adding a language is sourcing a release, and
refusing one because Sierra did not publish it would be the arrangement failing its own test.
Every entry above was confirmed against the release itself except Russian and Polish, which
nobody here has.

The letter goes in front of a line of dialogue's lip-sync (`E014ED3S6J1.YAK` against
`F014ED3S6J1.YAK`) and in front of a scripted moment (`ECOFFEEPOT.MOM`). Scripts never
write it — `StartVoiceOver("1LLJ644QR1")` — so the engine adds it, which is why a plate
taken straight from an action file matches nothing on disk.

**The letter is not unique and is not derivable from the code.** Portuguese, Russian and
Polish all carry `E` and are told apart by what is inside the file. A build that assumed one
letter per language would read Portuguese out of the English pack and never say so, which
is why `GameLanguage` is a record rather than a letter.

Here is how much of the game each release in `ContentWorkspace/Localized` actually
changes:

| | assets it holds | assets that differ | bitmaps | on geometry | pack |
| --- | ---: | ---: | ---: | ---: | ---: |
| German | 36,944 | 14,679 | 683 | 96 | 300 MB |
| Italian | 36,935 | 14,491 | 691 | 83 | 313 MB |
| Spanish | 36,946 | 8,150 | 710 | 91 | 63 MB |
| Portuguese | 36,834 | 8,452 | 755 | 102 | 21 MB |
| French | 15,092 | 14,658 | 659 | 82 | 306 MB |
| English | 14,806 | 15,265 | 885 | 124 | 328 MB |

English's own figure is derived rather than measured — it is the English spelling of
everything some other language changes, so it grows every time another release is added.

**The releases fall into two kinds, and the pipeline tells them apart rather than being
told.** German, Italian and French re-recorded every line: 6,556 dialogue files each, and
that is where 90% of their packs go. Spanish and Portuguese did not re-record a single one —
every dialogue file in both is byte-identical to English — so what they change is their text,
their bitmaps and their lip-sync, and they cost 63 MB and 21 MB against German's 300.

Portuguese is the extreme case: it ships no cutscene audio at all, because there is nothing
in its release that differs from what the shared pictures already carry.

For French, the breakdown by family:

| Family | Count | What it is |
| --- | ---: | --- |
| dialogue recordings | 6,556 | every spoken line, re-recorded |
| `.YAK` | 7,255 | the lip-sync for each of them |
| `.BMP` | 659 | pictures with words painted into them |
| `.HTML` | 80 | Sidney's library |
| `.ANM` | 54 | animations retimed to the new recordings |
| `.MOM` | 33 | scripted moments |
| `.TXT` | 10 | the string table, the screen layouts, Sidney's data |
| `.MOD` `.MUL` `.ACT` `.NVC` `.FON` `.WAV` `.DOC` | 11 | the rest |

## What the interface can say, and what it cannot

GK3 localised exactly one family of per-object text: the 293 names of the things the player
carries, under `v_black_marker` in the string table's `[ToolTips]` section. The port had
never read one — it drew the identifier with its underscores taken out, so the game's "Tape
of Abbé's phone call" came out as "Abbe Tape" — and it does now, which is an improvement in
English and the whole of what a French game can say about a pocketful of objects in French.

There is **no** table anywhere in the data for the nouns under the cursor or for the verbs
in the right-click menu. The original drew verbs as icons and never named the thing being
pointed at, so `BATHROOM_DOOR` and `LOOK` have no translation to find: those two read as
English in every language, and the only way to change that is to write the words, which is
translation rather than extraction.

The same is true of everything the port added and the 1999 game never had: the settings
screen, the journal's 142 objectives and their hints, the toolbar's own labels. They are in
`Assets/Story/*.txt` and in the source, in English. Localising them is a separate piece of
work with a different shape — it needs translators, not a comparison — and nothing here
pretends to have done it.

**Sidney is the exception, and it is not one of the port's own screens.** `ESIDNEY.TXT` is
re-cut for every release and carries every menu, button and refusal the machine has; the
port read it for the paragraphs and wrote its buttons out in English beside them. It asks
for all of them by their 1999 key now — see [sidney.md](sidney.md), which also has the two
faults that turned up in the doing: a parchment answer matched against the *word* FRENCH,
which is FRANZÖSISCH in German and OCCITAN in French, so the step the story turns on was
unreachable in five languages; and a main menu that turned a row's words into a screen, so a
French menu opened nothing. The dozen sentences Sidney says that the 1999 game has no string
for are a table in `SidneyWords`, which is translation rather than extraction and is the one
place in the port where that has been done.

## The cutscenes

A cutscene is the same footage in every language and a different recording over it. Sierra
shipped a whole BIK per language anyway, and copying that arrangement would mean shipping
the picture once per language. So the picture is imported once and each language
contributes an audio track — five megabytes instead of a hundred and fifty.

**Except when it is not the same pixels, and there are two ways that happens.**

Some cutscenes are a different *edit*. `DAY3-3` runs 430 seconds in English and 153 in French
and German — nearly five minutes shorter — and `212PEND`, `DAY3-1` and `DAY3-A` differ too.

Others are the same edit with *words burned into the picture*. GK3's intro carries its
location captions as part of the frame, so every localisation repainted them; `DAY3-A` is the
same in German and Spanish. Neither of these can take a soundtrack laid over the shared
picture: the first drifts apart within seconds, the second shows English captions over
foreign speech.

**So the pictures are compared exactly rather than approximately.** Both are decoded to raw
RGB and hashed. Bink is deterministic — the same master gives the same frames — so two
releases cut from the same footage hash identically and anything else does not. No threshold,
no similarity score, and a length difference changes the hash by itself. It costs one decode
of a 320x240 movie.

That matters, because the first version of this compared durations. Durations agree for the
French intro to a hundredth of a second, so it was called shared, and a French game would
have played three and a half minutes of English captions under a French soundtrack.

The sound is hashed the same way, and it is what tells a dub from a subtitle:

| | dubbed | own picture | shared unchanged | no shared cut |
| --- | ---: | ---: | ---: | ---: |
| French | 11 | 5 | 0 | 0 |
| German | 11 | 5 | 18 | 6 |
| Italian | 11 | 5 | 18 | 0 |
| Spanish | 0 | 2 | 31 | 0 |
| Portuguese | 0 | 0 | 34 | 6 |

Spanish and Portuguese cost almost nothing here: they did not re-record their cutscenes, so
those are byte-identical to English and ship nothing at all. Portuguese's two "own picture"
entries in the asset table are text, not film.

**Six cutscenes have no shared cut to compare against** — `DAY1-1`, `DAY1-2`, `DAY2-3`,
`DAY2-4`, `DAY3-C`, `DAY3-D`. The import has not produced an MP4 for them, so there is
nothing to lay a soundtrack over and they play in whatever language the shared picture was
imported from. Running `import-video` over the installation first fixes it, and the warning
says so.

## Subtitles

**GK3 wrote subtitles for its cutscenes and never showed them.** Fourteen of the films carry
a `.YAK` of the film's own name — `205PEND.YAK` beside `205PEND.bik` — whose `[GK3]` section
is a list of `SpeakerCaption` nodes:

```
[GK3]
33
40,SpeakerCaption, 200, GRACE,Way to go, kid.
330,SpeakerCaption, 452, WILKES,Hey, Girlie.  You seen Madeline?
[OPTIONS]
1
0,FRAMERATE,30
```

A start frame, an end frame, who is speaking and what they say — and the file states its own
frame rate, which for every cutscene is 30 rather than the 15 an ordinary animation runs at.
232 captions across the fourteen films, translated in every release.

**They are worth most where a language never dubbed its cutscenes.** Spanish and Portuguese
re-recorded nothing, so those releases are a Spanish game and a Portuguese game whose films
are spoken in English. These subtitles are the whole of what those two have, and Sierra
shipped them.

They cost nothing to ship: the YAKs are already in each language pack, under the names the
game asks for, and `MoviePlayer` reads the film's own through `AnimationLibrary` — which
reads through the archives, which read through the language pack. Nothing in the film path
knows what a language is.

Drawn under **Write out what is said in films**, which is a row of its own next to the one
that governs the room's captions. The two are different decisions: a caption is small and
beside whoever is speaking, and a subtitle is across the bottom of a full-screen picture with
nothing else on it, so somebody may well want the first and not the second over a cutscene
they can hear perfectly well. Both default on.

The last caption to have started is the one shown — GK3's overlap, because that is how people
talk.

Six of the spoken films have no captions at all: `INTRO`, `202AEND`, `DAY2-1`, `DAY2-2`,
`DAY3-A` and `DAY3-B`. Four of the releases carry an `INTRO.YAK` the English one does not, so
those four subtitle the opening and English does not.

### And it found a defect in the caption reader

The reader trims every comma-separated field and puts the caption back together, and it was
rejoining on a bare comma — so `Perfekt, Kleiner.` came out as `Perfekt,Kleiner.` in **every
caption in the game that contains one**, the room's as well as the films'. It had been
invisible for the same reason most things are: a room's caption is on screen for two seconds
under a speaking character, and a missing space there reads as a typeface quirk. At the size
a subtitle is drawn it reads as a bug.

## The pictures with words in them

A road sign, a shop front, a note on a table, a label on a bottle. Most of GK3's 6,657
textures carry no words at all and are shared by every language; the ones that do had to be
repainted per language in 1999 and have to be enhanced per language now.

```
ContentWorkspace/enhanced/localtextures/FR/27KASHAF.png
```

A PNG there stands in front of the shared enhanced texture of the same name, for that
language only.

### Sidney's encyclopedia is only a fifth translated, and that is Sierra's doing

The 390 pages the search screen reaches are in the archives and read through the language
pack, so nothing in the port has to know. What is worth knowing is what comes back: German,
Spanish, French and Italian each translated **the same 80 pages** and shipped the other 310
byte-identical to English, so `ABRAXAS.HTML` is in English inside the German `core.brn`.
Portuguese translated all 390. Reproduced faithfully because it is what the disc holds.

**Seven hundred and fifty bitmaps differ, and only about a hundred are worth anybody's
afternoon.** Six hundred and fifty of them are the 1999 interface — Sidney's buttons, the
options screens, the binocular controls, the toolbar — every one a picture with a word
painted on it, every one localised, and the port draws none of them: it renders its own
interface, with its own text, at the size of the window.

The two are told apart by the texture plan's own reference count: whether any room, prop or
character names this texture. That is a fact the plan already has, derived from the whole
corpus, and it is a far better test than any list of name prefixes — nothing about
`BLUEAPPLE` or `ABBEPRNT3` says "interface" except that no piece of geometry has ever asked
for it. The survivors are listed under `"surfaces"` for each language in
`manifests/localization.json`, and that is the work list.

**Nothing in the pipeline writes into `enhanced/localtextures`.** It is read by the packer
and reported on by the extractor, and that is all. The directory is hand-curated: somebody
prunes the hundred candidates down to the ones actually worth repainting and then repaints
them, so the shape of that decision *is which files are there* — and a run that helpfully put
the pruned ones back would undo an afternoon's work without touching a byte of anybody's
painting.

That is not a hypothetical. The extractor did seed the directory when it was first written,
and a re-derivation on 2026-09-05 copied thirty-four deliberately-deleted pictures back in
and said nothing about it. The seeding was removed rather than put behind a flag, because a
flag is something a script somebody else wrote passes.

An empty directory is a perfectly good directory: the shared texture is used for every name
it does not hold, and the language's own 1999 bitmap for every name the shared set does not
hold either.

## Producing a language

```
GK3Reborn.Tools extract-localized --workspace ContentWorkspace --source <GK3>/Data
rebuild-content.cmd --languages
```

`extract-localized` reads one subdirectory per language from `ContentWorkspace/Localized`,
named for its ISO 639-1 code. Each may hold `*.brn` archives — read with the engine's own
reader in the engine's own search order — or a dumped tree of loose files, in which case the
shallowest copy of a name wins. It compares them, writes what differs into
`enhanced/localized/<CODE>/`, and `pack-content` turns each of those into
`Reborn_<CODE>.rebarn`.

```
enhanced/localized/FR/
  localized/          14,658 assets under their 1999 names
  movie-audio/        11 soundtracks for the shared cuts
  video/              5 movies whose picture is France's own
  manifests/          which language the pack is for
enhanced/localtextures/FR/
                      the repainted colour textures, if anybody has made any
```

A release directory may be a whole unpacked installation — `GK3.exe` beside a `Data`
directory — and the archives are found inside it. It may also be a dumped tree of loose
files, in which case the shallowest copy of a name wins. Both are read, and the directory may
be named for the language in any of the obvious ways: `es`, `ES`, `ESP`, `SPA` or `Spanish`
all name the same one.

Adding Russian is sourcing a Russian release, dropping it in as `Localized/RU`, and running
those two commands. It is not a code change, and nothing in the plan, the packer or the menu
has to be edited — which is the whole point of the arrangement, and German, Spanish, Italian
and Portuguese were each added exactly that way.

**A release that is not a localisation is said out loud.** The smallest real one here changes
8,150 assets; a release that changes a few dozen is the same game under a different label, or
a download whose localised half is somewhere else. `GK3R2309` says so with the count. A
directory named for no language anybody recognises gets `GK3R2308` rather than silence.

### How the set is decided

For every asset in every non-baseline language, compare it against the baseline's own
spelling of the same asset. Different, or absent there, means localised. English's set is
then *derived*: for every canonical name any other language localises, take English's
spelling of it, from the English release or from the installation.

**Bitmaps are compared as pictures, not as bytes.** GK3's own container is a raw RGB565
bitmap with an eight-byte header, and a dumped release may well have been written back out
as an ordinary Windows bitmap. Those two files never compare equal and always decode to the
same picture, so a byte comparison would declare every bitmap in the game localised and the
packs would be six times the size they need to be. On the two releases here that rule alone
takes 97 bitmaps out of the set.

**Only two families have their letter taken off**: `.YAK` and `.MOM`, plus the string table
by name. Everything else is compared under the name it has. That distinction is the one
thing here that is silently catastrophic when it is wrong in either direction:

- strip too eagerly and GK3's cutscene lip-sync files — `205PEND.YAK`, named for the scene
  rather than for a line — collide into one asset;
- strip too little and seven thousand French `.YAK` files have no English counterpart, so
  the English pack is empty and every voice-over in an English game on a French install
  reports a length of zero.

`ESIDNEY.TXT` is the case that shows why the rule is by family: it keeps its `E` in the
French release and changes its *contents* instead, exactly like a bitmap.

### What it reports

`reports/localization.md` and `manifests/localization.json`. Read the first. It names, per
language, how many assets of each family differ, what became of each movie, and — as a
warning — anything it could not resolve. On the four releases here that is ten names some
other release has and the English one does not:

```
ERROR.TXT  MISSING.TXT  LARRYINT.WAV  INTRO.YAK  EEKNOCK.MOM  EETOAST.MOM
RC2306P.NVC  SID_TEXT_14_WHT.FON  SID_TEXT_18_WHT.FON
```

Forty-two across six releases is fine — a release may simply have a line the others do not.
A *large* number there means the prefix rule has matched something it should not have, and
that is what the warning is for.

## What wins at runtime

```
overrides/                       the player's own files
Reborn_<CODE>.rebarn             the language
Reborn.rebarn, RebornMaterials   the remake's shared content
*.brn                            the installation
```

**Under the overrides.** A file a player put in `overrides/` is theirs and stays theirs
whatever language the game is in.

**Over the archives.** Everything the language pack does not hold falls through to the
installation, which is what makes an incomplete pack harmless: what a player loses by not
having one is that language, not the game.

**Over the restored dialogue, and that is the one rule worth stating twice.** A restored
master is a cleaned-up copy of a recording in one language; a language pack holds a
different actor saying a different sentence. Handing a French game an English master because
the English one had been remastered would be the loudest possible way to get this wrong. The
English case is handled where it belongs — `extract-localized` leaves out of the English
pack the lines `enhanced/audio` restores, so those fall through to the restored master
rather than being shadowed by the 1999 recording of the same line.

**The cutscenes are the exception to "loose beats packed".** Everywhere else in this project
a loose file wins over a pack, because the looser and more recent thing wins while a set is
still moving. A shared picture where the language has its own is not a stale picture, it is
the wrong one, so there the language wins over both.

**Language packs are not part of the shared set.** `RebarnContent` skips any file matching
`Reborn_<CODE>.rebarn`; the game opens exactly one of them, through `LocalizedContent`.
Merging every language an install happens to carry into the shared namespace would put the
last one alphabetically in front of the archives for everybody.

## Text encoding

Every text asset in the game is one byte a character, and nothing in the file says which
code page: no mark, no header, only bytes. So the language decides. Windows-1252 for the
six Western European localisations, 1251 for Russian, 1250 for Polish.

This matters more than it looks. Latin-1 and Windows-1252 differ in exactly one place, and
it is the place French uses most: the curly apostrophe is `0x92`, which Latin-1 leaves as a
control character. `L’Empereur` then arrives with a hole in it. The three tables are written
out in `Foundation/Gk3Encoding.cs` rather than taken from the platform: three tables of 128
characters are smaller than a package reference and behave identically everywhere, with no
registration call at startup for anybody to forget.

**Anything outside Western Europe is not one byte a character, and that is where the line
is.** GBK, which Simplified Chinese uses, is twenty-two thousand mappings in which a byte
above `0x80` begins a pair — not a table anybody hand-writes, and getting it wrong is not a
visible failure: the text decodes, into the wrong characters, silently. So the platform's own
code-page provider is registered on first use and asked for any page there is no table for.
It ships with .NET 10 and needs no package.

## Changing language

It takes effect at the next start, and the row says so. The language decides which pack the
archives were opened through, which letter every voice-over carries, which code page the
text was decoded in and which of the enhanced textures carries words — and all four were
settled before the window existed. Swapping them live would mean rebuilding the string
table, the fonts, the animation and sound caches, the interface atlas and the room, which is
a larger thing to own than the row is worth.

The startup log says what was asked for and what is actually answering, because a French
game running on the English archives because the pack was not built looks exactly like a
French game until somebody reads a word:

```
Language: French (fr), 14675 entries (293 MB): 4 video, 1 manifests, 14658 localized, 12 movie-audio
Names: 471 from FSTRINGS.TXT
Movies: French re-cuts 4 of them and supplies the soundtrack for 12 more
film: INTRO, 320x240 at 30 fps, 213.0s, sound 48000 Hz 2 ch (localised),
      picture from Reborn.rebarn:INTRO.mp4, sound from Reborn_FR.rebarn:INTRO.m4a
```
