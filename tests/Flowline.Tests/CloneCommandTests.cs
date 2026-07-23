using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;

namespace Flowline.Tests;

public class CloneCommandTests
{
    // ── HasManagedContent ───────────────────────────────────────────────────
    // Detects whether Solution/src already has the managed layer (PAC's "{name}_managed.xml"
    // sibling files, written only when unpacked with --packagetype Both), so clone's re-sync
    // branch can skip when a solution already cloned as managed is re-run unchanged.

    [Fact]
    public void HasManagedContent_NoSrcFolder_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var dataverseSolutionFolder = Path.Combine(root, "Solution");
            Directory.CreateDirectory(dataverseSolutionFolder);

            var result = CloneCommand.HasManagedContent(dataverseSolutionFolder);

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
            var srcFolder = Path.Combine(root, "Solution", "src", "Entities", "Account", "FormXml", "main");
            Directory.CreateDirectory(srcFolder);
            File.WriteAllText(Path.Combine(srcFolder, "{guid}.xml"), "<form />");

            var result = CloneCommand.HasManagedContent(Path.Combine(root, "Solution"));

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
            var srcFolder = Path.Combine(root, "Solution", "src", "Entities", "Account", "FormXml", "main");
            Directory.CreateDirectory(srcFolder);
            File.WriteAllText(Path.Combine(srcFolder, "{guid}.xml"), "<form />");
            File.WriteAllText(Path.Combine(srcFolder, "{guid}_managed.xml"), "<form />");

            var result = CloneCommand.HasManagedContent(Path.Combine(root, "Solution"));

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
        var dataverseSolutionFolder = Path.Combine(root, "Solution");
        Directory.CreateDirectory(dataverseSolutionFolder);

        // pac names the .cdsproj after the solution and clone never renames it.
        var cdsprojPath = Path.Combine(dataverseSolutionFolder, $"{solutionName}.cdsproj");
        File.WriteAllText(cdsprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        return (root, Path.Combine(root, $"{solutionName}.sln"), cdsprojPath);
    }

