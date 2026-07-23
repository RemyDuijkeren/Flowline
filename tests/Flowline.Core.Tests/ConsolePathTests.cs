using FluentAssertions;
using Flowline.Core.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class ConsolePathShortenPathTests
{
    // Live bug (2026-07-23): ConsolePath.ShortenPath's default markup:true output was passed to
    // console.Verbose, whose VerboseRenderable escapes its entire input — turning the embedded
    // [bold]...[/] tags into literal text instead of rendering them. Same failure class as
    // FormatRelativePath's markup:false escape hatch, which this mirrors.
    [Fact]
    public void ShortenPath_MarkupFalse_ContainsNoMarkupTags()
    {
        var path = @"C:\Users\someone\AppData\Local\Microsoft\PowerAppsCLI\authprofiles_v2.json";

        var result = ConsolePath.ShortenPath(path, markup: false);

        result.Should().NotContain("[bold]");
        result.Should().NotContain("[/]");
        result.Should().EndWith("authprofiles_v2.json");
    }

    [Fact]
    public void ShortenPath_MarkupTrue_WrapsLastSegmentInBoldMarkup()
    {
        var path = @"C:\Users\someone\AppData\Local\Microsoft\PowerAppsCLI\authprofiles_v2.json";

        var result = ConsolePath.ShortenPath(path);

        result.Should().Contain("[bold]authprofiles_v2.json[/]");
    }

    [Fact]
    public void ShortenPath_ShortPath_ReturnsUnchangedRegardlessOfMarkup()
    {
        const string path = "short/path.json";

        ConsolePath.ShortenPath(path, markup: false).Should().Be("short/path.json");
        ConsolePath.ShortenPath(path).Should().Contain("[bold]path.json[/]");
    }

    [Fact]
    public void ConsoleVerbose_WithShortenPathMarkupFalse_RendersNoLiteralMarkupTags()
    {
        // Reproduces the live repro: console.Verbose(some long path via ShortenPath) must not print
        // literal "[bold]...[/]" — it must either render bold or (with markup:false) print plain text.
        var console = new TestConsole();
        var path = @"C:\Users\someone\AppData\Local\Microsoft\PowerAppsCLI\authprofiles_v2.json";

        console.Verbose($"Loaded 4 PAC auth profile(s) from {ConsolePath.ShortenPath(path, markup: false)}");

        console.Output.Should().NotContain("[bold]");
        console.Output.Should().NotContain("[/]");
        console.Output.Should().Contain("authprofiles_v2.json");
    }
}
