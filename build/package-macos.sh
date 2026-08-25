#!/usr/bin/env bash
# Builds GK3Reborn.pkg, the macOS installer package, from a published GK3Reborn.app.
#
# The bundle itself is made by the publish, not here: FolderProfileMac lays out
# publishmac/GK3Reborn.app and RebornWriteMacAppBundle in GK3Reborn.Host.csproj writes its
# Info.plist. That half is plain file layout and runs on any machine. This half cannot:
# every tool it needs - codesign, pkgbuild, productbuild, sips, iconutil - ships with macOS
# and exists nowhere else.
#
# Signing is not optional on Apple silicon. An arm64 executable with no signature at all is
# killed by the kernel on launch, so a bundle published from Windows or Linux will not run
# until it has been through this script once. The default is an ad-hoc signature, which is
# enough to make it run on the machine that made it. Handing it to anybody else needs a
# real Developer ID and notarisation: --sign-app, --sign-pkg, --notarize.
#
#   ./build/package-macos.sh --publish
#   ./build/package-macos.sh --sign-app "Developer ID Application: ..." \
#                            --sign-pkg "Developer ID Installer: ..." \
#                            --notarize my-keychain-profile
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

app=""
out="$root/artifacts/macos"
identifier="org.gk3reborn.game"
install_location="/Applications"
title="GK3Reborn"
version=""
sign_app="-"
sign_pkg=""
notarize=""
publish=0
keep_stage=0

usage() {
    cat <<'USAGE'
Usage: build/package-macos.sh [options]

  --publish              Run dotnet publish -p:PublishProfile=FolderProfileMac first.
  --app PATH             The .app to package. Default: the publish profile's output.
  --out DIR              Where to write the package. Default: artifacts/macos.
  --identifier ID        Package identifier. Default: org.gk3reborn.game.
  --install-location DIR Where the .app is installed. Default: /Applications.
  --version V            Overrides the version read from the bundle's Info.plist.
  --sign-app IDENTITY    Codesign identity for the app. Default: "-", ad hoc.
  --sign-pkg IDENTITY    Developer ID Installer identity for the package. Default: unsigned.
  --notarize PROFILE     notarytool keychain profile; submits, waits and staples.
  --keep-stage           Leave the intermediate files behind for inspection.
  -h, --help             This.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --publish)          publish=1 ;;
        --app)              app="$2"; shift ;;
        --out)              out="$2"; shift ;;
        --identifier)       identifier="$2"; shift ;;
        --install-location) install_location="$2"; shift ;;
        --version)          version="$2"; shift ;;
        --sign-app)         sign_app="$2"; shift ;;
        --sign-pkg)         sign_pkg="$2"; shift ;;
        --notarize)         notarize="$2"; shift ;;
        --keep-stage)       keep_stage=1 ;;
        -h|--help)          usage; exit 0 ;;
        *)                  echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

if [ "$(uname -s)" != "Darwin" ]; then
    cat >&2 <<'WRONG'
package-macos.sh needs macOS. codesign, pkgbuild and productbuild are part of the operating
system and have no equivalent elsewhere.

The .app itself does build anywhere:

    dotnet publish src/GK3Reborn.Host -p:PublishProfile=FolderProfileMac

Copy the resulting publishmac/GK3Reborn.app to a Mac - as an archive, so that nothing is
lost on the way - and run this script there with --app.
WRONG
    exit 1
fi

for tool in codesign pkgbuild productbuild plutil ditto; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "missing: $tool. Install the command line tools: xcode-select --install" >&2
        exit 1
    fi
done

if [ "$publish" -eq 1 ]; then
    echo "==> dotnet publish"
    dotnet publish "$root/src/GK3Reborn.Host" -p:PublishProfile=FolderProfileMac --nologo
fi

if [ -z "$app" ]; then
    app="$root/src/GK3Reborn.Host/publishmac/GK3Reborn.app"
fi

if [ ! -d "$app" ]; then
    echo "no bundle at $app. Pass --publish to build one, or --app to point at one." >&2
    exit 1
fi

app="$(cd "$app" && pwd)"
contents="$app/Contents"
plist="$contents/Info.plist"

if [ ! -f "$plist" ]; then
    echo "$app has no Contents/Info.plist; it is not a bundle." >&2
    exit 1
fi

executable="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$plist")"
minimum_system="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$plist")"

if [ -z "$version" ]; then
    version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$plist")"
fi

if [ ! -f "$contents/MacOS/$executable" ]; then
    echo "$app declares CFBundleExecutable $executable, which is not there." >&2
    exit 1
fi

echo "==> $app"
echo "    $executable $version, macOS $minimum_system and later"

# ---------------------------------------------------------------------------------------
# Icon. The publish carries a PNG into the bundle and stops there, because iconutil is
# only here. An .icns already in place is left alone, so a hand-made one wins.
# ---------------------------------------------------------------------------------------

icon_png="$contents/Resources/AppIcon.png"
icon_icns="$contents/Resources/AppIcon.icns"

