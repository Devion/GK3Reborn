#!/usr/bin/env bash
# Runs every test project.
#
# The test assemblies are Microsoft.Testing.Platform applications, so they are
# executed directly. `dotnet test` is deliberately not used: on SDK 10.0.302 its
# MTP driver reports "Zero tests ran" (exit code 5) for these xunit.v3 4.0.0
# projects, while the same assemblies discover and pass every test when run
# directly. Revisit when either component updates.
set -uo pipefail

cd "$(dirname "$0")/.."
configuration="${1:-Debug}"

dotnet build GK3Reborn.slnx -c "$configuration" --nologo -v q || exit 1

failed=0
for project in tests/*/; do
    name="$(basename "$project")"
    dll="${project}bin/$configuration/net10.0/$name.dll"
    if [ ! -f "$dll" ]; then
        echo "missing: $dll"
        failed=1
        continue
    fi

    echo "=== $name ==="
    dotnet exec "$dll" || failed=1
done

exit "$failed"
