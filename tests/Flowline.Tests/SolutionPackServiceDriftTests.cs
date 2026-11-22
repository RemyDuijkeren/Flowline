using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Solutions;
using Flowline.Services;
using Spectre.Console.Testing;

namespace Flowline.Tests;

// The drift guard pack and deploy share: build output that differs from the committed solution stops the run
// unless --force drift is passed, and the warnings name what differs either way.
public class SolutionPackServiceDriftTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"PackDrift_{Guid.NewGuid():N}");
    string SolutionFolder => Path.Combine(_root, "Solution");

    public SolutionPackServiceDriftTests()
    {
        // Forward slashes: the .slnx Path attribute is raw text, and a backslash is no separator on Linux.
        Write("WebResources/WebResources.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("Test.slnx", """<Solution><Project Path="WebResources/WebResources.csproj" /></Solution>""");
        Write("Solution/src/WebResources/cr_Contoso/existing.js", "same");
        Write("WebResources/dist/existing.js", "same");
        Write("WebResources/dist/new.js", "only built locally");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ValidateDrift_FailsWithPackMessage_WhenBuildOutputIsOnlyLocal()
    {
        var console = new TestConsole();
        var layout = await SolutionFileLayout.LoadAsync(_root);

        var act = () => SolutionPackService.ValidateDriftAsync(console, forceDrift: false, "Contoso", layout, SolutionFolder, "pack", CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message == SolutionPackService.BuildDriftMessage("pack"));
        console.Output.Should().Contain("Only local: new.js");
    }

    [Fact]
    public async Task ValidateDrift_WarnsButContinues_WhenDriftIsForced()
    {
        var console = new TestConsole();
        var layout = await SolutionFileLayout.LoadAsync(_root);

        var act = () => SolutionPackService.ValidateDriftAsync(console, forceDrift: true, "Contoso", layout, SolutionFolder, "deploy", CancellationToken.None);

        await act.Should().NotThrowAsync();
        console.Output.Should().Contain("Only local: new.js");
    }

    [Fact]
    public async Task ValidateDrift_Passes_WhenBuildOutputMatchesTheCommittedSolution()
    {
        File.Delete(Path.Combine(_root, "WebResources", "dist", "new.js"));
        var layout = await SolutionFileLayout.LoadAsync(_root);

        var act = () => SolutionPackService.ValidateDriftAsync(new TestConsole(), forceDrift: false, "Contoso", layout, SolutionFolder, "pack", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
