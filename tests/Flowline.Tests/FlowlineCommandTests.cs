using Flowline;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class FlowlineCommandTests
{
    [Theory]
    [InlineData(  0, "0ms")]
    [InlineData(500, "500ms")]
    [InlineData(999, "999ms")]
    public void FormatDuration_UnderOneSecond_ShowsMilliseconds(int ms, string expected)
    {
        FlowlineCommand<FlowlineSettings>.FormatDuration(TimeSpan.FromMilliseconds(ms))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(1,  "1s")]
    [InlineData(59, "59s")]
    public void FormatDuration_UnderOneMinute_ShowsSeconds(int seconds, string expected)
    {
        FlowlineCommand<FlowlineSettings>.FormatDuration(TimeSpan.FromSeconds(seconds))
            .Should().Be(expected);
    }

    [Fact]
    public void PackageFolder_ReturnsSlnFolderWithSolutionSubfolder()
    {
        // The folder name is role-based and identical in every repo — only the .cdsproj inside it
        // carries the solution's identity.
        var slnFolder = Path.Combine("repos", "MyProject");
        FlowlineCommand<FlowlineSettings>.PackageFolder(slnFolder)
            .Should().Be(Path.Combine("repos", "MyProject", "Solution"));
    }

    [Fact]
    public void RedactSensitiveArgs_ClientSecret_ReplacesWithAsterisks()
    {
        SubprocessCapture.RedactSensitiveArgs("--client-secret abc123")
            .Should().Contain("***");
    }

    [Theory]
    [InlineData(60,  "1m 0s")]
    [InlineData(61,  "1m 1s")]
    [InlineData(90,  "1m 30s")]
    [InlineData(120, "2m 0s")]
    [InlineData(125, "2m 5s")]
    public void FormatDuration_OneMinuteOrMore_ShowsMinutesAndSeconds(int seconds, string expected)
    {
        FlowlineCommand<FlowlineSettings>.FormatDuration(TimeSpan.FromSeconds(seconds))
            .Should().Be(expected);
    }

    [Fact]
    public void FindProjectRoot_WhenConfigInStartDir_ReturnsStartDir()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, ".flowline"), "{}");

        var result = FlowlineCommand<FlowlineSettings>.FindProjectRoot(root);

        result.Should().Be(root);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void FindProjectRoot_WhenConfigInParentDir_ReturnsParent()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, ".flowline"), "{}");
        var sub = Directory.CreateDirectory(Path.Combine(root, "deep", "nested")).FullName;

        var result = FlowlineCommand<FlowlineSettings>.FindProjectRoot(sub);

        result.Should().Be(root);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void FindProjectRoot_WhenNoConfigAnywhere_ReturnsNull()
    {
        var isolated = Directory.CreateTempSubdirectory().FullName;

        var result = FlowlineCommand<FlowlineSettings>.FindProjectRoot(isolated);

        result.Should().BeNull();
        Directory.Delete(isolated, recursive: true);
    }

    // Minimal concrete subclass — FlowlineCommand<TSettings> is abstract, and ConnectToDataverseAsync /
    // GetAndCheckEnvironmentInfoAsync are protected, so a test-local subclass is the only seam available.
    sealed class TestCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture)
        : FlowlineCommand<FlowlineSettings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
    {
        protected override Task<int> ExecuteFlowlineAsync(CommandContext context, FlowlineSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<(IOrganizationServiceAsync2 Connection, PacProfile Profile)> Connect(
            DataverseConnector connector, string environmentUrl, CancellationToken cancellationToken, PacProfile? resolvedProfile = null) =>
            ConnectToDataverseAsync(connector, environmentUrl, cancellationToken, resolvedProfile);
    }

    static TestCommand MakeCommand(ProfileResolutionService profileResolutionService)
    {
        var console = new TestConsole();
        return new TestCommand(console, new FlowlineRuntimeOptions(), profileResolutionService, NullLoggerFactory.Instance, new SubprocessCapture(console));
    }

    [Fact]
    public async Task ConnectToDataverseAsync_WithResolvedProfile_DoesNotReResolve()
    {
        const string environmentUrl = "https://contoso.crm4.dynamics.com";
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var givenProfile = new PacProfile { Name = "Given", Resource = environmentUrl };
        var resolveCalls = 0;
        var profileService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => { resolveCalls++; return new ProfileFound(givenProfile); },
            IsProfileActiveOverride = _ => true
        };
        var command = MakeCommand(profileService);

        try
        {
            await command.Connect(connector, environmentUrl, CancellationToken.None, resolvedProfile: givenProfile);
        }
        catch (InvalidOperationException)
        {
            // Expected — no real PAC CLI auth cache in this test environment; ConnectViaPacAsync fails
            // past the point this test cares about. Only the resolve-call count matters here.
        }

        resolveCalls.Should().Be(0);
    }

    [Fact]
    public async Task ConnectToDataverseAsync_WithoutResolvedProfile_ResolvesExactlyOnce()
    {
        const string environmentUrl = "https://contoso.crm4.dynamics.com";
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var resolvedProfile = new PacProfile { Name = "Resolved", Resource = environmentUrl };
        var resolveCalls = 0;
        var profileService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => { resolveCalls++; return new ProfileFound(resolvedProfile); },
            IsProfileActiveOverride = _ => true
        };
        var command = MakeCommand(profileService);

        try
        {
            await command.Connect(connector, environmentUrl, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Expected — see note above.
        }

        resolveCalls.Should().Be(1);
    }
}
