# PC virtual reality

VR renders the room separately for each eye using the headset's predicted poses,
eye separation and asymmetric projection. Both camera modes are immersive 3D.
Menus and prerecorded films appear on a floating panel anchored in tracking space.
Looking around does not drag the panel along with your head.

## Launch

This initial implementation targets Windows x64 and Direct3D 12 through OpenXR.
Use Quest 2 connected to a PC through Quest Link/Air Link, or a PC headset with
SteamVR. Select the corresponding active OpenXR runtime before launching:

```powershell
GK3Reborn.exe --vr --skip-intro
GK3Reborn.exe --vr --vr-camera original --skip-intro
```

First-person is the default. `--vr-camera original` anchors the playspace at the
authored camera position while retaining head tracking and stereo depth. Both modes
keep the playspace stationary during scripted walks, pans, glides and forced camera
overrides, including conversations. Room changes place it once in the new room;
scripts never continuously translate or rotate the headset. `--vr-scale` sets scene units per metre;
the default is approximately 36.36, based on a 60-unit eye height at 1.65 metres.

The runtime must expose the OpenXR D3D12 graphics binding. The renderer selects
the GPU requested by that runtime. Standalone Quest/Android, PSVR, macOS and Linux
VR are not implemented. OpenXR provides a common interface, but this does not
establish compatibility with every headset or controller.

## Controls

| Input | Action |
|---|---|
| Physical head movement | Roomscale movement and looking around |
| Left trigger, held | Preview a ballistic teleport arc |
| Left trigger, released | Teleport if the landing is valid |
| Right stick left/right | Smooth turning; optional 30-degree snap turn in VR settings |
| Left stick | Walk when Smooth or Both locomotion is enabled |
| Right controller aim and trigger | Open an object's/person's actions, then point and select; hold to drag UI controls |
| Look at left wrist; right controller aim and trigger | Read location/timeblock; select the notebook or inventory shortcut |
| Left Menu button | Pause/resume; back out of screens |
| Right B | Close the action popup or go back |
| Right A | Open/close inventory |
| Left X | Open/close journal |
| Left Y | Bring the panel in front of you again |
| Right stick up/down over UI | Scroll |

The table uses Quest Touch labels. Index uses right A for inventory and left A for
journal; Vive/Microsoft controllers use the corresponding stick/trackpad clicks.
The Meta/Oculus **system** button belongs to the runtime. The application's pause
binding uses the left controller's **Menu** button.

Panels offer a controller-operated **Keyboard** button for letters, numbers,
space, delete and Enter, including Sidney inputs and save names. Pointing away
from a panel never clicks the last hovered button. Menu buttons and the right trigger
must be released after focus loss or a transition before UI input is rearmed.
Teleportation separately requires releasing the left trigger. B hides an open
keyboard before backing out of the screen.

The location/timeblock and journal/inventory shortcuts live above the left wrist
instead of in the floating HUD's top bar. Raise the left controller and look down
at it, then point and click with the right trigger. The shortcuts use the original
notebook and inventory sprites. Journal and inventory open on the larger floating
panel. The wrist display hides when left-grip tracking is lost or a full screen is
open; the controller button shortcuts remain available.

## VR settings and comfort

**Settings > VR** appears only while VR is enabled. Settings persist with the
other game preferences. Smooth turning is the default, with adjustable speed;
snap turning remains available. Locomotion can be Teleport, Smooth, Both or
Roomscale only. Smooth first-person movement uses the existing walk boundaries
and ground collision. Both camera modes use these locomotion settings.

**Seated height offset** adds -0.5 to +1.5 metres to head and hand placement,
without moving the floor or changing scale. Raise it when playing seated; use
zero when standing at the expected eye height.

**Show character hands** displays the current protagonist's original hand and forearm meshes
at the grip poses. Grip pressure curls the fingers; trigger pressure and trigger
touch affect the index region; thumb contact affects the thumb region on Touch.
The original meshes have no finger skeleton: this is procedural deformation of
low-poly hands, not individually tracked fingers. Controllers without capacitive
touch still respond to grip and trigger pressure. Hands hide on tracking loss.
A right-hand aiming beam and pointer show where interaction is directed.

Scene transitions fade the live stereo room fully to black before releasing it,
keep the loading period black, then fade the new placement in over at least
0.45 seconds. They do not show a flattened desktop screenshot in the headset.

A cyan arc/ring marks a valid landing; red means obstructed or invalid. Landings
check the walkable floor and the player's footprint. Crossing scene geometry briefly
dims the room without making it completely black. The collision segment advances
every frame, so an earlier intersection cannot latch the dimming. The player's own
body is excluded from this check. Leaning outside the walking boundary or a missing
floor sample does not black out the view; walking and teleportation still validate landings.
Tracking/focus loss cancels teleportation and requires a fresh trigger press.
Use the runtime's recenter control to recalibrate the tracking origin.

Bindings are provided for Oculus Touch, Valve Index, HTC Vive and Microsoft motion
controllers. Hardware testing of these bindings is still required.

## Building

Fetch the pinned official Khronos loader once before building or publishing a
Windows VR package:

```powershell
./build/fetch-openxr.ps1
dotnet build GK3Reborn.slnx
```

The script verifies the release archive's SHA-256 and places `openxr_loader.dll`
under `libs/win-x64`. The existing host publish rules include that directory.
The loader does not install a headset runtime.

VR currently uses raster rendering, disables temporal upscaling/frame generation,
and renders the two eyes sequentially with a GPU wait before releasing each image.
This prioritizes correctness and prevents shared temporal history from mixing eyes;
performance needs profiling on actual headsets. The desktop mirrors the left eye.

## Validation status

The solution builds; automated tests cover eye separation, asymmetric frusta,
world scale, seated offset, camera anchoring, smooth/snap turning, teleport arcs,
release/cancel rules, panel picking, menu visibility, controller input routing and
procedural hand curl. The focused VR, Direct3D geometry, first-person, scene camera
and HUD suite passes (112 tests), including stationary scripted viewpoints, wrist
picking and blackout recovery. A Direct3D regression verifies that models added
after scene finalization receive drawable materials. The original Gabriel hands
and forearms also completed 120 simulated pose/curl updates through Vulkan, with
all six added batches present in the drawable scene.
This development machine has no active OpenXR runtime (`XR_ERROR_RUNTIME_UNAVAILABLE`),
so no headset presentation, comfort, refresh-rate or controller usability claim is
made yet. Test Quest Link and SteamVR separately, including runtime recentering,
tracking loss, menus, room changes and scripted actor movement.

References: [OpenXR specification](https://registry.khronos.org/OpenXR/specs/1.1-khr/html/xrspec.html),
[official loader release](https://github.com/KhronosGroup/OpenXR-SDK-Source/releases/tag/release-1.1.63).
