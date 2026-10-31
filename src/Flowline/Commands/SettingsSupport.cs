using Flowline.Settings;
using System.ComponentModel;
using Flowline.Core.Environments;
using System.Xml.Linq;
using Flowline.Core;
using Flowline.Core.Configure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>
/// Options every <c>settings</c> operation that touches an environment carries.
/// </summary>
/// <remarks>
/// The environment positional is deliberately not here (KTD1). <c>apply</c> and the component operations
/// require it, <c>capture</c> makes it optional for the all-roles sweep, and one
/// <see cref="CommandArgumentAttribute"/> cannot be both — so each leaf declares its own at position 0 and
/// only the shared options live on the base.
/// </remarks>
public abstract class SettingsSettings : DataverseSettings
{
    [CommandOption(SolutionNameSettings.OptionTemplate)]
    [Description(SolutionNameSettings.OptionDescription)]
    public string? SolutionName { get; set; }

    [CommandOption("--dry-run")]
    [Description("Report what would change and write nothing")]
    [DefaultValue(false)]
    public bool DryRun { get; set; } = false;
}

/// <summary>Pure helpers shared by the <c>settings</c> operations, testable without a checkout.</summary>
public static class SettingsSupport
{
    /// <summary>
    /// Stand-alone is a flag naming the solution plus no project to read one from (KTD3).
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="DeployCommand.ResolveStandalone"/> — a flag plus the absence of a project —
    /// rather than push's flag-only-then-throw form, so the two precedents do not diverge further. Pure so
    /// the rule is testable without a checkout.
    ///
    /// Either flag qualifies: <c>--solution-name</c> states the name outright, and <c>--from &lt;zip|folder&gt;</c>
    /// names an artifact that carries it. Without this, a stand-alone capture with only the artifact would be
    /// judged project mode and stop at "No Flowline project found" — the artifact it was handed ignored.
    /// </remarks>
    public static bool ResolveStandalone(string? solutionName, string? artifactPath, string startDir) =>
        (!string.IsNullOrWhiteSpace(solutionName) || !string.IsNullOrWhiteSpace(artifactPath))
        && FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(startDir) is null;

    /// <summary>
    /// Stand-alone for a component operation is simply the absence of a project (R15).
    /// </summary>
    /// <remarks>
    /// Unlike push and capture, no flag distinguishes the two modes here: without a project the run is
    /// stand-alone whether or not <c>--solution-name</c> was given, and the missing flag is then reported
    /// by name.
    ///
    /// Keying it on the flag instead made that report unreachable. Without the flag the run was judged
    /// project mode, and the base class's project gate said "No Flowline project found — run flowline
    /// clone" first, telling someone outside a project to create one rather than to pass the one flag
    /// that would have worked. Pure so the rule is testable without a checkout.
    /// </remarks>
    public static bool ResolveComponentStandalone(string startDir) =>
        FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(startDir) is null;

    /// <summary>Inside a project, accepts a <c>--solution-name</c> that matches it and rejects one that doesn't.</summary>
    /// <remarks>
    /// Same rule as push and generate, through the same check, so a stand-alone script keeps working when it
    /// is run from inside a project folder. Rejecting the flag outright, as this first shipped, was the one
    /// place the solution name behaved differently by command.
    /// </remarks>
    public static void ValidateSolutionName(string? solutionName, string? projectSolutionName)
    {
        if (projectSolutionName is not null)
            FlowlineCommand<FlowlineSettings>.ValidateSolutionMatchesConfig(solutionName, projectSolutionName);
    }

    /// <summary>The message a role keyword earns when there is no project to resolve it from.</summary>
    /// <summary>
    /// Every operation registered under the <c>settings</c> branch, in registration order (KTD23).
    /// </summary>
    /// <remarks>
    /// A second copy of what <c>Program.cs</c> registers, and deliberately so: the hint below runs from
    /// the exception handler, which is outside the command app and cannot ask it what it knows. An
    /// operation added there and forgotten here only means the hint stays quiet for that one, which is
    /// the failure direction that costs nothing.
    /// </remarks>
    static readonly string[] s_operations = ["apply", "capture", "toggle", "set"];

