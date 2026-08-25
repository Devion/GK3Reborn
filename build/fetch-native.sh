#!/usr/bin/env bash
# Fetches the hand-dropped half of libs/<rid>.
#
# One thing is not on NuGet and so cannot arrive with a restore: MoltenVK, the only Vulkan
# there is on a Mac. Everything else under libs/<rid> - glfw3, soft_oal, shaderc_shared and
# their peers - comes from the Silk.NET native packages and is put there by the targets in
# GK3Reborn.Host.csproj. Windows and Linux have nothing to fetch at all: the cutscenes are
# decoded by the engine's own H.264 and AAC code, so the FFmpeg build this script used to
# download is no longer needed anywhere (docs/formats/video.md).
#
# CI runs this so that a downloadable build is a complete one, and it is the same command
# a contributor runs, so a development tree and a release are populated the same way.
#
# Every download is pinned to an exact release and verified against a hash recorded here
# rather than one fetched alongside it: a hash served by the host it vouches for proves
# only that the bytes arrived intact.
set -uo pipefail

cd "$(dirname "$0")/.."

rid="${1:-}"

case "$rid" in
    win-x64|linux-x64)
        echo "libs/$rid needs nothing beyond what NuGet provides."
        exit 0
        ;;
    osx-arm64)
        url=https://github.com/KhronosGroup/MoltenVK/releases/download/v1.4.2/MoltenVK-macos.tar
        sha=f95765a6229cb7b915990a2890ce12ebe36a730b021545d3d52ae69ce4c4024e
        want="libMoltenVK.dylib"
        ;;
    *)
        echo "usage: $0 <win-x64|linux-x64|osx-arm64>" >&2
        exit 2
        ;;
esac

target="libs/$rid"
missing=0
for name in $want; do
    [ -f "$target/$name" ] || missing=1
done

if [ "$missing" -eq 0 ]; then
    echo "$target is already complete."
    exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

archive="$work/$(basename "$url")"

echo "fetching $(basename "$url")"
curl -fsSL --retry 3 -o "$archive" "$url" || { echo "download failed: $url" >&2; exit 1; }

# shasum on a Mac, sha256sum everywhere else. Neither is present on both.
if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$archive" | cut -d' ' -f1)"
else
    actual="$(shasum -a 256 "$archive" | cut -d' ' -f1)"
fi

if [ "$actual" != "$sha" ]; then
    echo "checksum mismatch for $(basename "$url")" >&2
    echo "  expected $sha" >&2
    echo "  actual   $actual" >&2
    exit 1
fi

case "$archive" in
    *.zip)     unzip -q "$archive" -d "$work/x" ;;
    *.tar.xz)  mkdir -p "$work/x" && tar -xJf "$archive" -C "$work/x" ;;
    *.tar)     mkdir -p "$work/x" && tar -xf "$archive" -C "$work/x" ;;
    *)         echo "no rule for $archive" >&2; exit 1 ;;
esac

# Searched for by name rather than read from a known path inside the archive, so that a
# publisher rearranging their layout is a loud failure below rather than an empty
# directory that only shows up as a missing library.
mkdir -p "$target"
failed=0
for name in $want; do
    found="$(find "$work/x" -type f -name "$name" | head -1)"

    if [ -z "$found" ]; then
        # A versioned file behind a symlinked soname cannot unpack on a filesystem without
        # symlinks; take the real file and give it the name the loader looks for.
        found="$(find "$work/x" -type f -name "$name.*" | sort | tail -1)"
    fi

    if [ -z "$found" ]; then
        echo "$name is not in $(basename "$url")" >&2
        failed=1
        continue
    fi
    cp -L "$found" "$target/$name"
    echo "  $target/$name"
done

exit "$failed"
