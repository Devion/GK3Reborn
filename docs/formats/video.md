# Video

GK3 ships 40 movies as Bink — `binkvideo` 320×240 at 30 fps with `binkaudio_rdft` stereo —
and refers to each by name and never by file. G-Engine's `VideoHelper` strips the extension
deliberately, because some locales substitute AVI for BIK, so **the name is the identity and
the container is an implementation detail**.

## Where a movie comes from

Two sources, and which of them is read is the player's decision.

| run with | packs | `enhanced/video` |
| --- | --- | --- |
| *(nothing)* | read | read, and **wins** where both have a movie |
| `--rebarn` | read | not read at all |

The same rule as every other enhanced kind: `--rebarn` means the packs and nothing else,
which is the only way to measure what the shipped form does, and without it the looser and
more recent thing wins while a set is still moving. `VideoLibrary` is the whole of it.

A movie in a pack is read as a **window onto the pack's memory mapping** rather than copied
out of it. `212PBEGIN.mp4` is 118 MB; decoding it costs less than copying it would.
`MappedStream` is the read-only seekable stream that makes that possible, and it exists
because the framework has none over `ReadOnlyMemory<byte>`.

## What plays them

The engine's own decoders, in managed code: `Formats/Video/Mp4` reads the container,
`Formats/Video/H264` the pictures, `Formats/Video/Aac` the sound, and `Content/Movie`
puts them together. Nothing native, nothing to install, nothing to version — a movie plays
on Windows, Linux and a Mac alike, with the same bytes. [ADR 0010](../adr/0010-decode-cinematics-in-managed-code.md)
says why that replaced FFmpeg; the short form is that FFmpeg was sixty megabytes of
per-platform, per-generation shared libraries with no build at all for Apple silicon.

**Correct means "matches FFmpeg".** The H.264 decoder is compared to FFmpeg sample for
sample over every converted clip — all 34 are bit-exact, 57,000 frames — and the AAC
decoder to within 5e-7 of FFmpeg's float output. Tiny x264 streams with FFmpeg's CRCs
are embedded in the tests so the comparison holds in CI without either FFmpeg or the
clips; `H264DecoderTests` and `AacDecoderTests` run the full comparison wherever both
exist.

What the H.264 decoder does: progressive 8-bit 4:2:0, 4:4:4 and monochrome; CAVLC and
CABAC; I, P and B slices with every intra mode, 8x8 transforms, scaling matrices,
weighted (explicit and implicit), spatial and temporal direct prediction, multiple
reference frames, long-term references, and the deblocking filter. What it refuses, by
name, at parse time: interlaced coding (fields and MBAFF), 4:2:2, high bit depth, slice
groups, data partitioning, SP/SI slices and lossless transform bypass — none of which the
import can produce. The AAC decoder is AAC-LC with all of its tools; SBR signalling is
accepted and the core is played at the core rate.

**Speed.** Single-threaded: 320x240 at roughly 900 frames a second, 1440x1080 at 40–50
against a 30 fps requirement. Decoding runs ahead of the clock on its own thread, a few
frames deep, so the render loop only ever picks up a finished frame; one that is not ready
when its time comes is skipped and the last stays on screen, which is invisible where a
late frame would be a stutter.

**A movie that will not open is skipped, and says so** (`GK3R1162`): the file is damaged,
or uses a coding tool the decoder refuses, and the fix is to re-import it with the
standard settings. There is no longer a "no decoder" state: the decoder is always there.

## What the scripts ask for

| function | shape | what it means |
| --- | --- | --- |
| `PlayFullScreenMovie(name)` | waitable | fill the screen |
| `PlayFullScreenMovieX(name, autoclose)` | waitable | the same, with an explicit close |
| `PlayMovie(name)` | waitable | the windowed form |

All three are **waitable**, and what they wait for is the movie's own length — so
`Gk3SheepApi.SecondsFor` answers for them as well as `Register` performing them. A script
that plays a cutscene and then speaks would otherwise speak over it. A movie that will not
play returns nothing to wait for and the script carries on, which is what the original does:
its callback runs whether or not the video played.

Thirteen call sites across the 224 compiled scripts, plus the title sequence.

## How it is drawn

`MoviePipeline`: one texture, one triangle, one draw, no vertex buffer at all. The corners
come from `gl_VertexIndex` and the fit comes from a push constant.

**Letterboxed rather than stretched**, and the bars are painted black by the same draw — the
triangle covers the window and the fragment shader returns black outside the picture. Filling
the window instead would make everybody in a cutscene short and wide, and leaving the bars
alone showed the room through them.

The picture is uploaded into one texture that is created with the movie and refreshed each
frame (`VulkanTexture.Refresh`). That is one submission and one wait per frame, which is a
device stall and the right trade only here: a movie has the screen to itself, so there is
nothing else in flight for the stall to hold up.

Sizes vary more than the originals suggest — 41×51 for the blood scans, 432×384 for the
parchments, 320×240 for most cutscenes, and 1440×1080 wherever a movie has been re-upscaled.
Nothing assumes 4:3.

## How it is timed

**The sound is handed over whole and the picture chases it.** The same arrangement as
dialogue, and for the same reason: a device given a buffer plays it at exactly the right rate
whatever the display is doing, and a picture a frame late is invisible where a sound a frame
late is a click. The longest movie is seven minutes, which is forty megabytes of PCM.

Frames are asked for **by time rather than counted out**, so a slow frame skips a picture
instead of putting the whole movie behind the sound for the rest of its length.

While a movie runs it has the screen and the keyboard: the world does not advance, no part of
the interface is drawn over it, and Escape ends it rather than leaving the room.

## Checking the corpus

```bash
GK3Reborn.Tools video-info --workspace <ws> [--packs <dir>] [--model NAME] [--deep]
```

Says which movies a run could play, which source each would come from, and decodes each to
prove it — the opening frame always and four more moments with `--deep`, because a container
will happily report a resolution for a file whose frames are missing. All 34 in the workspace
decode, 36.2 minutes in all.
