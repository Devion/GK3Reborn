# The cutscenes

What state the 1999 videos are in, how the remaining ones get restored, and the
regeneration route that was tried and rejected.

## What is actually in the folder

Every cutscene ships as Bink in `GK3/Data`, all of it 320x240 at 30 fps except
`Sierra.avi` at 640x480. [ADR 0003](adr/0003-video-runtime-format.md) covers why
they are converted to H.264 at import and
[ADR 0010](adr/0010-decode-cinematics-in-managed-code.md) how they are decoded at
runtime; this document is only about the picture going in.
`ContentWorkspace/enhanced/video/` holds the working copies, and they are in
three different states:

| State | Files | Note |
|---|---|---|
| **Restored, 1440x1080** | `212PBEGIN` `212PEND` `310ABEGIN` `DAY2-1` `DAY2-2` `DAY3-2` `DAY3-8` `DAY3-B` `SIERRA` | Topaz Starlight, 4.5x (Sierra 2.25x). Credits ran out before the rest. |
| **Still 320x240** | `202AEND` `205PEND` `207AEND` `DAY3-1` `DAY3-3` `DAY3-4` `DAY3-5` `DAY3-6` `DAY3-7` `DAY3-9` `DAY3-A` `INTRO` | ~29 minutes of film. `DAY3-7` is done as a test; the other eleven are the outstanding work. |
| **Not video at all** | `*SCAN` (41x51!) `PARCH*` `TENIER*` `POUSSIN*` | To be drawn by the engine instead — line work onto a surface is cheaper and sharper than an MP4 with a green background. Do not upscale these. |

The target is **1440x1080**, which is 4:3 — the same shape as the source, and the
same shape Topaz produced. It is worth saying plainly because it reads like a
widescreen number and is not one: nothing is being widened, and no part of any
frame is invented. A 16:9 version would mean either outpainting a third of every
frame from a 320-pixel-wide source, or cropping a third of the height off
framings that already sit heads-high.

## Restoring the remaining eleven

`GK3Reborn/tools/videoremake/upscale.py`, driven through the **working ComfyUI**
at `D:\AI\ComfyUI` on port 8188 — the one that already has the
`seedvr2_videoupscaler` node and the SeedVR2 weights.

```
python upscale.py DAY3-7 --chunks 1     # one chunk, to look at before committing
python upscale.py 202AEND               # one whole cutscene
python upscale.py --all                 # the remaining eleven
```

Needs `seedvr2_ema_3b_fp16.safetensors` and `ema_vae_fp16.safetensors` in
`models/SEEDVR2`. Output lands in `ContentWorkspace/enhanced/videoremake/`.

**Measured: 1.28 frames per second**, sustained, on the RTX 5090 with the 3B
model. That is about 18 minutes for a 46-second cutscene and roughly **11 hours**
for the remaining eleven. The 7B-sharp model is also on disk and would be
substantially slower; it has not been compared.

Three decisions inside the script are worth not undoing:

**Pixels come from the Bink, not from the enhanced MP4.** The 320x240 MP4s
measure 43.3 dB against their originals — a good transcode, but not a lossless
one, and a diffusion restorer sharpens compression artefacts into "detail"
enthusiastically. Only the audio is taken from the MP4, because that is the
track the game ships.

**Work is chunked and resumable.** 150 frames at a time, and any chunk already
on disk is skipped, so an eleven-hour run survives being interrupted. Each chunk
is given a 6-frame run-up from the tail of the previous one, which is then
discarded from its output — without it the joins pulse. Measured on the finished
`DAY3-7`: the largest inter-frame differences fall at frames 56, 234, 343, 792,
981 and 1106, none of them on a 150 boundary, so the run-up is doing its job.

**Do not add `-shortest` to the final mux.** The audio is a few milliseconds
shorter than the video, and letting it truncate silently costs the last frame of
the film. This was caught on `DAY3-7`, which came out 1378 frames against the
source's 1379.

## Whether SeedVR2 is the right tool

It did badly on still textures, and that experience does not transfer: it is a
video model that works in temporal batches and leans on motion across frames to
decide which detail is real. Given a lone still it has nothing to lean on and
invents. Given 30 fps footage it is playing to its strength — on `DAY3-7` the
wallpaper rosettes and a carved beam resolve where plain lanczos is mush, and a
photograph held up to camera turns from a coloured blur into a legible scene.

