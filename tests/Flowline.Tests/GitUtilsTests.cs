using FluentAssertions;
using Flowline.Commands;

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

    [Fact]
    public async Task GetLastCommitShaForPathAsync_OutsideAnyGitRepo_ShouldReturnNull()
    {
        var nonRepoDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(nonRepoDir);
        try
        {
            var sha = await GitUtils.GetLastCommitShaForPathAsync(
                Path.Combine(nonRepoDir, "src"), nonRepoDir);

            sha.Should().BeNull();
        }
        finally
        {
            Directory.Delete(nonRepoDir, recursive: true);
        }
    }

    // ── Multi-path overloads (R15) ────────────────────────────────────────────

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithMultiplePaths_DetectsChangeInEitherPath()
    {
        CreateAndCommitFile("Solution/src/existing.xml");
        CreateAndCommitFile("Plugins/Plugins.csproj");
        File.WriteAllText(Path.Combine(_root, "Plugins", "Plugins.csproj"), "modified");

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            [Path.Combine(_root, "Solution"), Path.Combine(_root, "Plugins", "Plugins.csproj")], _root);

        result.Should().ContainSingle().Which.Should().Be("Plugins/Plugins.csproj");
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithMultiplePaths_AndNoChangesInAny_ReturnsEmptyList()
    {
        CreateAndCommitFile("Solution/src/existing.xml");
        CreateAndCommitFile("Plugins/Plugins.csproj");

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            [Path.Combine(_root, "Solution"), Path.Combine(_root, "Plugins", "Plugins.csproj")], _root);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUncommittedChangesInPathAsync_WithMultiplePaths_IgnoresChangeOutsideAllGivenPaths()
    {
        CreateAndCommitFile("Solution/src/existing.xml");
        CreateAndCommitFile("docs/BRAINSTORM.md");
        File.WriteAllText(Path.Combine(_root, "docs", "BRAINSTORM.md"), "modified");

        var result = await GitUtils.GetUncommittedChangesInPathAsync(
            [Path.Combine(_root, "Solution"), Path.Combine(_root, "Plugins", "Plugins.csproj")], _root);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLastCommitShaForPathAsync_WithMultiplePaths_ReturnsMostRecentShaAcrossAll()
    {
        CreateAndCommitFile("Solution/src/first.xml");
        var olderSha = ReadGitOutput("rev-parse", "HEAD");

        CreateAndCommitFile("Plugins/Plugins.csproj");
        var newerSha = ReadGitOutput("rev-parse", "HEAD");

        var sha = await GitUtils.GetLastCommitShaForPathAsync(
            [Path.Combine(_root, "Solution"), Path.Combine(_root, "Plugins", "Plugins.csproj")], _root);

        sha.Should().Be(newerSha);
        sha.Should().NotBe(olderSha);
    }

    [Fact]
    public async Task GetLastCommitShaForPathAsync_WithMultiplePaths_AndNoCommitsTouchingEither_ReturnsNull()
    {
        CreateAndCommitFile("docs/BRAINSTORM.md");

        var sha = await GitUtils.GetLastCommitShaForPathAsync(
            [Path.Combine(_root, "Solution"), Path.Combine(_root, "Plugins", "Plugins.csproj")], _root);

        sha.Should().BeNull();
    }

    // ── DeployCommand.GetDeploymentInputPaths integration (R15) ───────────────
    // These exercise the actual scope DeployCommand's clean-check and cache-key call sites share —
    // GetDeploymentInputPaths feeding straight into the multi-path GitUtils methods above.

    [Fact]
    public async Task DeploymentInputPaths_UncommittedChangeUnderSolutionFolder_IsDetected()
    {
        CreateAndCommitFile("Solution/src/Other/Solution.xml");
        File.WriteAllText(Path.Combine(_root, "Solution", "src", "Other", "Solution.xml"), "modified");

        var changes = await GitUtils.GetUncommittedChangesInPathAsync(
            await DeployCommand.GetDeploymentInputPathsAsync(_root), _root);

        changes.Should().ContainSingle().Which.Should().Be("Solution/src/Other/Solution.xml");
    }

    [Fact]
    public async Task DeploymentInputPaths_UncommittedChangeUnderPluginsProjectFile_IsDetected()
    {
        CreateAndCommitFile("Plugins/Plugins.csproj");
        File.WriteAllText(Path.Combine(_root, "Plugins", "Plugins.csproj"), "modified");

        var changes = await GitUtils.GetUncommittedChangesInPathAsync(
            await DeployCommand.GetDeploymentInputPathsAsync(_root), _root);

        changes.Should().ContainSingle().Which.Should().Be("Plugins/Plugins.csproj");
    }

    [Theory]
    [InlineData("docs/BRAINSTORM.md")]
    [InlineData("tests/Flowline.Tests/SomeTest.cs")]
    [InlineData("CHANGES.md")]
    [InlineData("AGENTS.md")]
    [InlineData("CLAUDE.md")]
    public async Task DeploymentInputPaths_UncommittedChangeOutsideScope_IsIgnored(string relativePath)
    {
        CreateAndCommitFile(relativePath);
        File.WriteAllText(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)), "modified");

        var changes = await GitUtils.GetUncommittedChangesInPathAsync(
            await DeployCommand.GetDeploymentInputPathsAsync(_root), _root);

        changes.Should().BeEmpty();
    }

    [Fact]
    public async Task DeploymentInputPaths_CacheKeyChangesWhenSolutionFolderChanges_ButNotWhenDocsChange()
    {
        CreateAndCommitFile("Solution/src/Other/Solution.xml");
        var initialSha = await GitUtils.GetLastCommitShaForPathAsync(await DeployCommand.GetDeploymentInputPathsAsync(_root), _root);

        CreateAndCommitFile("docs/BRAINSTORM.md");
        var afterDocsSha = await GitUtils.GetLastCommitShaForPathAsync(await DeployCommand.GetDeploymentInputPathsAsync(_root), _root);

        CreateAndCommitFile("Solution/src/Other/Customizations.xml");
        var afterSolutionChangeSha = await GitUtils.GetLastCommitShaForPathAsync(await DeployCommand.GetDeploymentInputPathsAsync(_root), _root);

        afterDocsSha.Should().Be(initialSha); // docs-only commit must not invalidate the cache key
        afterSolutionChangeSha.Should().NotBe(initialSha); // a Solution/ change must invalidate it
    }
}
