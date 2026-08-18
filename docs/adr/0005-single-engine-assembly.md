# ADR 0005: One engine assembly, with areas as directories

- Status: accepted
- Date: 2026-08-18
- Supersedes: the multi-project layout sketched in `Plan/01-architecture.md` section 2

## Context

The plan sketched thirteen engine projects — `Foundation`, `Platform`, `Formats`,
`Content`, `Sheep`, `Game`, `Rendering`, `Rendering.Vulkan`, `Audio`, `Video`,
`UI`, `App`, `Bootstrap` — plus four tool executables and several test projects.
The stated reason was that a project graph makes illegal references impossible:
the compiler simply refuses to build `Formats` if it reaches into `Rendering`.

That is a real benefit, but the cost is paid on every single build and every
navigation. At scaffold time most of those projects held one or two files. Each
one still needs a `.csproj`, a package-reference list, a place in the solution,
and a spot in everyone's mental model. Twenty-one projects to hold roughly twenty
source files is ceremony, not architecture, and it makes ordinary work — moving a
type, adding a file, following a reference — slower for no gain.

## Decision

One engine assembly, `GK3Reborn.Engine`, with areas as directories and
namespaces:

```text
src/GK3Reborn.Engine/
  Foundation/        ids, diagnostics, clock, deterministic RNG
  Platform/          window, input, monitors
  Formats/           read-only parsers for original formats
  Content/           manifests, VFS, runtime asset cache
  Sheep/             compiler, bytecode, VM
  Game/              GK3 state, scenes, actions, persistence
  Rendering/         backend-neutral render services
  Rendering/Vulkan/  the Vulkan backend
  Audio/             mixer, spatialization, speaker routing
  Video/             cinematic playback
  UI/                retained-mode GPU UI
```

Namespaces still read `GK3Reborn.Formats`, `GK3Reborn.Rendering.Vulkan` and so
on, so nothing about how the code is organized or discussed changes — only how
many `.csproj` files exist.

Three projects remain alongside it:

- **`GK3Reborn.Host`** — the shipped executable. It stays separate because it
  installs native-library resolution from `libs/<rid>` *before* any engine type
  is loaded, and a separate assembly makes that ordering structural rather than a
  convention that a future edit could quietly break.
- **`GK3Reborn.Tools`** — one CLI with subcommands (`import-video`,
  `compile-content`, `inspect`, `sheep`) instead of four executables that would
  have shared all the same parsers and manifests.
- **`GK3Reborn.Tests`** — one test assembly, with directories mirroring the
  engine's areas.

Layering is now enforced by tests instead of by the compiler.
`tests/GK3Reborn.Tests/Architecture/LayeringTests.cs` reads the engine's source
files and asserts the rules the project graph used to guarantee: `Formats` may
not reach rendering, UI, gameplay, audio, video or platform code; `Foundation`
depends on nothing above it; `Content` does not know how assets are drawn or
played; `Sheep` is not a consumer of subsystems; `Game` never touches the Vulkan
backend directly; and only `Rendering/Vulkan` may use `Silk.NET.Vulkan`. A fourth
test enforces ADR 0004 by rejecting ambient randomness anywhere in engine code.

## Consequences

**Good.** Four projects instead of twenty-one. Adding a file means adding a file.
Build times drop and the solution is legible at a glance. The layering rules are
now written down in one readable list with the reasoning next to each entry,
which is more discoverable than the same rules implied by a graph of
`ProjectReference` elements.

**Bad.** A layering violation is caught by a failing test rather than a failing
compile, so it can exist in a working tree for a moment before anything
complains. The check is source-level: it reads `using` directives, so a violation
written with a fully qualified name — `GK3Reborn.Rendering.Foo.Bar()` inline —
would slip through. That is a deliberate trade for a dependency-free check with a
clear failure message; tighten it to IL analysis if it ever proves insufficient.

**Also.** Splitting an area back out into its own project later is mechanical:
the namespaces already match, so it is a `.csproj` and a set of file moves. The
decision is cheap to reverse if the engine grows enough to justify it.
