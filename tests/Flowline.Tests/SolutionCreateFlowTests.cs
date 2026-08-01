using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using FluentAssertions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class SolutionCreateFlowTests
{
    const string DevUrl = "https://contoso-dev.crm4.dynamics.com";

    static EnvironmentInfo MakeDevEnv() => new() { DisplayName = "Contoso Dev", EnvironmentUrl = DevUrl, Type = "Sandbox" };

    static readonly Func<ProjectSolution, string, string, CancellationToken, Task<int?>> s_succeedingBuild =
        (_, _, _, _) => Task.FromResult<int?>(null);

    static Func<ProjectSolution, string, string, CancellationToken, Task<int?>> FailingBuild(int exitCode) =>
        (_, _, _, _) => Task.FromResult<int?>(exitCode);

    static (SolutionCreateFlow Flow, TestConsole Console, IOrganizationServiceAsync2 OrgService) MakeFlow()
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => new ProfileFound(new PacProfile { Name = "Contoso", Resource = DevUrl }),
            IsProfileActiveOverride = _ => true
        };

        var orgService = Substitute.For<IOrganizationServiceAsync2>();
        // Default: no existing publisher, no existing solution -- individual tests override.
        orgService.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var capture = new SubprocessCapture(console);
        var solutionCreateService = new SolutionCreateService();
        var createSolutionService = new CreateSolutionService(console, capture);

        var flow = new SolutionCreateFlow(console, profileResolutionService, connector, solutionCreateService, createSolutionService)
        {
            ConnectOverride = (_, _, _) => Task.FromResult(orgService)
        };

        return (flow, console, orgService);
    }

    static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "flowline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // Pre-seeds a root so the scaffold's pull/plugins/webresources steps all hit their
    // "already there -- skipping" branches instead of shelling out to a real pac/dotnet process --
    // the same trick CloneCommandTests.ScaffoldSolutionAsync uses to keep these tests process-free.
    static void SeedScaffoldedRoot(string root, string uniqueName)
    {
        var dataverseSolutionFolder = Path.Combine(root, "Solution");
        Directory.CreateDirectory(Path.Combine(dataverseSolutionFolder, "src"));
        File.WriteAllText(Path.Combine(dataverseSolutionFolder, $"{uniqueName}.cdsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var pluginsFolder = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(pluginsFolder);
        File.WriteAllText(Path.Combine(pluginsFolder, $"{uniqueName}.Plugins.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var webresourcesFolder = Path.Combine(root, "WebResources");
        Directory.CreateDirectory(webresourcesFolder);
        File.WriteAllText(Path.Combine(webresourcesFolder, $"{uniqueName}.WebResources.csproj"), "<Project Sdk=\"Microsoft.Build.NoTargets/1.0\"></Project>");
    }

    // ── R14/R19: pre-write validation runs before anything else ────────────────

    [Fact]
    public async Task RunAsync_InvalidUniqueName_ThrowsBeforeConnecting()
    {
        var (flow, _, orgService) = MakeFlow();
        var root = CreateTempRoot();
        try
        {
            var act = () => flow.RunAsync(MakeDevEnv(), "class", null, "acme", null, root, new ProjectConfig(), s_succeedingBuild, CancellationToken.None);

            (await act.Should().ThrowAsync<FlowlineException>()).Which.Message.Should().Contain("keyword");
            await orgService.DidNotReceiveWithAnyArgs().RetrieveMultipleAsync(default!, default);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── R5/AE8: no --publisher-prefix + no TTY — errors naming the flag, never connects ──

    [Fact]
    public async Task RunAsync_NoPublisherPrefix_NonInteractive_ThrowsNamingFlag_WithoutConnecting()
    {
        var (flow, _, orgService) = MakeFlow();
        flow.IsInteractiveOverride = () => false;
        var root = CreateTempRoot();
        try
        {
            var act = () => flow.RunAsync(MakeDevEnv(), "MySolution", null, null, null, root, new ProjectConfig(), s_succeedingBuild, CancellationToken.None);

            (await act.Should().ThrowAsync<FlowlineException>()).Which.Message.Should().Contain("--publisher-prefix");
            await orgService.DidNotReceiveWithAnyArgs().RetrieveMultipleAsync(default!, default);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── R5/AE4: no --publisher-prefix + interactive — picker lists existing publishers + create-new ──

    [Fact]
    public async Task PickPublisherPrefixAsync_ExistingPublishers_ListsThemPlusCreateNewChoice()
    {
        var (flow, console, orgService) = MakeFlow();
        var existing = new Entity("publisher") { ["customizationprefix"] = "acme", ["friendlyname"] = "Acme Corp" };
        orgService.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([existing])));

        console.Interactive();
        console.Input.PushKey(ConsoleKey.Enter); // selects the first listed choice ("acme — Acme Corp")

        var selected = await flow.PickPublisherPrefixAsync(orgService, CancellationToken.None);

        selected.Should().Be("acme");
        console.Output.Should().Contain("acme").And.Contain("Create new publisher");
    }

    [Fact]
    public async Task PickPublisherPrefixAsync_CreateNewChoice_PromptsForPrefix()
    {
        var (flow, console, orgService) = MakeFlow();
        // No existing publishers configured -- MakeFlow's default returns an empty EntityCollection,
        // so the only choice is "+ Create new publisher".
        console.Interactive();
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("newco");

        var selected = await flow.PickPublisherPrefixAsync(orgService, CancellationToken.None);

        selected.Should().Be("newco");
    }

    // ── R10/R16: DEV role written only after a full success; a post-create failure reports the ──
    // ── created IDs and skips the write ──

    [Fact]
    public async Task RunAsync_FullSuccess_WritesDevRoleAndEmitsConfirmation()
    {
        var (flow, console, orgService) = MakeFlow();
        var publisherId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        orgService.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "publisher")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("publisher", publisherId)])));
        orgService.CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(solutionId));

        var root = CreateTempRoot();
        try
        {
            SeedScaffoldedRoot(root, "MySolution");
            var config = new ProjectConfig();

            var exitCode = await flow.RunAsync(MakeDevEnv(), "MySolution", null, "acme", null, root, config, s_succeedingBuild, CancellationToken.None);

            exitCode.Should().Be(0);
            config.DevUrl.Should().Be(DevUrl);
            console.Output.Should().Contain("DEV set to");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_BuildFailsAfterCreate_ReportsCreatedIdsForCleanup_DoesNotWriteDevRole()
    {
        var (flow, console, orgService) = MakeFlow();
        var publisherId = Guid.NewGuid();
        var solutionId = Guid.NewGuid();
        orgService.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "publisher")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("publisher", publisherId)])));
        orgService.CreateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(solutionId));

        var root = CreateTempRoot();
        try
        {
            SeedScaffoldedRoot(root, "MySolution");
            var config = new ProjectConfig();

            var exitCode = await flow.RunAsync(MakeDevEnv(), "MySolution", null, "acme", null, root, config, FailingBuild(13), CancellationToken.None);

            exitCode.Should().Be(13);
            config.DevUrl.Should().BeNull();
            console.Output.Should().Contain(publisherId.ToString()).And.Contain(solutionId.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── KTD3(a): humanize ────────────────────────────────────────────────────

    [Theory]
    [InlineData("MySolution", "My Solution")]
    [InlineData("DWE_Base", "DWE Base")]
    [InlineData("APIGateway", "API Gateway")]
    [InlineData("contoso", "contoso")]
    public void Humanize_ProducesExpectedSpacing(string uniqueName, string expected)
    {
        SolutionCreateFlow.Humanize(uniqueName).Should().Be(expected);
    }
}
