using Flowline;
using Flowline.Commands;
using Flowline.Utils;
using FluentAssertions;

namespace Flowline.Tests;

// A .flowline governs its whole subtree and the repository is the ceiling, so resolution is a walk with a
// boundary rather than a single-folder check. These cases pin the boundary, since getting it wrong is
// invisible from the call sites: too shallow and a standalone push slips past the "not inside a project"
// guard by cd'ing one folder down; too deep and a stray .flowline above a checkout captures it.
public class ProjectRootResolutionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-root-tests-" + Guid.NewGuid().ToString("N"));

    string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    static void Flowline(string folder) => File.WriteAllText(Path.Combine(folder, ".flowline"), "{}");

    // A normal clone: .git is a directory.
    static void GitRepo(string folder) => Directory.CreateDirectory(Path.Combine(folder, ".git"));

    // A worktree or submodule: .git is a FILE holding a gitdir: pointer.
    static void GitWorktree(string folder) => File.WriteAllText(Path.Combine(folder, ".git"), "gitdir: /somewhere/.git/worktrees/wt");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── The subtree rule ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindFlowlineProjectRoot_FromASubfolderOfAProject_ResolvesTheProject()
    {
        var repo = Dir("repo");
        GitRepo(repo);
        Flowline(repo);
        var deep = Dir("repo", "src", "nested");

        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(deep).Should().Be(repo);
    }

    [Fact]
    public void FindFlowlineProjectRoot_WithSeveralProjectsInOneRepo_ResolvesTheNearest()
    {
        var repo = Dir("repo");
        GitRepo(repo);
        var foo = Dir("repo", "solutions", "Foo");
        Flowline(foo);
        Flowline(repo);
        var inFoo = Dir("repo", "solutions", "Foo", "Plugins");

        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(inFoo).Should().Be(foo);
    }

    // ── The repository ceiling ──────────────────────────────────────────────────────────────────

    [Fact]
    public void FindFlowlineProjectRoot_StopsAtTheRepositoryRoot_IgnoringAFlowlineAboveIt()
    {
        Flowline(Dir());           // a stray .flowline above the checkout, e.g. one sitting in C:\Code
        var repo = Dir("repo");
        GitRepo(repo);
        var inside = Dir("repo", "src");

        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(inside).Should().BeNull();
    }

    [Fact]
    public void FindFlowlineProjectRoot_ChecksTheRepositoryRootItself_BeforeStopping()
    {
        // The ordinary layout: the project sits exactly at the repository root, so the boundary must be
        // applied after the config check, not before it.
        var repo = Dir("repo");
        GitRepo(repo);
        Flowline(repo);

        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(Dir("repo", "src")).Should().Be(repo);
    }

    [Fact]
    public void FindFlowlineProjectRoot_TreatsAWorktreeDotGitFileAsTheRepositoryRoot()
    {
        Flowline(Dir());
        var wt = Dir("wt");
        GitWorktree(wt);

        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(Dir("wt", "src")).Should().BeNull();
    }

    [Fact]
    public void FindFlowlineProjectRoot_WithNoRepositoryAnywhere_KeepsWalking()
    {
        // Deliberate: the setup check then reports the missing repository, which is the accurate problem.
        // Returning null here would have the caller claim no project exists while a .flowline sits there.
        var project = Dir("project");
        Flowline(project);

        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(Dir("project", "src")).Should().Be(project);
    }

    // ── Repository detection ────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindRepositoryRoot_FromASubfolder_ResolvesTheRepositoryRoot()
    {
        // A project in a repo subfolder is still in a Git repo — the check is "inside one", not
        // "contains .git". Getting this wrong rejects every nested Flowline project.
        var repo = Dir("repo");
        GitRepo(repo);

        GitUtils.FindRepositoryRoot(Dir("repo", "solutions", "Foo")).Should().Be(repo);
    }

    [Fact]
    public void FindRepositoryRoot_AcceptsADotGitFile()
    {
        var wt = Dir("wt");
        GitWorktree(wt);

        GitUtils.FindRepositoryRoot(Dir("wt", "src")).Should().Be(wt);
    }

    [Fact]
    public void FindRepositoryRoot_OutsideAnyRepository_ReturnsNull()
    {
        GitUtils.FindRepositoryRoot(Dir("loose", "folder")).Should().BeNull();
    }
}
