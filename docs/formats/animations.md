# Animations (`.ANM`, `.YAK`)

14,234 files, the largest asset family in the game, and the reason a line of dialogue takes
as long as it does.

Both extensions are the same format. An `.ANM` animates something — a door opening, an
actor walking. A `.YAK` is a line of recorded dialogue: the audio, the caption, and the lip
and gesture animation that go with it.

## Layout

An INI file. `[HEADER]` is a frame count on its own line; every other section lists things
that happen on a frame, opening with how many of them there are.

```
[HEADER]
150

[SOUNDS]
1
0,ClownTile1,100

[GK3]
1
0,SpeakerCaption, 149, GABRIEL,Starring:
```

| Section | Line | Meaning |
| --- | --- | --- |
| `[HEADER]` | `<frames>` | How long it is |
| `[ACTIONS]` | `<frame>,<act>,<x1>,<y1>,<z1>,<angle1>,…` | Start a vertex animation, optionally placed |
| `[SOUNDS]` | `<frame>,<sound>,<volume>` | Play a sound |
| `[GK3]` | `<frame>,SpeakerCaption,<end frame>,<noun>,<caption>` | Say a line |
| `[GK3]` | `<frame>,SPEAKER,<noun>` then `<frame>,CAPTION,<text>` | The ordinary form of the same thing |
| `[GK3]` | `<frame>,LIPSYNCH,<noun>,MOUTH03` | Put a mouth shape on somebody |
| `[GK3]` | `<frame>,FACETEX,<noun>,<bitmap>,<part>` | Paint a bitmap over a region of a face |
| `[GK3]` | `<frame>,UNFACETEX,<noun>,<part>` | Take it off again |
| `[MVISIBILITY]` | `<frame>,<model>,<on\|off>` | Draw a model from this frame on, or stop |
| `[MVISIBILITY]` | `<frame>,<model>,<mesh>,<submesh>,<on\|off>` | The same for one part of it |
| `[GK3]` | `<frame>,FOOTSTEP,<noun>` / `FOOTSCUFF` | Put a foot down |
| `[MTEXTURES]` | `<frame>,<model>,<mesh>,<submesh>,<texture>` | Repaint one submesh of a model |
| `[OPTIONS]` | `<frame>,FRAMERATE,<n>` | Run at this rate rather than fifteen |
| `[OPTIONS]` | `<frame>,SIMPLE,<n>` / `<frame>,NOINTERPOLATE` | Read past. `NOINTERPOLATE` is now a real instruction rather than a curiosity — see below — and is still not obeyed: nine clips ask for it, all of them the moped and the van. |
| `[STEXTURES]` | `<frame>,<scene>,<object>,<texture>` | Repaint a run of surfaces in the room itself |
| `[SVISIBILITY]` | `<frame>,<scene>,<object>,<on\|off>` | Draw part of the room from this frame on, or stop |
| `[MORPHS]` | | 7 files; not read |

A caption is a sentence and contains commas, so everything past the fourth field belongs to
the caption rather than being further fields. Section counts are ignored in favour of the
lines actually present, which is what the original does and what survives a file whose count
is wrong.

`SPEAKER`/`CAPTION` is 7,380 of the game's lines and `SpeakerCaption` only 211, in the long
cutscenes — a reader that handles only the documented-looking one understands three percent
of the dialogue.

**`[MVISIBILITY]` is how somebody who is not in the room walks into it.** 208 animations
carry one. `EmlRc1ExitLobby` is the plain case: two `[ACTIONS]` lines swing the hotel door
and move Emilio, two `[SOUNDS]` cues make the door's noise, and `0,eml,on` is the only thing
in the file that says he is there at all. Reading everything except that line gives a door
that opens by itself, with a sound, and nobody behind it — which is what it did.

Frame zero is applied the moment the animation starts rather than a tick later, because a
change on the opening frame states what is true while the animation runs; waiting shows one
frame of the old state, and for a character being brought into the room that is one frame of
them standing at the origin.

**`[STEXTURES]` is the room changing rather than a thing in it.** 198 lines across 78
animations, and the two families are not interchangeable: `[MTEXTURES]` names a mesh group
and a submesh of a model the scene loaded from a file of its own, while this names a run of
surfaces inside the room's own geometry — `rl2floor`, `bartop`, `lbywindow` — the same
objects `ShowSceneModel` addresses. The bar's dance floor under the disco ball is nine of
them, `checker_01` through `checker_03` cycled on a two-second loop; the light coming on in
Grace's office is one; the rest are the view through the lobby window gaining a parked car
or van.

**The scene name each line carries is read past.** Every one of the bar's says
`rl2_disco_a`, which is the variant the artist had open, and an animation is only ever
played by the room that owns it — so matching on it would reject exactly the lines the room
is asking for. An object the room does not have is skipped and reported as `GK3R3346`, which
is ordinary: the lobby's window animations are played by three timeblocks' worth of scenes.

**An animation of nothing but these still takes time.** `disco_flashdance_a` names no clips,
no sounds and no captions — it is a floor flashing and nothing else. Reporting it as taking
no time makes `wait LoopAnimation(...)` walk straight past it.

