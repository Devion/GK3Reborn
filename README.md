# GK3Reborn

A modern, GPL-3.0 C#/.NET 10 engine for *Gabriel Knight 3: Blood of the Sacred,
Blood of the Damned* (Sierra Studios, 1999).

GK3Reborn plays the complete game from a legally owned installation, replacing
the 1999 presentation and verb-based UI with a Vulkan renderer, spatial audio, a
pointer-first interaction model and an offline content pipeline that converts the
original data into modern formats.

**This project ships no original game assets.** It requires an installation you
own, which it reads and never modifies.

Status: **scaffold**. The solution builds, the test suite passes, and the video
import stage is implemented. Every other subsystem is a contract awaiting its
phase. See [`../Plan`](../Plan) for the full program plan.

## Requirements

| | |
|---|---|
| SDK | .NET 10 (pinned in `global.json`) |
| Platforms | Windows x64; Linux x64 designed in from day one. macOS is out of scope. |
| Import tools | FFmpeg and ffprobe on `PATH`, or `--ffmpeg-dir` |
| Game data | A legally obtained GK3 installation |

## Build and test

```bash
dotnet build GK3Reborn.slnx
./build/run-tests.sh            # or build/run-tests.ps1
```

Tests are run directly rather than through `dotnet test`. The test assemblies are
Microsoft.Testing.Platform applications; on SDK 10.0.302 the `dotnet test` MTP
driver reports "Zero tests ran" for these xunit.v3 4.0.0 projects while the same
assemblies discover and pass every test when executed directly. The scripts use
the working path; revisit when either component updates.

## Importing content

The importer reads a GK3 installation and writes converted content to a workspace
directory outside both the installation and this repository. The installation is
never written to.

```bash
dotnet run --project tools/GK3Reborn.Tools -- import-video \
    --source    "path/to/GK3/Data" \
    --workspace "path/to/ContentWorkspace"
```

The video stage converts the BIK and AVI cinematics to MP4 / H.264 + AAC,
preserving frame size, frame rate and duration exactly, and writes
`manifests/video.json` recording source and output hashes, probe results,
validation checks and the exact command used. Reruns are incremental: an output
whose source hash, converter version and output hash still match is left alone.

Odd-sized sources encode as H.264 4:4:4 rather than 4:2:0, because several Sidney
scan clips have odd frame dimensions (41x51, 389x424, 431x350) and padding or
cropping them would shift the UI overlays they sit under.

Videos are keyed by uppercase base name with no extension, because the game's
data references them that way.

## Layout

```text
src/
  GK3Reborn.Engine/            the engine, one assembly
    Foundation/                ids, diagnostics, clock, deterministic RNG
    Platform/                  window, input, monitors
    Formats/                   read-only parsers for original formats
    Content/                   manifests, VFS, runtime asset cache
    Sheep/                     compiler, bytecode, VM
    Game/                      GK3 state, scenes, actions, persistence
    Rendering/                 backend-neutral render services
    Rendering/Vulkan/          the Vulkan backend
    Audio/                     mixer, spatialization, speaker routing
    Video/                     cinematic playback
    UI/                        retained-mode GPU UI
  GK3Reborn.Host/              the shipped executable; native library resolution
tools/
  GK3Reborn.Tools/             offline CLI: import, compile, inspect, sheep
tests/
  GK3Reborn.Tests/             one test assembly, mirroring the engine's areas
docs/adr/                      architecture decision records
build/
```

Areas are directories and namespaces rather than separate projects — see
[ADR 0005](docs/adr/0005-single-engine-assembly.md). Layering is enforced by
`tests/GK3Reborn.Tests/Architecture/LayeringTests.cs`, which reads the engine's
sources and asserts the rules directly: `Formats` never reaches rendering, UI,
gameplay, audio, video or platform code; `Foundation` depends on nothing above
it; `Game` never touches the Vulkan backend; only `Rendering/Vulkan` may use
`Silk.NET.Vulkan`; and no engine code uses ambient randomness.

## Correcting derived content

Lighting rigs and PBR material channels are *derived*: the converter guesses them
from baked lightmaps and diffuse textures, because the 1999 assets carry nothing
better. Scenes are re-lit for modern range rather than matched to the original
([ADR 0006](docs/adr/0006-relight-for-modern-range.md)), so those guesses need
correcting once scenes can be seen in motion.

Corrections go in a human-owned file beside the generated one, which the
converter never writes:

```text
scenes/LBY.lighting.json           generated; regenerated freely
scenes/LBY.lighting.edits.json     yours; add / modify / remove operations
materials/LBY.materials.json       generated
materials/LBY.materials.edits.json yours
```

A `modify` carries only the fields it changes, so setting `roughness` changes
roughness and nothing else. Deleting a spurious light, nudging one into the right
place, adding a light the lightmap never implied, or dialling back a floor that
reads as wet are all the same mechanism — and none of it is lost when the
extractor improves and everything is regenerated. An edit whose target no longer
exists is reported by id and skipped, never fatal.

## Publishing layout

Release builds keep the install root clean: a single managed executable, with
native libraries under `libs/<rid>/` and converted content under `content/`.
Native resolution goes through an absolute-path resolver; the global `PATH` is
never modified.

## Locale

en-US is the only supported and validated locale. The text, font, layout and
audio paths are locale-neutral so the other official locales can be added once an
installation exists to validate against.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

The source license conveys no rights to the original game's assets or to any
derivative of them. See [CONTRIBUTING.md](CONTRIBUTING.md) before contributing
anything asset-shaped.
