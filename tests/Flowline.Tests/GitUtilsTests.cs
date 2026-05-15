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
}
