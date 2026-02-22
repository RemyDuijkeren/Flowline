using Flowline.Commands;
using FluentAssertions;
using Spectre.Console.Testing;
//using Spectre.Console.Cli.Testing;

namespace Flowline.Tests;

// https://spectreconsole.net/cli/unit-testing
public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        // // Given
        // var app = new CommandAppTester();
        // app.SetDefaultCommand<InitCommand>();
        //
        // // When
        // var result = app.Run();
        //
        // // Then
        // result.ExitCode.Should().Be(0);
        // result.Output.Should().Contain("Hello world.");
    }
}