    /// <summary>
    /// The operation names that used to exist, and what each one is now (KTD25).
    /// </summary>
    /// <remarks>
    /// The five component kinds were separate operations until they became one filter over two. Nothing
    /// had shipped, so there are no aliases and no deprecation; what there is instead is a message that
    /// tells anyone with the old spelling in their fingers or their scripts exactly what to type.
    /// </remarks>
    static readonly Dictionary<string, (string Operation, string Type)> s_retired = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flow"] = ("toggle", "flow"),
        ["workflow"] = ("toggle", "workflow"),
        ["plugin"] = ("toggle", "plugin"),
        ["envvar"] = ("set", "envvar"),
        ["connref"] = ("set", "connref"),
    };


    /// <summary>
    /// Explains a <c>settings</c> invocation that named the environment where the operation belongs
    /// (KTD23).
    /// </summary>
    /// <remarks>
    /// "In dev, do flow" is how the task is described out loud, so <c>settings dev flow</c> is what
    /// fingers reach for. Spectre answers that with "Unknown command 'dev'", which is true and teaches
    /// nothing: it names the token it could not route without saying that the token is fine and only in
    /// the wrong place.
    ///
    /// A message rather than accepting both orders. The operation is the command and the environment is
    /// its argument, the same shape <c>deploy</c> and <c>drift</c> use; making the parser lenient would
    /// buy one command's convenience with a grammar nothing else in the CLI shares.
    /// </remarks>
    /// <returns>The better message, or <c>null</c> when this is not that mistake.</returns>
    public static string? BuildArgumentOrderHint(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !string.Equals(args[0], "settings", StringComparison.OrdinalIgnoreCase))
            return null;

        var first = args[1];

        // A recognised operation in the right place is not this mistake, whatever else went wrong.
        if (IsOperation(first)) return null;

        // A retired operation name: the invocation is the right shape, the word just moved into --type.
        if (s_retired.TryGetValue(first, out var moved))
        {
            var rest = args.Skip(2).Select(Quote);

            return $"'{first}' is a component type, not an operation. " +
                   $"Try: flowline settings {moved.Operation} " +
                   string.Join(' ', rest.Concat([$"--type {moved.Type}"]));
        }

        // 'settings dev flow': the operation is present, just second. Rebuild the whole line so the
        // suggestion is something to run rather than a shape to apply by hand.
        if (args.Count > 2 && IsOperation(args[2]))
        {
            var rest = new[] { args[2], first }.Concat(args.Skip(3)).Select(Quote);

            return $"'{args[2]}' is the operation and it comes first, before the environment. " +
                   $"Try: flowline settings {string.Join(' ', rest)}";
        }

        // Wrong twice over: the environment first and a retired name second. The retired name is the
        // more useful thing to explain, because fixing only the order would still not run.
        if (args.Count > 2 && s_retired.TryGetValue(args[2], out var movedSecond))
        {
            var rest = new[] { first }.Concat(args.Skip(3)).Select(Quote);

            return $"'{args[2]}' is a component type, not an operation. " +
                   $"Try: flowline settings {movedSecond.Operation} " +
                   string.Join(' ', rest.Concat([$"--type {movedSecond.Type}"]));
        }

        // 'settings dev': an environment where an operation belongs, with no operation anywhere.
        if (EnvironmentTarget.IsTarget(first))
            return $"'{first}' is an environment, not an operation. The operation comes first: " +
                   $"flowline settings <operation> {Quote(first)}. " +
                   $"Operations are {string.Join(", ", s_operations)}.";

        return null;
    }

    /// <summary>
    /// Explains an unknown <c>--type</c> value, which the parser reports in its own words (KTD25).
    /// </summary>
    /// <remarks>
    /// Spectre answers with "Failed to convert 'bogus' to Nullable`1. Valid values are '', 'Flow', ...",
    /// which leaks a CLR type name, capitalises values the user types in lower case, and offers the empty
    /// string as though it were one. The valid values are right, so this keeps them and says the rest
    /// properly.
    ///
    /// Read from the invocation rather than from the parser's message: the message names no flag, so it
    /// cannot say which one was wrong, and matching on its wording would break the moment Spectre
    /// rephrases it. Reading argv means a rewording leaves this quiet rather than wrong.
    /// </remarks>
    public static string? BuildTypeValueHint(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !string.Equals(args[0], "settings", StringComparison.OrdinalIgnoreCase))
            return null;

        var valid = args[1].ToLowerInvariant() switch
        {
            "toggle" => Enum.GetNames<SettingsToggleCommand.StateType>(),
            "set" => Enum.GetNames<SettingsSetCommand.ValueType>(),
            _ => null,
        };

        if (valid is null) return null;

        var given = TypeArgument(args);
        if (given is null || valid.Any(v => string.Equals(v, given, StringComparison.OrdinalIgnoreCase)))
            return null;

        return $"'{given}' isn't a type for 'settings {args[1].ToLowerInvariant()}'. " +
               $"Valid types are {string.Join(", ", valid.Select(v => v.ToLowerInvariant()))}.";
    }

    /// <summary>The value given to <c>--type</c>, in either spelling the parser accepts.</summary>
    static string? TypeArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
                return args[i]["--type=".Length..];

            if (string.Equals(args[i], "--type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                return args[i + 1];
        }

        return null;
    }

    static bool IsOperation(string token) =>
        s_operations.Contains(token, StringComparer.OrdinalIgnoreCase);

    /// <summary>The message a target that is neither a role nor a URL earns, before anything resolves it.</summary>
    /// <remarks>
    /// Without this the token went to the PAC profile lookup as a URL, and a component name typed where
    /// the environment belongs came back as "No PAC auth profile found for ApprovalFlow", with advice to
    /// authenticate against it.
    ///
    /// When the component name is the environment, the two were swapped, and the line to run is the
    /// caller's own with the two exchanged. Otherwise there is nothing to swap with, so it says what a
    /// target is.
    /// </remarks>
    public static string BuildNotAnEnvironmentError(string target, string? name, IReadOnlyList<string> args)
    {
        if (name is not null && EnvironmentTarget.IsTarget(name))
        {
            var swapped = args.ToArray();
            var targetAt = PositionalIndex(swapped, target, after: -1);
            var nameAt = PositionalIndex(swapped, name, after: targetAt);

            if (targetAt >= 0 && nameAt >= 0)
            {
                (swapped[targetAt], swapped[nameAt]) = (swapped[nameAt], swapped[targetAt]);
                return $"'{target}' isn't an environment, and '{name}' is. The environment comes first: " +
                       $"'{Suggestion(swapped)}'.";
            }
        }

        return EnvironmentTarget.NotATarget(target,
            $"Put the environment first: flowline settings <operation> {EnvironmentTarget.TargetPlaceholder}");
    }

    /// <summary>The message a scripted write with no component name earns.</summary>
    /// <remarks>
    /// Without a name an unattended run used to list the components and exit 0, so a script that passed
    /// <c>--off</c> believed something was off and nothing had changed. The suggestion is the caller's own
    /// line with <c>&lt;name&gt;</c> after the environment, so <c>--type</c> and <c>--dry-run</c> survive
    /// the paste.
    /// </remarks>
    public static string BuildMissingNameError(string noun, string verb, string target, IReadOnlyList<string> args)
    {
        var targetAt = PositionalIndex(args, target, after: -1);
        if (targetAt < 0)
            return $"Name the {noun} to {verb}: 'flowline settings <operation> {target} <name>'.";

        var withName = args.ToList();
        withName.Insert(targetAt + 1, "<name>");

        return $"Name the {noun} to {verb}: '{Suggestion(withName)}'.";
    }

    /// <summary>Where a positional value sits in the invocation, searching after the operation.</summary>
    /// <remarks>
    /// Matched by value because an option may come before the positionals (<c>--type flow prod</c>), so
    /// position alone says nothing. An option value equal to the positional would be found first; that
    /// takes a component named after its own <c>--type</c> value, and the suggestion is only advice.
    /// </remarks>
    static int PositionalIndex(IReadOnlyList<string> args, string value, int after)
    {
        var operationAt = Enumerable.Range(0, args.Count).FirstOrDefault(i => IsOperation(args[i]), -1);

        for (var i = Math.Max(after, operationAt) + 1; i < args.Count; i++)
            if (args[i] == value)
                return i;

        return -1;
    }

    /// <summary>The caller's invocation as a line to paste, with any <c>--value</c> replaced.</summary>
    /// <remarks>
    /// The value is never repeated: an environment variable value can be a secret, and this line ends up
    /// in CI logs and the log file. The caller still has their own command line to copy it from.
    /// </remarks>
    static string Suggestion(IReadOnlyList<string> args)
    {
        var tokens = new List<string>(args.Count);

        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--value=", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add("--value=<value>");
                continue;
            }

            tokens.Add(Quote(args[i]));

            if (string.Equals(args[i], "--value", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                tokens.Add("<value>");
                i++;
            }
        }

        return "flowline " + string.Join(' ', tokens);
    }

    // A component name routinely has spaces in it, so a suggestion that can be pasted has to keep them
    // together.
    internal static string Quote(string token) => token.Contains(' ') ? $"\"{token}\"" : token;

    /// <summary>Names the file a run resolved, before anything is written.</summary>
    /// <remarks>
    /// An input verdict in the same shape as the environment and solution lines above it, not an
    /// announcement of the work — the tone guide's one-line-per-resolved-input act. With no confirmation
    /// prompt, this line plus the environment line is what an operator has to catch a run pointed at the
    /// wrong file or the wrong environment before it writes.
    /// </remarks>
    public static string BuildResolutionNote(SettingsFileLocation location) =>
        $"Settings: [bold]{Markup.Escape(Path.GetFileName(location.Path))}[/] ({SourceLabel(location.Source)})";

    static string SourceLabel(SettingsFileSource source) => source switch
    {
        SettingsFileSource.Explicit => "--settings-file",
        SettingsFileSource.RoleConvention => "role convention",
        _ => "shared fallback",
    };

    /// <summary>What a dry run says it would do to a component.</summary>
    /// <remarks>
    /// Shared with the single-component path. The two report methods keep separate outcome types by
    /// decision (KTD8), but the words a reader sees for the same event are not part of that split, and two
    /// copies would be free to drift apart.
    /// </remarks>
    public static string BuildWouldChangeLine(string name) => $"Would change [bold]{Markup.Escape(name)}[/]";

    /// <summary>What a real write says it did.</summary>
    /// <remarks>
    /// The suspended wording holds in both directions: a suspended flow is either activated again or moved
    /// to draft, and either way what the reader needs to know is that it had stopped itself, so a
    /// re-suspension after this run is not a surprise.
    /// </remarks>
    public static string BuildUpdatedLine(string name, bool wasSuspended) =>
        wasSuspended
            ? $"[bold]{Markup.Escape(name)}[/] updated — it was suspended before this run"
            : $"[bold]{Markup.Escape(name)}[/] updated";

    /// <summary>Dry-run completion wording, in the statement form the other commands use.</summary>
    public static string BuildDryRunCompleteMessage(string environment) =>
        $"Dry run complete — {Markup.Escape(environment)} is untouched. Run without --dry-run to apply.";

    /// <summary>
    /// The sign-off every settings command that actually did something ends on.
    /// </summary>
    /// <remarks>
    /// One per command family, the way clone, deploy, provision and scaffold each have their own. A
    /// salute, because what this family does is carry out a declared instruction.
    ///
    /// Only on a run that changed something. A dry run wrote nothing and has nothing to sign off, and a
    /// cancelled or partly-applied run is not a success to celebrate.
    /// </remarks>
    public const string Kaomoji = "(￣^￣)ゞ";

    /// <summary>The finish line for a real apply.</summary>
    public static string BuildAppliedMessage(string environment) =>
        $"{Markup.Escape(environment)} is configured. Re-run any time — the same file changes nothing twice. {Kaomoji}";

    /// <summary>The finish line for a run that was interrupted partway through the file.</summary>
    /// <remarks>
    /// It has to say two things an operator acts on: the components listed above are the ones that were
    /// written, and the rest of the file was never reached. Re-running is the fix, and a re-run is safe.
    /// </remarks>
    public static string BuildCancelledMessage(string environment) =>
        $"Stopped early — the components above were applied to {Markup.Escape(environment)}, the rest weren't. " +
        "Re-run to finish.";

    /// <summary>How many solution components this file says nothing about.</summary>
    /// <summary>
    /// Says which tables were published, and that the change is now live (KTD30).
    /// </summary>
    /// <remarks>
    /// Every clause is past tense and about what happened. An earlier version ended on "a form change
    /// isn't visible until its table is published", which is true and read as an instruction: the line
    /// announced a finished step and then appeared to ask for one.
    ///
    /// The cost of the publish moved to <see cref="BuildPublishScopeNote"/> rather than being dropped.
    /// It is real but it is not news on most runs, and competing with the result is what made this line
    /// confusing.
    /// </remarks>
    public static string BuildPublishedLine(IReadOnlyList<string> tables) =>
        $"Published {NameThem(tables)} — the form {(tables.Count == 1 ? "change is" : "changes are")} live now.";

    /// <summary>What else went out with the publish, for a run that asked for detail.</summary>
    /// <remarks>
    /// Publishing a table sends every pending customization on it, not only the form that was switched.
    /// Someone with unfinished work on that table is entitled to know, and the publish request has no
    /// narrower scope to offer: a per-form publish is accepted by Dataverse and silently does nothing.
    /// </remarks>
    public static string BuildPublishScopeNote(IReadOnlyList<string> tables) =>
        $"Publishing {(tables.Count == 1 ? "a table" : "these tables")} also sends out any other pending "
        + "customizations on it.";

    /// <summary>Says a publish was refused, and what the change is waiting on.</summary>
    /// <remarks>
    /// Never phrased as a failed run. The state change was written and stands; only the publish that makes
    /// it visible did not, and re-running it is safe.
    /// </remarks>
    public static string BuildPublishFailedLine(IReadOnlyList<string> tables, string detail) =>
        $"The change is saved, but publishing {NameThem(tables)} failed — {detail} " +
        "The app shows the old state until that table is published.";

    /// <summary>What a component reports when Dataverse refused the write and said nothing usable.</summary>
    /// <remarks>
    /// The fallback only runs when the outcome carried no detail, so there is no reason to pass on. Naming
    /// the component and where to look at it is what is left, and it is what an operator or an agent acts
    /// on — "X was refused" on its own is neither.
    /// </remarks>
    public static string BuildRefusedLine(string name) =>
        $"Dataverse refused the change to {name} and gave no reason. Check the component in the maker " +
        "portal, then re-run.";

    /// <summary>What a component reports when the write failed and carried no detail.</summary>
    /// <remarks>Retry first: unlike a refusal, a failure with nothing attached is usually transport.</remarks>
    public static string BuildFailedLine(string name) =>
        $"Couldn't write {name}, and Dataverse gave no reason. Re-run to retry; if it keeps failing, check " +
        "the component in the maker portal.";

    /// <summary>Says which tables a real run would publish.</summary>
    public static string BuildWouldPublishLine(IReadOnlyList<string> tables) =>
        $"Would publish {NameThem(tables)} so the form change takes effect.";

    static string NameThem(IReadOnlyList<string> tables) => tables.Count switch
    {
        0 => "customizations",
        1 => tables[0],
        2 => $"{tables[0]} and {tables[1]}",
        _ => $"{string.Join(", ", tables.Take(tables.Count - 1))} and {tables[^1]}",
    };

    /// <summary>
    /// Warns about what a fresh deploy would not reproduce (KTD33).
    /// </summary>
    /// <remarks>
    /// Says what is at stake rather than counting omissions. The old wording reported that N components
    /// "aren't in the file", which was true of most of any solution and so was never a reason to do
    /// anything.
    ///
    /// One sentence covers both halves of the set — a component switched off here, and a value nothing has
    /// set — because the question is the same for both. The remedy is one command, since this is exactly
    /// what a capture would add.
    /// </remarks>
    public static string BuildUndeclaredWarning(int count) =>
        count == 1
            ? "1 component isn't recorded in the settings file, so a fresh deploy wouldn't reproduce it. "
              + "Run 'flowline settings capture' to record it, or --verbose to see it."
            : $"{count} components aren't recorded in the settings file, so a fresh deploy wouldn't "
              + "reproduce them. Run 'flowline settings capture' to record them, or --verbose to list them.";

    /// <summary>A capture's dry run leaves a file unwritten, not an environment untouched.</summary>
    public static string BuildPullDryRunMessage(string path) =>
        $"Dry run complete — {Markup.Escape(path)} wasn't written. Run without --dry-run to write it.";

    /// <summary>The error a capture earns when it cannot name the file it would write.</summary>
    /// <remarks>
    /// KTD17: falling back to the shared, un-suffixed file is the one thing this must not do. That file is a
    /// live fallback for every environment, so a capture written there would apply one environment's
    /// connection identifiers everywhere.
    /// </remarks>
    public static string BuildUninferrableRoleError(string target) =>
        $"Couldn't tell which environment '{target}' is, so there's no role to name the file after. " +
        "Pass --settings-file with the path to write.";

    /// <summary>The error a destination flag earns when the capture is a whole-project sweep.</summary>
    public static string BuildSweepDestinationError() =>
        "--settings-file names one file, and capturing every configured environment writes one per role. " +
        "Name an environment, or drop --settings-file.";

    /// <summary>Decides whether a capture's artifact argument names a packed zip or an unpacked folder.</summary>
    /// <param name="baseDirectory">
    /// What a relative path is relative to. Defaults to the working directory, which is what a path typed
    /// into a shell means. A caller passes one only to avoid the alternative, which is moving the whole
    /// process to a directory so that a relative path resolves — global state that other work running at
    /// the same time inherits.
    /// </param>
    public static (string Path, bool IsZip) ResolveSolutionInput(string path, string? baseDirectory = null)
    {
        var full = Path.GetFullPath(path, baseDirectory ?? Directory.GetCurrentDirectory());

        if (Directory.Exists(full)) return (full, false);
        if (File.Exists(full)) return (full, true);

        throw new FlowlineException(ExitCode.NotFound,
            $"No solution zip or folder at '{path}'.");
    }

    /// <summary>Reads a solution's unique name from an unpacked folder's manifest.</summary>
    /// <remarks>
    /// <see cref="DeployCommand.ReadArtifactSolutionManifest"/> is zip-only and throws on a directory, so an
    /// unpacked folder reads <c>Other/Solution.xml</c> and hands it to the same parser that helper uses.
    /// </remarks>
    public static string? ReadFolderSolutionUniqueName(string folder)
    {
        var manifest = Path.Combine(folder, "Other", "Solution.xml");
        if (!File.Exists(manifest))
            throw new FlowlineException(ExitCode.NotFound,
                $"No solution manifest at '{manifest}' — is '{folder}' an unpacked solution folder?");

        return DeployCommand.ParseSolutionManifest(XDocument.Load(manifest)).UniqueName;
    }
}
