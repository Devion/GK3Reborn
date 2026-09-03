# GK3Reborn

A modern, GPL-3.0 C#/.NET 10 engine for *Gabriel Knight 3: Blood of the Sacred,
Blood of the Damned* (Sierra Studios, 1999).

GK3Reborn plays the complete game from a legally owned installation, replacing
the 1999 presentation and verb-based UI with a Vulkan renderer, spatial audio, a
pointer-first interaction model and an offline content pipeline that converts the
original data into modern formats.

**This project ships no original game assets.** It requires an installation you
own, which it reads and never modifies.

Status: **early**. The solution builds and the test suite passes. The content
pipeline reads the original archives and converts the cinematics, models, textures
and scenes. A room now loads the way the game builds it — both of its initialisation
files, their conditions decided against a point in the story — and renders under
Vulkan with the artists' own light rigs and optional ray-traced shadows and
occlusion. Everything after that is still ahead: nothing walks, nothing is
clickable, no script drives a scene, and there is no audio or UI. See
[`../Plan`](../Plan) for the full program plan, and
[docs/known-issues.md](docs/known-issues.md) for what is known to be wrong.

## Requirements

| | |
|---|---|
| SDK | .NET 10 (pinned in `global.json`) |
| Platforms | Windows x64 and Linux x64, designed in from day one; macOS on Apple silicon |
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
# Extract every entry from every Barn archive.
dotnet run --project tools/GK3Reborn.Tools -- extract-barn \
    --source    "path/to/GK3/Data" \
    --workspace "path/to/ContentWorkspace"

# Convert the cinematics.
dotnet run --project tools/GK3Reborn.Tools -- import-video \
    --source    "path/to/GK3/Data" \
    --workspace "path/to/ContentWorkspace"
