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

`FFMediaToolkit`, over FFmpeg. Decoding H.264 is not something to write, and the two
alternatives were worse: Media Foundation is Windows only and the platform scope is Windows
*and* Linux, and re-encoding to something the engine could already read costs four to five
times the disk to avoid a dependency the content pipeline already has.

**It is a versioned dependency.** The binding is written against **FFmpeg 7.1** and looks for
that generation's shared libraries by name — `avcodec-61`, `avformat-61`, `avutil-59`,
`swscale-8`, `swresample-5`. A newer FFmpeg is not a substitute, because its libraries are
called something else; that is how FFmpeg versions its ABI rather than a choice made here.

Looked for in `libs/<rid>` first — where `Plan/01` puts native libraries and where
`NativeLibraryLocator` resolves everything else from — walking up from the executable, so a
development tree finds the one at the root of the checkout and an installation finds the one
beside it. Failing that, whatever the system has, which is how a Linux box with the
distribution's FFmpeg works with nothing copied anywhere.

```bash
build/fetch-native.sh win-x64      # libs/win-x64/av*.dll, sw*.dll
build/fetch-native.sh linux-x64    # libs/linux-x64/libav*.so.NN, libsw*.so.N
build/fetch-native.sh osx-arm64    # MoltenVK only; see below
```

That is the same script CI runs, so a development tree and a published archive are
populated from the same pinned build. It downloads an **LGPL shared build**, verifies the
SHA-256 of the archive against a hash recorded in the script rather than one fetched
alongside it, and copies out only the libraries the binding needs. Running it again when
they are already there does nothing. A Linux machine with the distribution's own FFmpeg
7.1 needs none of it.

The pin is an archived BtbN autobuild rather than their rolling `latest`, which has moved
on to 8.1 and 9.0 — different library names, so not substitutes. **There is no FFmpeg for
Apple silicon**: nobody publishes a 7.1 shared build for it, so a Mac plays the game
without its cutscenes unless the machine has its own. `fetch-native.sh osx-arm64` fetches
MoltenVK, which is a different problem — without it a Mac has no Vulkan at all.

**Not having it is not an error.** A machine with no FFmpeg plays the whole game without its
cutscenes, and says so once (`GK3R1160`). Refusing to start over a missing cutscene would be
far worse than missing the cutscene.

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
