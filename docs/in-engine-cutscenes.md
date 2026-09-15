# Re-shooting cutscenes in the engine

This guide is for designers who want to recreate one of the 1999 pre-rendered cutscenes (`enhanced/video/*.mp4`) inside the engine,
with the remake's lighting, ray tracing and a 16:9 frame. It walks through the manual workflow that was proven on `212PBEGIN` in
September 2026, the tools the engine gives you, and the traps found so far.

The engine does the rendering. The creative work is yours: you stage the actors, place the cameras and time everything to the
original audio.

## Stages

Keep working on the engine and the pipeline: recording, rails, moving scenery, clip handling and tooling. 
**It will stage, direct or render a recreation of a cutscene itself.** 

That covers choosing the shots, placing and animating the characters, and producing the video.

## What you need

- A Debug or Release build of the host: `src/GK3Reborn.Host/bin/<config>/net10.0/GK3Reborn.exe`.
- The game data in `GK3/Data` (the host finds it on its own in a checkout).
- FFmpeg on the `PATH`, to turn frames into an MP4 with the original sound.
- The original cutscene, for timing and reference: `ContentWorkspace/enhanced/video/<NAME>.mp4`.
- The cutscene's dialogue file, for line timings: `ContentWorkspace/normalized/dialogue/<NAME>.YAK`. Caption frames there are at 30 fps
  and line up with the video with no offset.

Nothing in this workflow goes into a `.rebarn` pack. Everything lives in a working folder of your own, outside the repository.

## The pieces

| Piece | What it is | Where it lives |
|---|---|---|
| Scene copy | A copy of the room's timeblock `.SIF`, edited to hold your cast, their marks and nothing else | `<work>/overrides/<ROOM><TB>.SIF` |
| Rail | A JSON file of camera keyframes, cuts, and optional model paths | `<work>/<NAME>.rail.json` |
| Cue list | Console commands on given frames: moving actors, switching idles, walking | the `--run` argument |
| Recording | The host run on a fixed clock, writing every frame as a PNG | `<work>/frames/` |
| Encode | FFmpeg joining the frames with the original audio | `<work>/<NAME>.mp4` |

## Step 1: break the original into shots

1. Find the cuts. FFmpeg's scene detection finds nearly all of them:
   `ffmpeg -i NAME.mp4 -vf "select='gt(scene,0.3)',showinfo" -f null - 2>&1 | findstr pts_time`
2. Write down each shot's start and end time, its location, who is in it, and the camera move.
3. From the `.YAK`, note when each line starts (frame / 30) and who speaks it. You will switch that actor to their talk behaviour then.
4. Pull one reference still per shot. You will compare against them later.

`tools/videoremake/212PBEGIN.shots.json` is a worked example of this breakdown.

## Step 2: copy and trim the scene

Every room is two scene files: the general one (`CSE.SIF`) and the timeblock one (`CSE212P.SIF`). The general one supplies the room itself,
the floor and the sky. The timeblock one decides who is there. You only copy and replace the **timeblock** file.

1. Copy `ContentWorkspace/normalized/scenes/<ROOM>/<ROOM><TB>.SIF` to `<work>/overrides/`.
2. `[ACTORS]`: list everyone the cutscene needs, whether or not the original timeblock had them. Take each actor's model code and
   behaviour scripts from another scene where they appear (search the `scenes` folder for `noun=WILKES` and so on).
3. Give every actor a `pos=` naming a mark, and define the marks under `[POSITIONS]`:
   `A_BUTHANE, pos={-20.0, 3.0, 110.0}, heading=175.0`.
   Headings are degrees, zero along +Z. Heights snap to the floor, so a rough Y is fine.
4. Define a mark for every place anybody stands in any shot. Actors are moved between marks by cues, not by editing the file.
5. For actors who are not in the opening shots, add "offstage" marks somewhere the camera never looks.
6. `[MODELS]`: keep the scenery the cutscene needs and delete what gets in the way. `212PBEGIN` dropped `cse_vandoor` so the van stands open.
7. Empty `[ACTIONS]`, `[TRIGGERS]` and the camera sections. The room's own scripts would otherwise start, move people and cut the camera
   behind your back.
8. Pick idle scripts whose clips are **relative** (see the traps below). The plain `xxxIdle.gas` and `xxxTalk.gas` for each character
   generally are.

The engine loads your copy in place of the real one when you pass `--overrides <work>/overrides`. Nobody else's game is affected.

## Step 3: find your coordinates

You need room coordinates for marks and cameras. The quickest way is an overhead photograph:

```
GK3Reborn.exe --scene CSE --timeblock 212P --no-movies --skip-intro --settings <work>/settings.json ^
  --width 1280 --height 720 --eye 0,1400,-150 --aim 0,-89 --frames 60 --screenshot <work>/overview.png
```

