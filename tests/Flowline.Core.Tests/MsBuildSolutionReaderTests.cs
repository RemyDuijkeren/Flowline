using Flowline.Core;
using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class MsBuildSolutionReaderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly MsBuildSolutionReader _reader = new();

    public MsBuildSolutionReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // A .sln holding one csproj and one cdsproj. Backslash separators, as the format stores them.
    const string SlnWithBothProjects = """
Microsoft Visual Studio Solution File, Format Version 12.00
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "DWE_Base.Plugins", "Plugins\DWE_Base.Plugins.csproj", "{11111111-1111-1111-1111-111111111111}"
EndProject
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "DWE_Base", "Solution\DWE_Base.cdsproj", "{22222222-2222-2222-2222-222222222222}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
	EndGlobalSection
EndGlobal
""";

    // The same two projects expressed as .slnx. Forward slashes, as that format stores them.
    const string SlnxWithBothProjects = """
<Solution>
  <Project Path="Plugins/DWE_Base.Plugins.csproj" />
  <Project Path="Solution/DWE_Base.cdsproj" Type="C#" />
</Solution>
""";

    string Write(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    static string Sep(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public async Task ReadProjectsAsync_SlnWithCsprojAndCdsproj_ReturnsBoth()
    {
        var path = Write("DWE_Base.sln", SlnWithBothProjects);

        var projects = await _reader.ReadProjectsAsync(path);

        projects.Should().HaveCount(2);
        projects.Should().ContainSingle(p => p.IsCdsProject)
                .Which.Path.Should().Be(Sep("Solution/DWE_Base.cdsproj"));
        projects.Should().ContainSingle(p => p.IsCsProject)
                .Which.Path.Should().Be(Sep("Plugins/DWE_Base.Plugins.csproj"));
    }

    [Fact]
    public async Task ReadProjectsAsync_SlnxWithSameProjects_ReturnsIdenticalResultToSln()
    {
        // The formats store separators differently. Callers must not be able to tell them apart.
        var slnProjects = await _reader.ReadProjectsAsync(Write("A.sln", SlnWithBothProjects));
        var slnxProjects = await _reader.ReadProjectsAsync(Write("B.slnx", SlnxWithBothProjects));

        slnxProjects.Select(p => p.Path).Should().BeEquivalentTo(slnProjects.Select(p => p.Path));
        slnxProjects.Select(p => p.Extension).Should().BeEquivalentTo(slnProjects.Select(p => p.Extension));
    }

    [Theory]
    [InlineData("DWE_Base.sln", SlnWithBothProjects)]
    [InlineData("DWE_Base.slnx", SlnxWithBothProjects)]
    public async Task ReadProjectsAsync_EitherFormat_ReturnsRelativePathsWithOneSeparator(string fileName, string content)
    {
        var projects = await _reader.ReadProjectsAsync(Write(fileName, content));

        projects.Should().AllSatisfy(p =>
        {
            Path.IsPathRooted(p.Path).Should().BeFalse("paths are relative to the solution folder");
            var foreign = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';
            p.Path.Should().NotContain(foreign.ToString(), "the reader normalizes separators");
        });
    }

    [Fact]
    public async Task ReadProjectsAsync_DisplayNameDiffersFromFileName_ReportsTheNameTheFileDeclares()
    {
        // The library exposes two names: DisplayName is what the file says, ActualDisplayName is
        // re-derived from the filename. Reading the derived one makes Name silently wrong for any
        // solution that named a project differently — and `msbuild -t:<Name>` uses the declared one.
        var path = Write("Friendly.sln", """
Microsoft Visual Studio Solution File, Format Version 12.00
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyFriendlyName", "Plugins\DWE_Base.Plugins.csproj", "{11111111-1111-1111-1111-111111111111}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
	EndGlobalSection
EndGlobal
""");

        var projects = await _reader.ReadProjectsAsync(path);

        projects.Should().ContainSingle().Which.Name.Should().Be("MyFriendlyName");
    }

    [Fact]
    public async Task ReadProjectsAsync_SolutionWithNoProjects_ReturnsEmptyNotNull()
    {
        var path = Write("Empty.slnx", "<Solution />");

        var projects = await _reader.ReadProjectsAsync(path);

        projects.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ReadProjectsAsync_MalformedSolution_ThrowsConfigInvalid()
    {
        var path = Write("Broken.slnx", "<Solution><Project Path=");

        var act = () => _reader.ReadProjectsAsync(path);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }

    [Fact]
    public async Task ReadProjectsAsync_MissingFile_ThrowsNotFoundNamingThePath()
    {
        var missing = Path.Combine(_root, "Nope.sln");

        var act = () => _reader.ReadProjectsAsync(missing);

        var ex = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        ex.ExitCode.Should().Be(ExitCode.NotFound);
        ex.Message.Should().Contain("Nope.sln");
    }

    [Fact]
    public async Task ReadProjectsAsync_UnsupportedExtension_ThrowsConfigInvalidRatherThanNullReference()
    {
        // GetSerializerByMoniker returns null for anything that isn't .sln/.slnx.
        var path = Write("notasolution.txt", "hello");

        var act = () => _reader.ReadProjectsAsync(path);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }

    [Theory]
    [InlineData("Solution/DWE_Base.cdsproj")]
    [InlineData("Solution\\DWE_Base.cdsproj")]
    [InlineData("solution/dwe_base.cdsproj")]
    public async Task FindProjectAsync_PathDifferingBySeparatorOrCase_StillMatches(string query)
    {
        // The library compares raw strings, so normalization here is what makes idempotency work.
        var path = Write("DWE_Base.sln", SlnWithBothProjects);

        var found = await _reader.FindProjectAsync(path, query);

        found.Should().NotBeNull();
        found!.IsCdsProject.Should().BeTrue();
    }

    [Fact]
    public async Task FindProjectAsync_ProjectNotInSolution_ReturnsNull()
    {
        var path = Write("DWE_Base.sln", SlnWithBothProjects);

        var found = await _reader.FindProjectAsync(path, "Other/Other.csproj");

        found.Should().BeNull();
    }

    [Fact]
    public void FindSolutionFile_OnlySln_ReturnsIt()
    {
        Write("DWE_Base.sln", SlnWithBothProjects);

        _reader.FindSolutionFile(_root).Should().EndWith("DWE_Base.sln");
    }

    [Fact]
    public void FindSolutionFile_OnlySlnx_ReturnsIt()
    {
        Write("DWE_Base.slnx", SlnxWithBothProjects);

        _reader.FindSolutionFile(_root).Should().EndWith("DWE_Base.slnx");
    }

    [Fact]
    public void FindSolutionFile_BothPresent_PrefersSlnx()
    {
        // The state `dotnet sln migrate` leaves behind: the .slnx is the one the user migrated to.
        Write("DWE_Base.sln", SlnWithBothProjects);
        Write("DWE_Base.slnx", SlnxWithBothProjects);

        _reader.FindSolutionFile(_root).Should().EndWith("DWE_Base.slnx");
    }

    [Fact]
    public void FindSolutionFile_NoSolutionFile_ReturnsNullNotThrows()
    {
        // "No solution file yet" is a state `sln add` creates from, not an error.
        _reader.FindSolutionFile(_root).Should().BeNull();
    }

    [Fact]
    public void FindSolutionFile_MissingFolder_ReturnsNull()
    {
        _reader.FindSolutionFile(Path.Combine(_root, "does-not-exist")).Should().BeNull();
    }

    [Fact]
    public void HasCoexistingSolutionFiles_BothSharingBaseName_ReturnsTrue()
    {
        Write("DWE_Base.sln", SlnWithBothProjects);
        Write("DWE_Base.slnx", SlnxWithBothProjects);

        _reader.HasCoexistingSolutionFiles(_root).Should().BeTrue();
    }

    [Fact]
    public void HasCoexistingSolutionFiles_DifferentBaseNames_StillReturnsTrue()
    {
        // `dotnet build` can't choose between these any more than it can between a same-name pair,
        // so staying silent here would mean writing to whichever sorts first without warning.
        Write("One.sln", SlnWithBothProjects);
        Write("Two.slnx", SlnxWithBothProjects);

        _reader.HasCoexistingSolutionFiles(_root).Should().BeTrue();
    }

    [Fact]
    public void HasCoexistingSolutionFiles_TwoFilesOfTheSameFormat_ReturnsTrue()
    {
        Write("One.sln", SlnWithBothProjects);
        Write("Two.sln", SlnWithBothProjects);

        _reader.HasCoexistingSolutionFiles(_root).Should().BeTrue();
    }

    [Theory]
    [InlineData("Plugins/X.csproj")]
    [InlineData("/Plugins/X.csproj")]
    public void NormalizePath_RelativeInput_StripsAnyLeadingSeparator(string input)
    {
        MsBuildSolutionReader.NormalizePath(input)
            .Should().Be(Sep("Plugins/X.csproj"));
    }

    [Fact]
    public void NormalizePath_RootedInput_StaysRooted()
    {
        // Stripping unconditionally re-roots an absolute path, turning /opt/x.csproj into opt/x.csproj.
        var rooted = Path.Combine(Path.GetTempPath(), "x.csproj");

        var normalized = MsBuildSolutionReader.NormalizePath(rooted);

        Path.IsPathRooted(normalized).Should().BeTrue();
    }

    [Fact]
    public async Task ReadProjectsAsync_LockedFile_ThrowsConfigInvalidNotRawIoException()
    {
        // The realistic case is the solution file being open in an IDE.
        var path = Write("Locked.sln", SlnWithBothProjects);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = () => _reader.ReadProjectsAsync(path);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }

    [Fact]
    public void HasCoexistingSolutionFiles_SingleFile_ReturnsFalse()
    {
        Write("DWE_Base.slnx", SlnxWithBothProjects);

        _reader.HasCoexistingSolutionFiles(_root).Should().BeFalse();
    }
}
