using Flowline.Presentation;
using FluentAssertions;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests.Presentation;

public class WelcomeScreenTests
{
    [Fact]
    public void WriteWelcomeScreen_WritesLogoAndVersion()
    {
        var console = new TestConsole();

        console.WriteWelcomeScreen();

        console.Output.Should().Contain("Flowline CLI v");
    }
}