Looking straight down with `--aim 0,-89`, screen right is +X and screen up is +Z. The centre of the picture is the `--eye` X and Z. At
1280×720 with the default 60° field of view, one pixel is `2 × height × tan(30°) / 720` units, which is 2.244 at a height of 1400.

To check where an actor actually ended up, add a cue: `--run "@40 DumpActor(\"BUTHANE\")"`. The answer is printed to the console log.

Always pass `--settings` with a scratch file. Otherwise a headless run can rewrite the player's own settings.

## Step 4: write the rail

The rail is the camera, keyed over time. Times are seconds from the first **recorded** frame, so they are the original video's own
timestamps.

```json
{
  "fps": 30,
  "warmup": 90,
  "keys": [
    { "t": 30.408, "pos": [-40, 35, -230], "angle": [6, -3],  "fov": 52, "cut": true },
    { "t": 35.129, "pos": [5, 35, -230],   "angle": [2, -3],  "fov": 52, "ease": true },
    { "t": 35.129, "pos": [-17, 64, 55],   "angle": [-3, 1],  "fov": 30, "cut": true },
    { "t": 38.947, "pos": [-17, 64, 62],   "angle": [-3, 1],  "fov": 30, "ease": true }
  ]
}
```

- `pos` is where the camera stands. `angle` is heading and pitch in degrees, in the scene files' own convention: **positive pitch looks
  down**. You can copy a camera straight out of a `.SIF`'s `[CINEMATIC_CAMERAS]` as a starting point.
- `fov` is the vertical field of view in degrees. 30–35 is a close-up, 40–45 medium, 50+ wide.
- Between two keys the camera travels in a straight line and turns the short way round. `ease` smooths the move that ends at that key.
- `cut: true` means jump. The previous key is held until this key's time, then the camera is here. Start every shot with two keys at
  the same `t`: the last key of the previous shot, then this shot's first key marked `cut`.
- `warmup` is frames drawn and thrown away before recording, so the ray-traced denoisers have settled. 90 (3 s) is enough.
- `fps` should match the original, 30 for GK3's cutscenes.

### Model paths

A rail can also move placed models, such as a vehicle arriving:

```json
"models": [
  { "name": "cse_van", "pivot": [141, 0, -28],
    "keys": [ { "t": -2.5, "pos": [-88, 4, -620], "heading": 180 }, { "t": 4.5, "pos": [141, 0, -28], "heading": 0 } ] }
]
```

`pivot` is a point on the model where the room placed it. Each key says where that point should be and how many degrees the model has
turned from its placed heading. Keys are interpolated linearly, so generate them densely (every 1/15 s) along a smooth curve. See
`van_path.py` in the 212PBEGIN working folder for a generator.

An optional `"until": 6.6` on a model path releases it after that video time. Use this for passengers who must resume normal walking
after a vehicle parks. End their path on the identity transform before releasing it.

**Known limit:** only loaded props or actors can be moved. Changing a BSP-only scene object to `type=prop` does not create a movable
model when no matching `.MOD` exists. For the CSE van, hide `cse_van` and `cse_vandoor` as scene objects and place the original `VAN.MOD`
as a prop instead. `Recording: no model <name> in the room to move` means the requested movable model was not loaded.

## Step 5: write the cue list

`--run` takes semicolon-separated console commands. `@N` runs a command on frame N. **Frame numbers count the warm-up**, so a cue at
video time `t` is frame `warmup + round(t × 30)`.

| Command | Use |
|---|---|
| `SetActorPosition("GRACE","A_GRACE")` | Stand somebody on a mark, on the cut, so nobody sees them move. |
| `SetIdleGAS("BUTHANE","madTalk.gas")` | Switch to talk gestures when a line starts, and back to `madIdle.gas` when it ends. |
| `WalkTo("MOSELY","FR_CS3")` | Walk to a mark from the room's own or your scene file. |
| `StartAnimation("GraCseWaveToDef")` | Play a specific clip. |
| `DumpActor("GRACE")` | Print where somebody is, for checking. |

A shell script that builds the string from small helper functions is far easier to keep straight than one long line. The 212PBEGIN
working folder has `record2.sh` as a model.

## Step 6: record

```
GK3Reborn.exe --scene CSE --timeblock 212P --no-movies --skip-intro --settings <work>/settings.json ^
  --overrides <work>/overrides --width 1280 --height 720 --rt high ^
  --record <work>/frames --rail <work>/NAME.rail.json --frames <warmup + total frames> --run "<cues>"
```

- `--record DIR` runs the room on a fixed 1/fps clock, hides the interface and writes `frame_00000.png` onwards.
- `--frames` is warm-up plus the video's length in frames. A 64.7 s cutscene at 30 fps with 90 warm-up frames is 2032.
- `--width/--height` set the native output size, including 3840×2160. Resolution does not change the fixed recording clock.
- `--record-fps` and `--record-warmup` override the rail's values.