if [ -f "$icon_png" ] && [ ! -f "$icon_icns" ] && command -v iconutil >/dev/null 2>&1; then
    echo "==> icon"
    iconset_root="$(mktemp -d)"
    iconset="$iconset_root/AppIcon.iconset"
    mkdir -p "$iconset"

    # The names are fixed by iconutil and every one of them has to be there, or it refuses
    # the set. 512x512@2x is the 1024 the Finder actually shows on a Retina display.
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" "$icon_png" \
             --out "$iconset/icon_${size}x${size}.png" >/dev/null
        sips -z $((size * 2)) $((size * 2)) "$icon_png" \
             --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
    done

    iconutil --convert icns "$iconset" --output "$icon_icns"
    rm -rf "$iconset_root"
    rm -f "$icon_png"
fi

# ---------------------------------------------------------------------------------------
# Signing, inside out. A bundle's signature seals the files under it, so anything nested is
# signed first and re-signing one afterwards breaks the seal. --deep would do it in one
# call, and Apple deprecated it for the reason it looks convenient: it signs nested code
# with whatever identity and entitlements happen to be on the command line.
# ---------------------------------------------------------------------------------------

echo "==> signing as $sign_app"

# A bundle that has been through a zip, a download or a Windows share carries extended
# attributes that codesign will not seal over.
xattr -cr "$app"

# Published from a filesystem with no permission bits, the executable arrives without its.
chmod +x "$contents/MacOS/$executable"

entitlements="$root/build/macos/entitlements.plist"
hardened=()

if [ "$sign_app" != "-" ]; then
    # The hardened runtime is a precondition of notarisation and is what makes the
    # entitlements mean anything. Left off for an ad-hoc signature, which cannot be
    # notarised and gains nothing from being restricted.
    hardened+=(--options runtime --timestamp)

    if [ -f "$entitlements" ]; then
        hardened+=(--entitlements "$entitlements")
    fi
fi

while IFS= read -r -d '' library; do
    codesign --force --sign "$sign_app" ${hardened[@]+"${hardened[@]}"} "$library"
done < <(find "$contents" \( -name '*.dylib' -o -name '*.so' \) -type f -print0)

codesign --force --sign "$sign_app" ${hardened[@]+"${hardened[@]}"} "$app"
codesign --verify --strict --verbose=2 "$app"

# ---------------------------------------------------------------------------------------
# The package.
# ---------------------------------------------------------------------------------------

mkdir -p "$out"
stage="$out/stage"
rm -rf "$stage"
mkdir -p "$stage"

# pkgbuild maps the contents of --root onto --install-location, so the staging directory
# holds the bundle rather than being it. Copied with ditto, which keeps the extended
# attributes the signature lives in; cp -R drops them and the installed copy fails to
# verify on first launch.
ditto "$app" "$stage/$(basename "$app")"

component_pkg="$out/$title-component.pkg"
component_plist="$out/component.plist"

pkgbuild --analyze --root "$stage" "$component_plist" >/dev/null

# Relocatable is the default and is wrong for a game. It means the installer hunts for an
# existing copy of this bundle identifier anywhere on the disk and updates that instead, so
# a copy the player once dragged to an external drive silently becomes the install target.
plutil -replace 0.BundleIsRelocatable -bool false "$component_plist"

echo "==> pkgbuild"
pkgbuild --root "$stage" \
         --component-plist "$component_plist" \
         --identifier "$identifier" \
         --version "$version" \
         --install-location "$install_location" \
         "$component_pkg"

resources="$out/resources"
rm -rf "$resources"
mkdir -p "$resources"
cp "$root/build/macos/welcome.html" "$resources/welcome.html"
cp "$root/LICENSE" "$resources/license.txt"

distribution="$out/distribution.xml"
sed -e "s|@TITLE@|$title|g" \
    -e "s|@IDENTIFIER@|$identifier|g" \
    -e "s|@VERSION@|$version|g" \
    -e "s|@MINIMUM_SYSTEM@|$minimum_system|g" \
    -e "s|@COMPONENT@|$(basename "$component_pkg")|g" \
    "$root/build/macos/distribution.xml" > "$distribution"

product="$out/$title-$version.pkg"
unsigned_product="$product"

if [ -n "$sign_pkg" ]; then
    unsigned_product="$out/$title-$version-unsigned.pkg"
fi

echo "==> productbuild"
productbuild --distribution "$distribution" \
             --package-path "$out" \
             --resources "$resources" \
             "$unsigned_product"

if [ -n "$sign_pkg" ]; then
    echo "==> productsign as $sign_pkg"
    productsign --sign "$sign_pkg" "$unsigned_product" "$product"
    rm -f "$unsigned_product"
    pkgutil --check-signature "$product"
fi

if [ -n "$notarize" ]; then
    echo "==> notarytool"
    xcrun notarytool submit "$product" --keychain-profile "$notarize" --wait

    # Stapling the ticket onto the package is what lets it install on a machine that is
    # offline: without it Gatekeeper has to ask Apple, and a laptop with no network reads
    # the silence as a refusal.
    xcrun stapler staple "$product"
    xcrun stapler validate "$product"
fi

if [ "$keep_stage" -ne 1 ]; then
    rm -rf "$stage" "$resources" "$component_plist" "$component_pkg"
fi

echo
echo "$product"

if [ "$sign_app" = "-" ]; then
    cat <<'ADHOC'

Signed ad hoc, which is enough to run on this machine and nowhere else: macOS refuses to
open a downloaded copy of an ad-hoc package. Distributing it takes a Developer ID and
notarisation - see --sign-app, --sign-pkg and --notarize.
ADHOC
fi
