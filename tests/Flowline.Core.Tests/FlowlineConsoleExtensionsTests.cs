using FluentAssertions;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class FlowlineConsoleExtensionsTests
{
    [Fact]
    public void Verbose_WritesVerboseMarkupToConsole()
    {
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions();

        console.Verbose("test message", options);

        console.Output.Should().Contain("test message");
    }

    [Fact]
    public void Verbose_AppendsMessageToVerboseOutputBuffer()
    {
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions();

        console.Verbose("buffered message", options);

        options.VerboseOutput.Lines.Should().Contain("buffered message");
    }

    [Fact]
    public void Verbose_EmitsVerboseMarkupUnconditionally()
    {
        // Without VFH in the pipeline, VerboseMarkup always reaches the console —
        // suppression is VFH's responsibility, not Console.Verbose's.
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions { IsVerbose = false };

        console.Verbose("always written", options);

        console.Output.Should().Contain("always written");
        options.VerboseOutput.Lines.Should().Contain("always written");
    }
}
