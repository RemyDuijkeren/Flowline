using FluentAssertions;
using Flowline.Core.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class VerboseFilterHookTests
{
    [Fact]
    public void VerboseRenderable_WhenNotVerbose_IsNotRenderedToTerminal()
    {
        var console = new TestConsole();
        console.Pipeline.Attach(new VerboseFilterHook(new FlowlineRuntimeOptions()));

        console.Write(new VerboseRenderable("hello"));

        console.Output.Should().BeEmpty();
    }

    [Fact]
    public void VerboseRenderable_WhenVerbose_IsRenderedToTerminal()
    {
        var console = new TestConsole();
        console.Pipeline.Attach(new VerboseFilterHook(new FlowlineRuntimeOptions { IsVerbose = true }));

        console.Write(new VerboseRenderable("hello"));

        console.Output.Should().NotBeEmpty().And.Contain("hello");
    }

    [Fact]
    public void NonVerboseRenderable_WhenNotVerbose_IsStillRendered()
    {
        var console = new TestConsole();
        console.Pipeline.Attach(new VerboseFilterHook(new FlowlineRuntimeOptions()));

        console.Write(new Markup("[green]ok[/]"));

        console.Output.Should().Contain("ok");
    }

    [Fact]
    public void IsVerbose_ReadLazily_EnabledAfterConstruction()
    {
        var options = new FlowlineRuntimeOptions { IsVerbose = false };
        var console = new TestConsole();
        console.Pipeline.Attach(new VerboseFilterHook(options));

        console.Write(new VerboseRenderable("suppressed"));
        console.Output.Should().BeEmpty();

        options.IsVerbose = true;
        console.Write(new VerboseRenderable("visible"));
        console.Output.Should().Contain("visible");
    }

    [Fact]
    public void Pipeline_VerboseRenderable_LoggedByLRH_EvenWhenSuppressedFromTerminal()
    {
        var options = new FlowlineRuntimeOptions { IsVerbose = false };
        var logger = new CaptureLogger();
        var console = new TestConsole();
        // Program.cs order: VFH first (outer), LRH second (inner)
        console.Pipeline.Attach(new VerboseFilterHook(options));
        console.Pipeline.Attach(new LoggingRenderHook(logger));

        console.Write(new VerboseRenderable("verbose detail"));

        console.Output.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(e => e.Message.Contains("verbose detail"));
    }

    private sealed class CaptureLogger : ILogger<LoggingRenderHook>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
