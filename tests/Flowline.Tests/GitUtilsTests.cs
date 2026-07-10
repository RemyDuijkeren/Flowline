using CliWrap;
using CliWrap.Buffered;
using FluentAssertions;

namespace Flowline.Tests;

public class GitUtilsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public GitUtilsTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // git creates read-only pack files on Windows; clear attributes before deleting
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(_root, true);
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

    string ReadGitOutput(params string[] args)
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
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    void CreateAndCommitFile(string relativePath, string content = "content")
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGit("add", relativePath);
        RunGit("commit", "-m", "add file");
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithCleanRepo_ShouldReturnEmptyList()
    {
        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            Path.Combine(_root, "src"), _root);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithModifiedTrackedFile_ShouldReturnFilePath()
    {
        CreateAndCommitFile("src/form.xml");
        File.WriteAllText(Path.Combine(_root, "src", "form.xml"), "modified");

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            Path.Combine(_root, "src"), _root);

        result.Should().ContainSingle().Which.Should().Be("src/form.xml");
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithStagedNewFile_ShouldReturnFilePath()
    {
        var filePath = Path.Combine(_root, "src", "new.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "content");
        RunGit("add", "src/new.xml");

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            Path.Combine(_root, "src"), _root);

        result.Should().ContainSingle().Which.Should().Be("src/new.xml");
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithUntrackedFile_ShouldReturnFilePath()
    {
        CreateAndCommitFile("src/existing.xml");  // src/ must be tracked for pathspec to match untracked entries
        File.WriteAllText(Path.Combine(_root, "src", "untracked.xml"), "content");

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            Path.Combine(_root, "src"), _root);

        result.Should().ContainSingle().Which.Should().Be("src/untracked.xml");
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithDirtyFileOutsidePath_ShouldReturnEmptyList()
    {
        var pluginPath = Path.Combine(_root, "Plugins", "plugin.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
        File.WriteAllText(pluginPath, "content");

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            Path.Combine(_root, "src"), _root);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithDeletedTrackedFile_ShouldReturnFilePath()
    {
        CreateAndCommitFile("src/delete.xml");
        File.Delete(Path.Combine(_root, "src", "delete.xml"));

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            Path.Combine(_root, "src"), _root);

        result.Should().ContainSingle().Which.Should().Be("src/delete.xml");
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithNonExistentPath_ShouldReturnEmptyList()
    {
        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            Path.Combine(_root, "nonexistent"), _root);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTagAsync_WithValidTag_ShouldCreateTag()
    {
        CreateAndCommitFile("readme.txt");

        await GitUtils.CreateTagAsync("1.0.1", _root, CancellationToken.None);

        var listResult = await Cli.Wrap("git")
                                  .WithArguments("tag")
                                  .WithWorkingDirectory(_root)
                                  .ExecuteBufferedAsync();
        listResult.StandardOutput.Trim().Should().Be("1.0.1");
    }

    [Fact]
    public async Task CreateTagAsync_WithDuplicateTag_ShouldThrowFlowlineException()
    {
        CreateAndCommitFile("readme.txt");

        await GitUtils.CreateTagAsync("1.0.1", _root, CancellationToken.None);

        var act = async () => await GitUtils.CreateTagAsync("1.0.1", _root, CancellationToken.None);
        await act.Should().ThrowAsync<FlowlineException>();
    }

    [Fact]
    public async Task GetCurrentBranchAsync_OnDefaultBranch_ReturnsBranchName()
    {
        CreateAndCommitFile("readme.txt");

        var branch = await GitUtils.GetCurrentBranchAsync(workingDirectory: _root);

        branch.Should().NotBeNullOrEmpty();
        branch.Should().NotBe("(detached)");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_InDetachedHeadState_ReturnsDetachedMarker()
    {
        CreateAndCommitFile("readme.txt");
        var sha = ReadGitOutput("rev-parse", "HEAD");
        RunGit("checkout", sha);

        var branch = await GitUtils.GetCurrentBranchAsync(workingDirectory: _root);

        branch.Should().Be("(detached)");
    }

    [Fact]
    public async Task GetLastCommitShaForPathAsync_WithCommittedFile_ShouldReturnHeadSha()
    {
        CreateAndCommitFile("src/form.xml");
        var expectedSha = ReadGitOutput("rev-parse", "HEAD");

        var sha = await GitUtils.GetLastCommitShaForPathAsync(
            Path.Combine(_root, "src"), _root);

        sha.Should().Be(expectedSha);
    }

    [Fact]
    public async Task GetLastCommitShaForPathAsync_WithNoCommitsTouchingPath_ShouldReturnNull()
    {
        CreateAndCommitFile("Plugins/plugin.cs");

        var sha = await GitUtils.GetLastCommitShaForPathAsync(
            Path.Combine(_root, "src"), _root);

        sha.Should().BeNull();
    }

    [Fact]
    public async Task GetLastCommitShaForPathAsync_WithTwoCommits_ShouldReturnNewestSha()
    {
        CreateAndCommitFile("src/first.xml");
        var firstSha = ReadGitOutput("rev-parse", "HEAD");

        CreateAndCommitFile("src/second.xml");
        var secondSha = ReadGitOutput("rev-parse", "HEAD");

        var sha = await GitUtils.GetLastCommitShaForPathAsync(
            Path.Combine(_root, "src"), _root);

        sha.Should().Be(secondSha);
        sha.Should().NotBe(firstSha);
    }

    [Fact]
    public async Task GetLastCommitShaForPathAsync_WithNonExistentPath_ShouldReturnNull()
    {
        var sha = await GitUtils.GetLastCommitShaForPathAsync(
            Path.Combine(_root, "nonexistent"), _root);

        sha.Should().BeNull();
    }
}
