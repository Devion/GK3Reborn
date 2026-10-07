# Fetches the official Khronos OpenXR desktop loader. Only needed for Windows VR.
$ErrorActionPreference = 'Stop'
$version = '1.1.63'
$sha256 = '4e5a50a8807ef66f25180ff224e7d8150b594aa8ee4b07590f9ade55a8e98703'
$repo = Split-Path $PSScriptRoot
$cache = Join-Path $repo 'obj/openxr-loader'
New-Item -ItemType Directory -Force $cache | Out-Null
$archive = Join-Path $cache "OpenXR.Loader.$version.zip"
Invoke-WebRequest "https://github.com/KhronosGroup/OpenXR-SDK-Source/releases/download/release-$version/OpenXR.Loader.$version.nupkg" -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) { throw 'OpenXR loader checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
$destination = Join-Path $repo 'libs/win-x64'
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $cache 'native/x64/release/bin/openxr_loader.dll') -Destination $destination
Write-Host "Installed Khronos OpenXR loader $version in $destination"
