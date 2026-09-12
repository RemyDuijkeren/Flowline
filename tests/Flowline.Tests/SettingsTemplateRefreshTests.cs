using FluentAssertions;
using Flowline.Commands;
using Flowline.Core.Configure;
using Flowline.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests;

/// <summary>
/// Covers <see cref="PullCommand.RefreshSharedTemplateAsync"/> and its report line — the R17/KTD14 refresh
/// <c>clone</c> and <c>pull</c> (<see cref="PullCommand"/>) both run after writing fresh solution source.
/// The report formatting is plain string composition (no I/O); the location and ordering tests establish
/// structural facts without a checkout; the end-to-end test runs the real `pac solution create-settings`
/// against a hand-built unpacked solution folder, which is what proves no environment or PAC login is
/// needed — the same shape as <see cref="DiffCommandTests"/>'s "never constructs a Dataverse connection".
/// </summary>
public class SettingsTemplateReportTests
{
    [Fact]
    public void FormatTemplateReport_FirstCreation_NoChanges_SaysCreated()
    {
        PullCommand.FormatTemplateReport(created: true, "deploymentSettings.json", added: [], vanished: [])
            .Should().Be("Created deploymentSettings.json — no changes.");
    }

    [Fact]
    public void FormatTemplateReport_Refresh_NoChanges_SaysRefreshed()
    {
        PullCommand.FormatTemplateReport(created: false, "deploymentSettings.json", added: [], vanished: [])
            .Should().Be("Refreshed deploymentSettings.json — no changes.");
    }

    [Fact]
    public void FormatTemplateReport_NamesWhatAppearedAndVanished_NotTheWholeFile()
    {
        var message = PullCommand.FormatTemplateReport(created: false, "deploymentSettings.json",
            added: ["EnvironmentVariables: cr123_NewFlag"], vanished: ["ConnectionReferences: cr123_Retired"]);

        message.Should().Be("Refreshed deploymentSettings.json (+EnvironmentVariables: cr123_NewFlag; -ConnectionReferences: cr123_Retired)");
    }

    [Fact]
    public void FormatTemplateReport_EscapesMarkupInAddedAndVanishedNames()
    {
        var message = PullCommand.FormatTemplateReport(created: false, "deploymentSettings.json",
            added: ["EnvironmentVariables: cr123_[Flag]"], vanished: []);

        message.Should().Contain("[[Flag]]");
    }
}

/// <summary>
/// The shared template lands beside the .cdsproj, not under the unpacked source `sync`'s uncommitted-changes
/// gate scans — proving the write can never trip that gate regardless of where it runs relative to it.
/// </summary>
public class SettingsTemplateLocationTests
{
    [Fact]
    public void SharedTemplatePath_IsOutsideTheSrcFolderTheDirtyCheckScans()
    {
        var dataverseSolutionFolder = Path.Combine(Path.GetTempPath(), "flowline-loc-test", "Solution");
        var srcPath = Path.Combine(dataverseSolutionFolder, "src"); // SyncCommand.ExecuteFlowlineAsync's own srcPath

        var location = SettingsFileLocator.Locate(dataverseSolutionFolder, role: null, explicitPath: null, forWriting: true);

        location.Path.Should().NotStartWith(srcPath);
        location.Path.Should().Be(Path.Combine(dataverseSolutionFolder, SettingsFileLocator.SharedFileName));
    }
}

