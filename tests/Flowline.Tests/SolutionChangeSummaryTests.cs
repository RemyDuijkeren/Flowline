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
    public void ParseComponentPath_Workflow_Xaml_UsesStaticName(string path, string expectedGroup, string expectedName)
    {
        var result = SolutionChangeSummary.ParseComponentPath(path);

        result.Should().NotBeNull();
        result!.Group.Should().Be(expectedGroup);
        result.StaticName.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("Workflows/MyFlow-45534473-EA8B-4AD0-A20F-67C7F430C5FA.json.data.xml", "Workflows", "MyFlow")]
    public void ParseComponentPath_Workflow_JsonDataXml_UsesXmlRead(string path, string expectedGroup, string expectedFallback)
    {
        var result = SolutionChangeSummary.ParseComponentPath(path);

        result.Should().NotBeNull();
        result!.Group.Should().Be(expectedGroup);
        result.StaticName.Should().BeNull();
        result.XmlRead.Should().Be(SolutionChangeSummary.XmlRead.WorkflowName);
        result.FallbackName.Should().Be(expectedFallback);
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
    public async Task ComputeAsync_WithDeletedPluginStep_ResolvesNameFromGitHistory()
    {
        var guid = Guid.NewGuid().ToString("D");
        // Leading BOM + xml declaration mirrors real Dataverse-exported XML; git show preserves the BOM
        var xml = $"""
            ﻿<?xml version="1.0" encoding="utf-8"?>
            <SdkMessageProcessingStep Name="MyPlugin: Create of account" SdkMessageId="{guid}" />
            """;
        var relPath = $"SdkMessageProcessingSteps/{{{guid}}}.xml";
        CommitFile(relPath, xml);
        DeleteFile(relPath);

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);

        result.TotalFiles.Should().Be(1);
        result.Groups.Should().ContainSingle(g => g.Label == "Plugin Steps");
        result.Groups[0].Items.Should().ContainSingle(i => i.ComponentName == "MyPlugin: Create of account");
        result.Groups[0].Items[0].Status.Should().Be(SolutionChangeSummary.ChangeStatus.Deleted);
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

public class SolutionChangeSummarySubChangesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-tests-sub", Guid.NewGuid().ToString("N"));
    readonly string _srcFolder;

    public SolutionChangeSummarySubChangesTests()
    {
        _srcFolder = Path.Combine(_root, "solutions", "TestSln", "src");
        Directory.CreateDirectory(_srcFolder);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, ".gitkeep"), "");
        RunGit("add", ".gitkeep");
        RunGit("commit", "-m", "init");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        Directory.Delete(_root, true);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static string EntityXml(params (string logicalName, string type)[] attribs) => $"""
        <Entity>
          <EntityInfo>
            <entity Name="Account">
              <attributes>
                {string.Join("\n    ", attribs.Select(a => $"<attribute><LogicalName>{a.logicalName}</LogicalName><Type>{a.type}</Type></attribute>"))}
              </attributes>
            </entity>
          </EntityInfo>
        </Entity>
        """;

    static string SavedQueryXml(string[] cols, string? filter = null, string? orderAttr = null) => $"""
        <savedqueries>
          <savedquery>
            <LocalizedNames><LocalizedName languagecode="1033" description="Test View" /></LocalizedNames>
            <layoutxml><grid><row id="id">{string.Join("", cols.Select(c => $"<cell name=\"{c}\" />"))}</row></grid></layoutxml>
            <fetchxml><fetch><entity name="account">
              {string.Join("", cols.Select(c => $"<attribute name=\"{c}\" />"))}
              {(filter != null ? $"<filter><condition attribute=\"{filter}\" operator=\"eq\" value=\"1\" /></filter>" : "")}
              {(orderAttr != null ? $"<order attribute=\"{orderAttr}\" descending=\"false\" />" : "")}
            </entity></fetch></fetchxml>
          </savedquery>
        </savedqueries>
        """;

    static string OptionSetXml(params (string value, string label)[] opts) => $"""
        <optionsets>
          <optionset Name="av_status">
            <options>
              {string.Join("\n  ", opts.Select(o => $"<option value=\"{o.value}\"><labels><label languagecode=\"1033\" description=\"{o.label}\" /></labels></option>"))}
            </options>
          </optionset>
        </optionsets>
        """;

    static string FormXml(string[] fields, string[] sections = null!, string[] tabs = null!)
    {
        var fieldRows = string.Join("", fields.Select(f => $"<row><cell datafieldname=\"{f}\" /></row>"));
        var sectionXml = string.Join("\n", (sections ?? ["section1"]).Select(s =>
            $"<section name=\"{s}\"><labels><label languagecode=\"1033\" description=\"{s}\" /></labels>" +
            $"<rows>{fieldRows}</rows></section>"));
        var tabXml = string.Join("\n", (tabs ?? ["tab1"]).Select(t =>
            $"<tab name=\"{t}\"><labels><label languagecode=\"1033\" description=\"{t}\" /></labels>" +
            $"<columns><column><sections>{sectionXml}</sections></column></columns></tab>"));
        return $"""
            <forms><systemform>
              <LocalizedNames><LocalizedName languagecode="1033" description="Main Form" /></LocalizedNames>
              <form><tabs>{tabXml}</tabs></form>
            </systemform></forms>
            """;
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
        RunGit("commit", "-m", "add");
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
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    async Task<SolutionChangeSummary.ChangeItem> GetSingleItemAsync()
    {
        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);
        return result.Groups.SelectMany(g => g.Items).Single();
    }

    // ── Entity.xml attribute diff ─────────────────────────────────────────────

    [Fact]
    public async Task Entity_AttributeAdded_SubChangeShowsNameAndType()
    {
        CommitFile("Entities/Account/Entity.xml", EntityXml(("av_existing", "Text")));
        WriteFile("Entities/Account/Entity.xml", EntityXml(("av_existing", "Text"), ("av_new", "Lookup")));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Added &&
            s.Description == "av_new (Lookup)");
    }

    [Fact]
    public async Task Entity_AttributeRemoved_SubChangeShowsNameOnly()
    {
        CommitFile("Entities/Account/Entity.xml", EntityXml(("av_keep", "Text"), ("av_remove", "bit")));
        WriteFile("Entities/Account/Entity.xml", EntityXml(("av_keep", "Text")));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Deleted &&
            s.Description == "av_remove");
    }

    [Fact]
    public async Task Entity_AttributeModified_SubChangeShowsNameOnly()
    {
        CommitFile("Entities/Account/Entity.xml", EntityXml(("av_field", "Text")));
        WriteFile("Entities/Account/Entity.xml", EntityXml(("av_field", "Memo"))); // type changed

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Modified &&
            s.Description == "av_field");
    }

    [Fact]
    public async Task Entity_NewFile_NoSubChanges()
    {
        WriteFile("Entities/Account/Entity.xml", EntityXml(("av_new", "Text")));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Entity_SubChangesOrderedAddedRemovedModified()
    {
        CommitFile("Entities/Account/Entity.xml", EntityXml(("av_remove", "Text"), ("av_modify", "Text")));
        WriteFile("Entities/Account/Entity.xml", EntityXml(("av_add", "Lookup"), ("av_modify", "Memo")));

        var item = await GetSingleItemAsync();

        var changes = item.SubChanges!.ToList();
        changes[0].Status.Should().Be(SolutionChangeSummary.ChangeStatus.Added);
        changes[1].Status.Should().Be(SolutionChangeSummary.ChangeStatus.Deleted);
        changes[2].Status.Should().Be(SolutionChangeSummary.ChangeStatus.Modified);
    }

    // ── SavedQuery view diff ──────────────────────────────────────────────────

    [Fact]
    public async Task View_ColumnAdded_SubChangeShowsColumnName()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name", "telephone1"]));
        WriteFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name", "telephone1", "emailaddress1"]));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Added &&
            s.Description == "emailaddress1");
    }

    [Fact]
    public async Task View_ColumnRemoved_SubChangeShowsColumnName()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name", "telephone1"]));
        WriteFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name"]));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Deleted &&
            s.Description == "telephone1");
    }

    [Fact]
    public async Task View_FilterChanged_SubChangeShowsFilterFlag()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name"]));
        WriteFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name"], filter: "statecode"));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Modified &&
            s.Description == "filter changed");
    }

    [Fact]
    public async Task View_SortChanged_SubChangeShowsSortFlag()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name"]));
        WriteFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name"], orderAttr: "name"));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Modified &&
            s.Description == "sort changed");
    }

    [Fact]
    public async Task View_ColumnAddedOnly_NoFalseFilterFlag()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name"]));
        WriteFile($"Entities/Account/SavedQueries/{{{guid}}}.xml", SavedQueryXml(["name", "telephone1"]));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().NotContain(s => s.Description == "filter changed");
    }

    // ── OptionSet diff ────────────────────────────────────────────────────────

    [Fact]
    public async Task OptionSet_OptionAdded_SubChangeShowsLabelAndValue()
    {
        CommitFile("OptionSets/av_status.xml", OptionSetXml(("100000000", "Active"), ("100000001", "Inactive")));
        WriteFile("OptionSets/av_status.xml", OptionSetXml(("100000000", "Active"), ("100000001", "Inactive"), ("100000002", "Pending")));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Added &&
            s.Description == "Pending (100000002)");
    }

    [Fact]
    public async Task OptionSet_OptionRemoved_SubChangeShowsLabelAndValue()
    {
        CommitFile("OptionSets/av_status.xml", OptionSetXml(("100000000", "Active"), ("100000001", "Inactive")));
        WriteFile("OptionSets/av_status.xml", OptionSetXml(("100000000", "Active")));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Deleted &&
            s.Description == "Inactive (100000001)");
    }

    [Fact]
    public async Task OptionSet_LabelChanged_SubChangeShowsNewLabelAndValue()
    {
        CommitFile("OptionSets/av_status.xml", OptionSetXml(("100000000", "Active")));
        WriteFile("OptionSets/av_status.xml", OptionSetXml(("100000000", "Enabled")));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Modified &&
            s.Description == "Enabled (100000000)");
    }

    [Fact]
    public async Task OptionSet_NewFile_NoSubChanges()
    {
        WriteFile("OptionSets/av_status.xml", OptionSetXml(("100000000", "Active")));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().BeNullOrEmpty();
    }

    // ── FormXml diff ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Form_FieldAdded_SubChangeShowsFieldName()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", FormXml(["av_field1"]));
        WriteFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", FormXml(["av_field1", "av_field2"]));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Added &&
            s.Description == "av_field2");
    }

    [Fact]
    public async Task Form_FieldRemoved_SubChangeShowsFieldName()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", FormXml(["av_field1", "av_field2"]));
        WriteFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", FormXml(["av_field1"]));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Deleted &&
            s.Description == "av_field2");
    }

    [Fact]
    public async Task Form_SectionAdded_SubChangeShowsSectionPrefix()
    {
        var guid = Guid.NewGuid().ToString("D");
        CommitFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", FormXml([], ["section1"]));
        WriteFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", FormXml([], ["section1", "section2"]));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().ContainSingle(s =>
            s.Status == SolutionChangeSummary.ChangeStatus.Added &&
            s.Description.StartsWith("section:"));
    }

    [Fact]
    public async Task Form_NewFile_NoSubChanges()
    {
        var guid = Guid.NewGuid().ToString("D");
        WriteFile($"Entities/Account/FormXml/main/{{{guid}}}.xml", FormXml(["av_field1"]));

        var item = await GetSingleItemAsync();

        item.SubChanges.Should().BeNullOrEmpty();
    }

    // ── Deleted-file sub-change path ──────────────────────────────────────────

    [Fact]
    public async Task Entity_FileDeleted_SubChangesShowAllAttributesAsDeleted()
    {
        CommitFile("Entities/Account/Entity.xml", EntityXml(("av_field1", "Text"), ("av_field2", "Lookup")));
        File.Delete(Path.Combine(_srcFolder, "Entities", "Account", "Entity.xml"));

        var result = await SolutionChangeSummary.ComputeAsync(_srcFolder, _root);
        var item = result.Groups.SelectMany(g => g.Items).Single();

        item.SubChanges.Should().HaveCount(2);
        item.SubChanges!.Should().OnlyContain(s => s.Status == SolutionChangeSummary.ChangeStatus.Deleted);
        item.SubChanges!.Should().Contain(s => s.Description == "av_field1");
        item.SubChanges!.Should().Contain(s => s.Description == "av_field2");
    }
}

