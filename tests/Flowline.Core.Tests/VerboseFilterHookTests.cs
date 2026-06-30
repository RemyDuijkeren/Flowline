using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class VerboseFilterHookTests
{
    [Fact]
    public void VerboseMarkup_WhenNotVerbose_IsNotRenderedToTerminal()
    {
        var console = new TestConsole();
        console.Pipeline.Attach(new VerboseFilterHook(isVerbose: false));

        console.Write(new VerboseMarkup("hello"));

        console.Output.Should().BeEmpty();
    }

    [Fact]
    public void VerboseMarkup_WhenVerbose_IsRenderedToTerminal()
    {
        var console = new TestConsole();
        console.Pipeline.Attach(new VerboseFilterHook(isVerbose: true));

        console.Write(new VerboseMarkup("hello"));

        console.Output.Should().NotBeEmpty().And.Contain("hello");
    }

    [Fact]
    public void NonVerboseMarkup_WhenNotVerbose_IsStillRendered()
    {
        var console = new TestConsole();
        console.Pipeline.Attach(new VerboseFilterHook(isVerbose: false));

        console.Write(new Markup("[green]ok[/]"));

        console.Output.Should().Contain("ok");
    }
}
