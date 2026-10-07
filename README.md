# GK3Reborn

A modern, GPL-3.0 C#/.NET 10 engine for *Gabriel Knight 3: Blood of the Sacred,
Blood of the Damned* (Sierra Studios, 1999).

GK3Reborn plays the complete game from a legally owned installation, replacing
the 1999 presentation and verb-based UI with a DX12/Vulkan renderer, spatial audio, a
pointer-first interaction model and an offline content pipeline that converts the
original data into modern formats.

Status: **stable**.

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
