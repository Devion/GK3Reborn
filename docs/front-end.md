# The menu in front of the game

The original opens on a 640×480 painting with its buttons drawn into it. Every label is
baked in one language at one size, the hovered and pressed states are separate images of
the same painting, and adding a row means new art. There is no volume control worth the
name, no way to say how fast Gabriel should walk, and nothing that could carry a setting
the 1999 build did not already have.

**None of GK3's interface bitmaps are used here.** The menu is rectangles and text: sharp
at any resolution, translatable, and able to say things the original had no button for. A
new row costs a line of code and no art at all.

**The letters are outlines, not the game's sheets.** GK3's fonts are bitmaps drawn for a
640×480 screen; the best that can be done with one on a modern display is to magnify it by
a whole number, and it looks it. `Formats/Fonts` reads a TrueType file and draws it —
`TrueTypeFile` for the tables and `GlyphRasterizer` for the shapes, both written here for
the same reason every other format in this project is. The atlas is cut at the size the
window actually is and re-cut when the window changes, so nothing is ever a magnified
pixel. There are two of them: the room's captions and the menu, which wants larger text
because it is the only thing on screen.

Hinting is deliberately not implemented — it is a bytecode interpreter serving 96-dpi
screens that no longer exist, and a well-made face at menu sizes reads perfectly without
it. Kerning lives in `GPOS` in modern fonts and is not read either, which costs a little
air around a few pairs. What *is* read is what silently breaks: the character map,
composite glyphs (every accented letter in the French this game is set among is one), and
the metrics that put a comma below the line and a capital on it.

The face is Noto Serif under the SIL Open Font Licence, carried inside the engine assembly
so a shipped game has one whatever its working directory is. A `.ttf` or `.otf` in the
workspace's `enhanced/fonts` is preferred over it, `--font-file` names one outright, and
`--bitmap-font` puts GK3's own sheets back — which is also what happens on its own if no
outline font can be read.

**The title screen itself is the game's own.** `TITLE.BMP` is a painting of an angel with
the game's name in it, not a widget with a label baked into one language, so it is kept and
filled to the window. The rows over it are still drawn: `Intro`, `Play`, `Restore`,
`Settings`, `Quit` — the original's own five, in its own order — in a slim panel down in the
left-hand corner, clear of the lettering. The menu draws no heading of its own there,
because the picture already says what the game is. `THEME.WAV`, the largest sound in the
archives and played nowhere else, runs underneath on the music bus.

It is looked for in three places, in the order somebody working on the picture would want
them: `enhanced/textures/TITLE.PNG`, then the block-compressed build **or a pack**, then the
original in the archives. A shipped game has only the last two, so the packed form is not an
afterthought — it is the one that ships. The console line says which was used, because an
upscale and the 640×480 original are indistinguishable on screen until somebody has actually
done the upscale.

**It fills the window without changing shape, and stops cropping before it cuts the
lettering.** A 4:3 painting on a 16:9 display is covered outright — a third of its height
goes, which is the black bands it was drawn with and a little of the angel. Past a third it
stops and lets bars appear instead, because an ultrawide display would otherwise crop until
the game's own name ran off the bottom. `MoviePipeline.Fit` is the whole of that rule and is
tested against every shape of picture and window that turns up.

**No page says which keys work.** A menu that explains what an arrow key does is a menu that
thinks the player has not used one.

**The menu grows with the window.** Its atlas is cut for the window's height — an em of
about a twenty-sixth of it — and re-cut when that changes, so going fullscreen re-draws the
letters rather than stretching them. Captions are sized smaller, at a thirty-third, because
they must not cover the room.

## What it is made of

Three pieces, and the split is what makes any of it testable.

| | |
| --- | --- |
| `Game/Settings.cs` | what the player has chosen, and where it is kept |
| `UI/Menu.cs` | `MenuItem`, `MenuAction`, and `MenuPage`, which draws a page and hit-tests it |
| `UI/FrontEnd.cs` | which page is showing, what is on it, and what choosing a row does |

`FrontEnd` never mentions a window, a device or a renderer. It turns settings into rows
and rows back into settings, which is the only way to check by test that a slider moves
the thing it is labelled with. `MenuPage` lays a page out and remembers where every row
went **in the same pass**, exactly as `GameHud` does its verb menu: what the player clicks
is necessarily what they saw, because there is only one set of rectangles.

Everything is measured in `Overlay.LineHeight`. Nothing is in pixels, so the whole page
grows with the font the interface picked for the window.

## Every setting has somewhere to go

A setting with no destination is a promise the interface cannot keep. These are the
destinations, and there are no others in the file.

