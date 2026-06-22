dotnet pack --no-restore
dotnet tool uninstall -g Flowline 2>$null
dotnet tool install -g Flowline --source ./artifacts/nupkg --source https://api.nuget.org/v3/index.json --prerelease
