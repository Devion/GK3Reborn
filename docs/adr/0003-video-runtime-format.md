# ADR 0003: Convert cinematics to MP4 / H.264 + AAC at import time

- Status: accepted
- Date: 2026-08-18

## Context

The game ships 26 Bink (`.bik`) and 14 AVI cinematics. Measured on the reference
installation: readable Bink files are `binkvideo` 320x240 at 30 fps with
`binkaudio_rdft` 22050 Hz stereo; 13 AVIs are `msrle` and one is `cinepak`, all with
`pcm_s16le` audio.

Bink is a proprietary format whose decoder availability is outside the project's
control. Decoding it at runtime ties playback to that availability forever, and the
original brief asks for a modern format anyway.

Converted cinematics derive from copyrighted originals and are produced locally by
the importer, so they are never distributed. The constraints are therefore decoder
availability on Windows and Linux, the license of the FFmpeg build that *is*
shipped, and import time and disk cost — not redistribution of the media itself.

## Decision

Convert offline, at import, with a pinned FFmpeg: **MP4 / H.264, CRF 16,
`+faststart`, with AAC at 192 kbps resampled once to 48 kHz** to match the mixer
rate. Frame size, frame rate and duration are preserved exactly and verified against
the source after every conversion.

Sources whose frame dimensions are odd encode as **4:4:4** rather than 4:2:0. H.264
4:2:0 cannot represent odd dimensions, and several Sidney scan clips are odd sized
(41x51, 389x424, 431x350). Padding or cropping would shift the UI overlays those
clips sit under, so the pixel format changes instead of the geometry. They are tiny,
so the cost is nil.

Outputs are keyed by **uppercase base name with no extension**. Game data references
videos without one — G-Engine's `VideoHelper` strips it deliberately so localizations
can substitute AVI for BIK — so the logical identity is the bare name.

Subtitles are **not** muxed into the container. Cinematic captions live in the YAK
animation with the same base name and are played by the caption system, which is
also what drives in-game dialogue captions.

## Consequences

**Good.** Runtime playback needs only a commodity H.264 decoder. Conversion is
verified, hashed, recorded with its exact command line, and incremental. The 34
readable sources convert to 179 MB from 651 MB of source.

**Bad.** A transcode generation is lost against the originals; at CRF 16 on 320x240
material this is not visible, but it is not lossless either. Import requires an
external FFmpeg, which is one more thing to acquire and version-check.

**Known issue.** Six `.bik` files in the reference installation (`day1-1`, `day1-2`,
`day2-3`, `day2-4`, `day3-c`, `day3-d` — the six largest cinematics) carry no Bink
magic, contain no container signature in the first 4 MB, and show a flat byte
distribution over all 256 values. ffprobe rejects them and G-Engine has no special
handling for them either, so the reference implementation cannot play them. The
importer records them as `unreadable-source` with a remediation message rather than
failing the run. Verify or re-acquire the installation before concluding anything
about the format.
