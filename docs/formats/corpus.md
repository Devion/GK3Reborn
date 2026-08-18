# What is actually in the archives

Findings from pipeline stage C2 (`GK3Reborn.Tools inventory`) against the retail
reference installation: 36,957 assets, 1,256 MB decompressed.

## Extensions are not types

The archives appear to contain **2,775 distinct file extensions**. They contain
about a dozen file types.

The reason is audio. Of 7,852 audio assets, only 1,170 are named `.WAV`. The rest
carry a three-character dialogue code where the extension goes — `.N61`, `.6J1`,
`.B61`, and 2,744 others. Every one of them is an ordinary RIFF/WAVE file.

Classifying by name would therefore mishandle **85% of the game's audio**, which
is why `AssetClassifier` reads leading bytes and treats the extension as a hint at
most.

| Kind | Count | MB | Distinct extensions |
|---|---:|---:|---:|
| Text | 13,635 | 9.1 | 15 |
| Audio | 7,852 | 347.3 | **2,747** |
| BitmapGk3 | 6,330 | 383.9 | 1 |
| ActorAnimation | 5,796 | 381.0 | 1 |
| Model | 1,878 | 17.3 | 1 |
| Html | 417 | 1.2 | 2 |
| BitmapWindows | 328 | 11.7 | 1 |
| Lightmap | 226 | 36.4 | 1 |
| SheepBytecode | 224 | 0.7 | 1 |
| Font | 138 | 0.0 | 2 |
| SceneGeometry | 110 | 56.0 | 1 |
| DesignDocument | 14 | 4.9 | 1 |
| Executable | 6 | 6.5 | 2 |
| ZipArchive | 1 | 0.0 | 1 |
| Unknown | 2 | 0.0 | 1 |

## Signatures

Several formats write their tag little-endian, so it reads reversed on disk.

| On disk | Meaning |
|---|---|
| `RIFF` | WAVE audio |
| `61nM` | GK3 bitmap |
| `BM` | Windows bitmap |
| `LDOM` | model geometry (`MODL`) |
| `HTCA` | actor animation (`ACTH`) |
| `TLUM` | lightmap (`MULT`) |
| `NECS` | BSP scene geometry (`SCEN`) |
| `GK3S` | compiled Sheep bytecode |
| `Font`, `Bitm` | bitmap font |
| `<htm` | Sidney document |
| `D0 CF 11 E0` | OLE compound document |
| `MZ` | Windows executable |

Text assets have no signature. They are Latin-1, use CRLF — and sometimes CR CR
LF — line endings, and are occasionally NUL-terminated, so a trailing NUL says
nothing about the format while a NUL inside content reliably means binary.

## The design documents

The archives contain 14 Word documents: the original team's own design
documentation, shipped inside the game data.

| Document | Size |
|---|---:|
| `SHEEP ENGINE.DOC` | 2,285,056 |
| `SIF.DOC` | 1,620,480 |
| `NVC.DOC` | 428,032 |
| `PERSISTENCE.DOC` | 233,472 |
| `GK3 FONTS.DOC` | 185,856 |
| `TIMEBLOCKBIBLE.DOC` | 65,024 |
| `GAS.DOC` | 60,416 |
| `DATAUSAGE.DOC` | 57,856 |
| `SOUND TRACK FILES.DOC` | 37,888 |
| `CLOTHESANM.DOC` | 36,352 |
| `MOS_ZZZ.DOC` | 25,088 |
| `FOOTSTEP.DOC` | 23,040 |
| `GRAR33FLIP.DOC` | 22,528 |
| `OFFICIAL EGGS.DOC` | 20,992 |

These are the authoritative descriptions of the formats and systems this project
has to reimplement, written by the people who built them. Anywhere the plan says
a format is "partly understood", these should be consulted before reverse
engineering from the reference implementation's reader.

They are Sierra's copyrighted material. Read them from a local extraction; never
commit them, quote them wholesale, or redistribute them.

## Known exceptions

**Two assets have a zeroed header.** `GAB_GABDOORLOCKED.ACT` and
`GAB_GABKITCLSDUMB.ACT` begin with 48 zero bytes where `HTCA` should be, while
still carrying roughly 17 KB of content each. This is a defect in the retail data,
not in the reader. They stay classified `Unknown` rather than being assumed to be
actor animations on the strength of their names, so the defect stays visible.

**Six executables and one zip.** `GK3R.EXE`, `COMBINE.EXE`, `VIEWER.EXE`,
`BINKPLAY.EXE`, `MSVCP60.DLL`, `MSVCRT.DLL` and `VIEWER_RESOURCES.ZIP` were left
in the archives. They are development tools, not game content, and nothing
references them.

## References

The inventory resolves asset-shaped tokens found in text assets against the
corpus index: **10,683 resolve, 152 do not**.

Dangling references by target type: `.ANM` 81, `.HTML` 22, `.GAS` 12, `.NVC` 10,
`.STK` 8, `.BMP` 6, `.TXT` 5, `.SCN` 5, `.SEQ` 2, `.SHP` 1.

Some are expected — `talk.gas` is referenced by several scene init files and does
not exist, suggesting a name resolved at runtime or a cut feature. Each one is
recorded in `manifests/corpus.json` so the C2 gate ("no dangling reference without
an explicit exception") can be closed deliberately rather than by assertion.

The scan is intentionally shallow: it finds candidate references without needing a
parser for every text format. Audio is excluded from the pattern, because its
extensions are dialogue codes rather than a fixed set and including them would
match any word followed by a period.
