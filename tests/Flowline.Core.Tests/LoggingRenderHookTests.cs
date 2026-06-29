using FluentAssertions;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class LoggingRenderHookTests
{
    readonly CaptureLogger _logger = new();
    readonly TestConsole _console;

    public LoggingRenderHookTests()
    {
        _console = new TestConsole();
        _console.Pipeline.Attach(new LoggingRenderHook(_logger));
    }

    [Theory]
    [InlineData("[green]✓[/] msg", LogLevel.Information)]
    [InlineData("· msg", LogLevel.Information)]
    [InlineData("[dim]↷ msg[/]", LogLevel.Information)]
    [InlineData("[yellow]Warning:[/] something", LogLevel.Warning)]
    [InlineData("[red]Error:[/] something", LogLevel.Error)]
    [InlineData("[dim]verbose detail[/]", LogLevel.Debug)]
    public void MarkupLine_LogsAtCorrectLevel(string markupText, LogLevel expectedLevel)
    {
        _console.MarkupLine(markupText);

        _logger.Entries.Should().ContainSingle(e => e.Level == expectedLevel);
    }

    [Fact]
    public void MarkupLine_Done_LogsAtInformation()
    {
        _console.MarkupLine($"\n[bold green]:rocket: All done![/]");

        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("🚀"));
    }

    [Fact]
    public void MarkupLine_Empty_DoesNotLog()
    {
        _console.MarkupLine("");

        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Write_NonMarkupRenderable_DoesNotLog()
    {
        _console.Write(new Text("spinner noise"));

        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void MarkupLine_MultipleMessages_HandlesEachIndependently()
    {
        _console.MarkupLine("[green]✓[/] done");
        _console.MarkupLine("[yellow]Warning:[/] heads up");
        _console.Write(new Text("noise"));

        _logger.Entries.Should().HaveCount(2);
        _logger.Entries[0].Level.Should().Be(LogLevel.Information);
        _logger.Entries[1].Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void MarkupLine_OutputStillReachesConsole()
    {
        _console.MarkupLine("[green]✓[/] visible");

        _console.Output.Should().Contain("✓ visible");
    }

    private sealed class CaptureLogger : ILogger<LoggingRenderHook>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
