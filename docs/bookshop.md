# St. George's Books

The bookshop in Rennes-le-Château is closed for the whole of GK3. Its door is a hit test
with one answer, "They're closed.", and the window has a `Fermé pour réparation` card in
it. This is the port's own easter egg: try that door five times running as Gabriel and it
gives, onto St. George's Books, his own shop in New Orleans from the first game, as he
might dream it.

It is new art and nothing else. Nobody at Sierra wrote, recorded or modelled any of it,
which is why it is neither in `CutContent.txt` nor in the dressing tables, and why it has a
switch of its own. Everything in it is borrowed: the surfaces are the game's own textures,
the lines are the game's own recordings, and the music is the first game's.

## How the door works

`RC1_ALL.NVC` answers `BOOKSTORE_DOOR OPEN` with one rule per ego. When the shop is
installed, `Assets/Story/Bookshop.txt` brings `rc1_all_sgb.nvc` into RC1's scope; it
counts Gabriel's tries in the game variable `BookshopTries`:

| try | what happens |
| --- | --- |
| 1–3 | "They're closed." |
| 4 | "They're closed." and "I'm tired of fartin' around. Besides I have the feelin' I'm runnin' out of time." |
| 5 | "If I'm gonna catch Montreaux with his pants down, I'm gonna have to be ballsy about it." "Gee... I coulda worded all that better." The door sounds, and he is inside. |

The count goes back to nought on the fifth try, so it is five again from outside. Grace
gets the original line whatever the count says. The new rules win over the shipped one by
their case names, not by file order: a named case outranks `GABE_ALL` in
`ActionResolver.Worth`.

**Coming back out** is the part the 1999 scripts cannot do. RC1's `PlaceEgo$` stands the
player on arrival by asking which room they came from, and a room it has never heard of
gets its fallback, the hotel door across the square. So the engine remembers: when a
player leaves a room for one the game never had, `Gk3SheepApi.Returning` keeps where they
stood, which way they faced and where the camera was, and the room loop stands them
there again after the entering script has run, with the view they left. The spot is
dropped if they arrive anywhere else. This is generic, not the shop's: TE2 gets it too.
A save made inside the shop loses the spot, and comes out at the hotel door.

## What is installed, and the switch

The room is `enhanced/rooms/Sgb.glb` with `SGB.SIF`, `SGB.SCN`, `SGB.STK`,
`SGBWLKBNDS.BMP`, `SGB_ALL.NVC` and `RC1_ALL_SGB.NVC` beside it, the camera shell
`enhanced/models/sgbcambnds.glb`, and the music `enhanced/audio/music/SGBTHEME.WAV.wav`.
All of it is packed: the room files under the `rooms` kind, which keeps a file's extension
so a room's `.SIF`, `.SCN` and `.STK` are three entries rather than one (see
[formats/rebarn.md](formats/rebarn.md)), the shell as a model, the music as audio.

The switch is whether anything answers for `SGB.SIF`, asked of `AddedAssets`, which reads
a loose `enhanced/rooms` in development and the packs in a shipped game. A player without
the enhanced content has exactly the game as it shipped. Since the shop, that layer is
opened whatever the cut-content tier says: it only answers for names the archives do not
know, and what sends a player to a room in it is a rule that has its own gate.

The startup log says which:

    Bookshop: installed, so RC1's bookstore door gives on the fifth try
    Bookshop: not installed — nothing answers for SGB.SIF, so RC1's bookstore door stays closed

## Building it

    python tools/rooms/build_sgb.py --workspace D:/Dev/GK3Reborn/ContentWorkspace \
        --music D:/Dev/GK3Reborn/ContentWorkspace/music/GK1_03_bookstore_SC.mp3
    rebuild-content.cmd --packs-only

or `rebuild-content.cmd --bookshop`, which runs both. No Blender: `tools/rooms/glbwriter.py`
writes the glTF directly, one node per object, one primitive per texture, with the
material's name being the texture the engine draws it with.

The room is the first game's shop as remembered: two storeys of shelves under an iron
gallery, two tall arched windows down the left wall, a straight stair up the brick wall on
the right, Gabriel's desk in the middle of the floor under the chandelier with his own
paperbacks on it, Grace's desk by the brick wall under a painting with her banker's lamp,
her ledger and the *Secrets of the Holy Grail* somebody left outside the hotel-room door.
The reference is the GK1 room as the 20th-anniversary remake drew it.

Every texture is one of the game's: the Château de Serres' carved bookcases and Larry
Chester's lawyer's shelves, Larry's floorboards, plaster and desk, the hotel's windows,
doors, rug and stair rail, Rennes-les-Bains' brick, and so on. `build_sgb.py` names them
all at the top. With the enhanced packs installed the shop wears the same upscaled
pictures as the rooms they came from.

### Lighting