/// <remarks>
/// Both tests here run <c>pac solution create-settings</c> for real, so both are skipped, following the
/// convention <c>ProjectScaffolderPluginsTests</c> and <c>DataverseConnectorTests</c> already use: CI has
/// no <c>pac</c> (no setup step in <c>.github/workflows/ci.yml</c>, unlike <c>dotnet</c>). Run them
/// locally before trusting a change to the template refresh.
///
/// Without the skip this did not merely fail. <c>PacUtils</c> ends the process when <c>pac</c> is
/// missing, so the test host crashed and took roughly six hundred unrelated tests with it, reporting
/// 1043 of 1649 and a green-looking count for everything that never ran.
///
/// The collection is kept even though both tests are skipped: it stops the class being scheduled beside
/// <c>PacUtilsTests</c>, which replaces the process-wide probe that decides whether <c>pac</c> exists,
/// and it is what these tests need the moment anyone un-skips them locally.
/// </remarks>
[Collection(PacResolutionCollection.Name)]
public class RefreshSharedTemplateAsyncTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-template-refresh-" + Guid.NewGuid().ToString("N"));

    public RefreshSharedTemplateAsyncTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // A minimal folder `pac solution create-settings --solution-folder` accepts: just enough of the
    // unpacked shape (Other/Solution.xml + Other/Customizations.xml) to run without a live environment.
    const string MinimalSolutionXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ImportExportXml version="9.2.0.0" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="crmliveservice">
          <SolutionManifest>
            <UniqueName>flowlinetest</UniqueName>
            <LocalizedNames><LocalizedName description="FlowlineTest" languagecode="1033" /></LocalizedNames>
            <Descriptions />
            <Version>1.0.0.0</Version>
            <Managed>0</Managed>
            <Publisher>
              <UniqueName>flowline</UniqueName>
              <LocalizedNames><LocalizedName description="Flowline" languagecode="1033" /></LocalizedNames>
              <Descriptions />
              <EMailAddress xsi:nil="true" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />
              <SupportingWebsiteUrl xsi:nil="true" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />
              <CustomizationPrefix>cr123</CustomizationPrefix>
              <CustomizationOptionValuePrefix>10000</CustomizationOptionValuePrefix>
            </Publisher>
            <RootComponents />
            <MissingDependencies />
          </SolutionManifest>
        </ImportExportXml>
        """;

    const string MinimalCustomizationsXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ImportExportXml version="9.2.0.0" SolutionPackageVersion="9.2" languagecode="1033" generatedBy="crmliveservice">
          <Entities />
          <Roles />
          <Workflows />
          <FieldSecurityProfiles />
          <Templates />
          <EntityMaps />
          <EntityRelationships />
          <OrganizationSettings />
          <optionsets />
          <CustomControls />
          <EntityDataProviders />
          <Languages><Language>1033</Language></Languages>
        </ImportExportXml>
        """;

    void WriteMinimalUnpackedSolution()
    {
        var other = Path.Combine(_root, "src", "Other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "Solution.xml"), MinimalSolutionXml);
        File.WriteAllText(Path.Combine(other, "Customizations.xml"), MinimalCustomizationsXml);
    }

    // Proves R17/KTD14's "no environment connection" claim end to end: no PAC login, no DataverseConnector,
    // no IOrganizationServiceAsync2 is constructed anywhere in this test, and the real `pac` subprocess still
    // succeeds — the ALREADY VERIFIED claim this unit was built on, reproduced through the command's own
    // entry point rather than a bare `pac` invocation.
    [Fact(Skip = "Requires the pac CLI on PATH — not available in CI; run locally to verify the real write")]
    public async Task RefreshSharedTemplateAsync_NoEnvironmentOrAuth_WritesTheSharedTemplate()
    {
        WriteMinimalUnpackedSolution();
        var console = new TestConsole();
        var capture = new SubprocessCapture(console);

        await PullCommand.RefreshSharedTemplateAsync(console, _root, _root, capture, NullLogger.Instance, CancellationToken.None);

        var path = Path.Combine(_root, SettingsFileLocator.SharedFileName);
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("EnvironmentVariables").And.Contain("ConnectionReferences");
        console.Output.Should().Contain("Created");
    }

    [Fact(Skip = "Requires the pac CLI on PATH — not available in CI; run locally to verify the real write")]
    public async Task RefreshSharedTemplateAsync_SecondRun_MergesRatherThanReplacing()
    {
        WriteMinimalUnpackedSolution();
        var console = new TestConsole();
        var capture = new SubprocessCapture(console);
        var path = Path.Combine(_root, SettingsFileLocator.SharedFileName);

        await PullCommand.RefreshSharedTemplateAsync(console, _root, _root, capture, NullLogger.Instance, CancellationToken.None);
        console.Output.Should().Contain("Created");

        // Second run against the same, unchanged solution: the file already exists, so this is a merge,
        // not a first creation — and it must not throw or need a connection either.
        await PullCommand.RefreshSharedTemplateAsync(console, _root, _root, capture, NullLogger.Instance, CancellationToken.None);

        console.Output.Should().Contain("Refreshed");
        File.Exists(path).Should().BeTrue();
    }
}