On an RTX 5090, 1280×720 at `--rt high` records at about 10 frames a second, so a minute of cutscene takes 3–4 minutes.

For quick staging passes, record at `--rt off` and 640×360. It is many times faster.

### Saved cuts and later 4K rendering

The working ledger is `ContentWorkspace/work/README.md`. Completed staging folders contain `render.json`, the rail, `cues.json`,
overrides, reference stills, and generation scripts. From the workspace root, use the shared renderer to reproduce saved staging:

```powershell
python ContentWorkspace/work/render_cutscene.py 212PBEGIN --width 3840 --height 2160
python ContentWorkspace/work/render_cutscene.py 212PEND --width 3840 --height 2160
python ContentWorkspace/work/render_cutscene.py 310ABEGIN --width 3840 --height 2160
```

Each command writes native 4K frames and a movie under that cut's `renders/3840x2160-high/`, preserving the approved 720p file.
`--dry-run` saves exact commands and input hashes without recording. Normal runs verify the numbered PNG sequence and dimensions,
encode with the original soundtrack, decode the result, and save media validation. Keep the source assets and content packs with
the staging archive; they are external dependencies. Do not rerun a staging generator for a resolution-only render of an approved cut.

A three-frame 3840×2160 RT-high check of `212PEND` passed on this workflow. Complete 4K movies still require rendering and visual review.

## Step 7: encode with the original sound

```
ffmpeg -framerate 30 -i <work>/frames/frame_%05d.png -i ContentWorkspace/enhanced/video/NAME.mp4 ^
  -map 0:v -map 1:a -c:v libx264 -crf 16 -pix_fmt yuv420p -c:a aac -b:a 192k -shortest <work>/NAME.mp4
```

To check a single shot against the original, cut the same range from both (`-ss <start> -t <length>`) and stack them. A side-by-side
still per shot shows more than watching the whole thing.

Remakes go to `ContentWorkspace/enhanced/videoremake/`. Close any player that has the file open before re-encoding. On Windows a locked
file makes renames fail, but FFmpeg can still overwrite it.

## Traps found so far

- **The player character ignores `pos=`.** The actor marked `ego` spawns at the room's entry point. Move them with a cue on frame 0.
- **An actor can stand on their mark while their body is elsewhere.** A clip with an absolute placement draws the model where the clip
  was authored, even though `DumpActor` still reports the mark. Check a script's clips before using it. An `[ACTIONS]` line in a
  clip's `.ANM` with only a name is relative; one with eight numbers after the name is absolute.
- **`--camera NAME` on the host is overridden** by the room's entry camera. Judge camera angles from recorded frames.
- **Scripts in the copied scene run.** Anything left in `[ACTIONS]` can move people and cut the camera. Empty it.
- **A walk ignores a later `SetActorPosition`.** Someone still walking keeps walking. Only send walks to actors who will not be moved again.
- **Actors inside a vehicle** can stand at ground level with their feet hidden below its floor, if the shots frame them from the chest up.
  From outside, the feet show under the body.
- **Absolute clips from another room cannot be reused yet.** The Poussin tomb's `VanPouIN`, for example, has the whole group seated in a
  van, but authored at the tomb's coordinates. Playing it elsewhere needs an offset in `SceneUpdate.Playing.Correction`, which is not built.
- **After a clip that is not the actor's idle, the idle does not restart by itself.** Set it again with `SetIdleGAS`.
- **Caption-only movie YAKs do not animate mouths.** Author `LIPSYNCH` nodes in face-only `.ANM` files and trigger each with
  `StartAnimation` on its dialogue frame. The 212PBEGIN working folder's `mouths.py` generates approximate audio-driven cues from the
  soundtrack and line windows; it does not perform phoneme alignment. Include blink cues in longer face animations, since those
  animations temporarily own the face's expression channel. Clothing variants WI2 and LH2 use the WIL and LHO face artwork.
- **Generated animation frame rates must be explicit.** Ordinary ANMs default to 15 fps. If you author mouth or body frames at
  the movie's 30 fps, add `[OPTIONS]`, a count of `1`, and `0,FRAMERATE,30`; otherwise the animation lasts twice as long.
- **An ACT's target is in its header.** Renaming a clip file for a cloned prop does not retarget the animation. Update its 32-byte
  model-name field at byte 20 as well. The 310ABEGIN awake sleeper uses `glw` there so the body and necklace move together.
- **Inspect an animation's action, not just its label.** `GLB_SLEEP` is the R25 bed setup; `GabSleepSofa` is a separate couch setup.
  Grace's `GraR25sfig2` contains typing, while `GraR25sfig1` is a hand-to-cheek gesture. A random seated idle is not continuous typing.
