#!/usr/bin/env bash
# Fetches the hand-dropped half of libs/<rid>.
#
# Two things are not on NuGet and so cannot arrive with a restore. FFmpeg decodes the
# cutscenes, and MoltenVK is the only Vulkan there is on a Mac. Everything else under
# libs/<rid> - glfw3, soft_oal, shaderc_shared and their peers - comes from the Silk.NET
# native packages and is put there by the targets in GK3Reborn.Host.csproj.
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

# FFmpeg is a versioned dependency, not "whatever is current". The binding is written
# against 7.1 and looks for that generation by name - avcodec-61, avformat-61, avutil-59,
# swscale-8, swresample-5. BtbN's rolling "latest" tag has moved on to 8.1 and 9.0, whose
# libraries are called something else, so the pin is an archived autobuild rather than a
# moving target. See docs/formats/video.md.
ffmpeg_tag=autobuild-2024-09-30-15-36
ffmpeg_base=https://github.com/BtbN/FFmpeg-Builds/releases/download/$ffmpeg_tag

case "$rid" in
    win-x64)
        url=$ffmpeg_base/ffmpeg-n7.1-win64-lgpl-shared-7.1.zip
        sha=8d465b17e2ac84b529b584dd1f8c9bd06b49a221231af38e7e4d4b7d23aec222
        want="avcodec-61.dll avdevice-61.dll avfilter-10.dll avformat-61.dll avutil-59.dll swresample-5.dll swscale-8.dll"
        ;;
    linux-x64)
        url=$ffmpeg_base/ffmpeg-n7.1-linux64-lgpl-shared-7.1.tar.xz
        sha=c9e8b980a81b693f2186a9b3d38d69318773ca0f8955fadd8e3557c9f4ca5ba3
        want="libavcodec.so.61 libavdevice.so.61 libavfilter.so.10 libavformat.so.61 libavutil.so.59 libswresample.so.5 libswscale.so.8"
        ;;
    osx-arm64)
        # No FFmpeg: nobody publishes a 7.1 shared build for Apple silicon, so a Mac plays
        # the game without its cutscenes unless the machine has its own. That is a
        # supported state - see GK3R1160 - rather than a failure.
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
# directory that only shows up as a cutscene not playing.
mkdir -p "$target"
failed=0
for name in $want; do
    found="$(find "$work/x" -type f -name "$name" | head -1)"

    if [ -z "$found" ]; then
        # A Linux build ships the soname as a symlink to a fully versioned file -
        # libavcodec.so.61 pointing at libavcodec.so.61.19.100 - and a filesystem without
        # symlinks cannot unpack that at all. Take the real file and give it the name the
        # loader looks for, which is the soname either way and is what ends up shipped.
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
