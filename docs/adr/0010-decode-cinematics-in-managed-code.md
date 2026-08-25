# ADR 0010: Decode the cinematics in managed code

- Status: accepted
- Date: 2026-08-25
- Amends: [ADR 0003](0003-video-runtime-format.md), whose runtime-decoder assumption this replaces

## Context

ADR 0003 chose MP4 / H.264 + AAC as the runtime form of the cinematics and left playback
to FFmpeg through `FFMediaToolkit`. That worked, and it was the one dependency that did not
travel: FFmpeg is a set of shared libraries whose names change with every generation
(`avcodec-61` is 7.1 and nothing else), the LGPL build had to be fetched and hash-checked
per platform by `build/fetch-native.sh`, nobody publishes such a build for Apple silicon at
all, and a Linux box worked only if its distribution's FFmpeg happened to be the pinned
generation. Sixty megabytes of `libs/<rid>` existed for forty short movies, and the macOS
port shipped without them.

The clips themselves are narrow. Measured over all 34: x264 High profile (and High 4:4:4
Predictive for the seven odd-sized scans), CABAC, B-frames with a pyramid, 8x8 transforms,
weighted prediction, spatial and temporal direct, 8-bit, progressive, one slice per
picture; AAC-LC at 48 kHz stereo. The largest are 1440x1080 at 30 fps.

## Decision

Decode both in the engine, in C#, with no native code: `Formats/Video/Mp4` reads the
container, `Formats/Video/H264` decodes the pictures, `Formats/Video/Aac` decodes the
sound, and `Content/MoviePlayback` plays them on a decode-ahead thread with the sound as
the clock, exactly as before.

The H.264 decoder implements what the clips use and refuses, by name, what they do not:
interlaced coding, 4:2:2, high bit depth, slice groups, data partitioning, SP/SI slices,
lossless transform bypass. CAVLC is implemented alongside CABAC because it is small and
makes the decoder useful for streams the import did not produce. The AAC decoder is LC
only, with every LC tool (TNS, M/S, intensity, PNS, both window shapes); SBR and PS
signalling is accepted and the core is decoded at the core rate.

**Correctness is defined as "matches FFmpeg".** The tests compare the decoders' output to
FFmpeg's sample for sample: every one of the 34 clips decodes bit-exactly (57,000 frames),
and the AAC output is within 5e-7 of FFmpeg's float output (136 dB SNR). Tiny x264
streams with FFmpeg-derived CRCs are embedded in the tests so that this holds in CI without
FFmpeg or the clips; the full comparison runs wherever both exist.

The constant tables of the standards — CABAC initialisation, CAVLC codes, AAC Huffman
codebooks — are transcribed from JCodec (FreeBSD) and JAADec (public domain) rather than
typed from the specifications, and credited in the files that hold them. No code from
either is used.

## Consequences

**Good.** No native video dependency anywhere: `build/fetch-native.sh` fetches only
MoltenVK, `libs/<rid>` loses seven libraries, THIRD-PARTY.md loses an LGPL entry, and the
macOS build plays its cutscenes. Nothing can be missing at run time, so the "no decoder"
diagnostic (`GK3R1160`) and the whole `MoviePlayback.Prepare` search are gone. FFmpeg
remains an import-time tool only, as ADR 0003 already had it.

**Bad.** Speed. Single-threaded managed code decodes 320x240 at ~900 fps and 1440x1080 at
40–50 fps against a 30 fps requirement, which is enough on the machines the game targets
but leaves less headroom than FFmpeg's SIMD did; the decode-ahead thread absorbs hiccups
and a late frame is skipped rather than shown late. About 10,000 lines of decoder are now
ours to maintain, and a clip re-encoded with a tool the decoder refuses will not play until
the import is re-run with the standard settings — the diagnostic (`GK3R1162`) says so.

**Known limits.** No interlaced, 4:2:2, or 10-bit H.264; no HE-AAC (SBR is dropped, the
core plays at half bandwidth); no fragmented MP4. None of these can come out of the import.
