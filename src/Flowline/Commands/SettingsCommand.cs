using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>
/// What bare <c>flowline settings</c> does: name the operations and fail with a typed code (R13, KTD2).
/// </summary>
/// <remarks>
/// Spectre's own missing-command path returns 1, which is <c>GeneralError</c>, and the agent-facing CLI
/// contract forbids that as a shortcut. A default command with no required arguments fires cleanly on the
/// bare invocation and owns the exit code, while every named operation still routes normally.
///
/// The list is grouped rather than flat (KTD18). Two of the seven operations move a whole file and five
/// touch a single component, and nothing in Spectre's own flat help says so. This output is ours to lay
/// out, so it says it here.
/// </remarks>
public class SettingsCommand(
    IAnsiConsole console,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : FlowlineCommand<SettingsCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings;

    // Listing the operations must work anywhere, including outside a project — the whole point is to tell
    // someone who does not yet know the grammar what to type next.
    protected override bool RequiresFlowlineProject => false;

    // Nor does it need a git repo, pac, or any other tool: it prints a list and stops. The shared setup
    // check runs before the command body, so without this override someone who types `flowline settings`
    // outside a repo gets "No usable Git repo" instead of the operations they asked for — the one thing
    // this command exists to prevent (R13).
    protected override Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>The operations, in registration order, grouped as the help groups them (KTD18).</summary>
    internal static readonly (string Heading, (string Name, string Description)[] Operations)[] Groups =
    [
        ("Whole file",
        [
            ("push", "apply a settings file to an environment"),
            ("pull", "write a settings file from an environment"),
        ]),
        ("One component",
        [
            ("flow", "turn a cloud flow on or off"),
            ("workflow", "turn a classic workflow on or off"),
            ("plugin", "turn a plugin step on or off"),
            ("envvar", "set an environment variable value"),
            ("connref", "bind a connection reference"),
        ]),
    ];

    protected override Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        Console.WriteLine("Settings for one environment.");

        foreach (var (heading, operations) in Groups)
        {
            Console.WriteLine();
            Console.MarkupLine($"  [bold]{heading}[/]");
            foreach (var (name, description) in operations)
                Console.MarkupLine($"    [bold]{name,-9}[/] {Markup.Escape(description)}");
        }

        Console.WriteLine();

        throw new FlowlineException(ExitCode.ValidationFailed,
            "Name an operation — run 'flowline settings <operation> --help' for its arguments.");
    }
}
