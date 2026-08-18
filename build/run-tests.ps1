#!/usr/bin/env pwsh
# Runs every test project.
#
# The test assemblies are Microsoft.Testing.Platform applications, so they are
# executed directly. `dotnet test` is deliberately not used: on SDK 10.0.302 its
# MTP driver reports "Zero tests ran" (exit code 5) for these xunit.v3 4.0.0
# projects, while the same assemblies discover and pass every test when run
# directly. Revisit when either component updates.
param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet build GK3Reborn.slnx -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$failed = 0
foreach ($project in Get-ChildItem -Directory tests) {
    $dll = Join-Path $project.FullName "bin/$Configuration/net10.0/$($project.Name).dll"
    if (-not (Test-Path $dll)) {
        Write-Host "missing: $dll"
        $failed = 1
        continue
    }

    Write-Host "=== $($project.Name) ==="
    dotnet exec $dll
    if ($LASTEXITCODE -ne 0) { $failed = 1 }
}

exit $failed
