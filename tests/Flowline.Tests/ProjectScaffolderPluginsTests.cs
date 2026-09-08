using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Spectre.Console.Testing;

namespace Flowline.Tests;

/// <summary>
/// Covers the actual write <c>scaffold plugins</c> and <c>clone</c>/<c>init</c> share —
/// <see cref="ProjectScaffolder.ScaffoldPluginsProjectAsync"/> — which runs the real <c>pac plugin init</c>
/// and <c>dotnet add package</c>/<c>dotnet sln add</c> subprocesses. <see cref="ScaffoldCommandTests"/> covers
/// every decision the command makes before reaching this leaf, using fixtures that never invoke them.
/// </summary>
/// <remarks>
/// CI has no <c>pac</c> CLI (no setup step in <c>.github/workflows/ci.yml</c>, unlike <c>dotnet</c>), so
/// every test that runs <c>pac plugin init</c> for real is <c>[Fact(Skip = ...)]</c> — the same convention
/// <c>DataverseConnectorTests</c> uses for its own CI-incompatible real calls. Run these locally (verified:
/// they pass with pac 2.11.2) before trusting a change to the write path.
/// </remarks>
public class ProjectScaffolderPluginsTests
{
    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static (ProjectScaffolder Scaffolder, TestConsole Console) MakeScaffolder()
    {
        var console = new TestConsole();
        var capture = new SubprocessCapture(console);
        return (new ProjectScaffolder(console, capture), console);
    }

    /// <summary>Covers R29. Next to a solution file, the project is named after it and added to it —
    /// mirroring what <c>ScaffoldWebResourcesProjectAsync</c> does for WebResources.</summary>
    [Fact(Skip = "Requires the pac CLI on PATH — not available in CI; run locally to verify the real write")]
    public async Task ScaffoldPluginsProjectAsync_WithASolutionFile_WritesTheProjectAndAddsItToTheSolutionFile()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = Path.Combine(root, "Contoso.slnx");
            File.WriteAllText(slnPath, "<Solution />");
            var (scaffolder, console) = MakeScaffolder();
            var pluginsFolder = Path.Combine(root, "Plugins");

            await scaffolder.ScaffoldPluginsProjectAsync(pluginsFolder, "Contoso.Plugins.csproj", slnPath, CancellationToken.None);

            var csproj = Path.Combine(pluginsFolder, "Contoso.Plugins.csproj");
            File.Exists(csproj).Should().BeTrue();
            var xml = File.ReadAllText(csproj);
            xml.Should().Contain("Flowline.Attributes");
            xml.Should().Contain("MinVer");
            File.ReadAllText(slnPath).Should().Contain("Contoso.Plugins.csproj");
            console.Output.Should().Contain("Plugins project ready");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers R29/KTD7. With no solution file, the project lands under the generic name and
    /// nothing is registered — mirroring the standalone WebResources scaffold.</summary>
    [Fact(Skip = "Requires the pac CLI on PATH — not available in CI; run locally to verify the real write")]
    public async Task ScaffoldPluginsProjectAsync_WithNoSolutionFile_WritesTheProjectUnderTheGenericName()
    {
        var root = CreateTempRoot();
        try
        {
            var (scaffolder, _) = MakeScaffolder();
            var pluginsFolder = Path.Combine(root, "Plugins");

            await scaffolder.ScaffoldPluginsProjectAsync(pluginsFolder, ProjectScaffolder.StandalonePluginsProjectFileName, slnFilePath: null, CancellationToken.None);

            File.Exists(Path.Combine(pluginsFolder, "Plugins.csproj")).Should().BeTrue();
            Directory.EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly).Should().BeEmpty();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers R29. <c>--name</c> writes a second plugin project alongside an existing one, named
    /// and added exactly as asked.</summary>
    [Fact(Skip = "Requires the pac CLI on PATH — not available in CI; run locally to verify the real write")]
    public async Task ScaffoldPluginsProjectAsync_WithAName_WritesASecondProjectAndAddsItToTheSolutionFile()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = Path.Combine(root, "Contoso.slnx");
            File.WriteAllText(slnPath, "<Solution />");
            var (scaffolder, _) = MakeScaffolder();
            var extraFolder = Path.Combine(root, "Extra");

            await scaffolder.ScaffoldPluginsProjectAsync(extraFolder, "Extra.csproj", slnPath, CancellationToken.None);

            File.Exists(Path.Combine(extraFolder, "Extra.csproj")).Should().BeTrue();
            File.ReadAllText(slnPath).Should().Contain("Extra.csproj");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Covers the split in ProjectScaffolder: <c>SetupPluginsProjectAsync</c> (what <c>clone</c>/
    /// <c>init</c> call) still refuses an empty <c>Plugins</c> folder collision exactly as before the leaf
    /// was extracted — this behaviour was not meant to change (U10 step 2).</summary>
    [Fact]
    public async Task SetupPluginsProjectAsync_WithAnEmptyPluginsFolder_StillRefusesWithConfigInvalid()
    {
        var root = CreateTempRoot();
        try
        {
            var slnPath = Path.Combine(root, "Contoso.slnx");
            // A syntactically valid, empty solution file — via the writer rather than hand-written XML, so
            // SolutionFileLayout.LoadAsync (backed by the real MSBuild solution serializer) can parse it.
            // The entry itself is never read: PluginProjects only resolves .csproj entries, and this one
            // is a .cdsproj that need not exist on disk.
            await new MsBuildSolutionWriter().AddProjectAsync(slnPath, Path.Combine("Solution", "Contoso.cdsproj"), CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(root, "Plugins"));
            var layout = await SolutionFileLayout.LoadAsync(root, CancellationToken.None);
            var (scaffolder, _) = MakeScaffolder();

            var act = async () => await scaffolder.SetupPluginsProjectAsync(root, slnPath, "Contoso", layout, CancellationToken.None);

            (await act.Should().ThrowAsync<FlowlineException>())
                .Where(e => e.ExitCode == ExitCode.ConfigInvalid)
                .And.Message.Should().Contain("Plugins");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
