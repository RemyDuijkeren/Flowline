using FluentAssertions;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Spectre.Console.Testing;

namespace Flowline.Tests;

/// <summary>
/// Characterization coverage for U1's extraction of the WebResources template-writing core out of
/// <see cref="ProjectScaffolder.SetupWebResourcesProjectAsync"/>. <c>CloneCommandTests</c> /
/// <c>InitCommandTests</c> never exercise the template writes themselves — they hand-write stub
/// <c>.csproj</c> files and assert only solution-file registration — so this file is the only guard on
/// the refactor: every written template file must match its embedded manifest resource byte for byte,
/// both before and after the split, and the project file must land last on disk.
/// </summary>
public class ProjectScaffolderWebResourcesTests
{
    /// <summary>The eight embedded templates <c>SetupWebResourcesProjectAsync</c> writes, and where each
    /// lands relative to the WebResources folder. Mirrors the <c>EmbeddedResource</c> entries in
    /// <c>src/Flowline/Flowline.csproj</c>.</summary>
    static readonly (string LogicalName, string RelativePath)[] s_configAndDocFiles =
    [
        ("Flowline.Templates.WebResources.package.json", "package.json"),
        ("Flowline.Templates.WebResources.rollup.config.mjs", "rollup.config.mjs"),
        ("Flowline.Templates.WebResources.tsconfig.json", "tsconfig.json"),
        ("Flowline.Templates.WebResources.eslint.config.mjs", "eslint.config.mjs"),
        ("Flowline.Templates.WebResources.README.md", "README.md"),
    ];

    static readonly (string LogicalName, string RelativePath)[] s_srcFiles =
    [
        ("Flowline.Templates.WebResources.src.example.ts", Path.Combine("src", "example.ts")),
        ("Flowline.Templates.WebResources.src.example-js.js", Path.Combine("src", "example-js.js")),
    ];

    const string ProjectFileLogicalName = "Flowline.Templates.WebResources.WebResources.csproj";

    /// <summary>Every template file <c>SetupWebResourcesProjectAsync</c> writes, project file included.</summary>
    static IEnumerable<(string LogicalName, string RelativePath)> AllTemplateFiles(string projectFileName) =>
        s_configAndDocFiles.Concat(s_srcFiles).Append((ProjectFileLogicalName, projectFileName));

