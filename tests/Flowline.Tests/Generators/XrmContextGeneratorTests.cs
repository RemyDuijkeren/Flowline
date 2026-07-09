using System.Text.Json;
using CliWrap;
using FluentAssertions;
using Flowline.Core.Services;
using Flowline.Generators;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class XrmContextGeneratorTests : IDisposable
{
    public XrmContextGeneratorTests()
    {
        XrmContextGenerator.ResetCache();
    }

    public void Dispose()
    {
        XrmContextGenerator.ResetCache();
        XrmContextGenerator.CheckCommandExistsFunc = null;
    }

    // ── Tool resolution ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetBestXrmContextCommandAsync_UsesDnx_WhenDnxAvailable()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", ""));

        var (command, prefixArgs, suffixArgs) = await XrmContextGenerator.GetBestXrmContextCommandAsync();

        command.Should().Be("dnx");
        prefixArgs.Should().ContainSingle().Which.Should().Be("XrmContext");
        suffixArgs.Should().ContainSingle().Which.Should().Be("--prerelease");
    }

    [Fact]
    public async Task GetBestXrmContextCommandAsync_FirstArgIsXrmContext_WhenDnxAvailable()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", ""));

        var (_, prefixArgs, _) = await XrmContextGenerator.GetBestXrmContextCommandAsync();

        prefixArgs![0].Should().Be("XrmContext");
    }

    [Fact]
    public async Task GetBestXrmContextCommandAsync_LastArgIsPrerelease_WhenDnxAvailable()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", ""));

        var (_, _, suffixArgs) = await XrmContextGenerator.GetBestXrmContextCommandAsync();

        suffixArgs![^1].Should().Be("--prerelease");
    }

    [Fact]
    public async Task GetBestXrmContextCommandAsync_UsesDotnetToolRun_WhenDnxUnavailable()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dotnet", ""));

        var (command, prefixArgs, suffixArgs) = await XrmContextGenerator.GetBestXrmContextCommandAsync();

        command.Should().Be("dotnet");
        prefixArgs.Should().ContainInOrder("tool", "run", "xrmcontext");
        suffixArgs.Should().BeNull();
    }

    [Fact]
    public async Task GetBestXrmContextCommandAsync_ThrowsBuildFailed_WhenNeitherAvailable()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((false, ""));

        Func<Task> act = async () => await XrmContextGenerator.GetBestXrmContextCommandAsync();

        await act.Should().ThrowAsync<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.BuildFailed);
    }

    [Fact]
    public async Task GetBestXrmContextCommandAsync_ThrowsMessage_WithInstallHint()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((false, ""));

        Func<Task> act = async () => await XrmContextGenerator.GetBestXrmContextCommandAsync();

        await act.Should().ThrowAsync<FlowlineException>()
            .WithMessage("*dotnet tool install -g XrmContext*");
    }

    // ── appsettings.json content ─────────────────────────────────────────────

    [Fact]
    public void BuildAppSettingsJson_Solutions_ContainsSolutionName()
    {
        var json = XrmContextGenerator.BuildAppSettingsJson("https://org.crm.dynamics.com", "C:/out", "My.Namespace", "MySolution", []);

        using var doc = JsonDocument.Parse(json);
        var solutions = doc.RootElement
            .GetProperty("XrmContext")
            .GetProperty("Solutions");
        solutions.GetArrayLength().Should().Be(1);
        solutions[0].GetString().Should().Be("MySolution");
    }

    [Fact]
    public void BuildAppSettingsJson_NamespaceSetting_EqualsModelNamespace()
    {
        var json = XrmContextGenerator.BuildAppSettingsJson("https://org.crm.dynamics.com", "C:/out", "My.Namespace", "MySolution", []);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("XrmContext")
            .GetProperty("NamespaceSetting")
            .GetString()
            .Should().Be("My.Namespace");
    }

    [Fact]
    public void BuildAppSettingsJson_OutputDirectory_EqualsTempOutputPath()
    {
        var json = XrmContextGenerator.BuildAppSettingsJson("https://org.crm.dynamics.com", "C:/out/path", "My.Namespace", "MySolution", []);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("XrmContext")
            .GetProperty("OutputDirectory")
            .GetString()
            .Should().Be("C:/out/path");
    }

    [Fact]
    public void BuildAppSettingsJson_GenerateCustomApis_IsTrue()
    {
        var json = XrmContextGenerator.BuildAppSettingsJson("https://org.crm.dynamics.com", "C:/out", "My.Namespace", "MySolution", []);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("XrmContext")
            .GetProperty("GenerateCustomApis")
            .GetBoolean()
            .Should().BeTrue();
    }

    [Fact]
    public void BuildAppSettingsJson_DataverseUrl_EqualsDevUrl()
    {
        var json = XrmContextGenerator.BuildAppSettingsJson("https://org.crm.dynamics.com", "C:/out", "My.Namespace", "MySolution", []);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("DATAVERSE_URL")
            .GetString()
            .Should().Be("https://org.crm.dynamics.com");
    }

    [Fact]
    public void BuildAppSettingsJson_Entities_IncludedWhenExtraTablesNonEmpty()
    {
        var json = XrmContextGenerator.BuildAppSettingsJson("https://org.crm.dynamics.com", "C:/out", "My.Namespace", "MySolution", ["account", "contact"]);

        using var doc = JsonDocument.Parse(json);
        var entities = doc.RootElement
            .GetProperty("XrmContext")
            .GetProperty("Entities");
        entities.GetArrayLength().Should().Be(2);
        entities[0].GetString().Should().Be("account");
        entities[1].GetString().Should().Be("contact");
    }

    [Fact]
    public void BuildAppSettingsJson_Entities_AbsentWhenExtraTablesEmpty()
    {
        var json = XrmContextGenerator.BuildAppSettingsJson("https://org.crm.dynamics.com", "C:/out", "My.Namespace", "MySolution", []);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("XrmContext")
            .TryGetProperty("Entities", out _)
            .Should().BeFalse();
    }

    // ── Env vars ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildEnvVars_InjectsClientId_WhenServicePrincipal()
    {
        var profile = new PacProfile { Kind = "ServicePrincipal", ApplicationId = "my-app-id", TenantId = "my-tenant" };

        var envVars = XrmContextGenerator.BuildEnvVars(profile);

        envVars.Should().ContainKey("AZURE_CLIENT_ID").WhoseValue.Should().Be("my-app-id");
    }

    [Fact]
    public void BuildEnvVars_InjectsTenantId_WhenServicePrincipal()
    {
        var profile = new PacProfile { Kind = "ServicePrincipal", ApplicationId = "my-app-id", TenantId = "my-tenant" };

        var envVars = XrmContextGenerator.BuildEnvVars(profile);

        envVars.Should().ContainKey("AZURE_TENANT_ID").WhoseValue.Should().Be("my-tenant");
    }

    [Fact]
    public void BuildEnvVars_InjectsClientSecret_WhenPresentInProcessEnv()
    {
        var originalSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "test-secret-value");
            var profile = new PacProfile { Kind = "ServicePrincipal", ApplicationId = "my-app-id", TenantId = "my-tenant" };

            var envVars = XrmContextGenerator.BuildEnvVars(profile);

            envVars.Should().ContainKey("AZURE_CLIENT_SECRET").WhoseValue.Should().Be("test-secret-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", originalSecret);
        }
    }

    [Fact]
    public void BuildEnvVars_NoClientSecret_WhenAbsentFromProcessEnv()
    {
        var originalSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);
            var profile = new PacProfile { Kind = "ServicePrincipal", ApplicationId = "my-app-id", TenantId = "my-tenant" };

            var envVars = XrmContextGenerator.BuildEnvVars(profile);

            envVars.Should().NotContainKey("AZURE_CLIENT_SECRET");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", originalSecret);
        }
    }

    [Fact]
    public void BuildEnvVars_Empty_WhenNoProfile()
    {
        var envVars = XrmContextGenerator.BuildEnvVars(null);

        envVars.Should().BeEmpty();
    }

    [Fact]
    public void BuildEnvVars_Empty_WhenUserProfile()
    {
        var profile = new PacProfile { Kind = "User", ApplicationId = "some-id", TenantId = "some-tenant" };

        var envVars = XrmContextGenerator.BuildEnvVars(profile);

        envVars.Should().BeEmpty();
    }

    // ── Working directory + cleanup ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WorkingDir_IsSetToTempAppsettingsDir_NotTempOutputPath()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", ""));

        string? capturedWorkingDir = null;
        var generator = new CapturingXrmContextGenerator(
            new TestConsole(),
            (cmd, ctx, ct) =>
            {
                capturedWorkingDir = cmd.WorkingDirPath;
                return Task.FromResult(MakeSuccessResult());
            });

        var context = MakeContext();
        await generator.RunAsync(context);

        capturedWorkingDir.Should().NotBeNull();
        capturedWorkingDir.Should().NotBe(context.TempOutputPath);
        capturedWorkingDir.Should().Contain("flowline-xrmcontext-");
    }

    [Fact]
    public async Task RunAsync_DeletesTempAppsettingsDir_AfterSuccess()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", ""));

        string? capturedWorkingDir = null;
        var generator = new CapturingXrmContextGenerator(
            new TestConsole(),
            (cmd, ctx, ct) =>
            {
                capturedWorkingDir = cmd.WorkingDirPath;
                return Task.FromResult(MakeSuccessResult());
            });

        await generator.RunAsync(MakeContext());

        Directory.Exists(capturedWorkingDir).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_DeletesTempAppsettingsDir_AfterToolException()
    {
        XrmContextGenerator.CheckCommandExistsFunc = (cmd, args, ct) =>
            Task.FromResult((cmd == "dnx", ""));

        string? capturedWorkingDir = null;
        var generator = new CapturingXrmContextGenerator(
            new TestConsole(),
            (cmd, ctx, ct) =>
            {
                capturedWorkingDir = cmd.WorkingDirPath;
                throw new InvalidOperationException("simulated tool failure");
            });

        Func<Task> act = async () => await generator.RunAsync(MakeContext());

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(capturedWorkingDir).Should().BeFalse();
    }

    // ── Env vars — resolvedSecret precedence ─────────────────────────────────

    [Fact]
    public void BuildEnvVars_ResolvedSecret_WinsOverProcessEnv_WhenBothSet()
    {
        var originalSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "env-secret");
            var profile = new PacProfile { Kind = "ServicePrincipal", ApplicationId = "my-app-id", TenantId = "my-tenant" };

            var envVars = XrmContextGenerator.BuildEnvVars(profile, resolvedSecret: "resolved-secret");

            envVars.Should().ContainKey("AZURE_CLIENT_SECRET").WhoseValue.Should().Be("resolved-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", originalSecret);
        }
    }

    [Fact]
    public void BuildEnvVars_ResolvedSecret_InjectedDirectly_WhenProvided()
    {
        var originalSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", null);
            var profile = new PacProfile { Kind = "ServicePrincipal", ApplicationId = "my-app-id", TenantId = "my-tenant" };

            var envVars = XrmContextGenerator.BuildEnvVars(profile, resolvedSecret: "resolved-secret");

            envVars.Should().ContainKey("AZURE_CLIENT_SECRET").WhoseValue.Should().Be("resolved-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", originalSecret);
        }
    }

    [Fact]
    public void BuildEnvVars_Empty_WhenUniversalProfile()
    {
        var profile = new PacProfile { Kind = "UNIVERSAL" };

        var envVars = XrmContextGenerator.BuildEnvVars(profile);

        envVars.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static GenerationContext MakeContext() => new(
        Service: null!,
        RemoteSolution: null!,
        SolutionName: "TestSolution",
        DevUrl: "https://org.crm.dynamics.com",
        ModelNamespace: "TestSolution.Models",
        ExtraTables: [],
        TempOutputPath: Path.Combine(Path.GetTempPath(), "xrmcontext-output-test"),
        XrmContextAuth: null,
        Verbose: false,
        OutputLabel: "Test");

    static CommandResult MakeSuccessResult() =>
        new(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1));

    private class CapturingXrmContextGenerator(
        Spectre.Console.IAnsiConsole console,
        Func<Command, GenerationContext, CancellationToken, Task<CommandResult>> executeFunc)
        : XrmContextGenerator(console)
    {
        internal override Task<CommandResult> ExecuteCommandAsync(Command command, GenerationContext context, CancellationToken cancellationToken) =>
            executeFunc(command, context, cancellationToken);
    }
}