**A footstep node says only when and whose.** What it sounds like is three other files'
business: the floor texture underfoot through `FLOORMAP.TXT`, the character's shoes through
`CHARACTERS.TXT`, and the pairing of the two through `FOOTSTEPS.TXT` and `FOOTSCUFFS.TXT`.
3,704 of them across the corpus. The commonest case is the hardest to wire, because a walk
cycle is looped by frame rather than played as an animation — see `docs/walking.md`.

**A frame rate is per animation.** Fifteen unless an `[OPTIONS]` line says otherwise, which
thirty of them do — from 5 to 580. The option carries a frame number, so in principle the
rate may change part-way through; one animation in the game does that and nothing appears to
play it, so the last rate named wins for the whole clip, as the reference also does. The rate
governs the vertex clips the animation starts as well as its own schedule.

The face nodes are the largest thing in the section by a long way: 98,410 `LIPSYNCH` and
1,268 `FACETEX`/`UNFACETEX`. They are what makes a mouth move while a line plays and what
makes a character blink. See `faces.md`.

## Duration

**Fifteen frames a second.** Nothing in the files says so — it is
`Animation::mFramesPerSecond` in the reference implementation. A reader that assumed thirty
would make every line of dialogue in the game half as long as it is.

So `duration = frames / 15`, and that is the whole reason this format is read at all. See
`docs/scripting.md` on waiting.

## Finding the file a script meant

This is the hard part, and getting it wrong is silent: every wait comes back zero and the
game plays at infinite speed.

A script says:

```
wait StartVoiceOver("0NQIB44QR1", 2)
```

That names neither a file nor one asset.

1. **The last character is a sequence number.** `0`–`9` are themselves, `A`–`Z` carry on
   from ten. The rest of the plate is fixed. Two lines means `…QR1` and `…QR2`.
2. **Spoken assets are localised by prefix.** The English recording of `0NQIB44QR1` is
   `E0NQIB44QR1.YAK`. Scripts never write the prefix; the engine adds it.

Miss step 2 and **none** of the game's 4,642 voice-overs resolve. With it, 4,916 of the
4,961 lines they name are found — 99.1%.

`StartYak` is the exception: it names one animation outright rather than a run of them.
`StartMom` is localised the same way as dialogue.

The reader tries four names in order — `<name>.ANM`, `<name>.YAK`, `<E><name>.YAK`,
`<E><name>.ANM` — and remembers the answer, including that there wasn't one.

## What is not read

The frames are a schedule, not a performance. Running one needs the vertex animation format
(`.ACT`) and an audio device, neither of which exists yet. What is read is duration, which
is what a script that said `wait` needs to know, and the sounds and captions, which are what
playing one will need.

## Corpus

`check-scenes --deep` reports the pacing it can account for:

```
21064 of 22556 waited statements have a length (93.4%), 1201 minutes of the player's time in all
```

The 6.6% that do not are calls whose length lives somewhere still unread — a soundtrack, a
walk, a conversation. Those stay instantaneous rather than being guessed at.

Voice-overs, measured over the corpus: median 3.1s, mean 3.7s, 95th percentile 8.0s, longest
44.5s.

## What starts them

Scripts, mostly — and for scenery, the room itself: a `gasprop` carries a `.GAS`
behaviour script that plays an animation and loops. See `behaviour-scripts.md`.

## Between the recorded frames

A clip records fifteen poses a second and the game draws sixty or more, so playing the poses
as they stand shows each of them four times over. `ActFile.PoseAt` mixes the two poses either side
of the moment instead — but only where the two are recorded on *consecutive frames*, which is
the reference's rule. A mesh that does not move is not written again, so a gap in the
recording is a pose held for the length of the gap; mixing across one sets the mesh off the
moment the hold begins and lands it as the hold ends. Rotation is a spherical mix rather than
a mix of the matrices, which would shrink whatever is between them. `ShapeAt` does the same for the vertex shapes, straight down the
line between the two recorded ones.

**The walk stride was the exception until 2026-08-24** and asked for whole frames, which is
why walking was the one thing in the game that still read as 1999 while the rest of a
character did not. It goes through the same path now, with the forward travel taken out at
the same fractional moment that poses the meshes; what still measures whole frames is
everything that asks a question *about* the clip — how far a stride travels, whether its last
frame closes onto its first — and the footsteps, which are events on numbered frames rather
than quantities to mix.

**What is still linear.** Two recorded poses are joined by a straight line, so a limb that
reaches the end of its swing changes direction in one frame rather than easing through it. A
cubic fit through the neighbouring poses would round that off, at the cost of overshoot where
a clip changes direction sharply — which on a foot plant reads as a slide. Not attempted yet.

**Nine clips say `NOINTERPOLATE`** and are interpolated anyway: `GABPROPFWD` and the rest of
the moped set, and `MADVAN_PL4`. None of them is a walk. Worth honouring now that everything
else is mixed, since a clip that asks for whole frames is asking for a reason.
