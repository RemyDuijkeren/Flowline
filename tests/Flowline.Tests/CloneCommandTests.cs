using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;

namespace Flowline.Tests;

public class CloneCommandTests
{
    // ── HasManagedContent ───────────────────────────────────────────────────
    // Detects whether Package/src already has the managed layer (PAC's "{name}_managed.xml"
    // sibling files, written only when unpacked with --packagetype Both), so clone's re-sync
    // branch can skip when a solution already cloned as managed is re-run unchanged.

    [Fact]
    public void HasManagedContent_NoSrcFolder_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var packageFolder = Path.Combine(root, "Package");
            Directory.CreateDirectory(packageFolder);

            var result = CloneCommand.HasManagedContent(packageFolder);

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasManagedContent_SrcWithOnlyUnmanagedFiles_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var srcFolder = Path.Combine(root, "Package", "src", "Entities", "Account", "FormXml", "main");
            Directory.CreateDirectory(srcFolder);
            File.WriteAllText(Path.Combine(srcFolder, "{guid}.xml"), "<form />");

            var result = CloneCommand.HasManagedContent(Path.Combine(root, "Package"));

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasManagedContent_SrcWithManagedFile_ReturnsTrue()
    {
        var root = CreateTempRoot();
        try
        {
            var srcFolder = Path.Combine(root, "Package", "src", "Entities", "Account", "FormXml", "main");
            Directory.CreateDirectory(srcFolder);
            File.WriteAllText(Path.Combine(srcFolder, "{guid}.xml"), "<form />");
            File.WriteAllText(Path.Combine(srcFolder, "{guid}_managed.xml"), "<form />");

            var result = CloneCommand.HasManagedContent(Path.Combine(root, "Package"));

            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // ── Solution file wiring ────────────────────────────────────────────────
    // Clone used to rename Package.cdsproj to .csproj so `dotnet sln add` would take it, add it,
    // rename it back, then string-replace the filename inside the .sln. The writer takes the
    // .cdsproj directly, so the rename — and the half-renamed state an interrupted clone left
    // behind — are both gone.

    static (string Root, string SlnPath, string CdsprojPath) CreateProject(string solutionName = "CrO7982")
    {
        var root = CreateTempRoot();
        var packageFolder = Path.Combine(root, "Package");
        Directory.CreateDirectory(packageFolder);

        var cdsprojPath = Path.Combine(packageFolder, "Package.cdsproj");
        File.WriteAllText(cdsprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        return (root, Path.Combine(root, $"{solutionName}.sln"), cdsprojPath);
    }

    [Fact]
    public async Task AddPackageProjectAsync_NoSolutionFile_WritesTheCdsprojEntryWithCSharpType()
    {
        var (root, slnPath, cdsprojPath) = CreateProject();
        try
        {
            var (created, added) = await CloneCommand.AddPackageProjectAsync(new MsBuildSolutionWriter(), root, slnPath, cdsprojPath);

            created.Should().BeTrue();
            added.Should().BeTrue();

            var content = await File.ReadAllTextAsync(slnPath);
            content.Should().Contain(@"Package\Package.cdsproj");
            // The C# project type GUID is what makes MSBuild load a .cdsproj at all — without it the
            // entry is inert and the SolutionPackager step never runs.
            content.Should().Contain("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}");

            var projects = await new MsBuildSolutionReader().ReadProjectsAsync(slnPath);
            projects.Should().ContainSingle(p => p.Path == Path.Combine("Package", "Package.cdsproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddPackageProjectAsync_NeverLeavesACsprojBesideTheCdsproj()
    {
        var (root, slnPath, cdsprojPath) = CreateProject();
        try
        {
            await CloneCommand.AddPackageProjectAsync(new MsBuildSolutionWriter(), root, slnPath, cdsprojPath);

            var packageFolder = Path.Combine(root, "Package");
            File.Exists(cdsprojPath).Should().BeTrue("the project keeps its own name throughout");
            Directory.EnumerateFiles(packageFolder, "*.csproj").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddPackageProjectAsync_EntryAlreadyPresent_DoesNotDuplicateIt()
    {
        var (root, slnPath, cdsprojPath) = CreateProject();
        try
        {
            var writer = new MsBuildSolutionWriter();
            await CloneCommand.AddPackageProjectAsync(writer, root, slnPath, cdsprojPath);

            var (created, added) = await CloneCommand.AddPackageProjectAsync(writer, root, slnPath, cdsprojPath);

            created.Should().BeFalse();
            added.Should().BeFalse();

            var projects = await new MsBuildSolutionReader().ReadProjectsAsync(slnPath);
            projects.Where(p => p.IsCdsProject).Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Interrupted clone (AE4 / R11) ───────────────────────────────────────

    [Fact]
    public async Task InterruptedClone_LeavesTheCdsprojUnderItsOwnNameAndReRunProceeds()
    {
        var (root, slnPath, cdsprojPath) = CreateProject();
        try
        {
            // Interruption partway through solution-file setup: the package folder and its project are
            // already on disk, and there is no solution file yet.
            File.Exists(cdsprojPath).Should().BeTrue();
            File.Exists(slnPath).Should().BeFalse();

            // Clone's guard only fires when Package.cdsproj is missing, so the re-run walks straight past
            // it — no delete-and-re-clone demand — and finishes the wiring the interrupted run started.
            var (created, added) = await CloneCommand.AddPackageProjectAsync(new MsBuildSolutionWriter(), root, slnPath, cdsprojPath);

            created.Should().BeTrue();
            added.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescribePackageFolderWithoutCdsproj_StrayProjectFile_NamesItAndAsksForARename()
    {
        var root = CreateTempRoot();
        try
        {
            // What an interruption between pac's folder rename and the .cdsproj rename leaves behind.
            var packageFolder = Path.Combine(root, "Package");
            Directory.CreateDirectory(packageFolder);
            File.WriteAllText(Path.Combine(packageFolder, "CrO7982.cdsproj"), "<Project />");

            var message = CloneCommand.DescribePackageFolderWithoutCdsproj(packageFolder);

            message.Should().Contain("CrO7982.cdsproj").And.Contain("Rename");
            message.Should().NotContain("Delete", "a one-file rename fixes this — deleting the folder throws away a finished clone");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescribePackageFolderWithoutCdsproj_NoProjectFileAtAll_StillAvoidsDeleteAdvice()
    {
        var root = CreateTempRoot();
        try
        {
            var packageFolder = Path.Combine(root, "Package");
            Directory.CreateDirectory(packageFolder);

            var message = CloneCommand.DescribePackageFolderWithoutCdsproj(packageFolder);

            message.Should().Contain("Package.cdsproj");
            message.Should().NotContain("Delete");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ValidateForce / HasForce ────────────────────────────────────────────

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingConfigAndAll()
    {
        var settings = new CloneCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new CloneCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_All_ApprovesConfig()
    {
        var settings = new CloneCommand.Settings { Force = ["all"] };

        settings.HasForce("config").Should().BeTrue();
    }
}
