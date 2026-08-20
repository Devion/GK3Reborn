# Audio (`.WAV` and everything pretending to be one)

7,852 sounds. **7,656 of them — 97.5% — are an MP3 stream inside a RIFF header.**

That is the fact everything else follows from. Format tag 85 with a
`MPEGLAYER3WAVEFORMAT` `fmt` chunk: 44.1 kHz, 64 kbps, mono for dialogue and stereo for a
few soundtracks. The 196 that are honestly PCM are 22 kHz footsteps and one looping fly.

So "read the WAV files" gets you a silent game. Every line of dialogue and almost every
soundtrack is compressed.

## Where that gets fixed

`Plan/01` settles it: **conversion is an import concern and the runtime never shells out.**
So the corpus is decoded once, offline, into the content workspace — beside the textures
that were converted out of GK3's own container for the same reason.

```bash
GK3Reborn.Tools import-audio --source <GK3>/Data --workspace <ContentWorkspace>
```

Output is `normalized/audio-pcm/<archive name>.wav`, PCM 16-bit. All 7,852 come through;
none are refused. The `.wav` is **appended** rather than substituted, because two archive
entries differ only in the extension that carries their sequence number.

The engine then reads plain PCM. Meeting a compressed file at runtime is `GK3R1121` — a
diagnostic saying the import has not been run, not a mystery silence.

## Finding the sound a script meant

Three hops, and the script names none of them.

```
wait StartVoiceOver("0NQIB44QR1", 1)
        │
        ├─ licence plate + line count, not an asset name
        ▼
E0NQIB44QR1.YAK          the language prefix is added by the engine
        │
        ├─ [SOUNDS] 0,A0NQIB44.QR1,100
        ▼
A0NQIB44.QR1             the audio, in the archives
```

See `docs/formats/animations.md` for the plate arithmetic and the language prefix, both of
which fail silently when wrong.

A soundtrack asks differently. `R25SNDTRKL.STK` names `R25Theme1`, and the archive holds
`R25THEME1.WAV`, so **a name with no extension is tried again with `.WAV`**. Without that,
every room is silent while every line of dialogue plays.

## Playing it

`OpenAlBackend` over OpenAL Soft. One buffer per sound, uploaded on first use and kept;
twenty-four sources handed out as things play, and a sound that cannot get one is dropped
rather than queued — a footstep that arrives late is worse than one that never arrives.

Buses are gain multipliers, and each voice remembers which bus it is on, so turning
dialogue down turns down the line being spoken rather than only the next one.

Silk ships the OpenAL *bindings* separately from the library they bind, so
`Silk.NET.OpenAL.Soft.Native` is referenced as well. Without it there is nothing to load and
opening a device fails at runtime rather than at build.

No device is a warning and a quiet game, never a refusal to start.

## What is not done

- **Soundtracks are a program**, not a file: pick one of these, wait four to nine seconds,
  repeat twice. The first sound of the first track is looped, which gives a room its tone
  but not its variety.
- **Nothing is placed in the room.** Every voice is head-relative; positioning one needs the
  emitter, which is a scene concern.
- **No fades, no `StopMethod`**, both of which the `.STK` files specify.
