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

**No row explains itself either.** Every settings row used to carry a sentence under it
saying what it did, each one true and well written, and together they were the reason a
picture page did not fit on the screen. A settings page is not read: it is scanned by
somebody who came for one row, and a paragraph under every other row is what they have to
get past to find it. So the rows are on their own, and the label carries the meaning —
`Skip the cat-hair moustache` rather than `Skip a puzzle` with a sentence explaining which.

What is allowed to stay is what the player cannot find out by trying it:

| stays | because |
| --- | --- |
| `Not installed: copy sl.dlss.dll…` | naming the file is the only way they can act on it |
| `Installed, and this card cannot run it` | a different sentence: nothing to download, nothing done wrong |
| `Asked for, and this display did not offer it` | the switch is on and the monitor is in SDR mode |
| `1280x720 to 2560x1440` | a reading of the row above, like a percentage |
| `Running: …` | what the renderer actually chose, which may not be what was asked |
| `The last three take effect at the next door` | the room was built from the sets chosen when it loaded |
| `Speakers take effect at the next start` | the device is opened once, and silence reads as broken |

Twenty-two label rows became nine, and only two of those nine are ever on screen at once in
a working game: the rest wait for a missing runtime, a refused colour space, or an upscaler
that is actually running. Where a sentence existed to explain a **dead** row, the row is dead
instead: choosing borderless fullscreen makes `Resolution` read `The monitor's own` and stop
being selectable, which says the same thing in no words at all. `DisplaySettingsTests` holds
the budget — a page may draw at most one line of prose, and Display, Playing and Made Easier
may draw none — so this cannot quietly grow back.

**The menu grows with the window.** Its atlas is cut for the window's height — an em of
about a twenty-sixth of it — and re-cut when that changes, so going fullscreen re-draws the
letters rather than stretching them. Captions are sized smaller, at a thirty-third, because
they must not cover the room.

**Up to a point, and then the player says.** The share is capped above about 1440 lines: a
twenty-sixth of a 4K screen is 83 pixels, which is nobody's idea of a settings page. That
cap is a single number standing in for every large display, and on plenty of them it is
still too big — so **Text size** on the Display page multiplies whatever the automatic rule
arrived at, from 60% to 160% in fives. It is a correction to the rule rather than a
replacement for it: a player who then goes fullscreen still gets letters cut for the new
window, only at their own share of it.

It is applied **after** the cap, not before, and that is the whole of why it works. Three
quarters of a twenty-sixth of 4K is 62 pixels, which caps straight back to the same 36 the
player was complaining about — a multiplier in front of the cap would be swallowed whole on
exactly the displays it exists for. Both atlases move together, because this is one
preference about reading and not two, and both are re-cut the frame after the row changes,
so the menu resizes under the slider being dragged.

The rule itself is `UI/TextSizing.cs` — arithmetic on a window height and a preference,
touching no device, font or atlas, which is what lets `TextSizingTests` check the thing a
player would actually ask about. Under `--bitmap-font` there is nothing to re-cut, so the
row moves which rung of GK3's ladder is asked for and how many screen pixels a sheet pixel
covers; whole numbers only, so there it is a step rather than a slider.

## One screen, five sections

The settings used to be a menu of five buttons, each leading to a page, each with a Back
row at the bottom of it. Comparing a row on Picture against a row on Display was four
keystrokes; comparing the upscaler against the lighting quality it is being asked to
reconstruct — which is a comparison somebody makes constantly — could not be done on one
screen at all.

They are now **one screen with the sections listed down its side**: Picture, Display,
Sound, Playing, Controls, and Back under them. Every section is one click or one press of
Page Down from every other. Two pages disappeared into the others in the process, and both
had only ever been pages because a single column had no other way to group anything:

* **Upscaling** is a group of rows on Picture. It is a picture setting, and a page of its
  own cost two keystrokes each way to reach six rows.
* **Made Easier** is a group of rows on Playing, under a heading of its own. The reasoning
  for the separate page was right — everything else on Playing is a preference about how
  the game is *presented*, and those two change what the story asks of the player — and the
  page was the wrong answer to it. A heading says the same thing in the same place.

