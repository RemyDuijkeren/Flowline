using Flowline.Core;
using Flowline.Core.Console;
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

    // The banner belongs to the root help screen, which is why `settings --help` has none. This screen is
    // the same kind of thing, so it does not get one either.
    protected override bool ShowWelcome => false;

    /// <summary>
    /// The operations, in registration order, grouped as the help groups them (KTD18).
    /// </summary>
    /// <remarks>
    /// The invocation column carries each operation's required argument and nothing else, which is what
    /// Spectre's own command list shows — an optional one is left off there, so capture appears bare.
    /// </remarks>
    internal static readonly (string Heading, (string Invocation, string Description)[] Operations)[] Groups =
    [
        ("Whole file",
        [
            ("push <target>", "Apply a settings file to an environment"),
            ("pull", "Write a settings file from an environment, or from every configured one"),
        ]),
        ("One component",
        [
            ("flow <target>", "Turn a cloud flow on or off, or read its state"),
            ("workflow <target>", "Turn a classic workflow on or off, or read its state"),
            ("plugin <target>", "Turn a plugin step on or off, or read its state"),
            ("envvar <target>", "Set an environment variable's value, or read it"),
            ("connref <target>", "Bind a connection reference to a connection, or read its binding"),
        ]),
    ];

    protected override Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        WriteSectionHeader("USAGE:");
        Console.WriteLine("    flowline settings <COMMAND>");
        Console.WriteLine();

        WriteSectionHeader("COMMANDS:");
        Console.Write(OperationList());
        Console.WriteLine();

        Console.MarkupLine($"[dim][link={FlowlineHelpProvider.DocsUrl}]Docs: {FlowlineHelpProvider.DocsUrl}[/][/]");
        Console.WriteLine();

        throw new FlowlineException(ExitCode.ValidationFailed,
            "Name an operation — run 'flowline settings <operation> --help' for its arguments.");
    }

    /// <summary>A section header in the same colour and shape Spectre's own help sections use.</summary>
    void WriteSectionHeader(string text)
    {
        Console.Write(new Text(text, new Style(FlowlineTheme.PrimaryColor)));
        Console.WriteLine();
    }

    /// <summary>
    /// The grouped operation list, laid out the way Spectre lays out a command list.
    /// </summary>
    /// <remarks>
    /// A grid rather than a hand-counted format string, so the description column lands where the real
    /// help puts it — four spaces past the longest invocation — instead of at a width that goes stale the
    /// first time an operation is renamed.
    ///
    /// The group labels are what this screen has and Spectre's flat list does not (KTD18). They sit at the
    /// same indent as the operations and are dimmed, so they read as labels without pushing the operations
    /// into a second level of indentation and out of line with every other help screen.
    /// </remarks>
    static Grid OperationList()
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().PadLeft(4).PadRight(4).NoWrap());
        grid.AddColumn(new GridColumn().PadLeft(0).PadRight(0));

        for (var i = 0; i < Groups.Length; i++)
        {
            if (i > 0) grid.AddEmptyRow();

            grid.AddRow(new Markup($"[dim]{Markup.Escape(Groups[i].Heading)}[/]"), new Text(string.Empty));

            foreach (var (invocation, description) in Groups[i].Operations)
                grid.AddRow(new Text(invocation), new Text(description));
        }

        return grid;
    }
}
