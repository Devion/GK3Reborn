# Contributing to GK3Reborn

## The one rule that matters most

**Never commit original game assets, or anything reconstructable from them.**

GK3Reborn is GPL-3.0. Gabriel Knight 3 is not ours. The source license conveys no
rights to Sierra/Activision's assets, and no amount of conversion changes that:
a decoded texture, a transcoded cinematic, an extracted string table and a
re-encoded voice line are all still the original work. This includes test fixtures.

Tests that need real data use synthetic fixtures in the public repository, or a
local, git-ignored conformance corpus. Test metadata may describe an expected
structure as long as the data cannot be reconstructed from it. `.gitignore` blocks
the known extensions, but it is a safety net, not the policy.

The freely downloadable GK3 demo is the intended corpus for contributors who need
real data to work against.

## Ground rules

- Builds are warning-free. Warnings are errors; do not suppress one without a
  written justification in the suppression itself.
- Layering is enforced by `tests/GK3Reborn.Tests/Architecture/LayeringTests.cs`,
  not by the project graph — the engine is one assembly (ADR 0005). If a layering
  test fails, the design is wrong, not the rule. Changing a rule needs a reason in
  the same commit.
- Parsers bounds-check everything and fail with a `Diagnostic` naming the file,
  offset, expectation and remediation. A parser that throws
  `IndexOutOfRangeException` is a bug even when the input is corrupt.
- No ambient nondeterminism in engine code: no wall-clock reads outside the platform
  layer, no `Random.Shared`, no hash-order iteration that reaches game state. See
  ADR 0004.
- Public APIs carry XML documentation that says what the thing is for, not what its
  name already says.
- Architecturally significant decisions get an ADR in `docs/adr/`. See ADR 0001.

## Before opening a pull request

```bash
dotnet build GK3Reborn.slnx      # must be clean
./build/run-tests.sh             # must be green
```

Describe what you changed and why, and say what you tested. If the change touches a
manifest, save, bytecode, material or UI schema, include the version bump and the
migration note — those schemas are compatibility surfaces.

## Attribution

Where code is adapted from G-Engine (GPL-3.0), keep a per-file attribution notice.
Where file-format understanding comes from G-Engine or GK3 Tools, say so in the
format documentation.