```

`extract-barn --verify` decompresses and validates everything without writing a
byte, which is the quick way to check an installation. Against the reference
install it reports 36,957 entries extracted, 20,991 pointers into other archives,
and 0 failures. The format, its quirks and how the result was validated are
documented in [docs/formats/barn.md](docs/formats/barn.md).

`inventory` classifies every asset by its contents and resolves the references
between them, writing `manifests/corpus.json`. It is worth reading
[docs/formats/corpus.md](docs/formats/corpus.md) before working with the data: the
archives look like they hold 2,775 file types but hold about a dozen, because most
audio assets carry a dialogue code where the extension goes. They also contain the
original team's design documents for Sheep, SIF, NVC and persistence.

`organize` produces the tree you actually work in: assets grouped by what they
are, textures converted to PNG, models converted to glTF, animations grouped by the character prefix their
names carry, and scene assets grouped by GK3's three-letter location codes — so
`scenes/LBY/` holds the lobby's geometry and all its timeblock variants together.
The raw extraction is left untouched; this is a derived view and re-running it is
always safe. See [docs/formats/textures.md](docs/formats/textures.md).

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

`pack-content` is the last stage: it encodes everything under `enhanced/` to
block-compressed DDS and packs it into the one or two `.rebarn` volumes that ship
beside the executable, so a built game is an executable and two files rather than
forty thousand.

```
dotnet run --project tools/GK3Reborn.Tools --   pack-content --workspace "path/to/ContentWorkspace"
```

`Reborn.rebarn` holds colour, emissive, models and video; `RebornMaterials.rebarn`
holds the normal, ORM and height maps, which the renderer already treats as
optional — deleting it degrades the picture instead of breaking the game. Entries
are stored rather than deflated and aligned to 256 bytes, so a texture is
memory-mapped and handed to the device without being decoded or copied. The engine
reads every `*.rebarn` beside itself in name order and the last one wins, which is
all a patch or a mod pack needs. `pack-list`, `pack-extract` and `pack-verify` read
one back; `pack-verify` also decodes every DDS with the engine's own reader, because
a checksum only proves the bytes survived, not that the loader will accept them.

`--rebarn` runs the game on the packs alone, with every loose source of enhanced
content taken out of the way, which is the only honest way to measure what the
shipped form costs. It refuses to start rather than fall back if no pack is found.

The source tree does not move: designers keep editing `enhanced/textures`, and
re-running `pack-content` catches the pack up. Only what changed is re-encoded. See
[docs/formats/rebarn.md](docs/formats/rebarn.md) for the container, the format
chosen for each channel and why there are no texture atlases.

### Overriding what shipped

A directory called `overrides/` beside the executable stands in front of everything:
the packs, and the game's own `.brn` archives, which no pack can reach. Drop a file
under the name the game already uses and it is what gets read — `textures/R25WALLS.png`
for a wallpaper, `R25.NVC` for a room's script — with no repack and nothing enabled.
`GK3Reborn --extract --name R25WALLS --as png` writes the content out in the layout
that directory reads back, decoding block-compressed textures to editable PNG on the
way. See [docs/overrides.md](docs/overrides.md).

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
  run-tests.sh, run-tests.ps1  the test suite
  package-macos.sh             GK3Reborn.app -> GK3Reborn.pkg; needs a Mac
  macos/                       Info.plist, entitlements, installer text, the icon
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

```console
build/fetch-native.sh win-x64                                          # once per rid
dotnet publish src/GK3Reborn.Host -p:PublishProfile=FolderProfile      # win-x64
dotnet publish src/GK3Reborn.Host -p:PublishProfile=FolderProfile1     # linux-x64
dotnet publish src/GK3Reborn.Host -p:PublishProfile=FolderProfileMac   # osx-arm64
```

`build/fetch-native.sh <rid>` fetches the half of `libs/<rid>/` that is not on
NuGet — MoltenVK, on a Mac; Windows and Linux have nothing to fetch — from a
pinned upstream release whose SHA-256 it verifies. CI runs the same script, so a
published archive and a development tree are populated identically. Running it
twice does nothing. The cutscenes need no native library at all: the engine
decodes H.264 and AAC itself ([docs/formats/video.md](docs/formats/video.md)).

```text
GK3Reborn.exe          every managed assembly, bundled by single-file publishing
libs/win-x64/          glfw3, soft_oal, shaderc_shared
licenses/              THIRD-PARTY.md, what is redistributed and under what terms
```

macOS is the one that is not a folder of files. See
[the macOS installer package](#the-macos-installer-package) below.

### Running on macOS

Apple silicon only; an Intel Mac would run the same build under Rosetta at a cost
the renderer cannot afford.

**Vulkan on a Mac is MoltenVK**, which translates to Metal. A downloaded release
already carries it. Building one yourself, run `build/fetch-native.sh osx-arm64`,
or install the Vulkan SDK for macOS, or drop `libMoltenVK.dylib` into
`libs/osx-arm64/` beside the executable by hand — Silk.NET looks for
`libvulkan.dylib` and `libMoltenVK.dylib`, and the native resolver adds that
directory to its search.

Two consequences, both handled rather than worked around:

- **It is a portability driver.** The instance opts in to enumerating one and the
  device enables `VK_KHR_portability_subset`. Without either, the machine reports
  no Vulkan devices at all.
- **Metal has no BC texture formats.** The packs are BC7, BC5 and BC4, so on this
  hardware they are expanded to eight-bit pixels on the host as they are loaded.
  The picture is the same; it costs four times the video memory. See
  `docs/rendering.md`, "Portability drivers, and devices with no block
  compression", for how that path is checked — and for `--expand-blocks`, which
  makes a Windows or Linux machine take it.

There is no ray tracing on this hardware: MoltenVK offers no acceleration
structures, so the tier is not reached and the raster path runs, which is what
the tier model is for.

### The macOS installer package

A Mac does not install a folder of files. `FolderProfileMac` therefore publishes a
bundle rather than a directory — `publishmac/GK3Reborn.app`, with `PublishDir`
pointed straight into `Contents/MacOS` so the executable and `libs/osx-arm64/` land
where they belong without being moved afterwards. `AppContext.BaseDirectory` is then
`Contents/MacOS`, which is what every path the game derives is relative to, exactly as
on the other two platforms. `RebornWriteMacAppBundle` in `GK3Reborn.Host.csproj` adds
the rest of what makes a directory an application: `Info.plist` from
[build/macos/Info.plist](build/macos/Info.plist), `PkgInfo`, and the icon.

That half runs anywhere. The installer package does not:

```console
./build/package-macos.sh --publish
```

`codesign`, `pkgbuild`, `productbuild`, `sips` and `iconutil` are part of macOS and
have no equivalent elsewhere, so the script refuses to run on anything else and says
so. Publish the bundle wherever you like, copy it to a Mac as an archive, and point
the script at it with `--app`. What comes out is `artifacts/macos/GK3Reborn-<version>.pkg`,
which installs the bundle into `/Applications`.

**Signing is not optional on Apple silicon.** An arm64 executable with no signature at
all is killed by the kernel on launch, so a bundle published from Windows or Linux does
not run until the script has signed it once. The default is an ad-hoc signature, which
is enough for the machine that made it. Giving the package to anybody else needs a real
Developer ID and notarisation:

```console
./build/package-macos.sh --publish \
    --sign-app "Developer ID Application: ..." \
    --sign-pkg "Developer ID Installer: ..." \
    --notarize my-keychain-profile
