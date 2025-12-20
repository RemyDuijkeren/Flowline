dotnet pack
dotnet tool uninstall -g Flowline
dotnet tool install -g Flowline --add-source ./artifacts/nupkg --prerelease
