using Flowline.Settings;
using FluentAssertions;

namespace Flowline.Tests;

// The solution used to be a bare word after push and generate. Scripts with that shape now fail to
// parse, and Spectre's "Unknown command 'Contoso'" does not say where the word went.
public class SolutionNameSettingsTests
{
    [Fact]
    public void ABareWordAfterPush_IsAnsweredWithTheFlag()
    {
        SolutionNameSettings.BuildPositionalHint(["push", "Contoso"])
            .Should().Contain("flowline push --solution-name Contoso");
    }

    // The suggestion has to be runnable, so the rest of the invocation comes with it.
    [Fact]
    public void TheSuggestion_CarriesTheRestOfTheInvocation()
    {
        SolutionNameSettings.BuildPositionalHint(["generate", "Contoso", "-o", "./Models"])
            .Should().Contain("flowline generate --solution-name Contoso -o ./Models");
    }

    // The rest of the line is repeated back, so a secret in it must not be.
    [Fact]
    public void AClientSecret_IsNotRepeated()
    {
        var hint = SolutionNameSettings.BuildPositionalHint(["generate", "Contoso", "--client-secret", "s3cr3t"]);

        hint.Should().Contain("--client-secret ***").And.NotContain("s3cr3t");
    }

    // 'push dev' reads as "push to dev", so an environment gets --env, not --solution-name.
    [Theory]
    [InlineData("dev")]
    [InlineData("PROD")]
    [InlineData("https://contoso-dev.crm4.dynamics.com")]
    public void AnEnvironment_IsAnsweredWithEnv(string word)
    {
        var hint = SolutionNameSettings.BuildPositionalHint(["push", word]);

        hint.Should().Contain($"flowline push --env {word}");
        hint.Should().NotContain("--solution-name");
    }

    [Fact]
    public void TheVerb_IsMatchedWithoutRegardToCase()
    {
        SolutionNameSettings.BuildPositionalHint(["PUSH", "Contoso"])
            .Should().Contain("--solution-name Contoso");
    }

    // Only the token straight after the verb: that is where the old shape put the solution.
    [Theory]
    [InlineData("push", "--env", "dev", "Contoso")]
    [InlineData("push")]
    [InlineData("deploy", "prod")]
    [InlineData("settings", "push", "test")]
    public void AnythingElse_IsLeftToTheParser(params string[] args)
    {
        SolutionNameSettings.BuildPositionalHint(args).Should().BeNull();
    }
}
