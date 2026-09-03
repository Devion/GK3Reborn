# Cut content

Gabriel Knight 3 shipped with a great deal of content still on the disc but not reachable
in play: objects whose lines were recorded and whose rules were written, and which the
player can never click. This is a survey of what is actually there, what each piece is
missing, and what it would take to put it back.

The investigation started from **Bonny Ploeg's *Gabriel Knight 3 Secrets***
(<http://bonny.ploeg.ws/gk3secret.html>), which catalogues the unused and disappeared
objects line by line from the shipped YAK files. Every line code quoted below that begins
`A0`–`A2` is one of theirs. That page is the reason this survey exists, and its inventory
was accurate everywhere it could be checked: **all 257 voice-over files it cites are
present in the 1999 archives.** Where the findings here differ it is only in naming — the
page identifies a line by what it says, and the action files name the noun it belongs to.

The restorations this survey found to be safe are in the game, off by default, and so are
four objects rebuilt from scratch because the survey found they never existed: see **How
this is delivered** below.

## What a restoration needs

A line of dialogue reaches the screen only if three separate things line up.

**The recording.** A voice-over is named in an action file as a ten-character code —
`StartVoiceOver("07LBP44PF1",1)` — and stored as `A` + the first seven characters + `.` +
the last three: `A07LBP44.PF1`. The code itself is structured, and the structure is what
lets an orphaned line be placed: three characters of location, two of noun, two of verb,
three identifying the line. `0CL` is the cemetery, `1EL` the hotel lobby, `44` is LOOK,
`32` PICKUP, `0L` THINK, `1O` SHOVEL. Reading every code every action file names gives a
dictionary of 392 location codes, 1,619 noun codes and 159 verb codes, which decodes the
lines no surviving rule mentions.

**The rule.** `<noun>, <verb>, <case>, script={...}` in an `.NVC` file. The case is a named
condition; the resolver picks the most specific case that is true, so a rule whose condition
can never hold is as dead as one that does not exist.

**Something to click.** A noun becomes clickable only where a scene initialisation file
binds a model to it — `model=r23clotheswpj, noun=CLOTHES, type=scene` — where the model is
either an object inside the room's BSP, a separate `.MOD` prop, or a `type=hittest` invisible
volume. No binding, no cursor, and the rule never runs however healthy it is.

So each cut object falls into one of four states, and the state decides the cost:

| | Recording | Rule | Clickable | To restore |
|---|---|---|---|---|
| **A** | yes | commented out | yes | uncomment one action line |
| **B** | yes | commented out | commented out | uncomment two lines |
| **C** | yes | yes, live | no binding | add one initialisation line, pointing at geometry that is already in the room |
| **D** | yes | none | no binding | author the rule *and* the binding; sometimes model the object |

Anything in A, B or C is restoring the developers' own data. D is authoring, and should be
treated as such.

## What the sweep found

Across the whole corpus, not only the objects the page lists:

- **115 noun bindings are commented out** in 52 initialisation files across 42 scenes.
- **203 action rules are commented out** in 55 action files. 176 of them carry a voice-over;
  for **120 of those, every line the rule plays is still on the disc**.
- **122 nouns have live action rules and no binding anywhere in their scene.** That count
  is an upper bound: close-up nouns (`*_CU`, `ITEMS_IN_ROOM`, the museum's `MS3I_PANEL1`–`20`)
  are bound by the inspect view rather than by the room, and the global and inventory nouns
  (`GABRIEL`, `ANY_OBJECT`, `CHURCH_PAMPHLET`) are not scene geometry at all.

Two causes account for nearly all of it, and neither is a deliberate cut.

**A model was renamed and the binding was commented out instead of corrected.** The clearest
case is the Orange Rock: `PL3.SIF:18` reads `//model=pl3_rocks,noun=ROCKS,type=scene`, and
the object in `PL3.BSP` is called `pl3_orangerock`. `WOD.SIF:40` names `wod_stones`; the room
holds `wod_rock01`–`04`. `WOD.SIF:46` names `wod_dectrees`; the room holds `wod_dectree01`–`06`.
One of the game's own files says so out loud — `CHU.SIF:45`,
`//model=chu_frame, noun=FRONT_DOOR, type=scene // this can't be right it makes all the walls the Front Door!!! jwm 2/2/99`.

**A noun was consolidated into a bigger one.** Emilio's sari is not missing: `r27_sari1` and
`r27_sari2` are real models, animated in six of Gabriel's R27 clips, and `R27.SIF:65-66`
binds both of them to `noun=WARDROBE`. The sari's own two lines belong to
`LINEN_OUTFIT_IN_SACK`, which nothing binds. The wardrobe keeps its noun through ten other
models, so pointing the two sari models at their own noun costs the wardrobe nothing.

A third signal is worth trusting when it appears: **six of the unbound nouns still have a
close-up camera set up for them** in `[INSPECT_CAMERAS]` — `ARM` `LOG`, `CLO` `LAUNDRY_BIN`,
`WOD` `HOLE`, `LMB` `DIRT_PILE`, `PLO` `MOUNT_CARDOU`, `VGR` `TIRE_TRACKS`. Somebody framed
the shot. Restoring the binding gets the intended framing for free.

## The disappeared objects, one at a time

### Restored — what the game now puts back

These are in. `--restore-cut-content` applies them; the table is
`src/GK3Reborn.Engine/Assets/Story/CutContent.txt` and
`GK3Reborn.Tools check-cut-content --source <GK3>/Data` applies it to a real installation
and reports every edit. All 39 apply against the GOG release; with `--all`, all 50; with
`--rebuilt`, all 75. `render-scene --restore-cut-content rebuilt` renders a room with them
in, which is how each of the built props was checked on screen, and
`check-scenes --restore-cut-content rebuilt` loads every location at every timeblock with
them applied, which is what says they broke nothing somewhere else.

**Objects that become clickable.**

| Object | Scene | Noun | What was wrong | The edit |
|---|---|---|---|---|
| Estelle's hole in the woods | WOD | `HOLE` | `wod_hole` bound to nothing, though the noun has live day-three rules and a close-up camera | bind, in the timeblocks where the hole is open |
| The log at Devil's Armchair | ARM | `LOG` | `arm_fallentree` bound to nothing; three live rules and a close-up camera | bind |
| The rocks at Devil's Armchair | ARM | `ROCKS` | binding commented out; `ARM_ALL.NVC` speaks for both characters | uncomment two lines |
| The rocks by Larry's house | LHE | `ROCKS` | the same, in `LHE.SIF` | uncomment |
| The illegible graves | CEM | `ILLEGIBLE_GRAVES` | `cem_graves` drawn with no noun; four rules commented out | add the noun, uncomment four rules |
| Emilio's newspaper | LBY | `EMILIOS_PAPER` | the binding was copied without its noun, the original left commented above it | add the noun, uncomment two rules |
| Roxanne's laundry | CLO | `LAUNDRY_BIN` | live rules and a close-up camera, nothing bound; `clo_sheets` already speaks as `SHEETS`, so the bin is the cart | bind `clo_maidcart` |
| Wilkes' clothes, and the missing pyjamas | R23 | `CLOTHES` | both piles folded into `SUITCASE`, which keeps `r23_suitcaseopen` | re-point four bindings, `hidden` flags intact |
| Emilio's sari | R27 | `LINEN_OUTFIT_IN_SACK` | both halves folded into `WARDROBE`, which keeps ten models | re-point two bindings |

**Rules whose noun is already clickable**, all observation verbs, all with their audio: the
Abbé's office door listened at through a glass and looked at while he is inside (3);
Mosely seen and spoken to for the first time in the dining room (2); the water marks on
the dining table (2); Buthane's and Lady Howard's doors listened at (2); the signpost
above Larry's (1); Mosely's moped once one is rented (1); the path up Cardou (2); and at
Poussin's tomb the *I am* words, the small rock, the stick and the tomb itself (6).

**A second tier, `--restore-cut-content all`**, holds eleven more whose verb can *do*
something rather than only say it: CLEAN on the water marks, PRESS on Larry's alarm clock,
ZOOM on the view of Blanchefort, RADIO at Solomon's statue, EXIT on both temple bridges,
OPEN on Gabriel's own door while the maid is in it, the moped in 307A, and PICKUP on the
stick. Each either introduces a verb the noun does not offer or stands in front of a live
rule with a more specific case — which is how the resolver works, so any of them can change
what an action does. That is why they are a tier of their own.

### Superseded, not cut — the trap

Half the page's objects look restorable and are not. A noun was renamed or folded into a
neighbour, and the neighbour is live and speaking for the same geometry. Binding the old
noun would either duplicate a line or take the object away from the noun that uses it.
Every one of these was proposed by the survey and then thrown out by checking what the
model is currently bound to:

| Cut noun | Scene | Already spoken for by | On |
|---|---|---|---|
| `ROCKS` | PL3 | `ORANGE_ROCK`, four live LOOK rules | `pl3_orangerock` |
| `ABBE_GRAVESTONE_FLAT` | CEM | `ABBE_GRAVESTONE_STANDING`, four live rules | `cem_abbegravestn` — the flat stone is not modelled at all |
| `STATION_OF_THE_CROSS` | CHU | `STATION_I` … `STATION_XIV`, individually | `chu_stationi`–`xiv` |
| `CHURCH_WINDOWS_STAING` | RC3 | `HIGH_WINDOWS`, `LOW_WINDOWS` | `rc3_highwindows*`, `rc3_lowwindows*` |
| `FRIEZE_MAGDALA` | RC3 | `CHURCH_DOOR_FRIEZE` | `rc3_churchfrieze` |
| `SYMBOLS_ON_DOORS` | TE1 | `DOORS` | `te1door*` |
| `TIRE_TRACKS_CS_EXT` | PL6 | `TIRE_TRACKS`, five live rules | `pl6_tiretrack_hittest` |
| `ALL_PLATES` | HAL | `R21_PLATE` … `R33_PLATE`, one per door | `hal_rm_21_number` … `hal_room_33_number`. Seven live nouns playing the two lines the commented-out generic one plays, so nothing is cut: *"All the room numbers are odd. Oh, well. So are the guests."* is reachable in the game as it shipped |
| `SIGN_POST` | RC2 | — | `rc2_signpost` is in no room's geometry; the model was cut, not the noun |
| `LADY_H_ESTELLE`, `TWO_MEN`, `BRIDGE_PLAYERS`, `WILKES_N_BUCHELLI` | several | the individual characters | one model each — a group noun bound to one actor's model takes that actor's own noun away |

`ROCKS` in WOD is a third case again: the binding is commented out, `wod_rock01`–`04` are
unbound and would take it — and no action file anywhere has a rule for that noun in that
room. There is nothing for it to say.

### The objects that have to be made

The rule and the recording are there; nothing in the room can be bound because the object
was never built into it. **Three of these are in**, behind
`--restore-cut-content rebuilt` — the furthest tier, and the only one that is not the
developers' own data.

A prop is placed by the coordinates baked into its own mesh — the coordinates of whichever
room it was modelled for — so borrowing one used to mean authoring an animation to move it.
`[MODELS]` lines now take `pos={x,y,z}` and `heading=deg`, which mean *stand here*: the
model is centred on the point in X and Z and its lowest vertex put at the point's Y, so a
position taken off a shelf with `render-scene --pick` lands the object on that shelf. No
`[MODELS]` line in the shipped corpus carries a `pos`, so the syntax was free.

**Then every candidate for reuse turned out to be spoken for.** Measured against the rooms
they would go in: `r31suitcase` is Lady Howard and Estelle's `CHEAP_SUITCASE_IN_CLOSET` and
`r21suitcase` is Buchelli's, both visible; `r33luggage_` is unused but its 35×27 footprint
will not fit the 27×29 hanging compartment in any rotation; and there is no flat sheet to
lie on a desk — `letters`, `letterm` and `letteru` are not paper at all but the carved
letters S, M and U at Poussin's tomb, the `I_AM_WORDS` restored above, while `envelope` is
a 63-unit inventory close-up and `oldnote_mesh` a zero-depth card standing on its edge.

So they are built. `tools/blender/make_props.py` writes one glTF binary per prop into
`enhanced/models`, and `GK3Reborn.Content.ModelLibrary` is what puts one in a room:
overrides first, then a content workspace, then a ReBarn pack.

| Object | Scene | Noun | Built as | Skinned with |
|---|---|---|---|---|
| Madeline's suitcase | R29 | `SUITCASE_IN_CLOSET` | a case 22×40×13, turned side-on to fit the 18.6 units of wardrobe floor left by her clothes | `LHOTRUNKF`, `LHOTRUNKS`, `LHOTRUNKT` — Lady Howard's trunk |
| Dr Wen's documents | R31 | `LETTER_FROM_WEN` | two sheets, fanned, on the writing desk. Two because the line counts them: *"He mentions three documents, but there're only two here."* | `RLCSTATIONARY` — the hotel's own paper |
| The lobby magazines | LBY | `MAGAZINES` | a stack of three on the coffee table | `MAGAZINEFRNT`, `MAGBACK`, `MAGBACK2` — covers the game already carries |
| The Abbé's cigarette ends | MA3 | `CIGARETTE_BUTT_PILE` | a scatter of ends and a dropped packet at the foot of the lookout post on Tour Magdala | `CIGPACKFRNT`, `CIGPACKTOP`, `CIGPACKSIDE`, `CIGPACKBOT` — the FRAIS packet, which is what the close-up line is about |
| The crow's nest, the crow, the hose and the black rug | RC2 | four nouns | the objects the cut crow's-nest puzzle wants; see below | `BarkOld`, `BLACK`, `HOSEPIECE` and `HOSEPIECEBACK` — which nothing in the shipped game uses — and `RUGTILE` |

Two rules hold for all of them, and both are about not being noticed.

**Every prop is textured with the game's own bitmaps and nothing else.** The rooms were lit
and baked against those colours in 1999, and an object painted with anything else reads as
an object from another game standing in this one. The material's *name* is the texture's
name; that is the whole binding, and it is what `GlbReader` reads.

**Every prop is built at the origin standing on the ground plane**, in glTF's own frame with
Y up and the exporter's axis conversion switched off. Which way that conversion goes is a
setting whose effect is invisible until a letter is standing on its edge on somebody's desk —
which is exactly how the first build of these came out. The one prop that does not stand on
anything is the rug, which hangs: its lowest vertex is the loose end in the air, so its `pos`
is the sill less the drop rather than a surface, and the table says so beside it.

**The model layer answers only for names the archives have no `.MOD` for.** That boundary is
the whole safety argument. `enhanced/models` also holds several hundred meshes from the
mesh-enhancement pass, named after props that *did* ship; a library that could stand in front
of those would swap every chair and lamp in the game for a generated one with nothing on
screen to say so. Replacing what shipped is a separate feature and wants a separate setting.

### Still to make

Nothing, of the objects this survey found. What is left is one close-up rather than a
model: `CIGARETTE_BUTT_CU`, the leaning-in view whose two lines turn on whether the player
has seen the same packet in the Abbé's office. A `_CU` noun is bound by the inspect view
rather than by the room, so it wants a close-up image and not geometry — the packet the
pile is built with is the picture it would be of.

### Missing something that cannot be supplied

| Object | Scene | What is missing |
|---|---|---|
| Madeline's hairbrush | B29 | No brush model exists anywhere in the corpus. Rules and both lines (`A2NLVS0L.PF1`, `A2NLVS44.PF1`) are intact; the bathroom holds only `b29_toothbrush` and `bth_sink`. |
| Lady Howard's peignoirs | R31 | Lines only (`A0M84C0Z.PF1`, `A0M84C44.PF1`). Noun code `4C` is used by no rule in the game, so there is no noun, no rule and no model — only `cs3_robe01`–`03`, which are Montreaux's. |
| The 'B.S.' in the church | CHU | Lines only (`A18L5Q44.BB1`, `.O21`). Noun code `5Q` unused; no inscription object in `CHU.BSP`. |
| The dining room as a doorway in the lobby | LBY | Lines only (`A1EL8344.6P1`, `.7E1`). Noun code `83` unused. |
| The spinning disk at the pendulum | TE3 | Line only (`A1REYA44.Q81`). The disk is an object but the script mounts Gabriel on the first click, so there is no moment at which a LOOK could be offered without changing the puzzle. |
| The burgling lines | — | `A2H8MP44.PF1`, `A2H8MQ44.PF1`, `A2H8NM44.PF1`. Location code `2H8` is referenced by no action file, script, moment or animation in the game. The scene they belong to is gone. |
| The columns at Blanchefort | CD1 | Two voiced lines (`A0ZLQA44.QR1`, `.QS1`) and live rules, and the columns are plainly there on screen — but they are inside `cd1_walls`, which is one object bound to `RUINS_WALLS`. Nothing available can separate them. Improved room geometry changes what is *drawn* and not what is *picked*, so splitting them in the overlay would not make them clickable; and a hit-test volume is not a prop but an ordinary BSP object the scene file hides, so one cannot be added without editing the room. Both a surface-level noun and a modified BSP are larger than this, for two lines. |
| Grace's e-mail from her mother, and Chadrel's | Sidney | `A02O2458.PF3`, `A02O247L.PF2`, `A02O5744.PF1`. Inventory/Sidney nouns with no rule; the mail screens are data-driven and adding a line means adding a message. |

### The crow's nest: the objects are back, the chain is not

The puzzle that the Cat Hair Moustache replaced is not a rumour. `RC2102P.NVC` is
twenty-two lines long and **every line of it is commented out** — the complete design, with
its own logic block:

- `BIRDS_NEST` with LOOK, PICKUP, THINK, HOSE and HOSE_AND_SPRAY_GUN;
- `CROW_AT_NEST`, seen once Gabriel knows about the bikes;
- `GARDEN_HOSE` with `CombineInvItems("SPRAY_GUN","HOSE","HOSE_AND_SPRAY_GUN")`;
- `TREE` handing over `BLACK_FIBERS` when sprayed;
- `WATER_INTERFACE` with an AIM verb and a `ON_NEST_FOR_10_SECONDS` case — an aiming
  mini-game;
- `BIRDS_NEST_ON_GRND` once it is down;
- seven named cases wiring it to `GetTopicCount("MONSIEUR_BIGOUT","T_RENT")`.

`RC2_1ALL.NVC` carries six more, and they give away where the fibres came from:
`BLACK_RUG_IN_WINDOW`, *"Someone's airing their rug."*

The supporting assets survive too. `SPRAY_GUN` is a real inventory item — `SprayBottle` in
`INVENTORYSPRITES.TXT`, "Spray bottle" in `ESTRINGS.TXT`, with its bitmaps and cursor art.
`BLACK_FIBERS` is an item, its string reused as "Black fur" for the shipped cat puzzle.
`GABRC2GRABSHOSEWW.ANM` is the animation the cut rule calls. `CEM102P.SIF` still switches
models on `DoesGabeHaveInvItem("SPRAY_GUN")`. And **`HOSEPIECE.BMP` and `HOSEPIECEBACK.BMP`
are in the archives and used by nothing** — the hose's own texture, made for this and never
placed.

**Eighteen of the nineteen recordings were deleted. Their YAKs were not.** Each still
carries its caption and simply names no sound. That was supposed to mean they play as
subtitles for the length the animation was cut to, and for a while it did not: the audio
layer read the caption off the animation and cleared it in the same call, because a line
with no recording was treated as a line with nothing to say. Every one of these was silent
*and* wordless while the waited `StartVoiceOver` went on spending its three seconds, which
is a click that visibly does nothing — see `docs/known-issues.md`. A caption-only line now
holds for as long as its animation is. Thirteen of the puzzle's lines have their exact
wording that way:

> *"He's using fibers from that black rug to line his nest. **I** could use some of those."*
> *"I ought to be able to use that hose on the bird's nest."*
> *"It's not gonna work as is. I can't aim the water flow."*

**What is in:** the four objects it wants, built by `make_props.py` and placed in RC2 — the
nest high in the museum tree, the crow sitting in it, the hose coiled by the museum steps in
`HOSEPIECE` and `HOSEPIECEBACK`, and the rug hung out of an upstairs window in `RUGTILE` (the line calls it
black; `RUG1` is cream). With them, the lines that describe them. The tree itself is a plain
restoration:
`rc2_museumtree` was bound to nothing and both of its rules were commented out with their
audio intact.

**Three of the four were wrong the first time, and all three in a way only looking at the
room shows.** The rug went over the museum railing, where it hung in the air beside the
steps; the noun is `BLACK_RUG_IN_WINDOW` and its `PICKUP` line is *"I can't get up there"*,
neither of which is true of a railing a player can walk to. It is now folded over the sill
of `rc2_highwindows2` — the nearer to the museum of the pair of upstairs windows on the west
side of the street, sill at y 193.4, wall facing (-0.999, 0, -0.042) — and drops 38 units
down the stone, which is as far as it can hang before the string course under it.

The other two were boxes, and a box is the wrong answer twice over. The nest was two stacked
cuboids, which in a tree reads as a crate somebody left up there; it is a solid of revolution
now, 26 across and 11.5 high on a woven cross-section taken round in sixteen steps, with a
cup deep enough that the crow sits *in* it rather than on top of it. **The hose was three
flat boxes, and its own texture said so.** `HOSEPIECE` is 128×16 — a length of hose seen
side-on, dark at both edges with a highlight down the middle — which is a skin for a tube and
nothing else; laid flat on the top of a box it put that highlight across a plate, and three
of those stacked read as a pile of crockery. It is three turns of round tube now, the strip
running once along each turn and once around it, with the top turn stopping 60° short so the
hose has an end. The two faces that leaves are capped with `HOSEPIECEBACK`, which is what
that texture is — a brass ferrule and the green — and the gap is put where FR_MS2 stands,
since that is the camera the player is on at the steps. Everything else in `make_props.py` is
something an artist would have built out of flat panels in 1999 and still would; a nest and a
hose are not.

The three of them cost `make_props.py` a `revolve` part: a closed (radius, height)
cross-section taken round the Y axis, optionally through part of a turn and capped where it
stops, and optionally mapped along itself rather than face by face. A point at radius nought
is one vertex on the axis and the ring beside it closes as a fan, which is how the nest gets
a floor without a separate cap. 224 triangles for the nest and 580 for the hose, against a
dozen for a magazine — which is the right trade only where the shape is the whole point.

**And the chain, including the interface it ends in.** Two things in `RC2102P.NVC` could not
simply be uncommented: the case `ON_NEST_FOR_10_SECONDS`, which a rule names and no `[LOGIC]`
block anywhere defines, and the interface that rule is about, which was never built. So there
is now `Game.WaterAiming` — ten seconds of water held on the nest, which is the number the
case is named for and the only thing about it that was not a choice.

The jet trails the aim, because a hose under pressure does, and the nest sways, because it is
in a tree; without either it would be parking a cursor. It is deliberately forgiving: time on
target is banked and bleeds back at half the rate it fills, so a wobble costs a moment rather
than the attempt, and there is no failure state — the way not to solve it is to leave. The
jet chases by an exponential rather than a fixed step, so it is no easier to hold on a faster
machine, and a test pins that.

The rules that hang off it are in `rc2_crowsnest.nvc`, which the table brings into the room's
scope with a new `append` operation — the one thing uncommenting cannot do. It is listed by
the *timeblock* file rather than by `RC2.SIF`, because a name in a general SIF has to say
which timeblocks it is for and this one says nothing of the sort; the original has the same
exception in CHU.

**The two puzzles converge where they should.** `INV102P.NVC` combines `BLACK_FIBERS` with
`SYRUP_PACKAGE` into `BLACK_MOUSTACHE`, and that rule is live and always was. The cat gives
black fur; the crow's nest gives black fibres; either feeds the same combine and the
moustache is made the same way. Nothing about the shipped chain is touched.

**Most of the way in was live too, which was a surprise.** `SPRAY_BOTTLE`/`PICKUP` in
`CEM102P.NVC` is not commented out, its approach animation is `gabcemgetsprtz`, and the
script it calls — `GetSpray` in `CEM102P.SHP` — holds `EgoTakeInvItem` and `SPRAY_GUN`. So
taking the Abbé's spritzer has been in the game all along, with nothing to use it on.

One gap was real: `HOSE` and `HOSE_AND_SPRAY_GUN` are named by the cut rules and **neither
has a sprite or a string anywhere**, so an inventory holding one would show a blank with no
name. Rather than invent two items, `rc2_crowsnest.nvc` makes the one the puzzle needs —
using the spray bottle on the hose gives `HOSE_AND_SPRAY_GUN` directly — and the table adds
its picture and its name with `append`, taking the spray bottle's own sprite, which is what
the player is carrying.

**The nest falls and the crow leaves.** RC2's own clips for this did not survive, so they
are made — in the game's own format rather than in a new one. A `.ACT` stores, per frame and
per mesh, either where a model's vertices are or where its mesh groups sit, and **2,188 of
the game's 5,796 clips are the second kind only**: transforms, no vertex data at all. A nest
coming out of a tree and a bird flying off are rigid motion and nothing else, so they are
that kind, and writing one is a 4×3 matrix per frame with a five-byte block header. The
format is `docs/formats/vertex-animation.md` and the engine's reader checks five invariants
while reading, which makes it its own test: the clips play, so the bytes are right.

`tools/rooms/make_rc2_animations.py` writes them. The nest is knocked sideways before it
drops, because water pushed it, and turns as it goes; the fall is quadratic and the last
four frames are it settled, because a clip that ends mid-air leaves the prop hanging there.
The crow waits two frames — long enough for the water to visibly hit — then climbs away.

Two things about the text file that goes with a clip cost an hour between them, and both
failed silently. **An `[ACTIONS]` section begins with a count**, and without it the only
action line is eaten as that count: the animation then names no clips and plays nothing,
which is not an error anywhere — it is just a prop that never moves. And **a clip with eight
zeros after its name is not "no placement" but "absolute at the origin"**, which would drop
the nest through the floor at the middle of the world; a prop wants no numbers at all.

**Checked against the whole corpus.** `check-scenes --restore-cut-content rebuilt` loads all
79 locations at all 17 timeblocks, and the result is identical to the same run without it:
the same diagnostics, the same codes, nothing new broken anywhere else.

The cat-hair moustache is untouched and still works. This is a second way through, not a
replacement.

**And the voice.** The twenty-five lines this restoration needs are spoken by
`tools/audio/make_restored_voice.py` through a running ComfyUI, from the plan
`tools/audio/plan_restored_voice.py` writes: what each line says, where those words came
from, who says it, and the recordings the clone is conditioned on. Nineteen say words the
game's own captions preserved and five say words we wrote, and the manifest keeps them
apart, because a line that is ours must never be filed as one of theirs. One of them —
*"Someone's airing their rug."* — is Grace's, and is cloned from Grace: the speaker of each
line is read off the rule that plays it, which is decisive where it can be measured, 583 of
the plates ending `QS1` being played under `GRACE_ALL` and none under `GABE_ALL`.

**How a recording reaches a line, and how it did not.** A YAK's `[SOUNDS]` names its
recording, and a deleted one names nothing — so audio put back under the 1999 name had
nobody asking for it, and fourteen spoken lines sat in the pack unplayed. The engine now
falls back to the recording the licence plate implies: `E1395D0LCW1` carries
`A1395D0L.CW1`, which **6,606 of the corpus's YAKs name exactly**. It is reached only where
the YAK names none, and that is what makes it safe — **of the 90 soundless YAKs in the
shipped game, not one has its implied recording in the archives**, so it can never give
voice to a line the developers silenced. See `docs/known-issues.md`.

Four of the twenty-five had no `.YAK` either, having been cut before one was made, and a
line with no wrapper cannot be spoken at all however good the audio is: the wrapper is what
the engine reads. Those four are written by the generator into `enhanced/rooms`, in the
game's own shape, and carry their caption with them.

A player who would rather have a real performance drops one into `overrides/audio/` under
the same 1999 name, and it wins over both.

### The Mysterious Room 2

The page reports an entire room cut from the temple, guessed from its lines to be a maze
with a contraption in it. It is more specific than that, and more completely documented.

There was no `TE2.BSP` — the room's geometry is not on the disc. But `TE2A.SCN` and
`TE2B.SCN` are, and a `.SCN` is the exported scene: it names the BSP (`BSP=Te2`), lists
every object in the room, and carries the full light rig. `TE2A.SCN` is dated 1 July 1999
and holds **38 objects and 60 lights**; `TE2B.SCN` is the same 38 objects under a second
rig of **148**. The object names describe the puzzle:

```
te2_firenookwalls  te2_waternookwalls  te2_airnookwalls  te2_earthnookwalls
te2_flint  te2_salamander  te2_firebasin  te2_firepipe  te2_fireplaque  te2_firepanel
te2_fishhead  te2_waterspout  te2_waterbasin  te2_waterpipe  te2_waterplaque
te2_vent  te2_gauge  te2_bellhanger  te2_airplaque  te2_te2_airpipe
te2_oilspout  te2_skulltop  te2_earthbasin  te2_leverbase  te2_earthplaque
te2_upperdoorl  te2_upperdoorr  te2_upperdoorhl  te2_upperdoorhr
te2_elevator_walls  te2_rockformations  te2_centerwalls  te2_innerfloorrim  …
```

Four elemental nooks — fire, water, air, earth — each with a basin, a pipe and a plaque; a
flint and a salamander; a fish head and a water spout; a vent, a gauge and a bell hanger; an
oil spout, a skull and a lever; and an elevator. Fire animations (`TE2FIREHI.ANM` and its
LOD variants), the four fire textures, `TE2LOWERDOOR.BMP`, `TE2WRNLTHR.BMP` and six
`TE2BLENDSLOPE*` animations survive with it. 62 voice-over files carry the location code
`1SE`, which no shipped room uses, and 61 of them are referenced by no action file, script,
moment or animation anywhere in the game.

The light rig also gives the room's size: the 60 lights of `TE2A.SCN` span 1,417 units by
1,404 with 267 of height — a square hall about as big as the museum, with the four nooks in
its corners.

**It loads.** Not by writing a `.BSP` — there is no writer for one here and there does not
need to be. What the rest of the engine actually asks a room for is what
`BspFile.FromParts` already takes: named objects, surfaces that name a texture and belong to
one, and polygons over shared vertices. glTF carries that shape, so `SceneFromModel` builds
a room out of a model and everything downstream — drawing, picking, the floor, hidden
objects, the light rig — works unchanged.

`GK3Reborn.Content.RoomLibrary` supplies them, from `overrides/`, a content workspace's
`enhanced/rooms`, or a ReBarn volume. **It answers only for names the archives have no
`.BSP` for** — the same boundary the prop library has, and with more at stake: a room is not
a chair, and its floor, walk boundary, cameras and bake all belong to the original. Replacing
a room that shipped is what the improved-geometry overlay does, carefully; this cannot do it
at all. Nothing here reaches a barn.

Two details are load-bearing. Every surface is marked `IgnoreLightmapFlag`, because there
are no lightmaps for a room that never shipped and a surface expecting one and finding none
is drawn black — so the room is lit by its own rig, which is exactly what survived. And a
node's transform is baked into the vertices rather than kept beside them: a `.MOD` keeps it
separate so a scene can pose the parts of a model, but a room is not posed.

So the chain is the game's own `TE2A.SCN` — its object list and its sixty lights — plus a
generated `Te2.glb`, plus a `TE2.SIF` in `overrides/`. The log reads:

```text
asset: TE2A.SCN, bsp Te2, 38 objects, 60 lights
geometry: Te2 built from a model, 504 triangles, 42 surfaces, no bake
Scene TE2: ... 60 authored lights
```

**What is in the room is a blockout and nothing more.** `tools/blender/build_te2.py` builds
it to the surviving specification: the size the light rig implies, the object names the scene
file lists — which is what lets a noun be bound to one — the nooks in the corners the lights
cluster in, and the temple's own textures with TE2's four surviving ones. Two things in it
are guesses and are marked as guesses in the script: which element is in which corner, which
nothing records, and the floor height. Everything else is invention.

**The puzzle is not.** Every one of the sixty-five lines under the location code `1SE` is in
the archives *and recorded* — "unreferenced" is not "missing" — and read together they
describe the puzzle exactly:

> *"It's square-shaped and in each corner is a kind of . . . well, some kind of vent or
> spigot or somethin'."* — Gabriel, on the radio to Grace
> *"Then I'd say your exit has something to do with those four corners."* — Grace

Pulling the chain lights the fire, and the fire opens the way out. It will not stay lit
while the water runs — *"The water just puts it out. That can't be right."* — nor without
air: *"That* should *work, but it's so suffocatin' down here — the fire won't stay lit."*
So: shut the spigot, crank the air shaft open, fill the vase from the lever, pull the chain,
and *"Wow! It worked!"* — followed immediately by *"Uh . . . I think it's time to find a way
out. Now."* and a timed escape whose failure lines are all there too.

`Assets/Rooms/TE2309P.NVC` is that, written out: twenty nouns, four elemental stations, and
a `[LOGIC]` block whose cases are the flags the lines imply. Every voice-over it names begins
`1SE`, and a test holds it to that — a line that did not would be one somebody invented. Two
verb codes in the room are used nowhere else in the game and are named from what their lines
do: `PULL`, on the chain, and `ENTER_SHAFT`, on the grating.

**A room needs three things and it has all three.** A scene file, an action file, and a walk
boundary — 9,632 of 16,384 texels open, generated from the same floor plan the geometry is
built from by `tools/rooms/make_te2_boundary.py`, because a boundary that disagrees with the
geometry reads as a haunting rather than as a bad bitmap. The camera shell comes from the
model library like a prop, wound inward.

All of it lives in `enhanced/rooms` beside the geometry, or in a ReBarn volume, because that
is what it is: content, built by `tools/` and packed like everything else. Nothing of a cut
room is in the engine — the engine carries the means to read one. The two hand-written files
have their source in `tools/rooms/te2/` and are published into the workspace by the same
script that writes the boundary. `AddedAssets` serves them, **last of every layer, after
every barn**, so it can only answer for a name the game does not know, and it is empty unless
the player asked for cut content.

```text
Added assets: 3 file(s) the game never had
geometry: Te2 built from a model, 504 triangles, 42 surfaces, no bake
walkable: TE2WLKBNDS, 128x128 over 1400x1380 units, 9632 of 16384 texels open
camera bounds: te2cambnds, 12 triangles
actions: 129 nouns from 6 of 6 sets, most specific first: te2309p.nvc
nouns: 20 on the scene's objects, 20 of them known to the action files
do PULL_CHAIN:PULL [NO_OIL] from te2309p.nvc:53
```

That last line is the puzzle resolving: with nothing done yet, the chain gives *"Okay. That
was interestin'. Pointless, but interestin'."* — which is the line the developers recorded
for exactly that state.

What is still missing is the staging: the fire does not light on screen, the doors do not
open, and there is no timer on the escape, because those are animations and TE2's are gone.
The lines play, the flags move, the cases resolve.

## How this is delivered, and how it is checked

**In the game.** *Playing → Cut content* cycles four ways and takes effect the next time
the player walks into a room:

| | What it adds |
|---|---|
| **Off** | nothing. The game as it was released. |
| **Things to look at** | rules and bindings the developers switched off, observation verbs only |
| **Everything, puzzles included** | and the restored rules whose verb can *do* something |
| **And objects rebuilt from scratch** | and the objects that were written and recorded but never modelled |

Each step is everything below it and one thing more, and only the last puts geometry in
the game that nobody at Sierra made. On the command line, `--restore-cut-content`,
`--restore-cut-content all` and `--restore-cut-content rebuilt` do the same for one run and
win over the setting. Off is the default in both.

**How it is delivered.** Not as replacement files. `src/GK3Reborn.Engine/Assets/Story/CutContent.txt`
is a table of edits — uncomment this rule, add this noun, point this model at that one —
applied to the archive's bytes on their way past. A rewritten `R23210A.SIF` would be a
derivative of Sierra's asset and this project ships none; and an edit that has to *find*
what it is changing can say so when the installation underneath is not what it expected,
where a wholesale replacement would silently impose 1999's file on a different release.
Nothing is written to the player's installation, and an override is never rewritten: a file
the player put in `overrides/` is theirs.

**How it is checked.** `GK3Reborn.Tools check-cut-content --source <GK3>/Data [--all]
[--rebuilt] [--verbose]` applies the table to a real installation and prints, per file, how many lines
changed and what they now say. An edit that cannot find its line reports `GK3R1190`–
`GK3R1193` and changes nothing. That is the whole safety story: a binding that names an
object a room does not contain fails silently, which is how most of this content was lost
in the first place.

**A room that never shipped** comes from the same three places a prop does: `overrides/`,
a workspace's `enhanced/rooms`, or a pack — and only ever for a name the archives have no
`.BSP` for. Its scene file goes in `overrides/` like any other game asset. See **The
Mysterious Room 2** above.

**Hand edits** still work the way they always did: a modified initialisation or action file
in `overrides/` replaces the archive's copy for that run — see [overrides.md](overrides.md).
`GK3Reborn --extract --from game --kinds SIF,NVC --name R23` writes the original out under
`overrides/game/` to start from.

Two rules hold for anything restored:

- **Restoration and reconstruction are labelled apart.** Everything in the table is the
  developers' own data switched back on. Anything that needs a model placed, a rule written
  or a line performed is new content and belongs behind its own switch, off by default.
- **A restored object is verified on screen, not in the file.** `check-cut-content` proves
  the edit applied; `render-scene --noun-map` proves the player can click what it bound.

## Credit

The catalogue of unused and disappeared objects, and the line-by-line transcription that
made this survey possible, are **Bonny Ploeg's** work:
*Gabriel Knight 3 Secrets*, <http://bonny.ploeg.ws/gk3secret.html>, and the companion
*GK3script* compilation of every YAK file in the game. Every specific claim on that page
that could be checked against the archives checked out.