```

The entitlements that go with a hardened-runtime signature are in
[build/macos/entitlements.plist](build/macos/entitlements.plist), and each is there
because of how .NET and Silk.NET work rather than as a precaution — the CLR needs
`allow-jit`, and the unsigned libraries in `libs/osx-arm64` need
`disable-library-validation`.

### Where a read-only install keeps things

Windows and Linux keep saves, the shader cache and the archives beside the executable,
because that is a directory somebody unpacked and owns. An installed `.app` is not:
it is read-only, and writing into a signed one would break the signature even where
the permissions allow it. So `Foundation/InstallPaths.cs` gives two roots instead of
one — the bundle's own `Contents/Resources` to read from, and
`~/Library/Application Support/GK3Reborn` to write to — and everything falls back to
the second only when it cannot use the first. Nothing about that is macOS-only in
effect; the fallback simply stops being hypothetical on a Mac.

For a player that means the archives go here:

```text
~/Library/Application Support/GK3Reborn/Data/     the eight .brn files
~/Library/Application Support/GK3Reborn/          a .rebarn pack, saves, settings.json
```

`--data <dir>` still overrides it, and a bundle that *is* writable — one sitting in a
folder you own rather than in `/Applications` — keeps its saves and shader cache
inside itself as usual. The installer says all of this on its welcome page, which is
[build/macos/welcome.html](build/macos/welcome.html).

### Filling a published tree

The build ships no game content. Two things go in beside the executable — or, on a
macOS install that cannot be written to, in `~/Library/Application Support/GK3Reborn`:

```text
Data/                  the original game's archives, copied from your installation
Reborn.rebarn          the converted content, built by `pack-content`
```

`Data/` wants these eight files and nothing else:

```text
ambient.brn  common.brn  core.brn  day1.brn  day123.brn  day2.brn  day23.brn  day3.brn
```

The `.bik` and `.avi` movies in the original `Data` directory are *not* needed by
a published game — they are Bink and Indeo, which nothing modern decodes, and the
pack carries converted H.264 in their place. They are only needed by the
conversion pipeline, which reads them from the installation directly. Everything
else in a GK3 installation — `GK3.exe`, `binkw32.dll`, the save games — is
unused.

The archives are found beside the executable in `Data/`, then loose in the
executable's own directory, then — for a development build only — six levels up
at `GK3/Data`. `--data <dir>` overrides all three. A `.rebarn` pack is looked for
beside the executable first, then in the content workspace. Without a pack the
game runs on the original 1999 art.

The managed assemblies go *into* the executable rather than into `libs/`;
relocating them would need a probing-path fallback that bundling makes
unnecessary. The native libraries are moved out of the `runtimes/<rid>/native`
tree by targets in `GK3Reborn.Host.csproj`, which also copy in whatever the
gitignored `libs/<rid>/` beside this file holds — MoltenVK on a Mac, put there by
`build/fetch-native.sh`. A checkout without it publishes fine.

Two loaders have to agree about `libs/<rid>`: the BCL's, hooked with
`NativeLibrary.SetDllImportResolver`, and Silk.NET's, which does its own
searching. `NativeLibraryLocator` installs into both.

A publish with no RID keeps the flat developer layout — loose assemblies, every
platform's natives — but still gathers them under `libs/<rid>/`. Pass
`-p:RebornCleanPublishLayout=false` for the stock SDK layout, which is worth
having when bisecting a native-loading failure.

`--offscreen` and `--render --headless-frames` are the two smoke tests that prove
a published tree loads its natives: the first renders without a window, the
second opens one for sixty frames.

## Starting the game

No arguments is how a player starts it, and no arguments has to mean something
sensible on its own: the intro, the menu, and then day one at ten in the morning
in the lobby. Ray tracing follows the picture quality in the player's settings
and falls back to none on a device without it. The `.rebarn` pack is used when
one is there, and the loose enhanced sets are ignored — which is all a shipped
install has anyway. Naming `--enhanced`, `--workspace` or `--uncompressed` is
what asks for the loose sets instead; `--rebarn` still forces packs-only and
refuses to start without a pack, which is what makes a measurement honest.

`GK3Reborn --help` lists every switch: where to start, what to draw with, which
content to read, and the ones for photographing a run with no keyboard.

Windows draws through Direct3D 12 and everything else through Vulkan; `--vulkan`
(or `--backend vulkan`) asks for the other on Windows, and the Display page
carries the same choice. Direct3D needs a card at feature level 11_0 with a
driver that speaks shader model 6.0, which is any card that has run a game
since about 2014 — a GeForce GTX 960M is enough. Ray tracing needs shader model
6.5 and inline ray tracing on top, and a card without them draws the baked
lighting instead. When Direct3D cannot start at all the game says so in the log,
opens Vulkan instead, and the settings page shows which one is running.

### Upscaling and HDR

Settings → Upscaling offers four: off, the engine's own, FSR and DLSS. The first two need
nothing installed and work on any device the game runs on. The other two are proprietary
redistributables which **this project ships none of** — copy them into `libs/` beside the
executable and the game finds them at the next launch:

| For | Copy in | From |
|---|---|---|
| FSR | `amd_fidelityfx_vk.dll` | [FidelityFX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) |
| DLSS | `sl.interposer.dll`, `sl.common.dll`, `sl.dlss.dll`, `nvngx_dlss.dll` | [Streamline](https://github.com/NVIDIA-RTX/Streamline) and [DLSS](https://github.com/NVIDIA/DLSS) |
| DLSS frame generation | `sl.dlss_g.dll`, `nvngx_dlssg.dll` | as above |

NVIDIA's download unpacks to a `streamline` subdirectory; `libs/streamline/` is looked in
too, so there is nothing to flatten. `--libs-dir` names somewhere else. The startup log and
the settings page both say what was found, what is missing and — when a runtime is installed
and still will not run — why.

DLSS is not offered on a card that is not NVIDIA's. FSR is offered on every card, because
FidelityFX is compute and runs anywhere.

Settings → Display carries the window mode — windowed, borderless over the monitor, or
fullscreen — the resolution, and high dynamic range with the display's luminances. See
[docs/upscaling.md](docs/upscaling.md) for the whole chain and for what is not done yet.

### When it will not start

Every run writes `log.txt` beside the executable, or in the user's own directory
when the install cannot be written to — the same place the saves go, which for a
macOS `.app` in `/Applications` is `~/Library/Application Support/GK3Reborn`. The
first console line says which. The previous run is kept as `log.previous.txt`, so
restarting after a crash does not destroy the log of it.

It holds everything the console shows and rather more besides: the machine, the
runtime and the architecture; every directory the game looked in for content, for
a pack and for the native libraries, and which one answered; whether the settings,
saves and shader-cache directories could actually be written to; and any unhandled
exception with its stack. That is the file to ask for when somebody reports that
the game will not start — a console has usually gone by then, and on Linux and
macOS a game started from a launcher or from Finder never had one.

Two failures are worth knowing by name, because both are silent otherwise and
neither can happen on the machine this is developed on:

- **The native payload is missing.** `libs/<rid>/` holds GLFW, OpenAL and shaderc,
  and without them the process dies inside its first P/Invoke. The log says so
  before anything is loaded, and says which library would not load as opposed to
  which was not there — on Linux the difference is usually a missing `libX11`, and
  `ldd` on the named file finds it.
- **A directory that differs only in case.** `Data` and `data` are one directory on
  Windows and two on Linux and macOS, so a player can be looking straight at the
  directory the game says is missing. When the name is there under another spelling
  the log says which one and where.

A third used to be silent and is not any more: **Direct3D 12 will not start** on
the card. The log carries `GK3R3422` with Direct3D's own reason — an HRESULT such
as `0x887A0004`, which is DXGI saying unsupported — and the run goes on in Vulkan.
Only a backend named on the command line is not fallen back from, because somebody
who typed `--d3d12` is finding out whether it works.

## Locale

en-US is the only supported and validated locale. The text, font, layout and
audio paths are locale-neutral so the other official locales can be added once an
installation exists to validate against.

## Acknowledgements

**Bonny Ploeg**, for *Gabriel Knight 3 Secrets*
(<http://bonny.ploeg.ws/gk3secret.html>) and the companion *GK3script* compilation
of every YAK file in the game. Their catalogue of the objects that were cut from
the game but left on the disc — object by object, line by line — is the starting
point for [docs/cut-content.md](docs/cut-content.md), which surveys what of that
content the shipped data can still reach and what it is missing. Every claim on
that page that could be checked against the archives held up.

**Clark Kromenaker**, for [G-Engine](https://github.com/kromenak/gengine), this
project's behavioural oracle and file-format reference, and the authors of
[GK3 Tools](https://sourceforge.net/projects/gk3tools/). See [NOTICE](NOTICE) for
what is adapted from each and under what terms.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

The source license conveys no rights to the original game's assets or to any
derivative of them. See [CONTRIBUTING.md](CONTRIBUTING.md) before contributing
anything asset-shaped.