If it ever does start inventing objectionably, `4x-UltraSharp`,
`4x_foolhardy_Remacri` and `RealESRGAN_x4plus` are already in `models/upscale_models`.
They are far faster, entirely deterministic and invent nothing at all, which for
1999 pre-rendered footage may simply be the more honest answer.

RIFE is **not** an option here and was never the right idea: it interpolates
frames to raise frame rate, not resolution. Every remaining file is already at
its native 30 fps, so there is nothing for it to do.

## The regeneration attempt, and why it stopped

Before the upscaling route, the cutscenes were regenerated from scratch with
**MiniMax H3** — a full storyboard for `212PBEGIN`, photoreal, 16:9, cut against
the original voice track. The picture quality arrived: a convincing Languedoc
wine estate, the right cast, the right beats. It was abandoned anyway, because
the model would not hold a scene together — background people appear and vanish
between shots and within them, and it does not track who is supposed to be in
the room.

Three mechanisms were tried and none fixed the two things that mattered:

| Route | Fixes | Does not fix |
|---|---|---|
| Free text-to-video | best cinematography | invents its own mouth movement; characters redrawn per shot |
| `ref2va` reference images | named characters hold identity across shots, at no extra render cost | extras, crowds, lip sync |
| `MiniMaxH3AddGuide` with the real dialogue | — | no demonstrable sync benefit |
| Fun ControlNet over the 1999 render | carries the old camera, blocking and lip flaps, which are already cut to the voice track | costs all the new cinematography; still populates the background at random |

No reliable way was found to *measure* lip sync. A mouth-motion-versus-speech-energy
correlation scored the 1999 original itself no better than the generated clips,
and single-frame open/closed checks are meaningless because mouths close between
phonemes constantly. Judge it by watching.

The tooling is kept at `GK3Reborn/tools/videoremake/` — `cast.json`,
`212PBEGIN.shots.json`, `h3_render.py` (t2v / ref2va / control modes),
`control_prep.py`, `assemble.py`, and `lipsync_check.py`, which does not work,
see above. `212PBEGIN.ref2va.mp4` is the best complete result.

**The storyboard JSON is worth keeping regardless of the video question.** It
holds the 13 shot boundaries from scene detection and the `212PBEGIN.YAK`
dialogue mapped onto them, and that mapping is exact: YAK caption frame divided
by 30 gives the time in seconds, with zero offset. That is reusable for
subtitles or any other per-shot work, and the same trick applies to the other
cutscenes.

### If it is ever revisited

Four traps, each of which cost real time:

- `jacokon/fasth3-live` — the FastH3 streaming repo everyone links — is gated
  behind a licence whose territory excludes the EU, UK, South Korea and the USA,
  and it is not needed. `Comfy-Org/MiniMax-H3` is ungated and carries more of
  what a remake wants. Its optimisations are all for real-time streaming, which
  an offline remake should not want.
- The `alibaba-pai` Fun ControlNet is in diffusers layout and `ModelPatchLoader`
  rejects it outright — separate `to_q/to_k/to_v` where ComfyUI wants a fused
  `qkv_proj`, `norm_q` where it wants `q_norm`. `Kijai/MiniMax-H3-experimental`
  ships the ComfyUI conversion.
- ComfyUI's `Canny` node runs kornia over the whole batch at once and **aborts
  the process** on shots longer than about 124 frames. Edges have to be made
  outside it; `control_prep.py` does this with OpenCV, encoded losslessly because
  ordinary h264 smears one-pixel edges into mush.
- Mixing the `fl2va` and `ref2va` checkpoints inside one ComfyUI lifetime stages
  both 21 GB models on top of the 27 GB text encoder and gets the process
  OOM-killed. Pin one checkpoint per run.

H3 ran in a **second, separate ComfyUI** at `D:\AI\ComfyUI-H3` (port 8189,
tracks master, its own `python_embeded`, reads weights through
`extra_model_paths.yaml`). The working install at `D:\AI\ComfyUI` was never
touched — it is pinned at 0.25.0 and has no H3 support at all. That second
install and roughly 88 GB of H3 weights are disposable if the route is not
revisited.
