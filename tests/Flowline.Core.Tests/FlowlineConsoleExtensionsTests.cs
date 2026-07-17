using FluentAssertions;
using Flowline.Core.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class FlowlineConsoleExtensionsTests
{
    [Fact]
    public void Verbose_WritesVerboseRenderableToConsole()
    {
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions();

        console.Verbose("test message");

        console.Output.Should().Contain("test message");
    }

    [Fact]
    public void Verbose_EmitsVerboseRenderableUnconditionally()
    {
        // Without VFH in the pipeline, VerboseRenderable always reaches the console —
        // suppression is VFH's responsibility, not Console.Verbose's.
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions { IsVerbose = false };

        console.Verbose("always written");

        console.Output.Should().Contain("always written");
    }
}
