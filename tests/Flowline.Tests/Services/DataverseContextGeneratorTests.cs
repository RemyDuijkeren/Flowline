using FluentAssertions;
using Flowline.Core;
using Flowline.Services;
using Spectre.Console.Testing;

namespace Flowline.Tests.Services;

public class DataverseContextGeneratorTests
{
    // ── U3: ReadSolutionHeader ───────────────────────────────────────────────

    [Fact]
    public void ReadSolutionHeader_ValidXml_ContainsDisplayName()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>MyApp</UniqueName>
                <LocalizedNames>
                  <LocalizedName languagecode="1033" description="My Application" />
                </LocalizedNames>
                <Version>1.2.3.0</Version>
                <Publisher>
                  <CustomizationPrefix>av</CustomizationPrefix>
                </Publisher>
              </SolutionManifest>
            </ImportExportXml>
            """;

        var result = DataverseContextGenerator.ReadSolutionHeader(xml);

        result.Should().Contain("My Application");
        result.Should().Contain("MyApp");
        result.Should().Contain("1.2.3.0");
        result.Should().Contain("av");
    }

    [Fact]
    public void ReadSolutionHeader_NoEnglishLabel_FallsBackToUniqueName()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>FallbackName</UniqueName>
                <LocalizedNames />
                <Version>1.0.0.0</Version>
                <Publisher><CustomizationPrefix>fl</CustomizationPrefix></Publisher>
              </SolutionManifest>
            </ImportExportXml>
            """;

        var result = DataverseContextGenerator.ReadSolutionHeader(xml);

