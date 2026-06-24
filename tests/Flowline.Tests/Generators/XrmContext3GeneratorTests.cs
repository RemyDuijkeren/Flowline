using FluentAssertions;
using Flowline.Config;
using Flowline.Core;
using Flowline.Generators;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class XrmContext3GeneratorTests
{
    const string ExePath = @"C:\tools\XrmContext.exe";
    const string EnvironmentUrl = "https://org.crm.dynamics.com";
    const string SolutionName = "MySolution";
    const string Namespace = "MySolution.Models";
    const string TempOutputPath = @"C:\temp\models~";

    readonly XrmContextToolProvider _toolProvider;
    readonly XrmContextRunner _runner;
    readonly XrmContext3Generator _generator;

    public XrmContext3GeneratorTests()
    {
        var console = new TestConsole();
        var runtimeOptions = new FlowlineRuntimeOptions();

        _toolProvider = Substitute.For<XrmContextToolProvider>(new HttpClient(), console, runtimeOptions);
        _runner = Substitute.For<XrmContextRunner>(console, runtimeOptions);
        _generator = new XrmContext3Generator(runtimeOptions, _toolProvider, _runner);

        _toolProvider.GetExePathAsync(Arg.Any<CancellationToken>()).Returns(ExePath);
        _runner.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<XrmContextAuth>(),
            Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    static GenerationContext MakeContext(XrmContextAuth? auth, string[]? extraTables = null) =>
        new(
            Service: Substitute.For<IOrganizationServiceAsync2>(),
            RemoteSolution: new SolutionInfo { SolutionUniqueName = SolutionName },
            SolutionName: SolutionName,
            DevUrl: EnvironmentUrl,
            ModelNamespace: Namespace,
            ExtraTables: extraTables ?? [],
            TempOutputPath: TempOutputPath,
            XrmContextAuth: auth,
            Verbose: false,
            OutputLabel: "MySolution/Plugins/Models"
        );

    // ── Type property ─────────────────────────────────────────────────────────

    [Fact]
    public void Type_ReturnsXrmContext3()
    {
        _generator.Type.Should().Be(GeneratorType.XrmContext3);
    }

    // ── Null auth guard ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NullAuth_ThrowsFlowlineExceptionNotAuthenticated()
    {
        var context = MakeContext(auth: null);

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _generator.RunAsync(context, CancellationToken.None));

        ex.ExitCode.Should().Be(ExitCode.NotAuthenticated);
    }

    [Fact]
    public async Task RunAsync_NullAuth_DoesNotCallToolProvider()
    {
        var context = MakeContext(auth: null);

        await Assert.ThrowsAsync<FlowlineException>(() =>
            _generator.RunAsync(context, CancellationToken.None));

        await _toolProvider.DidNotReceive().GetExePathAsync(Arg.Any<CancellationToken>());
    }

    // ── Delegation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CallsGetExePathAsync()
    {
        var context = MakeContext(new XrmContextAuth.BrowserOAuth());

        await _generator.RunAsync(context);

        await _toolProvider.Received(1).GetExePathAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PassesExePathToRunner()
    {
        var context = MakeContext(new XrmContextAuth.BrowserOAuth());

        await _generator.RunAsync(context);

        await _runner.Received(1).RunAsync(
            exePath: ExePath,
            environmentUrl: Arg.Any<string>(),
            auth: Arg.Any<XrmContextAuth>(),
            solutionName: Arg.Any<string>(),
            extraTables: Arg.Any<string[]>(),
            modelNamespace: Arg.Any<string>(),
            tempOutputPath: Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    // ── ClientSecret auth path ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ClientSecretAuth_ForwardsAuthToRunner()
    {
        var auth = new XrmContextAuth.ClientSecret("client-id", "client-secret");
        var context = MakeContext(auth);

        await _generator.RunAsync(context);

        await _runner.Received(1).RunAsync(
            exePath: ExePath,
            environmentUrl: EnvironmentUrl,
            auth: auth,
            solutionName: SolutionName,
            extraTables: null,
            modelNamespace: Namespace,
            tempOutputPath: TempOutputPath,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    // ── ConnectionString auth path ────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ConnectionStringAuth_ForwardsAuthToRunner()
    {
        var auth = new XrmContextAuth.ConnectionString("AuthType=OAuth;...");
        var context = MakeContext(auth);

        await _generator.RunAsync(context);

        await _runner.Received(1).RunAsync(
            exePath: ExePath,
            environmentUrl: EnvironmentUrl,
            auth: auth,
            solutionName: SolutionName,
            extraTables: null,
            modelNamespace: Namespace,
            tempOutputPath: TempOutputPath,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    // ── BrowserOAuth auth path ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_BrowserOAuthAuth_ForwardsAuthToRunner()
    {
        var auth = new XrmContextAuth.BrowserOAuth("custom-app-id");
        var context = MakeContext(auth);

        await _generator.RunAsync(context);

        await _runner.Received(1).RunAsync(
            exePath: ExePath,
            environmentUrl: EnvironmentUrl,
            auth: auth,
            solutionName: SolutionName,
            extraTables: null,
            modelNamespace: Namespace,
            tempOutputPath: TempOutputPath,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    // ── ExtraTables forwarding ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ExtraTablesNotEmpty_ForwardsTableArray()
    {
        var auth = new XrmContextAuth.BrowserOAuth();
        var extraTables = new[] { "account", "contact" };
        var context = MakeContext(auth, extraTables);

        await _generator.RunAsync(context);

        await _runner.Received(1).RunAsync(
            exePath: Arg.Any<string>(),
            environmentUrl: Arg.Any<string>(),
            auth: Arg.Any<XrmContextAuth>(),
            solutionName: Arg.Any<string>(),
            extraTables: extraTables,
            modelNamespace: Arg.Any<string>(),
            tempOutputPath: Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ExtraTablesEmpty_PassesNullToRunner()
    {
        var auth = new XrmContextAuth.BrowserOAuth();
        var context = MakeContext(auth, extraTables: []);

        await _generator.RunAsync(context);

        await _runner.Received(1).RunAsync(
            exePath: Arg.Any<string>(),
            environmentUrl: Arg.Any<string>(),
            auth: Arg.Any<XrmContextAuth>(),
            solutionName: Arg.Any<string>(),
            extraTables: null,
            modelNamespace: Arg.Any<string>(),
            tempOutputPath: Arg.Any<string>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }
}
