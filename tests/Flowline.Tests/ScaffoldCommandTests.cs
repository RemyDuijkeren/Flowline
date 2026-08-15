using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Diagnostics;

namespace Flowline.Tests;

/// <summary>
/// Covers the two decisions <c>flowline scaffold</c> makes before it writes anything: whether the part is
/// one it can write, and which mode the folder gets. Both are static and side-effect free, so every branch
/// is reachable without constructing the command or a console.
/// </summary>
public class ScaffoldCommandTests
{
    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void WriteFlowlineConfig(string folder) =>
        File.WriteAllText(Path.Combine(folder, ".flowline"), "{}");

    static void WriteSolutionFile(string folder) =>
        File.WriteAllText(Path.Combine(folder, "TestSolution.slnx"), "<Solution />");

    [Theory]
    [InlineData("webresources")]
    [InlineData("WebResources")]
    [InlineData("WEBRESOURCES")]
    public void ValidatePart_AcceptsWebResources_RegardlessOfCasing(string part)
    {
        var act = () => ScaffoldCommand.ValidatePart(part);

        act.Should().NotThrow();
    }

    /// <summary>Covers AE5. The error names what is accepted, because that is how an agent reading a failed
    /// run discovers the vocabulary.</summary>
    [Fact]
    public void ValidatePart_RejectsAnUnknownPart_NamingWhatIsAccepted()
    {
        var act = () => ScaffoldCommand.ValidatePart("plugins");

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .And.Message.Should().Contain("webresources");
    }

    [Fact]
    public void ResolveTarget_WithNeitherMarker_IsStandalone()
    {
        var root = CreateTempRoot();
        try
        {
            var target = ScaffoldCommand.ResolveTarget(root);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Standalone);
            target.Folder.Should().Be(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveTarget_WithBothMarkers_IsProjectMode()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFlowlineConfig(root);
            WriteSolutionFile(root);

            var target = ScaffoldCommand.ResolveTarget(root);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Project);
            target.Folder.Should().Be(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE9. A solution file alone does not make a Flowline project — <c>.flowline</c> does,
    /// and it is the marker every other command walks upward to find.</summary>
    [Fact]
    public void ResolveTarget_WithASolutionFileButNoConfig_IsStandalone()
    {
        var root = CreateTempRoot();
        try
        {
            WriteSolutionFile(root);

            var target = ScaffoldCommand.ResolveTarget(root);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Standalone);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE8. Half a project fails rather than quietly writing a generically-named project
    /// into a repo that expected a solution-named one.</summary>
    [Fact]
    public void ResolveTarget_WithAConfigButNoSolutionFile_FailsNamingTheMissingSolutionFile()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFlowlineConfig(root);

            var act = () => ScaffoldCommand.ResolveTarget(root);

            act.Should().Throw<FlowlineException>()
               .Where(e => e.ExitCode == ExitCode.NotFound)
               .And.Message.Should().Contain(".slnx");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers AE6. The project marker resolves upward, so a run from inside a project lands in
    /// project mode and writes into the project root — not beside the caller.</summary>
    [Fact]
    public void ResolveTarget_FromASubdirectoryOfAProject_ResolvesToTheProjectRoot()
    {
        var root = CreateTempRoot();
        try
        {
            WriteFlowlineConfig(root);
            WriteSolutionFile(root);
            var nested = Path.Combine(root, "Plugins", "Handlers");
            Directory.CreateDirectory(nested);

            var target = ScaffoldCommand.ResolveTarget(nested);

            target.Mode.Should().Be(ScaffoldCommand.ScaffoldMode.Project);
            target.Folder.Should().Be(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
