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
| `[OPTIONS]` | | Present on 13 files; not read |

A caption is a sentence and contains commas, so everything past the fourth field belongs to
the caption rather than being further fields. Section counts are ignored in favour of the
lines actually present, which is what the original does and what survives a file whose count
is wrong.

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
