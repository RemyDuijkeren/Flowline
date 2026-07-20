using Flowline.Core;
using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class MsBuildSolutionWriterTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly MsBuildSolutionWriter _writer = new();
    readonly MsBuildSolutionReader _reader = new();

    public MsBuildSolutionWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The C# project type GUID. A .sln entry is only loadable by MSBuild if it carries this.</summary>
    const string CSharpTypeGuid = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}";

    /// <summary>The legacy C# project type GUID, which older .sln writers emit instead.</summary>
    const string LegacyCSharpTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    string Path_(string relative) => Path.Combine(_root, relative);

    string Write(string fileName, string content)
    {
        var path = Path_(fileName);
        File.WriteAllText(path, content);
        return path;
    }

    static string Sep(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public async Task AddProjectAsync_CdsprojIntoNewSlnx_WritesProjectElementWithExplicitType()
    {
        var solution = Path_("DWE_Base.slnx");

        var added = (await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj")).Added;

        added.Should().BeTrue();
        var xml = await File.ReadAllTextAsync(solution);
        xml.Should().Contain("DWE_Base.cdsproj");
        // Type is mandatory: without it the library cannot resolve a type for .cdsproj at all, and MSBuild
        // would have no way to load the project.
        xml.Should().Contain("Type=\"C#\"");
    }

    [Fact]
    public async Task AddProjectAsync_CdsprojIntoNewSln_WritesCSharpTypeGuidAndConfigRowsForEveryConfiguration()
    {
        var solution = Path_("DWE_Base.sln");

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var text = await File.ReadAllTextAsync(solution);
        text.Should().ContainAny(CSharpTypeGuid, LegacyCSharpTypeGuid);
        text.Should().Contain("DWE_Base.cdsproj");

        // Without matching ProjectConfigurationPlatforms rows the project appears in the solution but is
        // never built. One row pair per configuration is the whole point of writing a .sln at all.
        text.Should().Contain("Debug|Any CPU.ActiveCfg");
        text.Should().Contain("Debug|Any CPU.Build.0");
        text.Should().Contain("Release|Any CPU.ActiveCfg");
        text.Should().Contain("Release|Any CPU.Build.0");
    }

    [Fact]
    public async Task AddProjectAsync_ProjectAlreadyPresent_ReturnsFalseAndDoesNotDuplicate()
    {
        var solution = Path_("DWE_Base.slnx");
        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var again = await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        again.Added.Should().BeFalse();
        again.Created.Should().BeFalse("the first call already made the file");
        var projects = await _reader.ReadProjectsAsync(solution);
        projects.Should().ContainSingle().Which.Path.Should().Be(Sep("Solution/DWE_Base.cdsproj"));
    }

    [Theory]
    [InlineData("Solution\\DWE_Base.cdsproj")]
    [InlineData("Solution/DWE_Base.cdsproj")]
    [InlineData("solution/dwe_base.cdsproj")]
    public async Task AddProjectAsync_ExistingEntryUsesOppositeSeparatorOrCase_StillNoOp(string reAddAs)
    {
        // The library's own duplicate check is exact-string, so it would happily write a second entry for
        // the same file spelled differently. Normalization in the writer is what prevents that.
        var solution = Write("DWE_Base.slnx", """
<Solution>
  <Project Path="Solution/DWE_Base.cdsproj" Type="C#" />
</Solution>
""");

        var added = (await _writer.AddProjectAsync(solution, reAddAs)).Added;

        added.Should().BeFalse();
        (await _reader.ReadProjectsAsync(solution)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("DWE_Base.slnx")]
    [InlineData("DWE_Base.sln")]
    public async Task AddProjectAsync_NoSolutionFileYet_CreatesItInRequestedFormatAndWritesEntry(string fileName)
    {
        // "No solution file yet" is the state migrating and hand-assembled projects start from.
        var solution = Path_(fileName);

        var result = await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        result.Added.Should().BeTrue();
        // Created is decided inside the writer off the same stat that picks its write path — callers no
        // longer run their own File.Exists, which was both duplicated and racy.
        result.Created.Should().BeTrue();
        File.Exists(solution).Should().BeTrue();
        var projects = await _reader.ReadProjectsAsync(solution);
        projects.Should().ContainSingle().Which.IsCdsProject.Should().BeTrue();
    }

    [Fact]
    public async Task AddProjectAsync_Csproj_IsAccepted()
    {
        // Refusing a .csproj is a command-level concern — `dotnet sln add` handles those fine, so the
        // writer has no reason to care.
        var solution = Path_("DWE_Base.slnx");

        var added = (await _writer.AddProjectAsync(solution, "Plugins/DWE_Base.Plugins.csproj")).Added;

        added.Should().BeTrue();
        (await _reader.ReadProjectsAsync(solution)).Should().ContainSingle().Which.IsCsProject.Should().BeTrue();
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSlnxWithCommentsAndUserElements_PreservesThem()
    {
        // Fidelity on .slnx round-trip is the library's documented design goal, and the reason the
        // library path is safe here at all. If it ever regresses, this catches it.
        var solution = Write("DWE_Base.slnx", """
<Solution>
  <!-- keep this comment -->
  <Folder Name="/Solution Items/">
    <File Path="README.md" />
  </Folder>
  <Project Path="Plugins/DWE_Base.Plugins.csproj" />
</Solution>
""");

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var xml = await File.ReadAllTextAsync(solution);
        xml.Should().Contain("<!-- keep this comment -->");
        xml.Should().Contain("/Solution Items/");
        xml.Should().Contain("README.md");
        xml.Should().Contain("DWE_Base.Plugins.csproj");
        xml.Should().Contain("DWE_Base.cdsproj");
    }

    [Fact]
    public async Task AddProjectAsync_UnsupportedExtension_ThrowsConfigInvalid()
    {
        var act = () => _writer.AddProjectAsync(Path_("notasolution.txt"), "Solution/DWE_Base.cdsproj");

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }

    [Fact]
    public async Task AddProjectAsync_MalformedExistingSolution_ThrowsConfigInvalid()
    {
        var solution = Write("Broken.slnx", "<Solution><Project Path=");

        var act = () => _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }
}