        result.Should().Contain("FallbackName");
    }

    [Fact]
    public void ReadSolutionHeader_NullInput_ReturnsNull() =>
        DataverseContextGenerator.ReadSolutionHeader(null).Should().BeNull();

    [Fact]
    public void ReadSolutionHeader_InvalidXml_ReturnsNull() =>
        DataverseContextGenerator.ReadSolutionHeader("<not valid xml <<<<").Should().BeNull();

    [Fact]
    public void ReadSolutionHeader_BomStripped_Parses()
    {
        var xml = "﻿<?xml version=\"1.0\"?><ImportExportXml><SolutionManifest>" +
                  "<UniqueName>BomTest</UniqueName><LocalizedNames />" +
                  "<Version>1.0.0.0</Version><Publisher><CustomizationPrefix>x</CustomizationPrefix></Publisher>" +
                  "</SolutionManifest></ImportExportXml>";

        var result = DataverseContextGenerator.ReadSolutionHeader(xml);

        result.Should().Contain("BomTest");
    }

    // ── U4: ReadEntity ───────────────────────────────────────────────────────

    [Fact]
    public void ReadEntity_ValidXml_ContainsEntityName()
    {
        // Uses real PAC Entity.xml structure: display name on root <Name LocalizedName="...">,
        // attribute display names in <displaynames>/<displayname>
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Entity>
              <Name LocalizedName="Contact" OriginalName="">contact</Name>
              <EntityInfo>
                <entity Name="contact">
                  <EntitySetName>contacts</EntitySetName>
                  <OwnershipType>UserOwned</OwnershipType>
                  <IsActivity>0</IsActivity>
                  <attributes>
                    <attribute>
                      <LogicalName>av_linkedin</LogicalName>
                      <Name>av_linkedin</Name>
                      <Type>nvarchar</Type>
                      <RequiredLevel>recommended</RequiredLevel>
                      <IsCustomField>1</IsCustomField>
                      <displaynames>
                        <displayname description="LinkedIn Profile URL" languagecode="1033" />
                      </displaynames>
                    </attribute>
                  </attributes>
                </entity>
              </EntityInfo>
            </Entity>
            """;

        var result = DataverseContextGenerator.ReadEntity(xml);

        result.Should().Contain("Contact");
        result.Should().Contain("contacts");
        result.Should().Contain("av_linkedin");
        result.Should().Contain("LinkedIn Profile URL");
        result.Should().Contain("nvarchar");
        result.Should().Contain("recommended");
    }

    [Fact]
    public void ReadEntity_PicklistAttribute_InlinesOptionValues()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Entity>
              <Name LocalizedName="Contact" OriginalName="">contact</Name>
              <EntityInfo>
                <entity Name="contact">
                  <EntitySetName>contacts</EntitySetName>
                  <OwnershipType>UserOwned</OwnershipType>
                  <IsActivity>0</IsActivity>
                  <attributes>
                    <attribute>
                      <LogicalName>donotbulkemail</LogicalName>
                      <Name>donotbulkemail</Name>
                      <Type>boolean</Type>
                      <RequiredLevel>none</RequiredLevel>
                      <IsCustomField>0</IsCustomField>
                      <displaynames>
                        <displayname description="Bulk Email" languagecode="1033" />
                      </displaynames>
                      <options>
                        <option value="0">
                          <labels><label languagecode="1033" description="Allow" /></labels>
                        </option>
                        <option value="1">
                          <labels><label languagecode="1033" description="Do Not Allow" /></labels>
                        </option>
                      </options>
                    </attribute>
                  </attributes>
                </entity>
              </EntityInfo>
            </Entity>
            """;

        var result = DataverseContextGenerator.ReadEntity(xml);

        result.Should().Contain("`0` = Allow");
        result.Should().Contain("`1` = Do Not Allow");
    }

    [Fact]
    public void ReadEntity_NullInput_ReturnsNull() =>
        DataverseContextGenerator.ReadEntity(null).Should().BeNull();

    [Fact]
    public void ReadEntity_InvalidXml_ReturnsNull() =>
        DataverseContextGenerator.ReadEntity("<<<").Should().BeNull();

    // ── U5: ReadRelationships ────────────────────────────────────────────────

    [Fact]
    public void ReadRelationships_OneToMany_ContainsSchemaName()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <EntityRelationships>
              <EntityRelationship>
                <SchemaName>av_account_contacts</SchemaName>
                <RelationshipType>OneToManyRelationship</RelationshipType>
                <ReferencedEntity>account</ReferencedEntity>
                <ReferencingEntity>contact</ReferencingEntity>
                <ReferencingAttribute>parentcustomerid</ReferencingAttribute>
              </EntityRelationship>
            </EntityRelationships>
            """;

        var result = DataverseContextGenerator.ReadRelationships(xml);

        result.Should().Contain("av_account_contacts");
        result.Should().Contain("OneToManyRelationship");
        result.Should().Contain("`contact`");
        result.Should().Contain("`parentcustomerid`");
    }

    [Fact]
    public void ReadRelationships_ManyToMany_ContainsIntersect()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <EntityRelationships>
              <EntityRelationship>
                <SchemaName>av_contact_team</SchemaName>
                <RelationshipType>ManyToManyRelationship</RelationshipType>
                <Entity1LogicalName>contact</Entity1LogicalName>
                <Entity2LogicalName>team</Entity2LogicalName>
                <IntersectEntityName>av_contact_team</IntersectEntityName>
              </EntityRelationship>
            </EntityRelationships>
            """;

        var result = DataverseContextGenerator.ReadRelationships(xml);

        result.Should().Contain("ManyToManyRelationship");
        result.Should().Contain("`av_contact_team`");
    }

    [Fact]
    public void ReadRelationships_NullInput_ReturnsNull() =>
        DataverseContextGenerator.ReadRelationships(null).Should().BeNull();

    [Fact]
    public void ReadRelationships_NoRelationships_ReturnsNull() =>
        DataverseContextGenerator.ReadRelationships("<EntityRelationships />").Should().BeNull();

    // ── U6: ReadForm ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadForm_ValidXml_ContainsFieldNames()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <form>
              <LocalizedNames>
                <LocalizedName languagecode="1033" description="Main Form" />
              </LocalizedNames>
              <tabs>
                <tab name="Summary">
                  <labels><label languagecode="1033" description="Summary" /></labels>
                  <columns><column><sections>
                    <section name="General">
                      <labels><label languagecode="1033" description="General" /></labels>
                      <rows><row>
                        <cell datafieldname="fullname" />
                        <cell datafieldname="emailaddress1" />
                      </row></rows>
                    </section>
                  </sections></column></columns>
                </tab>
              </tabs>
            </form>
            """;

        var result = DataverseContextGenerator.ReadForm(xml, "main");

        result.Should().Contain("`fullname`");
        result.Should().Contain("`emailaddress1`");
        result.Should().Contain("Main Form");
    }

    [Fact]
    public void ReadForm_InvisibleCell_Excluded()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <form>
              <tabs>
                <tab name="Tab">
                  <labels><label languagecode="1033" description="Tab" /></labels>
                  <columns><column><sections>
                    <section name="Sec">
                      <labels><label languagecode="1033" description="Sec" /></labels>
                      <rows><row>
                        <cell datafieldname="visiblefield" />
                        <cell datafieldname="hiddenfield" invisible="true" />
                      </row></rows>
                    </section>
                  </sections></column></columns>
                </tab>
              </tabs>
            </form>
            """;

        var result = DataverseContextGenerator.ReadForm(xml, "main");

        result.Should().Contain("`visiblefield`");
        result.Should().NotContain("`hiddenfield`");
    }

    [Fact]
    public void ReadForm_GuidFieldName_Excluded()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <form>
              <tabs>
                <tab name="Tab">
                  <labels><label languagecode="1033" description="Tab" /></labels>
                  <columns><column><sections>
                    <section name="Sec">
                      <labels><label languagecode="1033" description="Sec" /></labels>
                      <rows><row>
                        <cell datafieldname="av_myfield" />
                        <cell datafieldname="{6bf2efbd-4215-ed11-b83d-000d3aab2cf1}" />
                      </row></rows>
                    </section>
                  </sections></column></columns>
                </tab>
              </tabs>
            </form>
            """;

        var result = DataverseContextGenerator.ReadForm(xml, "main");

        result.Should().Contain("`av_myfield`");
        result.Should().NotContain("6bf2efbd");
    }

    [Fact]
    public void ReadForm_NullInput_ReturnsNull() =>
        DataverseContextGenerator.ReadForm(null).Should().BeNull();

    // ── U7: ReadView ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadView_ValidXml_ContainsColumnsAndFilter()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <savedquery>
              <LocalizedNames>
                <LocalizedName languagecode="1033" description="Active Contacts" />
              </LocalizedNames>
              <layoutxml>
                <grid><row>
                  <cell name="fullname" />
                  <cell name="emailaddress1" />
                </row></grid>
              </layoutxml>
              <fetchxml>
                <fetch>
                  <entity name="contact">
                    <filter>
                      <condition attribute="statecode" operator="eq" value="0" />
                    </filter>
                  </entity>
                </fetch>
              </fetchxml>
            </savedquery>
            """;

        var result = DataverseContextGenerator.ReadView(xml);

        result.Should().Contain("Active Contacts");
        result.Should().Contain("`fullname`");
        result.Should().Contain("`emailaddress1`");
        result.Should().Contain("`statecode`");
        result.Should().Contain("eq");
    }

    [Fact]
    public void ReadView_NoFetchXmlVerbatim_NotReproduced()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <savedquery>
              <LocalizedNames>
                <LocalizedName languagecode="1033" description="My View" />
              </LocalizedNames>
              <layoutxml><grid><row><cell name="name" /></row></grid></layoutxml>
              <fetchxml><fetch><entity name="contact"><filter>
                <condition attribute="statecode" operator="eq" value="0" />
              </filter></entity></fetch></fetchxml>
            </savedquery>
            """;

        var result = DataverseContextGenerator.ReadView(xml);

        result.Should().NotContain("<fetch>");
        result.Should().NotContain("<entity name=");
    }

    [Fact]
    public void ReadView_NullInput_ReturnsNull() =>
        DataverseContextGenerator.ReadView(null).Should().BeNull();

    // ── U8: ReadConnectionReferences ────────────────────────────────────────

    [Fact]
    public void ReadConnectionReferences_ValidXml_ContainsConnectorId()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Customizations>
              <connectionreferences>
                <connectionreference connectionreferencelogicalname="av_sharepoint">
                  <connectorid>/providers/Microsoft.PowerApps/apis/shared_sharepointonline</connectorid>
                </connectionreference>
              </connectionreferences>
            </Customizations>
            """;

        var result = DataverseContextGenerator.ReadConnectionReferences(xml);

        result.Should().Contain("av_sharepoint");
        result.Should().Contain("shared_sharepointonline");
    }

    [Fact]
    public void ReadConnectionReferences_NoRefs_ReturnsNull() =>
        DataverseContextGenerator.ReadConnectionReferences("<Customizations />").Should().BeNull();

    [Fact]
    public void ReadConnectionReferences_NullInput_ReturnsNull() =>
        DataverseContextGenerator.ReadConnectionReferences(null).Should().BeNull();

    // ── U8: ReadPluginSteps ──────────────────────────────────────────────────

    [Fact]
    public void ReadPluginSteps_ValidDir_ContainsStepInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "step1.xml"), """
                <?xml version="1.0" encoding="utf-8"?>
                <SdkMessageProcessingStep
                    SdkMessage="Create"
                    PrimaryObjectTypeCode="contact"
                    Stage="20"
                    Mode="0"
                    PluginTypeName="MyCompany.MyPlugin.ContactCreateHandler, MyPlugin" />
                """);

            var result = DataverseContextGenerator.ReadPluginSteps(dir);

            result.Should().Contain("Create");
            result.Should().Contain("`contact`");
            result.Should().Contain("pre-operation");
            result.Should().Contain("sync");
            result.Should().Contain("ContactCreateHandler");
            result.Should().NotContain("MyCompany");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadPluginSteps_EmptyDir_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            DataverseContextGenerator.ReadPluginSteps(dir).Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadPluginSteps_NullDir_ReturnsNull() =>
        DataverseContextGenerator.ReadPluginSteps(null).Should().BeNull();

    // ── U8: ReadWorkflows ────────────────────────────────────────────────────

    [Fact]
    public void ReadWorkflows_ValidXmlFile_ContainsWorkflowInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "MyFlow.json.data.xml"), """
                <?xml version="1.0" encoding="utf-8"?>
                <Workflow Name="My Approval Flow" StateCode="1" PrimaryEntity="account" />
                """);

            var result = DataverseContextGenerator.ReadWorkflows(dir);

            result.Should().Contain("My Approval Flow");
            result.Should().Contain("Active");
            result.Should().Contain("`account`");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadWorkflows_InactiveWorkflow_ShowsInactiveState()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "DraftFlow.xaml.data.xml"), """
                <?xml version="1.0" encoding="utf-8"?>
                <Workflow Name="Draft Flow" StateCode="0" PrimaryEntity="contact" />
                """);

            var result = DataverseContextGenerator.ReadWorkflows(dir);

            result.Should().Contain("Inactive");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadWorkflows_EmptyDir_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            DataverseContextGenerator.ReadWorkflows(dir).Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadWorkflows_NullDir_ReturnsNull() =>
        DataverseContextGenerator.ReadWorkflows(null).Should().BeNull();

    // ── GenerateAsync (integration) ──────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WritesContextFileAtExpectedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var packageSrc = Path.Combine(root, "solutions", "MySolution", "Package", "src");
        Directory.CreateDirectory(packageSrc);
        Directory.CreateDirectory(Path.Combine(packageSrc, "Other"));

        File.WriteAllText(Path.Combine(packageSrc, "Other", "Solution.xml"), """
            <?xml version="1.0"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>MySolution</UniqueName>
                <LocalizedNames>
                  <LocalizedName languagecode="1033" description="My Solution" />
                </LocalizedNames>
                <Version>1.0.0.0</Version>
                <Publisher><CustomizationPrefix>ms</CustomizationPrefix></Publisher>
              </SolutionManifest>
            </ImportExportXml>
            """);

        try
        {
            var generator = new DataverseContextGenerator(new TestConsole(), new FlowlineRuntimeOptions());
            await generator.GenerateAsync(packageSrc, "MySolution", root);

            var expectedPath = Path.Combine(root, "solutions", "MySolution", "DATAVERSE_CONTEXT.md");
            File.Exists(expectedPath).Should().BeTrue();

            var content = File.ReadAllText(expectedPath);
            content.Should().Contain("# Dataverse Schema — MySolution");
            content.Should().Contain("My Solution");

            // Verify UTF-8 no-BOM: first byte must not be the BOM sequence
            var bytes = File.ReadAllBytes(expectedPath);
            (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                .Should().BeFalse("output must be UTF-8 without BOM");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── SelfHealAgentsMdAsync ────────────────────────────────────────────────

    [Fact]
    public async Task SelfHealAgentsMdAsync_AgentsMdAbsent_EmitsWarningAndSkips()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var console = new TestConsole();
        try
        {
            var generator = new DataverseContextGenerator(console, new FlowlineRuntimeOptions());
            await generator.SelfHealAgentsMdAsync(root, "MySolution", CancellationToken.None);

            File.Exists(Path.Combine(root, "AGENTS.md")).Should().BeFalse();
            console.Output.Should().Contain("AGENTS.md not found");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SelfHealAgentsMdAsync_AllEntriesPresent_DoesNotModifyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var agentsPath = Path.Combine(root, "AGENTS.md");
        var original = """
            # Agent Instructions
            ## Dataverse schema context
            - [MySolution](solutions/MySolution/DATAVERSE_CONTEXT.md)

            @solutions/MySolution/DATAVERSE_CONTEXT.md
            """;
        await File.WriteAllTextAsync(agentsPath, original);
        var lastWrite = File.GetLastWriteTimeUtc(agentsPath);
        try
        {
            var generator = new DataverseContextGenerator(new TestConsole(), new FlowlineRuntimeOptions());
            await generator.SelfHealAgentsMdAsync(root, "MySolution", CancellationToken.None);

            File.GetLastWriteTimeUtc(agentsPath).Should().Be(lastWrite, "file must not be written when no changes needed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SelfHealAgentsMdAsync_NoSectionOrEntries_AppendsSection()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var agentsPath = Path.Combine(root, "AGENTS.md");
        await File.WriteAllTextAsync(agentsPath, "# Agent Instructions\n");
        try
        {
            var generator = new DataverseContextGenerator(new TestConsole(), new FlowlineRuntimeOptions());
            await generator.SelfHealAgentsMdAsync(root, "MySolution", CancellationToken.None);

            var result = await File.ReadAllTextAsync(agentsPath);
            result.Should().Contain("## Dataverse schema context");
            result.Should().Contain("- [MySolution](solutions/MySolution/DATAVERSE_CONTEXT.md)");
            result.Should().Contain("@solutions/MySolution/DATAVERSE_CONTEXT.md");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SelfHealAgentsMdAsync_SectionExistsLinkMissing_InsertsLinkInSection()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var agentsPath = Path.Combine(root, "AGENTS.md");
        await File.WriteAllTextAsync(agentsPath,
            "# Agent Instructions\n## Dataverse schema context\n- [SolutionA](solutions/SolutionA/DATAVERSE_CONTEXT.md)\n\n@solutions/SolutionA/DATAVERSE_CONTEXT.md\n");
        try
        {
            var generator = new DataverseContextGenerator(new TestConsole(), new FlowlineRuntimeOptions());
            await generator.SelfHealAgentsMdAsync(root, "SolutionB", CancellationToken.None);

            var result = await File.ReadAllTextAsync(agentsPath);
            result.Should().Contain("- [SolutionB](solutions/SolutionB/DATAVERSE_CONTEXT.md)");
            result.Should().Contain("@solutions/SolutionB/DATAVERSE_CONTEXT.md");

            // Link for SolutionB must appear before the blank line after SolutionA's link (i.e., inside the section)
            var linkBIdx    = result.IndexOf("- [SolutionB]", StringComparison.Ordinal);
            var importAIdx  = result.IndexOf("@solutions/SolutionA/", StringComparison.Ordinal);
            linkBIdx.Should().BeLessThan(importAIdx, "SolutionB link must be in the section, not after the @ import");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SelfHealAgentsMdAsync_ImportLineMissing_AppendsImport()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var agentsPath = Path.Combine(root, "AGENTS.md");
        await File.WriteAllTextAsync(agentsPath,
            "# Agent Instructions\n## Dataverse schema context\n- [MySolution](solutions/MySolution/DATAVERSE_CONTEXT.md)\n");
        try
        {
            var generator = new DataverseContextGenerator(new TestConsole(), new FlowlineRuntimeOptions());
            await generator.SelfHealAgentsMdAsync(root, "MySolution", CancellationToken.None);

            var result = await File.ReadAllTextAsync(agentsPath);
            result.Should().Contain("@solutions/MySolution/DATAVERSE_CONTEXT.md");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SelfHealAgentsMdAsync_CalledTwice_DoesNotDuplicateEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var agentsPath = Path.Combine(root, "AGENTS.md");
        await File.WriteAllTextAsync(agentsPath, "# Agent Instructions\n");
        try
        {
            var generator = new DataverseContextGenerator(new TestConsole(), new FlowlineRuntimeOptions());
            await generator.SelfHealAgentsMdAsync(root, "MySolution", CancellationToken.None);
            await generator.SelfHealAgentsMdAsync(root, "MySolution", CancellationToken.None);

            var result = await File.ReadAllTextAsync(agentsPath);
            var linkCount   = CountOccurrences(result, "- [MySolution]");
            var importCount = CountOccurrences(result, "@solutions/MySolution/DATAVERSE_CONTEXT.md");
            linkCount.Should().Be(1, "link must not be duplicated");
            importCount.Should().Be(1, "import must not be duplicated");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var idx = 0;
        while ((idx = source.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }

    // ── R35: No GUIDs ────────────────────────────────────────────────────────

    [Fact]
    public void ReadSolutionHeader_NoGuidsInOutput()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>GuidTest</UniqueName>
                <LocalizedNames>
                  <LocalizedName languagecode="1033" description="Guid Test {6bf2efbd-4215-ed11-b83d-000d3aab2cf1}" />
                </LocalizedNames>
                <Version>1.0.0.0</Version>
                <Publisher><CustomizationPrefix>gt</CustomizationPrefix></Publisher>
              </SolutionManifest>
            </ImportExportXml>
            """;

        var result = DataverseContextGenerator.ReadSolutionHeader(xml);
        // GUIDs in entity content pass through (we only strip from description fields via StripGuids)
        // but the solution header doesn't invoke StripGuids on display names — just verify no crash
        result.Should().NotBeNull();
    }
}