| row | reaches |
| --- | --- |
| Overall, Music, Room tone, Effects, Speech | the mixer's nine buses, which nothing had ever set |
| Speakers | the layout `OpenAlBackend` opens with, at the next start |
| Lighting | `RayTracingQuality`, from the baked 1999 picture to every ray the frame can use |
| Higher-resolution textures | the enhanced set **and** its compressed build, so answering "no" means the original art |
| Hurrying pace | `SceneUpdate.HurryFactor`: what a double-click multiplies, and how much faster the stride plays to match |
| Camera travels between angles | `GameState.CameraGliding`, which scripts also read and set |
| Let the story move the camera | `GameState.CinematicsEnabled` |
| Write out what is said | whether the caption reaches the interface at all |
| Play the intro on starting | the two films below |
| Easter eggs | `GameState.EasterEggs`, which is the story's `EGG` flag: the built-in `EGG` action case and Sidney's sixth email |

**Easter eggs are the game's own switch, finished.** `EGG` is one of the built-in cases an
action file may be written against, and the original hard-codes it false with a note saying
it should return true when easter eggs are enabled — the switch never shipped, so the nine
rules written behind it have never been reachable in a playing game. They are hidden verbs
on things: Jean does a backflip, Grace stretches, the chicken meows, and the fridge in the
kitchen has a snack in it. Turning this on sets the story's `EGG` flag, which is what the
case reads and what Sidney's sixth email is written against.

Off by default, because the game as it shipped is the game as it shipped. And it is a
preference rather than a fact about the story, so it wins over a save: loading somebody
else's game does not turn it on, and loading a game saved before it was turned on does not
turn it off.

The five volumes reach nine buses because there are nine and only five sliders. A bus
left out is a sound nobody can turn down, and **which** one that would be depends on
which of two near-identical names the code that plays it happened to pick — speech, for
one, is played on `DialogueCentered` and not on `DialogueInWorld`.

Everything is applied the moment it changes rather than on the way out, so a volume is
heard while its slider is being dragged. The two that cannot be say so on the page rather
than quietly doing nothing: the speaker layout is what the device was opened with, and the
texture set is what the room standing round the player was built from, so one waits for
the next start and the other for the next door.

## Where the settings live

`%AppData%\GK3Reborn\settings.json` on Windows, `~/.config/GK3Reborn/settings.json` on
Linux. In the user's own profile rather than beside the executable: a game directory may
be read-only, shared between accounts, or replaced wholesale by an update, and none of
those should cost somebody their volume levels.

**Everything is clamped on the way in, and nothing about the file is fatal.** It is a
text file somebody may edit. A missing one is what a first run looks like; an unreadable
one, a truncated one, or one with a hand-typed volume of forty in it gives the defaults or
the nearest allowed value. Refusing to start because a preferences file has a stray comma
in it would be the worst trade in the program. Writing is the same the other way: a
read-only profile means the settings do not persist, not that the game stops.

They are written when the player leaves the menu, not on every keystroke — dragging a
volume slider across a page is a hundred changes and none of them is worth a write.

## Starting, pausing, leaving

Run the game with nothing in particular asked for and it plays `SIERRA` and `INTRO`, then
shows the menu, then opens in `R25` at `110A` — Gabriel's room at the hotel, where the
story begins.

Skipping is Enter, Escape, or the left button **held** for half a second — not a click. A
click is what somebody does by accident while the machine is still settling down, and losing
the opening of the game to a stray mouse is worse than holding a button for a moment. The
way out is written along the bottom for the first few seconds of each film, and holding
fills a bar, because a hold with nothing on screen is indistinguishable from a hold that is
not working.

**Skipping ends the film showing, not the sequence.** The publisher's logo and the intro are
two different things to sit through, and somebody who skips the first has said nothing about
whether they want to watch the second — so a cold start is two skips. The button has to be
let go between them, or one long press would take both.

Choosing `Intro` plays **the intro** and comes back to the menu — not the logo, because
somebody who asked for the intro asked for the intro. The theme stops while it runs. Escape in the room opens the same pages with the room still behind them,
dimmed — and no title art, so those pages keep their headings. Nothing of
the room advances while it is up, which is what pausing means. From a settings page,
Escape goes back one level; from the top of the menu it resumes. It does **not** leave the
game — that is a row somebody has to choose — and from the very first menu, where there is
nothing to resume, it does nothing at all.

Three ways to work it, all live at once: the arrow keys and Enter, the pointer, and
dragging a slider. A menu that can only be used one way is a menu somebody cannot use.

## Flags

| flag | |
| --- | --- |
| `--scene NAME` | opens in that room, as it always has. The menu is still on Escape. |
| `--front` | show the menu first even with `--scene` |
| `--start NAME` | begin somewhere other than `R25` |
| `--skip-intro` | this run only; the setting is not touched |
| `--front-page audio\|video\|gameplay\|options` | open on that page |
| `--font-file PATH` | draw the interface with that typeface |
| `--bitmap-font` | draw it with GK3's own sheets instead |
| `--frames N` `--screenshot PATH` | with `--front`: draw N frames of the menu, photograph it, and end |

`--frames` also skips the intro, because a run that photographs something and ends does
not want to sit through two films first. The last two exist for the same reason `--menu`
and `--pointer` do: a page three keystrokes into the menu cannot be reached by a run that
has no keyboard, and a page nobody can render is a page whose layout nobody can check.