public class SolutionChangeSummaryChangesFileTests : IDisposable
{
    readonly string _tempFolder = Path.Combine(Path.GetTempPath(), "flowline-changes-file-tests", Guid.NewGuid().ToString("N"));

    public SolutionChangeSummaryChangesFileTests() => Directory.CreateDirectory(_tempFolder);

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
            Directory.Delete(_tempFolder, true);
    }

    static SolutionChangeSummary Build(params SolutionChangeSummary.ChangeGroup[] groups) =>
        new(groups.SelectMany(g => g.Items).Count(), 0, 0, groups);

    static SolutionChangeSummary.ChangeItem Item(string name, SolutionChangeSummary.ChangeStatus status,
        params SolutionChangeSummary.SubChange[] subChanges) =>
        new(name, [], status, subChanges.Length > 0 ? subChanges : null);

    [Fact]
    public async Task WritesFile_WithEntitySubChanges()
    {
        var summary = Build(
            new SolutionChangeSummary.ChangeGroup("Account", [
                Item("entity metadata", SolutionChangeSummary.ChangeStatus.Modified,
                    new SolutionChangeSummary.SubChange("av_newfield (Text)", SolutionChangeSummary.ChangeStatus.Added),
                    new SolutionChangeSummary.SubChange("av_oldfield", SolutionChangeSummary.ChangeStatus.Deleted))
            ], IsEntity: true));

        await summary.WriteChangesFileAsync(_tempFolder, "TestSln", "Dev");

        var content = await File.ReadAllTextAsync(Path.Combine(_tempFolder, "CHANGES.md"));
        content.Should().Contain("# Changes — TestSln");
        content.Should().Contain("Synced from: Dev");
        content.Should().Contain("## Entities");
        content.Should().Contain("### Account");
        content.Should().Contain("**entity metadata**");
        content.Should().Contain("`+` av_newfield (Text)");
        content.Should().Contain("`-` av_oldfield");
    }

    [Fact]
    public async Task WritesFile_WithOptionSetSubChanges()
    {
        var summary = Build(
            new SolutionChangeSummary.ChangeGroup("OptionSets", [
                Item("av_status", SolutionChangeSummary.ChangeStatus.Modified,
                    new SolutionChangeSummary.SubChange("Active (100000000)", SolutionChangeSummary.ChangeStatus.Added))
            ]));

        await summary.WriteChangesFileAsync(_tempFolder, "TestSln", "Dev");

        var content = await File.ReadAllTextAsync(Path.Combine(_tempFolder, "CHANGES.md"));
        content.Should().Contain("## OptionSets");
        content.Should().Contain("### av_status");
        content.Should().Contain("`+` Active (100000000)");
    }

    [Fact]
    public async Task WritesFile_EntityWithoutSubChanges_ShowsIconLine()
    {
        var summary = Build(
            new SolutionChangeSummary.ChangeGroup("Account", [
                Item("entity metadata", SolutionChangeSummary.ChangeStatus.Added)
            ], IsEntity: true));

        await summary.WriteChangesFileAsync(_tempFolder, "TestSln", null);

        var content = await File.ReadAllTextAsync(Path.Combine(_tempFolder, "CHANGES.md"));
        content.Should().Contain("`+` entity metadata");
        content.Should().NotContain("Synced from:");
    }

    [Fact]
    public async Task DoesNotWriteFile_WhenNoChanges()
    {
        var summary = new SolutionChangeSummary(0, 0, 0, []);

        await summary.WriteChangesFileAsync(_tempFolder, "TestSln", "Dev");

        File.Exists(Path.Combine(_tempFolder, "CHANGES.md")).Should().BeFalse();
    }

    [Fact]
    public async Task CreatesFolder_WhenSlnFolderMissing()
    {
        var missingFolder = Path.Combine(_tempFolder, "nonexistent");
        var summary = Build(
            new SolutionChangeSummary.ChangeGroup("Account", [
                Item("entity metadata", SolutionChangeSummary.ChangeStatus.Modified)
            ], IsEntity: true));

        await summary.WriteChangesFileAsync(missingFolder, "TestSln", "Dev");

        File.Exists(Path.Combine(missingFolder, "CHANGES.md")).Should().BeTrue();
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

        summary.WriteTree(console, "Contoso Dev", verbose: false);

        console.Output.Should().Contain("No changes pulled from Contoso Dev.");
    }

    [Fact]
    public void Write_NoChanges_FallsBackToDevWhenEnvNameNull()
    {
        var console = new TestConsole();
        var summary = Build(0, 0, 0);

        summary.WriteTree(console, null, verbose: false);

        console.Output.Should().Contain("No changes pulled from DEV.");
    }

    [Fact]
    public void Write_WithChanges_PrintsHeadline()
    {
        var console = new TestConsole();
        var summary = Build(3, 47, 23, Group("Account", "entity metadata"));

        summary.WriteTree(console, "Dev", verbose: false);

        console.Output.Should().Contain("Changes (3 files, +47 -23)");
    }

    [Fact]
    public void Write_SingleFile_UsesFileNotFiles()
    {
        var console = new TestConsole();
        var summary = Build(1, 5, 0, Group("Account", "entity metadata"));

        summary.WriteTree(console, "Dev", verbose: false);

        console.Output.Should().Contain("1 file,");
        console.Output.Should().NotContain("1 files");
    }

    [Fact]
    public void Write_DefaultMode_PrintsGroupAndItemsWithoutFilePaths()
    {
        var console = new TestConsole();
        var summary = Build(2, 10, 5,
            Group("Account", "Information (main)", "Active Accounts"));

        summary.WriteTree(console, "Dev", verbose: false);

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

        summary.WriteTree(console, "Dev", verbose: true);

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

        summary.WriteTree(console, "Dev", verbose: false);

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

        summary.WriteTree(console, "Dev", verbose: false);

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

        summary.WriteTree(console, "Dev", verbose: false);

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

        summary.WriteTree(console, "Dev", verbose: false);

        var output = console.Output;
        output.Should().Contain("Account");
        output.Should().Contain("Contact");
        output.Should().Contain("Workflows");
    }
}
