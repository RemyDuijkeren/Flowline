# Clear stale packages first: MinVer versions each pack by git commit height since the last tag
# on whatever commit is currently checked out, not by build time. Old .nupkg files left in
# artifacts/nupkg from a different branch/commit (e.g. one with more commits since the last tag)
# can carry a HIGHER version number than the one you just built, even though it's older -- and
# 'dotnet tool install' with no --version picks the highest version across every source,
# silently reinstalling that stale build instead of the fresh one below.
Remove-Item ./artifacts/nupkg/*.nupkg, ./artifacts/nupkg/*.snupkg -ErrorAction SilentlyContinue

# Force a clean recompile before packing. Plain 'dotnet pack' reuses whatever the incremental
# up-to-date check considers current, and that has shipped a STALE Release binary when the source
# changed but MSBuild's timestamp check decided not to recompile -- you then install a tool that
# silently lacks your latest edit. --no-incremental on an explicit Release build guarantees fresh
# IL; 'pack --no-build' then just zips that output (pack defaults to Release, matching the build).
dotnet build -c Release --no-restore --no-incremental
dotnet pack -c Release --no-build
dotnet tool uninstall -g Flowline 2>$null
dotnet tool install -g Flowline --source ./artifacts/nupkg --source https://api.nuget.org/v3/index.json --prerelease
