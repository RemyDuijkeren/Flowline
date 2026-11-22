using FluentAssertions;
using Flowline.Core;
using Flowline.Diagnostics;
using Flowline.Services;
using Spectre.Console.Testing;

namespace Flowline.Tests;

// Packing reads the committed solution, not the folder on disk: these run against a real temp git repo.
public class SolutionPackServiceStagingTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"PackStaging_{Guid.NewGuid():N}");
    readonly List<string> _staged = [];
    string SolutionFolder => Path.Combine(_root, "Solution");
    string Src => Path.Combine(SolutionFolder, "src");

    public SolutionPackServiceStagingTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
        Write("Solution/src/Other/Solution.xml", "<ImportExportXml />");
    }

    public void Dispose()
    {
        foreach (var dir in _staged.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task StageTrackedSolution_LeavesIgnoredFileInSlotFolderOut()
    {
        Write("Solution/src/PluginAssemblies/ContosoPlugins-1/ContosoPlugins.dll", "committed");
        Write("Solution/src/PluginAssemblies/ContosoPlugins-1/ContosoPlugins.dll.data.xml", AssemblyDataXml("Contoso.Plugins"));
        Write(".gitignore", "*.pdb\n");
        Commit();
        Write("Solution/src/PluginAssemblies/ContosoPlugins-1/ContosoPlugins.pdb", "stray local build");

        var staged = await Stage();

        File.ReadAllText(Path.Combine(staged, "PluginAssemblies/ContosoPlugins-1/ContosoPlugins.dll")).Should().Be("committed");
        File.Exists(Path.Combine(staged, "PluginAssemblies/ContosoPlugins-1/ContosoPlugins.pdb")).Should().BeFalse();
        File.Exists(Path.Combine(staged, "Other/Solution.xml")).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureSlotsHaveBinaries_FailsNamingTheSlot_WhenBinaryIsGitignored()
    {
        Write("Solution/src/PluginAssemblies/ContosoPlugins-1/ContosoPlugins.dll.data.xml", AssemblyDataXml("Contoso.Plugins"));
        Write(".gitignore", "Solution/src/PluginAssemblies/**/*.dll\n");
        Commit();
        // Present on disk (a local pull put it there) but ignored, so not part of the commit.
        Write("Solution/src/PluginAssemblies/ContosoPlugins-1/ContosoPlugins.dll", "local only");

        var staged = await Stage();
        var act = () => SolutionPackService.EnsureSlotsHaveBinaries(staged);

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                        && e.Message.Contains("Contoso.Plugins")
                        && e.Message.Contains("--binaries-from-build"));
    }

    [Fact]
    public async Task EnsureSlotsHaveBinaries_FailsTheSame_WhenBinaryIsAbsentEverywhere()
    {
        Write("Solution/src/WebResources/cr_Sales/main.js.data.xml", "<WebResource><Name>cr_Sales/main.js</Name></WebResource>");
        Commit();

        var staged = await Stage();
        var act = () => SolutionPackService.EnsureSlotsHaveBinaries(staged);

        act.Should().Throw<FlowlineException>().Where(e => e.Message.Contains("cr_Sales/main.js"));
    }

    [Fact]
    public async Task EnsureSlotsHaveBinaries_Passes_ForPluginPackageWithCommittedNupkg()
    {
        Write("Solution/src/pluginpackages/cr_Contoso.Plugins/pluginpackage.xml", """<pluginpackage uniquename="cr_Contoso.Plugins" />""");
        Write("Solution/src/pluginpackages/cr_Contoso.Plugins/package/cr_Contoso.Plugins.nupkg", "nupkg");
        Write("Solution/src/WebResources/cr_Sales/main.js", "js");
        Write("Solution/src/WebResources/cr_Sales/main.js.data.xml", "<WebResource><Name>cr_Sales/main.js</Name></WebResource>");
        Commit();

        var staged = await Stage();

        var act = () => SolutionPackService.EnsureSlotsHaveBinaries(staged);
        act.Should().NotThrow();
    }

    async Task<string> Stage()
    {
        var staged = await SolutionPackService.StageTrackedSolutionAsync(SolutionFolder, _root, new SubprocessCapture(new TestConsole()), CancellationToken.None);
        _staged.Add(staged);
        return staged;
    }

    static string AssemblyDataXml(string simpleName) =>
        $"""<PluginAssembly FullName="{simpleName}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" />""";

    void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    void Commit()
    {
        RunGit("add", "-A");
        RunGit("commit", "-m", "snapshot");
    }

    void RunGit(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }
}