- **Use native walkers for walking actors.** `WalkTo` couples the stride's travel to its playback speed and follows the scene floor.
  `ContentWorkspace/work/212PEND/native_walk.py` stages the opening with native walks and camera tracking. Driving actor positions
  with a separate model rail caused sliding and led to unreliable external foot corrections.
- **Foot contact uses drawn triangles.** Character meshes contain unused rig-marker vertices far below the visible shoe. Bounds
  over every vertex falsely report penetration. The native walker's `StrideFooting` correction measures only triangle-referenced
  shoe vertices and applies one height offset for the cycle, preserving its natural motion.
- **Dependent timed commands need separate frames.** The host's command sort does not preserve order within a frame. Schedule
  `StopWalking`, `SetActorPosition`, and `WalkTo` on successive frames, or a late stop can cancel the walk just started.

## Checklist per cutscene

1. Shots, cut times and line times written down; one reference still per shot.
2. Scene copy with the full cast, all marks, offstage marks, and empty `[ACTIONS]`.
3. Overhead photograph and marks checked with `DumpActor`.
4. Rail with a `cut` pair at every shot boundary.
5. Cue list: frame-0 moves, per-shot moves on the cut, talk switches per line, walks.
6. A low-quality pass (`--rt off`, 640×360), a per-shot comparison sheet, fixes.
7. Final `--rt high` recording, encode, watch it through.


### 310ABEGIN: clothes, covers, and doorway follow-up

The saved 310ABEGIN staging keeps Gabriel's bare torso through the bedside reply and restores his white shirt at the 30-second sound cue while Grace is on camera. Its local torso poses narrow/collapse the shirt cuff geometry; the face rig and mouth tracks remain active. The actual shirt-pulling action is still an off-camera adaptation.

Do not flatten the bed covers below the fitted sheet: the R25 scene's visible bed surface reaches Y=36.5. The animated `top_sheet` materials now pull toward the foot of the bed, then gather clear of the sitting edge. Seated pelvis height matches the visible surface, with the adjustment tapering to zero at the knees and disappearing during the rise. The original lower legs and shoes retain their floor contact. Checks use only triangle-referenced vertices.

Hiding `r25door2hal_scene` alone leaves the separately instantiated `r25_doorknob` floating. Hide both and use the complete native `r25door2hal` prop, whose three meshes include the door and hardware. The staging extracts door-only motion from `MOSR25WPOP`, holding its open pose to accommodate the independently staged walker. Both listeners turn toward Mosely after his entrance. The final doorway camera is at (238,60,210), inside the room; a diagonal retreat from the earlier position hits the room divider. Current 310ABEGIN generators, overrides, cues and rail supersede its earlier archived versions for 3840�2160 recreation.


The 310ABEGIN sleeper is an upper-body prop: blanket clearance must also preserve coverage of its missing lower body. The revised pullback takes 11.1 seconds. It moves only 22% during the entire wake-up close-up and completes during the shirt insert before switching to the full seated actor. Do not accelerate the covers away from the waist just to clear the rising torso.


### 310ABEGIN: continuous waist and two-handed reading

Bed-height weights must stay attached to the same vertices as the actor stands. Recomputing the pelvis blend from world Z on every frame separated Gabriel's jeans from the fully lifted torso. The corrected weights are fixed from the seated pose; every waistband vertex receives the full upper-body lift, and the sleeve deformation excludes waist vertices. `check_opening.py` verifies this throughout both seated and rising clips while retaining the shoe-contact check. The slow blanket pullback now ends as a visible fold on the mattress at Z=74-98, in front of the footboard.

The one-handed passport pose is superseded by `reading_pose.py`: upper-body motion from `MOSDINPAPERLOWERB`, intact standing legs, and a yaw toward Gabriel and Grace. Use a rigid translation/yaw to place these source meshes; aligning the animated chest matrices across these clips produced an incorrect lean. The book is anchored at actual page-edge vertices to both palms and stays nearly horizontal. `check_reading.py` measures both palm-to-page distances, page-plane angle, and head-facing direction throughout the loop. Side cameras show the group while Mosely reads and addresses them. Current generators and saved overrides supersede the earlier passport-based staging for the 4K recreation.


For the latest 310ABEGIN edit, the clothing swap occurs at 33.2 s, inside the completely black hold, not at the audible cue while the image is still visible. Mosely's local PNG albedo overrides use a 0.55 linear-light gain, including face overlays to preserve matching lips and blinks; `lighting_grade.py` retains the reproducible operation and original enhanced sources. His final walk continues through the doorway to (228,0,315), then left to (188,0,315); the door holds until about 118.0 s, after he leaves view. The earlier EXIT mark at Z=281 was inside the room and must not be used as the final exit destination.