    static byte[] ReadEmbeddedResource(string logicalName)
    {
        using var stream = typeof(TemplateWriter).Assembly.GetManifestResourceStream(logicalName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Creates a real, empty solution file at <paramref name="root"/> — the fixture
    /// <c>SetupWebResourcesProjectAsync</c> needs, since it shells out to the real <c>dotnet sln add</c>
    /// once the template is written. A single unresolvable entry ("Dummy.csproj") is enough to make the
    /// writer create the file; <see cref="WebResourcesProjectResolver"/> filters non-existent candidates
    /// out, so it never taints mode detection.</summary>
    static async Task<string> CreateEmptySolutionFileAsync(string root, string solutionName)
    {
        var slnFilePath = Path.Combine(root, $"{solutionName}.slnx");
        await new MsBuildSolutionWriter().AddProjectAsync(slnFilePath, "Dummy.csproj");
        return slnFilePath;
    }

    /// <summary>
    /// A folder produced by a stand-alone <c>flowline scaffold webresources</c> holds the generic
    /// <c>WebResources.csproj</c>, not a solution-named one. A resolver that matched only the solution-named
    /// file reported "no project here", and <c>clone</c>/<c>init</c> then rewrote all eight templates through
    /// <see cref="TemplateWriter"/> — which truncates — destroying the user's edits. This pins the fix.
    /// </summary>
    [Fact]
    public async Task SetupWebResourcesProjectAsync_OverAStandaloneScaffold_LeavesItAndItsEditsAlone()
    {
        const string solutionName = "CrO7982";
        var root = CreateTempRoot();
        try
        {
            var slnFilePath = await CreateEmptySolutionFileAsync(root, solutionName);
            var layout = await SolutionFileLayout.LoadAsync(root);

            // What a stand-alone scaffold leaves behind, with one file the user then edited.
            var webresourcesFolder = Path.Combine(root, "WebResources");
            Directory.CreateDirectory(webresourcesFolder);
            File.WriteAllText(Path.Combine(webresourcesFolder, ProjectScaffolder.StandaloneWebResourcesProjectFileName), "<Project />");
            var edited = Path.Combine(webresourcesFolder, "package.json");
            File.WriteAllText(edited, "{ \"name\": \"my-edits\" }");
            var before = File.ReadAllBytes(edited);

            var console = new TestConsole();
            var scaffolder = new ProjectScaffolder(console, new SubprocessCapture(console));

            await scaffolder.SetupWebResourcesProjectAsync(root, slnFilePath, solutionName, layout, CancellationToken.None);

            File.ReadAllBytes(edited).Should().Equal(before, "clone/init must not rewrite a stand-alone scaffold's files");
            File.Exists(Path.Combine(webresourcesFolder, ProjectScaffolder.WebResourcesProjectFileName(solutionName)))
                .Should().BeFalse("no second, solution-named project should be written beside it");
            console.Output.Should().Contain("stand-alone scaffold", "the run must say why it left the folder alone");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── Step 1 — characterization: byte-identity, exercised through the real (unmodified) method ──

    [Fact]
    public async Task SetupWebResourcesProjectAsync_EachWrittenTemplateFile_MatchesEmbeddedResourceByteForByte()
    {
        const string solutionName = "CrO7982";
        var root = CreateTempRoot();
        try
        {
            var slnFilePath = await CreateEmptySolutionFileAsync(root, solutionName);
            var layout = await SolutionFileLayout.LoadAsync(root);

            var console = new TestConsole();
            var scaffolder = new ProjectScaffolder(console, new SubprocessCapture(console));

            await scaffolder.SetupWebResourcesProjectAsync(root, slnFilePath, solutionName, layout, CancellationToken.None);

            var webresourcesFolder = Path.Combine(root, "WebResources");
            var projectFileName = ProjectScaffolder.WebResourcesProjectFileName(solutionName);

            foreach (var (logicalName, relativePath) in AllTemplateFiles(projectFileName))
            {
                var targetPath = Path.Combine(webresourcesFolder, relativePath);
                File.Exists(targetPath).Should().BeTrue($"{relativePath} should have been written");

                var written = await File.ReadAllBytesAsync(targetPath);
                var embedded = ReadEmbeddedResource(logicalName);
                written.Should().Equal(embedded, $"{relativePath} must match its embedded resource byte for byte");
            }

            Directory.Exists(Path.Combine(webresourcesFolder, "src", "modules")).Should().BeTrue();
            Directory.Exists(Path.Combine(webresourcesFolder, "public")).Should().BeTrue();
            Directory.Exists(Path.Combine(webresourcesFolder, "dist")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Step 2 — the extracted core, reachable with no solution file and no layout ────────────

    [Fact]
    public async Task WriteWebResourcesTemplateAsync_NoSolutionFileNoLayout_ProducesTheSameFileSetTheOuterMethodDid()
    {
        // The whole point of KTD1: this is the leaf the standalone `scaffold` command (no .flowline, no
        // solution file) will call directly. Nothing here reads config or touches a solution file.
        var root = CreateTempRoot();
        try
        {
            const string projectFileName = "WebResources.csproj";
            var webresourcesFolder = Path.Combine(root, "WebResources");

            await ProjectScaffolder.WriteWebResourcesTemplateAsync(webresourcesFolder, projectFileName, CancellationToken.None);

            foreach (var (logicalName, relativePath) in AllTemplateFiles(projectFileName))
            {
                var targetPath = Path.Combine(webresourcesFolder, relativePath);
                File.Exists(targetPath).Should().BeTrue($"{relativePath} should have been written");
                (await File.ReadAllBytesAsync(targetPath)).Should().Equal(ReadEmbeddedResource(logicalName));
            }

            Directory.Exists(Path.Combine(webresourcesFolder, "src", "modules")).Should().BeTrue();
            Directory.Exists(Path.Combine(webresourcesFolder, "public")).Should().BeTrue();
            Directory.Exists(Path.Combine(webresourcesFolder, "dist")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Step 3 — the project file is written last (interrupted-run safety) ────────────────────
    // ResolveExistingWebResourcesFolder treats the project file's presence as the "already scaffolded"
    // marker, and R12 gives no overwrite flag. Written first, an interrupted scaffold would leave that
    // marker with the rest of the template missing, and every later run would refuse to finish it.

    [Fact]
    public async Task WriteWebResourcesTemplateAsync_ProjectFile_IsWrittenAfterEveryOtherTemplateFile()
    {
        var root = CreateTempRoot();
        try
        {
            const string projectFileName = "WebResources.csproj";
            var webresourcesFolder = Path.Combine(root, "WebResources");

            await ProjectScaffolder.WriteWebResourcesTemplateAsync(webresourcesFolder, projectFileName, CancellationToken.None);

            var projectFileWriteTime = File.GetLastWriteTimeUtc(Path.Combine(webresourcesFolder, projectFileName));

            foreach (var (_, relativePath) in s_configAndDocFiles.Concat(s_srcFiles))
            {
                var otherWriteTime = File.GetLastWriteTimeUtc(Path.Combine(webresourcesFolder, relativePath));
                otherWriteTime.Should().BeOnOrBefore(projectFileWriteTime,
                    $"{relativePath} must be written before the project file, so an interrupted run leaves no presence marker");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteWebResourcesTemplateAsync_WhenTheProjectFileWriteFails_EveryOtherTemplateFileIsAlreadyOnDisk()
    {
        // Deterministic stand-in for an interruption exactly at the last write: pre-occupying the project
        // file's target path with a stray directory makes TemplateWriter's File.Create fail specifically
        // there, at whatever point write order puts it. Every other template file and folder having already
        // landed by the time that failure happens is what proves the project file really is written last.
        var root = CreateTempRoot();
        try
        {
            const string projectFileName = "WebResources.csproj";
            var webresourcesFolder = Path.Combine(root, "WebResources");
            Directory.CreateDirectory(Path.Combine(webresourcesFolder, projectFileName));

            var act = () => ProjectScaffolder.WriteWebResourcesTemplateAsync(webresourcesFolder, projectFileName, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>("File.Create refuses a path a directory already occupies");

            foreach (var (_, relativePath) in s_configAndDocFiles.Concat(s_srcFiles))
                File.Exists(Path.Combine(webresourcesFolder, relativePath)).Should().BeTrue($"{relativePath} must already be written by the time the project file write is reached");

            Directory.Exists(Path.Combine(webresourcesFolder, "src", "modules")).Should().BeTrue();
            Directory.Exists(Path.Combine(webresourcesFolder, "public")).Should().BeTrue();
            Directory.Exists(Path.Combine(webresourcesFolder, "dist")).Should().BeTrue();

            // File.Exists is false for a path a directory occupies -- so from ResolveExistingWebResourcesFolder's
            // point of view, the "already scaffolded" marker never appeared.
            File.Exists(Path.Combine(webresourcesFolder, projectFileName)).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
