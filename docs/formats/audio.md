# Audio (`.WAV` and everything pretending to be one)

7,852 sounds. **7,656 of them — 97.5% — are an MP3 stream inside a RIFF header.**

That is the fact everything else follows from. Format tag 85 with a
`MPEGLAYER3WAVEFORMAT` `fmt` chunk: 44.1 kHz, 64 kbps, mono for dialogue and stereo for a
few soundtracks. The 196 that are honestly PCM are 22 kHz footsteps and one looping fly.

So "read the WAV files" gets you a silent game. Every line of dialogue and almost every
soundtrack is compressed.

## Where that gets decoded

In process, on first use, by NLayer.

`Plan/01` rules out an external process at runtime, which is a different thing from ruling
out decoding — and the difference turned out to be worth 3.7 GB. The corpus used to be
decoded once offline into `normalized/audio-pcm`, which cost that much disk to save about
eight milliseconds a sound; the compressed originals are 347 MB and already inside the
archives. `SoundLibrary` therefore reads the archives and nothing else, and `import-audio`
is gone.

### Two ways the decoder lies to you

Both return the right sample count and raise nothing, which is what makes them expensive.

- **`ReadSamples(byte[], …)` writes floats.** Read back as 16-bit that is exactly twice as
  many samples as the sound has, each the bit pattern of half a float. Use
  `ReadSamplesInt16`.
- **`ReadSamplesInt16` only fills correctly at index 0, a block at a time.** Ask for a whole
  clip in one call and it returns the full count with the back of it silent — one 99-frame
  clip decoded its first 71 frames sample-for-sample and left the last 28 as silence. Ask it
  to write at a non-zero index and it returns the right count of the wrong samples. So every
  read goes into a 16 KB block at index 0 and is copied out.

Verified against ffmpeg over all 7,852 sounds: **7,155 are sample-for-sample identical**,
619 correlate above 0.99, 75 below, and 3 differ in length. None are refused.

The 75 are all under four seconds and differ only in their first frame or two — a decoder
starting cold has no filter history, which is a large share of a 0.2 s clip and none of a
long one. The 3 are `THEME.WAV`, `TE5MIX.WAV` and `TEMPLEPORCHMIX.WAV`, each 250–308 s and
each exactly 2,304 samples — one MP3 frame, 26 ms — short, where NLayer drops a frame
ffmpeg conceals. Everything before the drop is sample-exact.

There is no unit test for any of this: pinning it needs real MP3 data, and the game's audio
does not belong in the repository. The check above is the guard, and it is a decode of the
whole corpus in 67 seconds.

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

### In the room, not at the head

A `.STK` either gives its sound a place or does not, and that is the whole of whether a
fountain sounds like it is across the square. RC1's is at `{3113, 114, -2337}` and carries
1,200 units; CSE's is 85 to 1,000. Room tone has no place because it comes from everywhere.

The rolloff is **inverse, clamped at both ends**, which is what FMOD gives the original and
what the two distances in a `.STK` mean: full volume within the minimum, the reciprocal of
distance after it, and level again past the maximum. Where a sound is placed but says no
distances, the game's own defaults are 200 and 2,000 units.

| distance from RC1's fountain | gain |
|---|---|
| within 100 | 0 dB |
| 200 | −6 dB |
| 400 | −12 dB |
| 800 | −18 dB |
| 1,200 and beyond | −21.6 dB |

The default OpenAL model is inverse *unclamped*, which keeps getting quieter for ever and
never levels off; it has to be asked for.

**Distance also takes the top off a sound**, and that is most of what tells a listener
something is far away rather than merely quiet — a fountain across a square is a hiss, and
the same fountain turned down is still a fountain at your feet. A low-pass through EFX,
straight-line from no filtering at the sound's minimum distance to a quarter of the high
frequencies at its maximum. It is a stand-in for air absorption rather than a model of it,
and it knows nothing about what is in the way. A device with no EFX skips it and still
places its sounds.

**Only mono sounds can be placed.** OpenAL plays a stereo buffer flat at the head whatever
position it is given. Every ambience in the game is mono, so this has not bitten, but a
stereo one would silently ignore its own soundtrack.

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
- **A sound that follows something stays where it started.** `Follow=blk_sedan` means the
  emitter moves with a model, and `Move` exists for it, but nothing asks the room where that
  model has got to.
- **One soundtrack a room.** A scene may list several — RC1 at ten in the morning names a
  fountain, a room tone and birdsong — and only the first sound of the first one is played.
- **No fades, no `StopMethod`**, both of which the `.STK` files specify.
