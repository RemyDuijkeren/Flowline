# Clear stale packages first: MinVer versions each pack by git commit height since the last tag
# on whatever commit is currently checked out, not by build time. Old .nupkg files left in
# artifacts/nupkg from a different branch/commit (e.g. one with more commits since the last tag)
# can carry a HIGHER version number than the one you just built, even though it's older -- and
# 'dotnet tool install' with no --version picks the highest version across every source,
# silently reinstalling that stale build instead of the fresh one below.
Remove-Item ./artifacts/nupkg/*.nupkg, ./artifacts/nupkg/*.snupkg -ErrorAction SilentlyContinue

dotnet pack --no-restore
dotnet tool uninstall -g Flowline 2>$null
dotnet tool install -g Flowline --source ./artifacts/nupkg --source https://api.nuget.org/v3/index.json --prerelease
