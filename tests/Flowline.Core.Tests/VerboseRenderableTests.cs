using FluentAssertions;
using Flowline.Core.Console;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class VerboseRenderableTests
{
    [Fact]
    public void Render_ProducesIdenticalTextToEquivalentMarkup()
    {
        var verboseConsole = new TestConsole();
        var markupConsole = new TestConsole();

        verboseConsole.Write(new VerboseRenderable("hello"));
        markupConsole.Write(new Markup("[dim]hello[/]"));

        // VerboseRenderable includes a trailing newline so VerboseFilterHook can suppress it as a single unit.
        verboseConsole.Output.Should().Be(markupConsole.Output + "\n");
    }

    [Fact]
    public void IsVerboseRenderable_ReturnsTrueForVerboseRenderableInstance()
    {
        IRenderable renderable = new VerboseRenderable("test");
        (renderable is VerboseRenderable).Should().BeTrue();
    }

    [Fact]
    public void IsMarkup_ReturnsFalseForVerboseRenderableInstance()
    {
        IRenderable renderable = new VerboseRenderable("test");
        (renderable is Markup).Should().BeFalse();
    }

    [Fact]
    public void Render_EscapesMarkupCharacters()
    {
        var console = new TestConsole();
        console.Write(new VerboseRenderable("[bold]"));
        console.Output.Should().Contain("[bold]");
    }

    [Fact]
    public void Render_WithMaxWidth_DoesNotThrow()
    {
        // Consistent with LoggingRenderHook's Render(options, int.MaxValue) extraction pattern.
        IRenderable renderable = new VerboseRenderable("test");
        var console = new TestConsole();
        var act = () => console.Write(renderable);
        act.Should().NotThrow();
    }
}