**The rows are laid in two columns where that helps.** A section fills the left column
downwards and then the right, so the order the keyboard walks is the order the eye reads
and Left and Right are still free to step a value. A row too wide for a column takes the
whole width instead; a section where more than a third of the rows are that wide gives the
columns up and uses one wide one, because a grid where most rows span reads as a layout
that has gone wrong rather than as a grid. Nothing is tuned per page — the same rule gives
Picture and Controls two columns and gives Playing one, because that is what those sections
are actually like.

## The page moves only when it has to

Reported as "the scroll loves to immediately jump, which for a user isn't nice". It did.
The page kept a single row index and recomputed it from the selection every frame, growing
a window of rows outwards from the chosen one — so the chosen row was always in the middle
of the panel and **every** step of the selection scrolled the whole page by one row. The
list moved and the cursor stood still, which is the wrong way round.

The page now keeps its scroll in pixels, moves only when the selection would otherwise
leave it, moves by the least it can, and takes about a fifth of a second to get there. A
row in the middle of the page is stepped past without the page moving at all. The wheel
scrolls the page and leaves the selection alone, and is not dragged back on the next frame:
revealing happens when the selection *moves*, not every frame. Rows are clipped to the
content, so a page part-way through a slide draws the top half of a row rather than a whole
one hanging over the edge.

`MenuScrollTests` holds all of that, against where the rows were actually drawn rather than
against the arithmetic that put them there.

## What it is made of

Three pieces, and the split is what makes any of it testable.

| | |
| --- | --- |
| `Game/Settings.cs` | what the player has chosen, and where it is kept |
| `UI/Menu.cs` | `MenuItem`, `MenuAction`, and `MenuPage`, which draws a page and hit-tests it |
| `UI/FrontEnd.cs` | which section is showing, what is on it, and what choosing a row does |
| `Platform/InputBindings.cs` | which key and which pad button do which job |

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
| Text size | `TextSizing.Em`, which is the size both atlases are cut at, and the magnification a bitmap sheet gets |
| Hurrying pace | `SceneUpdate.HurryFactor`: what a double-click multiplies, and how much faster the stride plays to match |
| Camera travels between angles | `GameState.CameraGliding`, which scripts also read and set |
| Let the story move the camera | `GameState.CinematicsEnabled` |
| Free camera, which may leave the room | the scene's camera shell, and `Directing`'s hold on the controls — both asked every frame |
| Write out what is said | whether the caption reaches the interface at all |
| Play the intro on starting | the two films below |
| Easter eggs | `GameState.EasterEggs`, which is the story's `EGG` flag: the built-in `EGG` action case and Sidney's sixth email |
| Skip the cat-hair moustache | `BLACK_MOUSTACHE` in Gabriel's pocket at Day 1, 2pm, and `Faces.ComposedFrom` so he wears it |
| Gabriel cannot be killed | `GameState.PlotArmour`, which `ScriptHost` reads on the way into every script function |
| Only real light sources | `RigBalance.Keep`, taken to nought: the artists' fills, ambients and bounces are switched off rather than turned down |
| Floors reflect the room | `ReflectionPlan.PlanarFloors`, which puts a large flat polished floor through the planar pass the mirrors use |
| How strongly | `ReflectionPlan.Strength`, which scales every reflection in the picture; one is the physical answer |
| Left stick moves the pointer | `IGameInput.PointerSpeed`, in logical pixels a second; nought is the switch as well as the speed |
| Every row on Controls | `InputBindings`, and through it what a key press and a pad button mean |

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

## Made easier

The last two rows sit under a heading of their own at the foot of Playing, and are set
apart on purpose: everything above them is a preference about how the game is *presented*,
and these two change what the story asks of the player. They had a page to themselves until
the settings became one screen; a heading says the same thing in the same place, without a
second screen to find. Both are off by default, both name the puzzle they take away rather
than saying "easier", and both work by changing what the shipped scripts do rather than by
editing them. `Game/Assists.cs` is the whole of it — every item, flag and function
name either of them touches is in that one file.

