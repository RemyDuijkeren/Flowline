#!/usr/bin/env bash
# Linux/macOS counterpart to reinstall-flowline.ps1. Keep the two in step.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

# Clear stale packages first: MinVer versions each pack by git commit height since the last tag
# on whatever commit is currently checked out, not by build time. Old .nupkg files left in
# artifacts/nupkg from a different branch/commit (e.g. one with more commits since the last tag)
# can carry a HIGHER version number than the one you just built, even though it's older -- and
# 'dotnet tool install' with no --version picks the highest version across every source,
# silently reinstalling that stale build instead of the fresh one below.
rm -f ./artifacts/nupkg/*.nupkg ./artifacts/nupkg/*.snupkg

# Force a clean recompile before packing. Plain 'dotnet pack' reuses whatever the incremental
# up-to-date check considers current, and that has shipped a STALE Release binary when the source
# changed but MSBuild's timestamp check decided not to recompile -- you then install a tool that
# silently lacks your latest edit. --no-incremental on an explicit Release build guarantees fresh
# IL; 'pack --no-build' then just zips that output (pack defaults to Release, matching the build).
dotnet build src/Flowline/Flowline.csproj -c Release --no-restore --no-incremental
dotnet pack src/Flowline/Flowline.csproj -c Release --no-build

# Not installed yet is a normal first run, so a failure here is not fatal.
dotnet tool uninstall -g Flowline >/dev/null 2>&1 || true

dotnet tool install -g Flowline \
    --source ./artifacts/nupkg \
    --source https://api.nuget.org/v3/index.json \
    --prerelease