`SceneFromModel` used to mark every surface of a built room self-lit, so a room without a
bake was drawn full bright. Now a glTF may mark a material `KHR_materials_unlit`, and once a
file marks anything unlit, the rest of it is lit by the room's authored lights. The shop
marks its window glass, its lamp shades, the chandelier's candles and the glow under
Grace's lamp; everything else takes `SGB.SCN`'s rig: a scenekey far out beyond the windows
that the engine takes as the sun and moves to the panes (so the daylight and its shafts
come in at them), the chandelier, the banker's lamp, three sconces and three fills. The
window objects are named `sgb_window01`/`02` so `Daylight.IsWindow` finds them. TE2, which
marks nothing unlit, is drawn as it always was.

### Camera bounds and walking

`sgbcambnds.glb` is one box wound inward, thirty units inside the walls, named by
`cameraBounds=` in `SGB.SIF` and loaded through the model library like TE2's.
`SGBWLKBNDS.BMP` is written by the builder from the same numbers the furniture is placed
with: the desks, chairs, cases, stairs, ladder, posts and coat stand are walled off and the
rest is open, with the 0–7 gradient away from everything. A headless walk to Grace's desk
takes 6.3 seconds along eight points.

## What he says

All of it is Gabriel's actor, from elsewhere in the story, chosen for saying something true
here. Which actor a recording belongs to is not written in the data (every `.YAK` says
`UNKNOWN`), so each line was checked against the rule it was recorded for: a `GABE_ALL`
case, a timeblock that is his, or a line only he would say. Two that read well were
Grace's and were dropped: CS3's "Old books -- in French" is her 212P sneak, and Magdala's
Jane Eyre staircase is her 207A morning.

| noun | line | borrowed from |
| --- | --- | --- |
| arriving | Reminds me of New Orleans. | R21 painting, 210A |
| leaving | I get the weirdest damn dreams when I'm on a case! | R25 couch, 210A |
| BOOKCASES, FLOOR_BOOKS | The books are all old -- and French. | Larry's house |
| UPPER_SHELVES, LOW_SHELVES | He has lots of books on Rennes-le-Château, but they're all in French. | the office |
| NOVELS | Yup, he's a writer all right. Only writers have this many books. | Larry's house |
| FRONT_TABLES | Gee, that book looks interestin'. Too bad the store's closed. | RC1's window |
| HOLY_BLOOD_BOOK | 'Secrets of the Holy Grail'. I wonder if that book's got somethin' to do with this area? | RC1's window |
| LADDER | Boy, those are up there. I'm glad I'm a Schattenjäger, instead of a window-washer. | the villa |
| WINDOWS | Wonder why they put the windows way up there? | the train station |
| STAIRS | Top o' the stairs to ya, Ma. | unused |
| RAILING | *Now* they put in a railin'. | Magdala |
| CHANDELIER, LAMP | Charming lamps. It's nice to be able to see just how *fucked* you really are. | the temple, unused |
| CURTAIN | Curtains in matchin' fabric. How suave. | Larry's house |
| PAINTING | Reminds me of New Orleans. | R21 |
| COAT_STAND | I wonder if it can still be called a coat rack if there're no coats on it? | R21 |
| RUG | I don't see any signs of a struggle in there. | the car at Larry's |
| DESK | There's nothin' in there but dust bunnies. | unused |
| CHAIR | Nice chair. | the church |
| OPEN_BOOK | There's some kinda book on the desk. | the lobby register |
| PAPERS | That's Gracie's old mail. | Sidney |
| GRACES_DESK | Looks like Gracie's unpacked. | R25 |
| GRACES_CHAIR | Even if I *did* wanna get married, I'm not sure it would be Gracie. She's like a chair, you know? | the bar |
| LEDGER | That analyze stuff is Gracie's thing. | Sidney |
| FRONT_DOOR | I hope I'm at the right one. | TE1 |
| BACK_DOOR | I don't need anythin' in there. Besides, if I wake up Gracie she'll want to come along. | the hall, 202A |
| BACK_DOOR, open | They're locked, Auntie Em! / Damn it! I thought for sure the exit would be... | the cellar doors, TE2 |

## Music

`SGBTHEME` is the first game's bookshop theme. `build_sgb.py --music` strips the ID3 tags
and wraps the MP3 stream in a RIFF header with format tag 85, which is how the 1999 game
stores its own music and what `WavFile` decodes in process through NLayer. It goes in as
`enhanced/audio/music/SGBTHEME.WAV.wav` and is packed as audio. `SGB.STK` plays it through
once on the way in and waits twenty to forty seconds before it comes round again.
`FontAndSoundTests` decodes the wrapped file when the workspace is present.

## Photographing it

    GK3Reborn.exe --scene RC1 --timeblock 110A --did BookshopTries=4 --do BOOKSTORE_DOOR:OPEN \
        --frames 7000 --screenshot sgb.png --settings scratch.json

`--did` presets the counter, because five `--do` actions all resolve on the first frame,
before the walk, and read a count of nought. Add `--then FRONT_DOOR:OPEN` for the round
trip; the log says `Returned: GABRIEL stood where they left RC1 for SGB`. `--rebarn` runs
it from the packs alone. A run without an audio device cannot show the music played; the
`Ambience: SGB.STK, opening with SGBTHEME` line says the soundtrack found it.