**The moustache.** GK3's most notorious chain is spray the cat, tape the hole it squeezes
through, peel the fur off the tape, combine that with a packet of maple syrup. This hands
over the finished `BLACK_MOUSTACHE` on the way into the first room of Day 1, 2pm — the only
timeblock any of those nouns exists in — and skips all of it. What is left is the cap, the
coat and the marker on Mosely's passport, which is assembly rather than puzzle; taking that
away too would leave the moped shop with nothing in front of it.

Once, and recorded as a story flag so it travels in the save: a player who has combined the
moustache into the cap and then reloads must not be handed another. A player already
carrying anything the moustache can *become* is past the puzzle and is given nothing, which
is what makes turning this on halfway through a game played without it safe.

**And he wears it, which is the game's own artwork.** `GA3` is a character in `FACES.TXT`
and `CHARACTERS.TXT` in his own right — the disguised Gabriel the original places offstage
and hidden in the moped shop — and his face bitmap is Gabriel's own with a moustache
painted into it, on the same layout, with all eight lip-sync mouths, a forehead, eyelids and
two blink animations to match. So `Faces` composes Gabriel's face out of `GA3`'s bitmaps and
paints the result onto `GAB`'s texture: he keeps his own model, his own clothes and his own
animations, and grows a moustache. `GABSMILE.ANM` and its eight relatives name `GAB_SMILE_01`
outright rather than through the config, so a patch named for the face it was painted for is
looked for under the artwork in use first — otherwise a smile would shave him for as long as
it lasted.

He wears it **all the time**, from the moment the row is switched on: in every room, on
every day, whatever the clock says and whatever he is actually carrying. The item and the
look are deliberately not tied together — one is a puzzle being skipped and the other is a
look somebody wanted — so he has it in the hotel on the first morning, hours before the game
has anything to say about a moustache. Toggling it does not wait for the next door either:
the faces in a room are composed when the room is built, so `Faces.Recompose` composes them
again on the spot and the console says how many changed.

The rest of the disguise stays in the bag. The cap and the gold coat are on GA3's *model*
rather than on his face, and a Gabriel permanently dressed as somebody else is not what the
row says. The pre-rendered films are films, so he is clean-shaven in those whatever this
says.

**Plot armour.** Five scripts in the game can kill Gabriel, all of them in the temple under
the château on the last night, and every one of them goes through the same door: a `Die`
function that stops the music, puts up the death screen and resets the puzzle behind it.
`TE1`, `TE3`, `TE4`, `TE5` and `TE6` are the only scripts in the corpus with a function by
that name, and the only ones with the `Restart` and `PostDeath` pair the death screen calls
back into when the player chooses to try again.

So the assistance answers that one door differently: `ScriptHost` runs `Restart` and then
`PostDeath` — the puzzle reset, and then started running again — and never enters `Die` at
all. That is exactly what the original does after a death, without the death and without the
screen. Both halves are checked before intervening, so a script with a `Die` and nowhere to
restart is left alone rather than half-run.

The staging still plays. He falls, or the pendulum swings, or the demon strikes: that is the
scene telling the player what went wrong, and it is over by the time the game says he is
dead. The console says `Plot armour: TE6 would have killed Gabriel` when it steps in,
because otherwise a player has no way to tell a puzzle that reset him from one that killed
him.

`PlotArmour` is on `GameState` beside `CinematicsEnabled` rather than read out of the
settings where it is wanted, and it is in the state hash: two runs made with different
answers to it diverge, and the harness should be able to see why. Like the easter eggs it is
a preference rather than a fact about the story, so it wins over a save.

The five volumes reach nine buses because there are nine and only five sliders. A bus
left out is a sound nobody can turn down, and **which** one that would be depends on
which of two near-identical names the code that plays it happened to pick — speech, for
one, is played on `DialogueCentered` and not on `DialogueInWorld`.

Everything is applied the moment it changes rather than on the way out, so a volume is
heard while its slider is being dragged. The two that cannot be say so on the page rather
than quietly doing nothing: the speaker layout is what the device was opened with, and the
texture set is what the room standing round the player was built from, so one waits for
the next start and the other for the next door.

## Controls

Which key does which job was a static table inside the windowing backend, which is the
right place for a decision nobody can change and the wrong place for one everybody wants
to. It is `Platform/InputBindings.cs` now: above the windowing library, named in the game's
own `InputKey` rather than in Silk.NET's, and carried in the settings file with everything
else the player has chosen.

