using System.Net;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests;

// The notice used to hang off CheckSetupAsync, so a command that skips setup, or one that dies on a
// missing project before setup runs, never checked and never printed. These cover the wiring — the
// verdict logic itself is UpdateNoticeTests.
public class UpdateNoticeWiringTests
{
    sealed class NoRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } = Array.Empty<string>().ToLookup(x => x, x => (string?)x);
        public IReadOnlyList<string> Raw { get; } = [];
    }

    sealed class FakeHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
    }

    sealed class TestCommand(
        IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService,
        SubprocessCapture capture, NuGetVersionClient nuGetVersionClient, FlowlineValidator validator)
        : FlowlineCommand<FlowlineSettings>(console, runtimeOptions, profileResolutionService, NullLoggerFactory.Instance, capture, nuGetVersionClient)
    {
        // Stands in for ScaffoldCommand/SlnAddCommand, which override the setup check away entirely and
        // need no project on disk either.
        public bool SkipSetup { get; set; }

        protected override bool RequiresFlowlineProject => !SkipSetup;

        protected override FlowlineValidator Validator => validator;
        protected override bool ShowWelcome => false;

        protected override Task CheckSetupAsync(FlowlineSettings settings, CancellationToken cancellationToken) =>
            SkipSetup ? Task.CompletedTask : base.CheckSetupAsync(settings, cancellationToken);

        protected override Task<int> ExecuteFlowlineAsync(CommandContext context, FlowlineSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<int> RunAsync(FlowlineSettings settings) =>
            ExecuteAsync(new CommandContext([], new NoRemainingArguments(), "test-command", null), settings, CancellationToken.None);
    }

    static (TestCommand Command, TestConsole Console, string NewerVersion) MakeCommand()
    {
        var running = NuGetVersion.Parse(FlowlineVersion.Display);
        var newer = new NuGetVersion(running.Major + 1, 0, 0).ToString();

        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 4096;

        var connector = new DataverseConnector(console, new HttpClient());
        var validator = new FlowlineValidator(
            new ValidationCacheStore(Path.Combine(Path.GetTempPath(), $"flowline-notice-wiring-{Guid.NewGuid()}.json")),
            new ValidationProbes());

        var command = new TestCommand(
            console, new FlowlineRuntimeOptions(),
            new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions()),
            new SubprocessCapture(console),
            new NuGetVersionClient(new HttpClient(new FakeHandler($$"""{"versions":["{{newer}}"]}"""))),
            validator);

        return (command, console, newer);
    }

    [Fact]
    public async Task ExecuteAsync_NoFlowlineProject_PrintsNoticeBeforeThrowing()
    {
        var (command, console, newerVersion) = MakeCommand();

        var act = () => command.RunAsync(new FlowlineSettings());

        await act.Should().ThrowAsync<FlowlineException>();
        console.Output.Should().Contain(newerVersion);
    }

    [Fact]
    public async Task ExecuteAsync_CommandSkipsSetupCheck_StillPrintsNotice()
    {
        var (command, console, newerVersion) = MakeCommand();
        command.SkipSetup = true;

        (await command.RunAsync(new FlowlineSettings())).Should().Be(0);

        console.Output.Should().Contain(newerVersion);
    }
}