    [Fact]
    public async Task AddDataverseSolutionProjectAsync_NoSolutionFile_WritesTheCdsprojEntryWithCSharpType()
    {
        var (root, slnPath, cdsprojPath) = CreateProject();
        try
        {
            var (created, added) = await CloneCommand.AddDataverseSolutionProjectAsync(new MsBuildSolutionWriter(), root, slnPath, cdsprojPath);

            created.Should().BeTrue();
            added.Should().BeTrue();

            var content = await File.ReadAllTextAsync(slnPath);
            content.Should().Contain(@"Solution\CrO7982.cdsproj");
            // The C# project type GUID is what makes MSBuild load a .cdsproj at all — without it the
            // entry is inert and the SolutionPackager step never runs.
            content.Should().Contain("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}");

            var projects = await new MsBuildSolutionReader().ReadProjectsAsync(slnPath);
            projects.Should().ContainSingle(p => p.Path == Path.Combine("Solution", "CrO7982.cdsproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddDataverseSolutionProjectAsync_NeverLeavesACsprojBesideTheCdsproj()
    {
        var (root, slnPath, cdsprojPath) = CreateProject();
        try
        {
            await CloneCommand.AddDataverseSolutionProjectAsync(new MsBuildSolutionWriter(), root, slnPath, cdsprojPath);

            var dataverseSolutionFolder = Path.Combine(root, "Solution");
            File.Exists(cdsprojPath).Should().BeTrue("the project keeps its own name throughout");
            Directory.EnumerateFiles(dataverseSolutionFolder, "*.csproj").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddDataverseSolutionProjectAsync_EntryAlreadyPresent_DoesNotDuplicateIt()
    {
        var (root, slnPath, cdsprojPath) = CreateProject();
        try
        {
            var writer = new MsBuildSolutionWriter();
            await CloneCommand.AddDataverseSolutionProjectAsync(writer, root, slnPath, cdsprojPath);

            var (created, added) = await CloneCommand.AddDataverseSolutionProjectAsync(writer, root, slnPath, cdsprojPath);

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
            // Interruption partway through solution-file setup: the Dataverse solution folder and its
            // project are already on disk, and there is no solution file yet.
            File.Exists(cdsprojPath).Should().BeTrue();
            File.Exists(slnPath).Should().BeFalse();

            // Clone's guard only fires when the .cdsproj is missing, so the re-run walks straight past
            // it — no delete-and-re-clone demand — and finishes the wiring the interrupted run started.
            var (created, added) = await CloneCommand.AddDataverseSolutionProjectAsync(new MsBuildSolutionWriter(), root, slnPath, cdsprojPath);

            created.Should().BeTrue();
            added.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescribeDataverseSolutionFolderWithoutCdsproj_StrayProjectFile_NamesItAndAsksForARename()
    {
        var root = CreateTempRoot();
        try
        {
            // A Solution/ folder holding another solution's project — a solution renamed in Dataverse,
            // or someone else's clone left in place.
            var dataverseSolutionFolder = Path.Combine(root, "Solution");
            Directory.CreateDirectory(dataverseSolutionFolder);
            File.WriteAllText(Path.Combine(dataverseSolutionFolder, "OtherSolution.cdsproj"), "<Project />");

            var message = CloneCommand.DescribeDataverseSolutionFolderWithoutCdsproj(dataverseSolutionFolder, "CrO7982.cdsproj");

            message.Should().Contain("OtherSolution.cdsproj").And.Contain("CrO7982.cdsproj").And.Contain("Rename");
            message.Should().NotContain("Delete", "a one-file rename fixes this — deleting the folder throws away a finished clone");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescribeDataverseSolutionFolderWithoutCdsproj_NoProjectFileAtAll_StillAvoidsDeleteAdvice()
    {
        var root = CreateTempRoot();
        try
        {
            var dataverseSolutionFolder = Path.Combine(root, "Solution");
            Directory.CreateDirectory(dataverseSolutionFolder);

            var message = CloneCommand.DescribeDataverseSolutionFolderWithoutCdsproj(dataverseSolutionFolder, "CrO7982.cdsproj");

            message.Should().Contain("CrO7982.cdsproj");
            message.Should().NotContain("Delete");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Solution file format (R7 / KD4 / KD7) ───────────────────────────────
    // .slnx is the .NET 10 default and holds a .cdsproj fine. The opt-out exists for the machines
    // clone cannot see — a teammate or build agent below SDK 9.0.200 opening the committed repo.

    [Fact]
    public void SolutionFileName_ByDefault_IsSlnx()
    {
        CloneCommand.SolutionFileName("CrO7982").Should().Be("CrO7982.slnx");
    }

    [Fact]
    public void Settings_SlnFlag_DefaultsToSlnx()
    {
        // The flag is opt-in, so an untouched Settings must land on .slnx — the whole point of R7.
        var settings = new CloneCommand.Settings();

        CloneCommand.SolutionFileName("CrO7982").Should().Be("CrO7982.slnx");
    }

    /// <summary>
    /// Scaffolds the three projects clone wires into a solution file, in the given format.
    /// </summary>
    /// <remarks>
    /// The .cdsproj goes through clone's own seam; the two .csproj entries go straight through the
    /// writer, because clone hands those to <c>dotnet sln add</c> and the suite has no process fake.
    /// The writer is what <c>dotnet sln add</c> would have produced either way — an entry with the
    /// C# type — so what the assertions compare is still the finished solution file.
    /// </remarks>
    static async Task<(string Root, string SlnFilePath)> ScaffoldSolutionAsync(
        string solutionName = "CrO7982", string? existingSolutionFileName = null)
    {
        var (root, _, cdsprojPath) = CreateProject(solutionName);

        // A pre-placed solution file stands in for a project that already had one. Clone reuses whatever
        // is there rather than creating a second file, so this is how the .sln path is still exercised
        // now that nothing asks clone to create one.
        if (existingSolutionFileName != null)
            File.WriteAllText(Path.Combine(root, existingSolutionFileName), string.Join(Environment.NewLine,
                "Microsoft Visual Studio Solution File, Format Version 12.00",
                "Global",
                "	GlobalSection(SolutionConfigurationPlatforms) = preSolution",
                "		Debug|Any CPU = Debug|Any CPU",
                "		Release|Any CPU = Release|Any CPU",
                "	EndGlobalSection",
                "	GlobalSection(ProjectConfigurationPlatforms) = postSolution",
                "	EndGlobalSection",
                "EndGlobal") + Environment.NewLine);

        var slnFilePath = CloneCommand.ResolveSolutionFilePath(root, solutionName);

        var writer = new MsBuildSolutionWriter();
        await CloneCommand.AddDataverseSolutionProjectAsync(writer, root, slnFilePath, cdsprojPath);

        foreach (var (folder, fileName) in new[]
                 {
                     ("Plugins", CloneCommand.PluginsProjectFileName(solutionName)),
                     ("WebResources", CloneCommand.WebResourcesProjectFileName(solutionName)),
                 })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, folder, fileName), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            await writer.AddProjectAsync(slnFilePath, Path.Combine(folder, fileName));
        }

        return (root, slnFilePath);
    }

    [Fact]
    public async Task Clone_ByDefault_WritesASlnxHoldingAllThreeProjects()
    {
        var (root, slnFilePath) = await ScaffoldSolutionAsync();
        try
        {
            Path.GetFileName(slnFilePath).Should().Be("CrO7982.slnx");
            File.Exists(slnFilePath).Should().BeTrue();

            var projects = await new MsBuildSolutionReader().ReadProjectsAsync(slnFilePath);

            projects.Select(p => p.Path).Should().BeEquivalentTo(
                Path.Combine("Solution", "CrO7982.cdsproj"),
                Path.Combine("Plugins", "CrO7982.Plugins.csproj"),
                Path.Combine("WebResources", "CrO7982.WebResources.csproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Clone_WithSlnOptOut_WritesASlnHoldingTheSameThreeProjects()
    {
        var (root, slnFilePath) = await ScaffoldSolutionAsync(existingSolutionFileName: "CrO7982.sln");
        try
        {
            Path.GetFileName(slnFilePath).Should().Be("CrO7982.sln");
            Directory.EnumerateFiles(root, "*.slnx").Should().BeEmpty("the opt-out replaces the format, it doesn't add a second file");

            var projects = await new MsBuildSolutionReader().ReadProjectsAsync(slnFilePath);

            projects.Select(p => p.Path).Should().BeEquivalentTo(
                Path.Combine("Solution", "CrO7982.cdsproj"),
                Path.Combine("Plugins", "CrO7982.Plugins.csproj"),
                Path.Combine("WebResources", "CrO7982.WebResources.csproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Clone_IntoAnExistingSln_GivesEveryProjectItsConfigurationRows()
    {
        // A .sln entry without matching ProjectConfigurationPlatforms rows shows in the solution and
        // never builds — which for the .cdsproj means SolutionPackager silently never runs.
        var (root, slnFilePath) = await ScaffoldSolutionAsync(existingSolutionFileName: "CrO7982.sln");
        try
        {
            var content = await File.ReadAllTextAsync(slnFilePath);

            foreach (var configuration in new[] { "Debug|Any CPU", "Release|Any CPU" })
            {
                CountOccurrences(content, $"{configuration}.ActiveCfg = {configuration}").Should().Be(3);
                CountOccurrences(content, $"{configuration}.Build.0 = {configuration}").Should().Be(3);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CrO7982.sln")]
    public async Task Clone_EitherFormat_TypesTheCdsprojAsACSharpProject(string? existingSolutionFileName)
    {
        // Without the C# project type the .cdsproj entry is inert in both formats: MSBuild will not
        // load it, so nothing packs. This is KD6 holding across the format switch.
        var (root, slnFilePath) = await ScaffoldSolutionAsync(existingSolutionFileName: existingSolutionFileName);
        try
        {
            var cdsproj = await new MsBuildSolutionReader()
                .FindProjectAsync(slnFilePath, Path.Combine("Solution", "CrO7982.cdsproj"));

            cdsproj.Should().NotBeNull();
            cdsproj!.IsCdsProject.Should().BeTrue();

            var content = await File.ReadAllTextAsync(slnFilePath);
            if (existingSolutionFileName != null)
                content.Should().Contain("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}");
            else
                content.Should().MatchRegex(@"CrO7982\.cdsproj""[^>]*Type=""C#""");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Clone_IntoAnExistingSln_ChangesTheSolutionFileFormatAndNothingElse()
    {
        var (slnxRoot, slnxPath) = await ScaffoldSolutionAsync();
        var (slnRoot, slnPath) = await ScaffoldSolutionAsync(existingSolutionFileName: "CrO7982.sln");
        try
        {
            // Every scaffolded file apart from the solution file itself is identical.
            static IEnumerable<string> ScaffoldedFiles(string root, string slnFilePath) =>
                Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => f != slnFilePath)
                         .Select(f => Path.GetRelativePath(root, f))
                         .Order(StringComparer.Ordinal);

            ScaffoldedFiles(slnRoot, slnPath).Should().BeEquivalentTo(ScaffoldedFiles(slnxRoot, slnxPath));

            // And both solution files describe the same membership.
            var reader = new MsBuildSolutionReader();
            var fromSlnx = await reader.ReadProjectsAsync(slnxPath);
            var fromSln = await reader.ReadProjectsAsync(slnPath);

            fromSln.Select(p => p.Path).Should().BeEquivalentTo(fromSlnx.Select(p => p.Path));
            fromSln.Select(p => p.Name).Should().BeEquivalentTo(fromSlnx.Select(p => p.Name));
        }
        finally
        {
            Directory.Delete(slnxRoot, recursive: true);
            Directory.Delete(slnRoot, recursive: true);
        }
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ── Project file naming (U1-U3 / KD1-KD4) ───────────────────────────────
    // Flowline owns the folder names — role-based, identical in every repo. pac owns the project file
    // names — identity-based, because that name is what escapes the repo into Dataverse.

    [Theory]
    [InlineData("CrO7982", "CrO7982.Plugins.csproj")]
    [InlineData("DWE_Base", "DWE_Base.Plugins.csproj")]
    public void PluginsProjectFileName_TakesTheSolutionNameVerbatim(string solutionName, string expected)
    {
        // The underscore is kept: DWE_Base and DWEBase are two distinct legal solutions, and stripping
        // it collapses them onto one assembly name — the anonymity this naming exists to remove.
        CloneCommand.PluginsProjectFileName(solutionName).Should().Be(expected);
    }

    [Theory]
    [InlineData("CrO7982", "CrO7982.WebResources.csproj")]
    [InlineData("DWE_Base", "DWE_Base.WebResources.csproj")]
    public void WebResourcesProjectFileName_TakesTheSolutionNameVerbatim(string solutionName, string expected)
    {
        CloneCommand.WebResourcesProjectFileName(solutionName).Should().Be(expected);
    }

    [Fact]
    public async Task Clone_ScaffoldsRoleNamedFoldersHoldingSolutionNamedProjects()
    {
        var (root, slnFilePath) = await ScaffoldSolutionAsync("DWE_Base");
        try
        {
            // Folders are the same in every repo...
            Directory.Exists(Path.Combine(root, "Solution")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, "Plugins")).Should().BeTrue();
            Directory.Exists(Path.Combine(root, "DWE_Base.Plugins")).Should().BeFalse("only the folder is renamed after init, and it lands on the role name");
            Directory.Exists(Path.Combine(root, "Package")).Should().BeFalse();

            // ...and the project files inside them carry the solution's identity.
            File.Exists(Path.Combine(root, "Solution", "DWE_Base.cdsproj")).Should().BeTrue();
            File.Exists(Path.Combine(root, "Plugins", "DWE_Base.Plugins.csproj")).Should().BeTrue();
            File.Exists(Path.Combine(root, "WebResources", "DWE_Base.WebResources.csproj")).Should().BeTrue();

            var projects = await new MsBuildSolutionReader().ReadProjectsAsync(slnFilePath);
            projects.Select(p => p.Path).Should().BeEquivalentTo(
                Path.Combine("Solution", "DWE_Base.cdsproj"),
                Path.Combine("Plugins", "DWE_Base.Plugins.csproj"),
                Path.Combine("WebResources", "DWE_Base.WebResources.csproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Solution names that break the scaffold (U2) ─────────────────────────
    // A Dataverse uniquename is [A-Za-z0-9_] with no reserved-word list, so C# keywords are legal
    // solution names. pac plugin init in a directory named event.Plugins reports success and writes
    // `namespace event.Plugins`, which fails CS1001. Clone refuses before pac runs.

    [Theory]
    [InlineData("event")]
    [InlineData("class")]
    [InlineData("int")]
    [InlineData("object")]
    [InlineData("lock")]
    [InlineData("params")]
    public void DescribeCSharpKeywordCollision_KeywordSolutionName_NamesTheKeywordAndTheNamespace(string solutionName)
    {
        var message = CloneCommand.DescribeCSharpKeywordCollision(solutionName);

        message.Should().NotBeNull();
        message.Should().Contain($"'{solutionName}'").And.Contain($"{solutionName}.Plugins");
    }

    [Theory]
    [InlineData("CrO7982")]
    [InlineData("DWE_Base")]     // an underscore breaks nothing — no sanitisation
    [InlineData("_123")]         // legal Dataverse name, legal C# identifier
    [InlineData("Event")]        // identifiers are case-sensitive, so this is not the keyword
    [InlineData("MyEventStore")]
    public void DescribeCSharpKeywordCollision_UsableSolutionName_ReturnsNull(string solutionName)
    {
        CloneCommand.DescribeCSharpKeywordCollision(solutionName).Should().BeNull();
    }

    // ── PluginsProjectAlreadyRegistered / WebResourcesProjectAlreadyRegistered ──
    // Clone's scaffold-skip check used to look only at the default Plugins/WebResources folder by name, so
    // re-cloning a project whose plugin/WebResources project was legitimately moved/renamed (project-
    // structure flexibility) scaffolded a spurious duplicate and registered it into the solution file
    // (tests/test-findings/clone-idempotent-reclone-duplicates-moved-plugins-webresources.md). These now
    // also ask the solution file, the same way push/sync/deploy already discover a moved project.

    const string PluginProjectXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="9.0.2" /></ItemGroup></Project>""";
    const string WebResourcesProjectXml = """<Project Sdk="Microsoft.Build.NoTargets/3.7.134"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string PlainCsprojXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    /// <summary>Writes a solution file (through the real writer) registering a `.cdsproj` plus whatever
    /// extra projects the test needs, then loads it exactly the way clone does mid-run.</summary>
    static async Task<SolutionFileLayout> LoadLayoutAsync(string root, params (string RelativePath, string Xml)[] extraProjects)
    {
        var cdsprojRelativePath = Path.Combine("Solution", "CrO7982.cdsproj");
        Directory.CreateDirectory(Path.Combine(root, "Solution"));
        File.WriteAllText(Path.Combine(root, cdsprojRelativePath), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var writer = new MsBuildSolutionWriter();
        var slnPath = Path.Combine(root, "CrO7982.slnx");
        await writer.AddProjectAsync(slnPath, cdsprojRelativePath);

        foreach (var (relativePath, xml) in extraProjects)
        {
            var full = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, xml);
            await writer.AddProjectAsync(slnPath, relativePath);
        }

        return await SolutionFileLayout.LoadAsync(root);
    }

    [Fact]
    public async Task PluginsProjectAlreadyRegistered_NothingAnywhere_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var layout = await LoadLayoutAsync(root);

            var result = CloneCommand.PluginsProjectAlreadyRegistered(Path.Combine(root, "Plugins"), layout);

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginsProjectAlreadyRegistered_DefaultFolderHasAProject_ReturnsTrue()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Plugins"));
            File.WriteAllText(Path.Combine(root, "Plugins", "CrO7982.Plugins.csproj"), PluginProjectXml);
            var layout = await LoadLayoutAsync(root);

            var result = CloneCommand.PluginsProjectAlreadyRegistered(Path.Combine(root, "Plugins"), layout);

            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginsProjectAlreadyRegistered_MovedAndRenamedElsewhereInSolutionFile_ReturnsTrue()
    {
        var root = CreateTempRoot();
        try
        {
            // The default folder holds nothing -- only the solution file knows the plugin project moved.
            var layout = await LoadLayoutAsync(root, (Path.Combine("Backend", "CrO7982.Backend.csproj"), PluginProjectXml));

            var result = CloneCommand.PluginsProjectAlreadyRegistered(Path.Combine(root, "Plugins"), layout);

            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WebResourcesProjectAlreadyRegistered_NothingAnywhere_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            var layout = await LoadLayoutAsync(root);
            var webresourcesCsproj = Path.Combine(root, "WebResources", "CrO7982.WebResources.csproj");

            var result = CloneCommand.WebResourcesProjectAlreadyRegistered(webresourcesCsproj, layout);

            result.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WebResourcesProjectAlreadyRegistered_MovedAndRenamedElsewhereInSolutionFile_ReturnsTrue()
    {
        var root = CreateTempRoot();
        try
        {
            // The default folder holds nothing -- only the solution file knows the WebResources project moved.
            var layout = await LoadLayoutAsync(root, (Path.Combine("Frontend", "ClientAssets.csproj"), WebResourcesProjectXml));
            var webresourcesCsproj = Path.Combine(root, "WebResources", "CrO7982.WebResources.csproj");

            var result = CloneCommand.WebResourcesProjectAlreadyRegistered(webresourcesCsproj, layout);

            result.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WebResourcesProjectAlreadyRegistered_GenuineTieInSolutionFile_PropagatesConfigInvalid()
    {
        var root = CreateTempRoot();
        try
        {
            // Two equally-unweighted candidates -- a 0-0 tie is still a tie (KD4, matching
            // WebResourcesProjectResolverTests.Resolve_TwoEquallyWeightedCandidates_ThrowsConfigInvalidNamingBoth).
            // Propagating this rather than swallowing it and scaffolding a third default-named project is the
            // documented design choice: scaffolding on top of an unresolved ambiguity would only make it worse.
            var layout = await LoadLayoutAsync(root,
                (Path.Combine("Alpha", "Alpha.csproj"), PlainCsprojXml),
                (Path.Combine("Beta", "Beta.csproj"), PlainCsprojXml));
            var webresourcesCsproj = Path.Combine(root, "WebResources", "CrO7982.WebResources.csproj");

            var act = () => CloneCommand.WebResourcesProjectAlreadyRegistered(webresourcesCsproj, layout);

            act.Should().Throw<FlowlineException>().Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ResolveExistingWebResourcesFolder ───────────────────────────────────
    // SetupWebResourcesProjectAsync's seed step (SeedWebResourceDistFromSrc) used to always seed into the
    // hardcoded WebResources/public folder, even when the real WebResources project had moved elsewhere --
    // leaving stray untracked seed files nobody builds (a related, separate issue found while verifying the
    // scaffold-skip fix above: tests/test-findings/clone-idempotent-reclone-duplicates-moved-plugins-webresources.md).
    // This resolver names the real folder so the seed step can write into it instead of guessing.

    [Fact]
    public async Task ResolveExistingWebResourcesFolder_NothingAnywhere_ReturnsNull()
    {
        var root = CreateTempRoot();
        try
        {
            var layout = await LoadLayoutAsync(root);
            var webresourcesFolder = Path.Combine(root, "WebResources");
            var webresourcesCsproj = Path.Combine(webresourcesFolder, "CrO7982.WebResources.csproj");

            var result = CloneCommand.ResolveExistingWebResourcesFolder(webresourcesFolder, webresourcesCsproj, layout);

            result.Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveExistingWebResourcesFolder_DefaultFolderHasAProject_ReturnsDefaultFolder()
    {
        var root = CreateTempRoot();
        try
        {
            var webresourcesFolder = Path.Combine(root, "WebResources");
            var webresourcesCsproj = Path.Combine(webresourcesFolder, "CrO7982.WebResources.csproj");
            Directory.CreateDirectory(webresourcesFolder);
            File.WriteAllText(webresourcesCsproj, WebResourcesProjectXml);
            var layout = await LoadLayoutAsync(root, (Path.Combine("WebResources", "CrO7982.WebResources.csproj"), WebResourcesProjectXml));

            var result = CloneCommand.ResolveExistingWebResourcesFolder(webresourcesFolder, webresourcesCsproj, layout);

            result.Should().Be(webresourcesFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveExistingWebResourcesFolder_MovedAndRenamedElsewhereInSolutionFile_ReturnsThatFolder()
    {
        var root = CreateTempRoot();
        try
        {
            // The default folder holds nothing -- only the solution file knows the WebResources project moved.
            var movedFolder = Path.Combine(root, "Frontend");
            var layout = await LoadLayoutAsync(root, (Path.Combine("Frontend", "ClientAssets.csproj"), WebResourcesProjectXml));
            var webresourcesFolder = Path.Combine(root, "WebResources");
            var webresourcesCsproj = Path.Combine(webresourcesFolder, "CrO7982.WebResources.csproj");

            var result = CloneCommand.ResolveExistingWebResourcesFolder(webresourcesFolder, webresourcesCsproj, layout);

            result.Should().Be(movedFolder);
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

    // ── Scaffolded AGENTS.md (U5) ───────────────────────────────────────────
    // Clone writes these instructions into the user's repo, where a coding agent reads them as fact.
    // Every path has to come from the layout clone just built, or the guidance contradicts the tree
    // sitting next to it.

    [Theory]
    [InlineData("CrO7982")]
    [InlineData("DWE_Base")]
    public void BuildAgentsFileContent_NamesTheProjectFilesClonePutOnDisk(string solutionName)
    {
        var content = CloneCommand.BuildAgentsFileContent(solutionName, $"{solutionName}.slnx", "Solution");

        content.Should().Contain($"Solution/{solutionName}.cdsproj")
               .And.Contain($"Plugins/{solutionName}.Plugins.csproj")
               .And.Contain($"WebResources/{solutionName}.WebResources.csproj")
               .And.Contain($"{solutionName}.slnx");
    }

    [Fact]
    public void BuildAgentsFileContent_NeverMentionsTheOldDataverseSolutionFolder()
    {
        var content = CloneCommand.BuildAgentsFileContent("CrO7982", "CrO7982.slnx", "Solution");

        content.Should().NotContain("Package/")
               .And.NotContain("Package.cdsproj")
               .And.NotContain("Plugins/Plugins.csproj")
               .And.NotContain("WebResources/WebResources.csproj");
    }

    [Fact]
    public void BuildAgentsFileContent_FollowsARelocatedDataverseSolutionFolder()
    {
        // The folder resolves from the solution file, so a project that moved its Dataverse solution
        // folder must not be handed instructions naming the folder clone would have created.
        var content = CloneCommand.BuildAgentsFileContent("CrO7982", "CrO7982.slnx", "Dataverse");

        content.Should().Contain("Dataverse/CrO7982.cdsproj")
               .And.Contain("Dataverse/src/")
               .And.NotContain("Solution/src/");
    }
}
