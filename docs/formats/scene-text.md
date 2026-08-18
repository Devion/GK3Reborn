# Scene text: initialisation files and scene assets

Two text formats say what a scene *is*. Neither is binary, and both were easy to
overlook next to the BSP and MUL files — which was a mistake, because between them
they carry the cameras, the props, the visibility rules and the entire original
lighting rig.

## The dialect

Both are INI with three additions, shared with several other GK3 text assets and
parsed once in `IniDocument`.

**A line may hold several key/value pairs**, comma-separated, and commas inside
braces belong to the value:

```
START, pos={235.26, 2.72, 57.99}, heading=-16.04, camera=START
```

That is four pairs — `START`, `pos`, `heading`, `camera` — not six.

**A bare token is a flag** whose value repeats its key, which is how `hidden`,
`ego` and `Default` are written.

**A section header may carry a condition**:

```
[MODELS={IsCurrentTime("202p") && GetEgoCurrentLocationCount() < 1}]
```

The same section then appears many times with contents that depend on the story's
state. Conditions are Sheep expressions and are kept verbatim; evaluating them
belongs to the virtual machine, not to a text parser.

Comments are `//` to end of line and `/* */` across lines.

### Whether commas separate pairs is per file type

Scene initialisation files write vectors braced (`pos={1, 2, 3}`) and need the
comma splitting. Scene assets write them bare (`Position=1,2,3`) and must not be
split. This cannot be inferred from a line — `Position=1,2,3` splits perfectly
happily into three plausible-looking pairs — so it is a parameter of the parse.

Getting it wrong is silent: every vector in the file reduces to its first
component, and a scene reports zero lights rather than an error. That is exactly
what happened here before the two dialects were separated.

## Scene initialisation files (`.SIF`)

One per scene, named for it: `R25.SIF`. Sections used so far:

| section | contents |
| --- | --- |
| `GENERAL` | `scene=` names the scene asset for this state; `floor=`, `boundary=`, `cameraBounds=`, `globalLight,pos={…}` |
| `ACTORS` | `model=`, `noun=`, `idle=`, `talk=`, `ego` |
| `MODELS` | `model=`, `noun=`, `type=`, `hidden` |
| `ROOM_CAMERAS` | name, `angle={yaw, pitch}` in degrees, `pos={…}`, `Default` |
| `CINEMATIC_CAMERAS`, `INSPECT_CAMERAS`, `DIALOGUE_CAMERAS` | same shape, different use |
| `POSITIONS` | named spots with `pos`, `heading`, and the `camera` to cut to |
| `REGIONS`, `TRIGGERS` | rectangles the game reacts to |

### Model types

`type=` decides what a model line means, and the four values behave differently:

| type | meaning |
| --- | --- |
| `prop`, `gasprop` | a `.MOD` file to load and place |
| `scene` | an object already baked into the BSP; the line only names and configures it |
| `hittest` | likewise baked in, but **never drawn** — a clickable volume |
| `noclick` | drawn, but not interactive |

Loading a model file for a `scene` line draws the same furniture twice, slightly
apart, which reads as z-fighting. Drawing a `hittest` puts a large flat slab
through the middle of the room. Both were live bugs before this table was written
down.

### Camera angles

`angle={yaw, pitch}` in degrees. The original composes the rotation as yaw about
Y then pitch about X and applies it to +Z (`GameCamera::SetAngle`,
`Transform::GetForward`), which gives

```
forward = (cos(pitch)·sin(yaw), −sin(pitch), cos(pitch)·cos(yaw))
```

Reversing the order produces a view that looks plausible from shallow cameras and
badly wrong from steep ones — an easy mistake to ship.

## Scene assets (`.SCN`)

One per scene *and timeblock*: `R25_M.SCN`, `R25_A.SCN`, `R25_E.SCN`,
`R25_N.SCN`. 229 in the corpus.

```
BSP=r25
Version=0x202
ExportDate=Wed Sep 22 14:54:01 1999

[Skybox]
Left=RLC_N_512LF
Front=RLC_N_512FT
Azimuth=-210.000000

[Models]
r25background=1
r25couch=1
…

[Lights]
standing_lamp_omni
moon(key)
…

[Light_standing_lamp_omni]
Type=0
Position=265.175781,80.611328,617.383789
…
```

`[Models]` lists the objects inside the BSP, not files. `[Lights]` lists the rig
by name and each light then gets its own section.

Not every file follows this layout: a few, dated 1998 rather than 1999, have no
`BSP=` key and list models under `[MODELS]` after a bare count. Falling back to
the BSP of the same name handles those.

### Lights

| key | meaning |
| --- | --- |
| `Type` | 0 point, 1 spot |
| `Position`, `Direction` | scene space; direction is already unit length |
| `Color` | each channel 0–1 |
| `HotSpot`, `Falloff` | cone half-angles in radians, the fully lit core and the outer edge |
| `AttenStart`, `AttenEnd`, `UseAtten` | falloff range, and whether it applies |
| `CastShadows` | whether this light cast shadows in the bake |
| `Intensity` | multiplier on the colour |
| `Radius` | emitter size, used by the bake for soft shadows |
| `Overshoot`, `DecayType` | not yet understood; parsed and kept |

Across the corpus: **4,109 lights in 222 of the 229 scene assets** — 2,595 point
and 1,514 spot, 2,618 of them marked as casting shadows. Median 6 per lit scene;
`TE2B` has 148.

The names are the artists' own and carry intent that no analysis of a lightmap
would recover: `standing_lamp_omni`, `window_hot_spot03`, `moon(key)`,
`sky_bounce_`, `fill_light01`, `shadow_maker`, `only_for_camera_shot_bath01`.

This is why [ADR 0007](../adr/0007-authored-light-rigs-from-scene-assets.md)
supersedes the plan's assumption that light positions would have to be inferred
from the bakes.

### Skyboxes

Most scenes name only the faces the fixed cameras can actually see and comment
the rest out, so missing faces are normal rather than an error.
