#!/usr/bin/env pwsh
# Asset-backed state audit. Requires a complete local import of the licensed game.
param(
    [string]$ContentRoot = (Join-Path $PSScriptRoot '../../ContentWorkspace/normalized'),
    [string]$Configuration = 'Debug',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$resolvedContent = (Resolve-Path -LiteralPath $ContentRoot).Path
foreach ($folder in @('scripts', 'scripts-disassembled', 'actions', 'scenes')) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedContent $folder) -PathType Container)) {
        throw "Missing content folder: $folder. Import the complete game before validating."
    }
}
$previousContent = $env:GK3_NORMALIZED_CONTENT
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $env:GK3_NORMALIZED_CONTENT = $resolvedContent
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    python tools/audit-story-state.py $resolvedContent artifacts/story-state-audit.json --check
    if ($LASTEXITCODE -ne 0) { throw 'Story-state audit found unreviewed candidates or incomplete content.' }
    $buildArgs = @('build', 'tests/GK3Reborn.Tests/GK3Reborn.Tests.csproj', '-c', $Configuration, '--nologo', '-v', 'q')
    if ($NoRestore) { $buildArgs += '--no-restore' }
    dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    dotnet exec "tests/GK3Reborn.Tests/bin/$Configuration/net10.0/GK3Reborn.Tests.dll" -class 'GK3Reborn.Tests.Game.*'
    if ($LASTEXITCODE -ne 0) { throw 'Gameplay regression tests failed.' }
    Write-Host 'Story validation passed against the reviewed baseline. See docs/script-state-audit.txt for unresolved findings.'
}
finally {
    $env:GK3_NORMALIZED_CONTENT = $previousContent
    Pop-Location
}
