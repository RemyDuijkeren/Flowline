using FluentAssertions;
using Flowline.Utils;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class SolutionChangeSummaryPathParserTests
{
    [Fact]
    public void ParseComponentPath_EntityPath_IsEntityTrue()
    {
        SolutionChangeSummary.ParseComponentPath("Entities/Account/Entity.xml")!.IsEntity.Should().BeTrue();
    }

    [Fact]
    public void ParseComponentPath_NonEntityPath_IsEntityFalse()
    {
        SolutionChangeSummary.ParseComponentPath("Workflows/MyFlow.json")!.IsEntity.Should().BeFalse();
    }

    [Theory]
    [InlineData("Entities/Account/FormXml/main/{1fed44d1-ae68-4a41-bd2b-f13acac4acfa}.xml", "Account", "Account/form/{1fed44d1-ae68-4a41-bd2b-f13acac4acfa}", null, "main form")]
    [InlineData("Entities/Account/FormXml/quick/{abc}.xml", "Account", "Account/form/{abc}", null, "quick form")]
    [InlineData("Entities/dh_Custom/SavedQueries/{58fb20ff-d5be-406f-908e-c777e9dedf5f}.xml", "dh_Custom", "dh_Custom/view/{58fb20ff-d5be-406f-908e-c777e9dedf5f}", null, "view")]
    [InlineData("Entities/Account/Entity.xml", "Account", "Account/entity", "entity metadata", null)]
    [InlineData("Entities/Account/RibbonDiff.xml", "Account", "Account/ribbon", "ribbon", null)]
    [InlineData("Entities/Account/Formulas/MyFormula.xaml", "Account", "Account/formula/MyFormula", "formula: MyFormula", null)]
    public void ParseComponentPath_EntityComponents_ReturnsExpectedParsedPath(
        string path, string expectedGroup, string expectedKey, string? expectedStaticName, string? expectedNameSuffix)
    {
        var result = SolutionChangeSummary.ParseComponentPath(path);

        result.Should().NotBeNull();
        result!.Group.Should().Be(expectedGroup);
        result.ComponentKey.Should().Be(expectedKey);
        result.StaticName.Should().Be(expectedStaticName);
        result.NameSuffix.Should().Be(expectedNameSuffix);
    }

    [Theory]
    [InlineData("Entities/Account/FormXml/main/{guid}.xml", true)]
    [InlineData("Entities/Account/SavedQueries/{guid}.xml", true)]
    [InlineData("Entities/Account/Entity.xml", false)]
    [InlineData("SdkMessageProcessingSteps/{guid}.xml", true)]
    [InlineData("Dashboards/{guid}.xml", true)]
    [InlineData("Workflows/MyWF-45534473-EA8B-4AD0-A20F-67C7F430C5FA.xaml", false)]
    public void ParseComponentPath_XmlReadNeeded_MatchesExpectation(string path, bool expectsXmlRead)
    {
        var result = SolutionChangeSummary.ParseComponentPath(path);

        result.Should().NotBeNull();
        var needsXml = result!.StaticName == null;
        needsXml.Should().Be(expectsXmlRead);
    }

    [Theory]
    [InlineData("Workflows/AccountWF01-45534473-EA8B-4AD0-A20F-67C7F430C5FA.xaml", "Workflows", "AccountWF01")]
    [InlineData("Workflows/NoGuidHere.xaml", "Workflows", "NoGuidHere")]
    [InlineData("Workflows/MyFlow-45534473-EA8B-4AD0-A20F-67C7F430C5FA.json.data.xml", "Workflows", "MyFlow")]
    public void ParseComponentPath_Workflow_StripsGuidSuffix(string path, string expectedGroup, string expectedName)
    {
        var result = SolutionChangeSummary.ParseComponentPath(path);

        result.Should().NotBeNull();
        result!.Group.Should().Be(expectedGroup);
        result.StaticName.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("OptionSets/my_optionset.xml", "OptionSets", "my_optionset")]
    [InlineData("Roles/SystemCustomizer.xml", "Roles", "SystemCustomizer")]
    public void ParseComponentPath_FilenameComponents_ReturnsFilenameAsName(string path, string expectedGroup, string expectedName)
    {
        var result = SolutionChangeSummary.ParseComponentPath(path);

        result.Should().NotBeNull();
        result!.Group.Should().Be(expectedGroup);
        result.StaticName.Should().Be(expectedName);
    }

    [Fact]
    public void ParseComponentPath_EnvVar_ReturnsFolderName()
    {
        var result = SolutionChangeSummary.ParseComponentPath("environmentvariabledefinitions/my_var/environmentvariabledefinition.xml");

        result.Should().NotBeNull();
        result!.Group.Should().Be("Environment Variables");
        result.StaticName.Should().Be("my_var");
        result.ComponentKey.Should().Be("envvars/my_var");
    }

    [Fact]
    public void ParseComponentPath_PluginStep_NeedsXmlRead()
    {
        var result = SolutionChangeSummary.ParseComponentPath("SdkMessageProcessingSteps/12345678-1234-1234-1234-123456789abc.xml");

        result.Should().NotBeNull();
        result!.Group.Should().Be("Plugin Steps");
        result.StaticName.Should().BeNull();
    }

    [Fact]
    public void ParseComponentPath_Dashboard_NeedsXmlRead()
    {
        var result = SolutionChangeSummary.ParseComponentPath("Dashboards/12345678-1234-1234-1234-123456789abc.xml");

        result.Should().NotBeNull();
        result!.Group.Should().Be("Dashboards");
        result.StaticName.Should().BeNull();
    }

    [Theory]
    [InlineData("AppModules/MyApp/AppModule.xml", "App Modules", "MyApp", "AppModules/MyApp")]
    [InlineData("AppModuleSiteMaps/MyApp/sitemap.xml", "App Modules", "MyApp", "AppModules/MyApp")]
    public void ParseComponentPath_AppModules_GroupsUnderAppModules(string path, string expectedGroup, string expectedName, string expectedKey)
    {
        var result = SolutionChangeSummary.ParseComponentPath(path);

        result.Should().NotBeNull();
        result!.Group.Should().Be(expectedGroup);
        result.StaticName.Should().Be(expectedName);
        result.ComponentKey.Should().Be(expectedKey);
    }

    [Fact]
    public void ParseComponentPath_WebResource_StripsDataXmlSuffix()
    {
        var result = SolutionChangeSummary.ParseComponentPath("WebResources/dh_folder/script.js.data.xml");

        result.Should().NotBeNull();
        result!.Group.Should().Be("Web Resources");
        result.StaticName.Should().Be("dh_folder/script.js");
        result.ComponentKey.Should().Be("WebResources/dh_folder/script.js");
    }

    [Fact]
    public void ParseComponentPath_WebResource_ThreeLevels_KeyIsIndividualFile()
    {
        var result = SolutionChangeSummary.ParseComponentPath("WebResources/av_Cr07982/images/claude-v1.png.data.xml");

        result.Should().NotBeNull();
        result!.Group.Should().Be("Web Resources");
        result.StaticName.Should().Be("av_Cr07982/images/claude-v1.png");
        result.ComponentKey.Should().Be("WebResources/av_Cr07982/images/claude-v1.png");
    }

    [Fact]
    public void ParseComponentPath_WebResource_OriginalAndDataXmlPairSameKey()
    {
        var original = SolutionChangeSummary.ParseComponentPath("WebResources/av_Cr07982/images/claude-v1.png");
        var metadata = SolutionChangeSummary.ParseComponentPath("WebResources/av_Cr07982/images/claude-v1.png.data.xml");

        original!.ComponentKey.Should().Be(metadata!.ComponentKey);
        original.StaticName.Should().Be(metadata.StaticName);
    }

    [Theory]
    [InlineData("Other/solution.xml")]
    [InlineData("Other/customizations.xml")]
    [InlineData("Entities/Account/UnknownFile.xml")]
    [InlineData("Entities/OnlyOneSegment")]
    [InlineData("UnknownFolder/something.xml")]
    public void ParseComponentPath_Skipped_ReturnsNull(string path)
    {
        SolutionChangeSummary.ParseComponentPath(path).Should().BeNull();
    }

    [Theory]
    [InlineData("Entities/Account/FormXml/main/form_managed.xml")]
    [InlineData("Workflows/MyFlow-12345678-1234-1234-1234-123456789abc.xaml.data.xml")]
    [InlineData("SdkMessageProcessingSteps/assembly.dll.data.xml")]
    public void IsExcluded_ExcludedFiles_ReturnsTrue(string path)
    {
        SolutionChangeSummary.IsExcluded(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("Entities/Account/Entity.xml")]
    [InlineData("Workflows/MyFlow-12345678-1234-1234-1234-123456789abc.xaml")]
    [InlineData("WebResources/folder/script.js.data.xml")]
    public void IsExcluded_NonExcludedFiles_ReturnsFalse(string path)
    {
        SolutionChangeSummary.IsExcluded(path).Should().BeFalse();
    }
}

public class SolutionChangeSummaryComputeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-tests", Guid.NewGuid().ToString("N"));
    readonly string _srcFolder;

    public SolutionChangeSummaryComputeTests()
    {
        _srcFolder = Path.Combine(_root, "solutions", "TestSln", "src");
        Directory.CreateDirectory(_srcFolder);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
        // Initial commit so HEAD exists
        var placeholder = Path.Combine(_root, ".gitkeep");
        File.WriteAllText(placeholder, "");
        RunGit("add", ".gitkeep");
        RunGit("commit", "-m", "init");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task ComputeAsync_WithNoChanges_ReturnsTotalFilesZero()
    {
        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.TotalFiles.Should().Be(0);
        result.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputeAsync_WithNewEntityMetadataFile_ReturnsEntityGroup()
    {
        WriteFile("Entities/Account/Entity.xml", "<entity/>");

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.TotalFiles.Should().Be(1);
        result.Groups.Should().ContainSingle(g => g.Label == "Account");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "entity metadata");
    }

    [Fact]
    public async Task ComputeAsync_WithFormFile_ResolvesFormTitleFromXml()
    {
        var guid = Guid.NewGuid().ToString("D");
        var xml = """
            <forms>
              <systemform>
                <LocalizedNames>
                  <LocalizedName languagecode="1033" description="Information" />
                </LocalizedNames>
              </systemform>
            </forms>
            """;
        WriteFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", xml);

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.Groups.Should().ContainSingle(g => g.Label == "Account");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "Information (main form)");
    }

    [Fact]
    public async Task ComputeAsync_WithViewFile_ResolvesViewTitleFromXml()
    {
        var guid = Guid.NewGuid().ToString("D");
        var xml = """
            <savedquery>
              <LocalizedNames>
                <LocalizedName languagecode="1033" description="Active Accounts" />
              </LocalizedNames>
            </savedquery>
            """;
        WriteFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", xml);

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.Groups.Should().ContainSingle(g => g.Label == "Account");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "Active Accounts (view)");
    }

    [Fact]
    public async Task ComputeAsync_ExcludesManagedAndSidecarFiles()
    {
        WriteFile("Entities/Account/Entity.xml", "<entity/>");
        WriteFile("Entities/Account/Entity_managed.xml", "<entity/>");
        WriteFile("Workflows/MyFlow-45534473-EA8B-4AD0-A20F-67C7F430C5FA.xaml.data.xml", "<data/>");

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.TotalFiles.Should().Be(1);
        result.Groups.Should().ContainSingle(g => g.Label == "Account");
    }

    [Fact]
    public async Task ComputeAsync_WithDeletedFormFile_ResolvesNameFromHead()
    {
        var guid = Guid.NewGuid().ToString("D");
        var xml = """
            <forms>
              <systemform>
                <LocalizedNames>
                  <LocalizedName languagecode="1033" description="Quick Create" />
                </LocalizedNames>
              </systemform>
            </forms>
            """;
        var relPath = $"Entities/Contact/FormXml/quick/{{{guid}}}.xml";
        CommitFile(relPath, xml);
        DeleteFile(relPath);

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.TotalFiles.Should().Be(1);
        result.Groups.Should().ContainSingle(g => g.Label == "Contact");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "Quick Create (quick form)");
    }

    [Fact]
    public async Task ComputeAsync_MultipleEntities_GroupsCorrectly()
    {
        WriteFile("Entities/Account/Entity.xml", "<entity/>");
        WriteFile("Entities/Contact/Entity.xml", "<entity/>");
        WriteFile("Workflows/MyWF-45534473-EA8B-4AD0-A20F-67C7F430C5FA.xaml", "<workflow/>");

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.TotalFiles.Should().Be(3);
        result.Groups.Should().HaveCount(3);
        result.Groups.Should().Contain(g => g.Label == "Account");
        result.Groups.Should().Contain(g => g.Label == "Contact");
        result.Groups.Should().Contain(g => g.Label == "Workflows");
    }

    [Fact]
    public async Task ComputeAsync_MixedStatusFiles_ComponentReportsModified()
    {
        var guid = "45534473-EA8B-4AD0-A20F-67C7F430C5FA";
        // Commit one file so it can be deleted, add another as new
        CommitFile($"Workflows/OldFlow-{guid}.json", "{}");
        DeleteFile($"Workflows/OldFlow-{guid}.json");
        WriteFile($"Workflows/OldFlow-{guid}.json.data.xml", "<data/>");

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.Groups.Should().ContainSingle(g => g.Label == "Workflows");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "OldFlow");
        result.Groups[0].Items[0].Status.Should().Be(SolutionChangeSummary.ChangeStatus.Modified);
    }

    [Fact]
    public async Task ComputeAsync_WorkflowWithDataXmlPair_TreatedAsOneComponent()
    {
        var guid = "45534473-EA8B-4AD0-A20F-67C7F430C5FA";
        WriteFile($"Workflows/MyFlow-{guid}.json", "{}");
        WriteFile($"Workflows/MyFlow-{guid}.json.data.xml", "<data/>");

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.Groups.Should().ContainSingle(g => g.Label == "Workflows");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "MyFlow");
        result.Groups[0].Items[0].FilePaths.Should().HaveCount(2);
    }

    [Fact]
    public async Task ComputeAsync_FolderGroupedComponents_DeduplicatesPerFolder()
    {
        WriteFile("environmentvariabledefinitions/my_var/environmentvariabledefinition.xml", "<def/>");
        WriteFile("environmentvariabledefinitions/my_var/environmentvariablevalue.xml", "<val/>");

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.Groups.Should().ContainSingle(g => g.Label == "Environment Variables");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "my_var");
        result.Groups[0].Items[0].FilePaths.Should().HaveCount(2);
    }

    void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_srcFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    void CommitFile(string relativePath, string content)
    {
        WriteFile(relativePath, content);
        var srcRelPath = Path.GetRelativePath(_root, _srcFolder).Replace('\\', '/');
        RunGit("add", srcRelPath + "/" + relativePath);
        RunGit("commit", "-m", "add file");
    }

    void DeleteFile(string relativePath)
    {
        var full = Path.Combine(_srcFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(full);
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

public class SolutionChangeSummaryWriteTests
{
    static SolutionChangeSummary Build(int totalFiles, int added, int removed, params SolutionChangeSummary.ChangeGroup[] groups) =>
        new(totalFiles, added, removed, groups);

    static SolutionChangeSummary.ChangeGroup Group(string label, params string[] names) =>
        new(label, names.Select(n => new SolutionChangeSummary.ChangeItem(n, [])).ToList());

    static SolutionChangeSummary.ChangeGroup GroupWithPaths(string label, string name, params string[] paths) =>
        new(label, [new SolutionChangeSummary.ChangeItem(name, paths)]);

    [Fact]
    public void Write_NoChanges_PrintsNoChangesMessage()
    {
        var console = new TestConsole();
        var summary = Build(0, 0, 0);

        summary.Write(console, "Contoso Dev", verbose: false);

        console.Output.Should().Contain("No changes pulled from Contoso Dev.");
    }

    [Fact]
    public void Write_NoChanges_FallsBackToDevWhenEnvNameNull()
    {
        var console = new TestConsole();
        var summary = Build(0, 0, 0);

        summary.Write(console, null, verbose: false);

        console.Output.Should().Contain("No changes pulled from DEV.");
    }

    [Fact]
    public void Write_WithChanges_PrintsHeadline()
    {
        var console = new TestConsole();
        var summary = Build(3, 47, 23, Group("Account", "entity metadata"));

        summary.Write(console, "Dev", verbose: false);

        console.Output.Should().Contain("Changes (3 files, +47 -23)");
    }

    [Fact]
    public void Write_SingleFile_UsesFileNotFiles()
    {
        var console = new TestConsole();
        var summary = Build(1, 5, 0, Group("Account", "entity metadata"));

        summary.Write(console, "Dev", verbose: false);

        console.Output.Should().Contain("1 file,");
        console.Output.Should().NotContain("1 files");
    }

    [Fact]
    public void Write_DefaultMode_PrintsGroupAndItemsWithoutFilePaths()
    {
        var console = new TestConsole();
        var summary = Build(2, 10, 5,
            Group("Account", "Information (main)", "Active Accounts"));

        summary.Write(console, "Dev", verbose: false);

        console.Output.Should().Contain("Account");
        console.Output.Should().Contain("Information (main)");
        console.Output.Should().Contain("Active Accounts");
    }

    [Fact]
    public void Write_VerboseMode_PrintsGroupThenItemThenPath()
    {
        var console = new TestConsole();
        var summary = Build(1, 5, 0,
            GroupWithPaths("Account", "entity metadata", "Entities/Account/Entity.xml"));

        summary.Write(console, "Dev", verbose: true);

        var output = console.Output;
        output.Should().Contain("Account");
        output.Should().Contain("entity metadata");
        output.Should().Contain("Entities/Account/Entity.xml");
    }

    [Fact]
    public void Write_EntityGroups_NestedUnderEntitiesNode()
    {
        var console = new TestConsole();
        var summary = Build(2, 10, 5,
            new SolutionChangeSummary.ChangeGroup("Account", [new SolutionChangeSummary.ChangeItem("entity metadata", [])], IsEntity: true),
            new SolutionChangeSummary.ChangeGroup("Contact", [new SolutionChangeSummary.ChangeItem("ribbon", [])], IsEntity: true));

        summary.Write(console, "Dev", verbose: false);

        var output = console.Output;
        output.Should().Contain("Entities");
        output.Should().Contain("Account");
        output.Should().Contain("Contact");
    }

    [Fact]
    public void Write_WebResources_RenderedAsFolderTree()
    {
        var console = new TestConsole();
        var summary = Build(2, 5, 0,
            new SolutionChangeSummary.ChangeGroup("Web Resources", [
                new SolutionChangeSummary.ChangeItem("av_Cr07982/images/claude-v1.png", [], SolutionChangeSummary.ChangeStatus.Added),
                new SolutionChangeSummary.ChangeItem("av_Cr07982/script/test.js", [], SolutionChangeSummary.ChangeStatus.Modified),
            ]));

        summary.Write(console, "Dev", verbose: false);

        var output = console.Output;
        output.Should().Contain("av_Cr07982");
        output.Should().Contain("images");
        output.Should().Contain("script");
        output.Should().Contain("claude-v1.png");
        output.Should().Contain("test.js");
        output.Should().NotContain("av_Cr07982/images/claude-v1.png");
    }

    [Fact]
    public void Write_StatusIcons_ShownForAddedModifiedDeleted()
    {
        var console = new TestConsole();
        var summary = Build(3, 10, 5,
            new SolutionChangeSummary.ChangeGroup("Account", [
                new SolutionChangeSummary.ChangeItem("New Form", [], SolutionChangeSummary.ChangeStatus.Added),
                new SolutionChangeSummary.ChangeItem("Changed Form", [], SolutionChangeSummary.ChangeStatus.Modified),
                new SolutionChangeSummary.ChangeItem("Old Form", [], SolutionChangeSummary.ChangeStatus.Deleted),
            ]));

        summary.Write(console, "Dev", verbose: false);

        var output = console.Output;
        output.Should().Contain("+ New Form");
        output.Should().Contain("~ Changed Form");
        output.Should().Contain("- Old Form");
    }

    [Fact]
    public void Write_MultipleGroups_AllGroupsPresent()
    {
        var console = new TestConsole();
        var summary = Build(3, 20, 10,
            Group("Account", "entity metadata"),
            Group("Contact", "ribbon"),
            Group("Workflows", "AccountWF01"));

        summary.Write(console, "Dev", verbose: false);

        var output = console.Output;
        output.Should().Contain("Account");
        output.Should().Contain("Contact");
        output.Should().Contain("Workflows");
    }
}
