# Third-party components

What a published GK3Reborn archive contains besides GK3Reborn itself. NOTICE points
here; this is the detail.

Nothing in this list is modified, and nothing is statically linked into the game. Every
one of them is a separate file that the game loads at run time and that can be replaced
with another build of the same thing.

**No original Gabriel Knight 3 asset appears in any archive, and none ever will.** The
game reads those from a legally obtained installation. See NOTICE, "Asset rights".

## Native libraries, in `libs/<rid>/`

| Component | Version | Licence | Source |
|---|---|---|---|
| MoltenVK — macOS only | 1.4.2 | Apache-2.0 | https://github.com/KhronosGroup/MoltenVK |
| OpenAL Soft (`soft_oal`) | 1.23.1 | LGPL-2.0-or-later | https://github.com/kcat/openal-soft |
| GLFW (`glfw3`) | via Silk.NET 2.23.0 | Zlib | https://github.com/glfw/glfw |
| shaderc (`shaderc_shared`) | via Silk.NET 2.23.0 | Apache-2.0 | https://github.com/google/shaderc |

MoltenVK is fetched by `build/fetch-native.sh`, which pins the exact upstream release and
verifies the SHA-256 of the archive it downloads. The rest arrive as NuGet native packages
and are relocated into `libs/<rid>/` by the targets in
`src/GK3Reborn.Host/GK3Reborn.Host.csproj`.

The cutscenes need nothing here. H.264 video and AAC audio are decoded by the engine's own
managed code (`src/GK3Reborn.Engine/Formats/Video`), which is why there is no FFmpeg in the
list: there used to be, and it was the one component with a different build per platform
generation and none at all for Apple silicon.

### The LGPL one

OpenAL Soft is LGPL. It is shipped as an unmodified shared library loaded at run time,
which is the arrangement the LGPL is written for: the terms are satisfied by saying so, by
not linking it statically, and by leaving it replaceable — delete the file and drop in your
own build of the same generation and the game will use it.

Its corresponding source is at the repository above, offered from the same kind of public
location as the binary itself.

## Managed dependencies

| Component | Version | Licence |
|---|---|---|
| NLayer | 2.0.1 | MIT |
| Silk.NET (Core, Input, Maths, OpenAL, Shaderc, Vulkan, Windowing, extensions) | 2.23.0 | MIT |
| Microsoft.Extensions.Logging and abstractions | 10.0.11 | MIT |

The .NET runtime itself is a prerequisite rather than a payload: these builds are
framework-dependent, so it is installed separately and is not in the archive.

## Embedded in the engine assembly

| Component | Version | Licence |
|---|---|---|
| Noto Serif Regular | 1.07 | SIL Open Font License 1.1 |

Carried as an embedded resource at `src/GK3Reborn.Engine/Assets/Fonts/`. The OFL permits
this: the font is not sold on its own and its name promotes nothing. A `.ttf` or `.otf` in
the content workspace's `enhanced/fonts` is used in preference to it.

## Standard tables carried in the decoders

The H.264 and AAC decoders are written here, but the constant tables of the two standards
— CABAC context initialisation, CAVLC code tables, AAC Huffman codebooks and scale-factor
band offsets — were transcribed rather than typed from the specifications, and the
transcriptions are credited in the files that hold them:

| Tables | Taken from | Licence |
|---|---|---|
| H.264 CABAC initialisation, range table, CAVLC codes (`Formats/Video/H264/Tables.Generated.cs`) | JCodec, https://github.com/jcodec/jcodec | FreeBSD (BSD-2-Clause) |
| AAC Huffman codebooks and band tables (`Formats/Video/Aac/AacCodebookTables.cs`, `AacTables.cs`) | JAADec, https://github.com/DV8FromTheWorld/JAADec | Public domain |

The values are those of ITU-T H.264 and ISO/IEC 14496-3; only their arrangement in a
source file was borrowed. No code from either project is used.

## Attribution rather than redistribution

G-Engine (GPL-3.0) is the behavioural oracle and format reference, and three of its
reconstructed data files are adapted in `src/GK3Reborn.Engine/Assets/Story`. Each carries
its attribution in its own header. GK3 Tools informs the format work. Neither ships as a
binary here. See NOTICE, "Attribution".