**Only the differences are written down.** A file that listed every binding would pin a
player to whatever the defaults were on the day they first opened this page — a key added
to an action in a later version would never reach anybody who had ever looked at Controls.
What is stored is what the player changed, so a default that improves improves for
everybody who did not have an opinion about it.

Choosing a row stops the screen and waits for a key or a button. Escape leaves the binding
alone and Backspace clears it; neither is bindable here, because they are the two things
somebody has to be able to do when the screen has stopped and is waiting for them. A key
given to one action is taken away from whatever else had it, in the same pass: two actions
on one key is not a state the player can see or get out of. A key row answered with a pad
button binds the pad button, because the row is a suggestion about which device is likelier
and not a rule.

## The gamepad

**This is a game played by pointing at things, so the one thing a pad has to be able to do
is point.** The left stick drives the cursor and the mouse takes it straight back the
moment it is touched, so there is no mode to be in and nothing to switch. The push is
squared, which is what every console cursor does about a stick that is otherwise either too
slow to cross the screen with or too coarse to land on anything, and the real cursor is
moved with it so the arrow the operating system draws is the one the game is acting on.

The face buttons go to the pointer rather than to the free camera's movement, for the same
reason: the button under the thumb should be the one that does the pointing. Bottom face
does the thing, right face asks what it does, left face looks closely. The rest of the
defaults are the shoulder buttons for the camera, the top face for the inventory, Back for
the journal, the left trigger for the hotspots and Start for the menu — every one of them
changeable on Controls.

**What the pad does in a menu is fixed and is not a binding.** A player who has rebound the
inventory to the bottom face button has said something about the game, not about the menu,
and a menu whose Choose button moved when they did would be a menu they could not get out
of. The D-pad and the left stick step the list, the bottom face chooses, the right face goes
back, and the shoulders step between the settings sections — which is where every console
game has put the same job for twenty years, and which Page Up and Page Down do on a
keyboard.

The buttons are named for where they are — "Bottom face", "Right shoulder" — rather than
for what is printed on them. The same physical button is A on an Xbox pad, Cross on a
PlayStation one and B on a Nintendo one, and a settings page that says "A" to somebody
holding a DualSense is a settings page that is wrong about the hardware in their hands.

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

**Get Unstuck** is the one row on the pause page that does something to the room rather
than to a setting. `SceneUpdate.Occupied` is four things — an action held back for its
approach walk, the seconds an action said it needed, a clip the story is playing on the
player, and scripts the room started and never finished — and `Directing` turns any of them
into a camera the player does not have and clicks that do not reach the floor. That is right
while the story is telling something and wrong the moment one of the four wedges, and a
player with no camera and no clicks has no way to say so, every way of saying so being a
click. So the escape hatch lives where they can still reach it.

It lets go of all four, of `ForcedCameraCuts` and of any close-up the view was pinned to,
and stands the player on the nearest walkable texel if they are off the floor. It is
deliberately **not** a reload: flags, counts, score and inventory are untouched, so what the
player had done is still done and only what was *happening* is abandoned — the difference
between giving up on a moment and giving up on a save. What it let go of is written to the
console in full, because somebody who reached for this has already spent a while wondering
whether the game was broken.

## Flags

| flag | |
| --- | --- |
| `--scene NAME` | opens in that room, as it always has. The menu is still on Escape. |
| `--front` | show the menu first even with `--scene` |
| `--start NAME` | begin somewhere other than `R25` |
| `--skip-intro` | this run only; the setting is not touched |
| `--front-page audio\|video\|gameplay\|assists\|options` | open on that page |
| `--font-file PATH` | draw the interface with that typeface |
| `--bitmap-font` | draw it with GK3's own sheets instead |
| `--frames N` `--screenshot PATH` | with `--front`: draw N frames of the menu, photograph it, and end |

`--frames` also skips the intro, because a run that photographs something and ends does
not want to sit through two films first. The last two exist for the same reason `--menu`
and `--pointer` do: a page three keystrokes into the menu cannot be reached by a run that
has no keyboard, and a page nobody can render is a page whose layout nobody can check.
