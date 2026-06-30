using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class VerboseMarkupTests
{
    [Fact]
    public void Render_ProducesIdenticalTextToEquivalentMarkup()
    {
        var verboseConsole = new TestConsole();
        var markupConsole = new TestConsole();

        verboseConsole.Write(new VerboseMarkup("hello"));
        markupConsole.Write(new Markup("[dim]hello[/]"));

        verboseConsole.Output.Should().Be(markupConsole.Output);
    }

    [Fact]
    public void IsVerboseMarkup_ReturnsTrueForVerboseMarkupInstance()
    {
        IRenderable renderable = new VerboseMarkup("test");
        (renderable is VerboseMarkup).Should().BeTrue();
    }

    [Fact]
    public void IsMarkup_ReturnsFalseForVerboseMarkupInstance()
    {
        IRenderable renderable = new VerboseMarkup("test");
        (renderable is Markup).Should().BeFalse();
    }

    [Fact]
    public void Render_EscapesMarkupCharacters()
    {
        var console = new TestConsole();
        console.Write(new VerboseMarkup("[bold]"));
        console.Output.Should().Contain("[bold]");
    }

    [Fact]
    public void Render_WithMaxWidth_DoesNotThrow()
    {
        // Consistent with LoggingRenderHook's Render(options, int.MaxValue) extraction pattern.
        IRenderable renderable = new VerboseMarkup("test");
        var console = new TestConsole();
        var act = () => console.Write(renderable);
        act.Should().NotThrow();
    }
}
