using System.ComponentModel;
using Flowline.Commands;
using Spectre.Console.Cli;

namespace Flowline.Settings;

// Base for push and generate: the commands that take the environment as --env and the solution as
// --solution-name. settings declares the same option on its own base, which has no --env, so the
// template and description live here once and both declarations use them.
public class SolutionNameSettings : EnvironmentSettings
{
    public const string OptionTemplate = "-n|--solution-name <name>";
    public const string OptionDescription = "Solution unique name: required outside a Flowline project. Inside one, leave it out or pass the project's own";

    [CommandOption(OptionTemplate)]
    [Description(OptionDescription)]
    public string? SolutionName { get; set; }

    /// <summary>
    /// Explains <c>push &lt;word&gt;</c> or <c>generate &lt;word&gt;</c>, the shape the solution
    /// positional had before it became <c>--solution-name</c>.
    /// </summary>
    /// <remarks>
    /// Spectre answers "Unknown command 'Contoso'", which names the token without saying where it went.
    /// Only the token straight after the verb is recognised: that is where the old shape put the
    /// solution, and nothing the parser accepts can start with a bare word there. A role keyword or a URL
    /// is answered with <c>--env</c>, because <c>push dev</c> reads as "push to dev".
    /// </remarks>
    /// <returns>The better message, or <c>null</c> when this is not that mistake.</returns>
    public static string? BuildPositionalHint(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith('-'))
            return null;

        var verb = args[0].ToLowerInvariant();
        if (verb is not ("push" or "generate"))
            return null;

        var word = args[1];
        var flag = SettingsSupport.LooksLikeTarget(word) ? "--env" : "--solution-name";
        var rest = new[] { flag, word }.Concat(args.Skip(2)).Select(SettingsSupport.Quote);

        return flag == "--env"
            ? $"'{word}' is an environment, and {verb} takes it as a flag. Try: flowline {verb} {string.Join(' ', rest)}"
            : $"The solution name is a flag on {verb}. Try: flowline {verb} {string.Join(' ', rest)}";
    }
}
