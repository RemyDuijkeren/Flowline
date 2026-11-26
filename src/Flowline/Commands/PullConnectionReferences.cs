using System.Text.RegularExpressions;
using Flowline.Core;
using Flowline.Core.ConnectionReferences;
using Flowline.Core.Console;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;

namespace Flowline.Commands;

/// <summary>
/// The connection reference review that runs inside <c>pull</c>, before the bump and export. Without
/// <c>--tidy-connrefs</c> it only reads and advises; with it, it asks at a terminal or acts on the safe part
/// unattended, then applies the cleanup to DEV.
/// </summary>
/// <remarks>
/// A helper beside pull's real job, so it never blocks the export unless the caller asked for the cleanup.
/// </remarks>
internal static class PullConnectionReferences
{
    internal enum Mode { Advise, Ask, Act }

    internal const string TidyCommand = "flowline pull --tidy-connrefs";

    /// <summary>
    /// No flag only advises, at a terminal or not. The flag is the consent: a terminal is asked, an
    /// unattended run acts on what needs no judgment.
    /// </summary>
    internal static Mode DecideMode(bool tidy, bool interactive) =>
        !tidy ? Mode.Advise : interactive ? Mode.Ask : Mode.Act;

    /// <summary>Runs the review and, when asked for, the cleanup. Throws only when the cleanup was asked for.</summary>
    internal static async Task RunAsync(
        IAnsiConsole console, Func<CancellationToken, Task<IOrganizationServiceAsync2>> connect,
        string environmentUrl, string solutionUniqueName, string settingsFolder, bool tidy, bool interactive,
        CancellationToken ct)
    {
        var mode = DecideMode(tidy, interactive);

        IOrganizationServiceAsync2 service;
        ConnectionReferenceReview review;
        try
        {
            var evidence = SettingsEvidence.Read(settingsFolder);
            (service, review) = await console.Status().FlowlineSpinner().StartAsync(
                "Checking connection references...", async _ =>
                {
                    var svc = await connect(ct);
                    return (svc, await ConnectionReferenceReviewer.ReadAsync(svc, solutionUniqueName, evidence, ct));
                });
        }
        catch (Exception ex) when (mode == Mode.Advise && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            console.Warning($"Couldn't check connection references: {Markup.Escape(ex.Message)}");
            return;
        }

        var c = review.Classification;

        if (mode == Mode.Advise)
        {
            var advice = AdviceLines(c);
            if (advice.Count == 0)
            {
                console.Verbose("Connection references: nothing to tidy");
                return;
            }

            console.Warning(Markup.Escape(advice[0]));
            console.Info(Markup.Escape(advice[1]));
            return;
        }

        if (!review.HasFindings)
        {
            console.Ok("Connection references are tidy");
            return;
        }

        foreach (var held in c.NotMerged)
            console.Info($"Not merging {Names(held.Members)}: {Markup.Escape(held.Reason)}");

        CleanupChoices choices;
        if (mode == Mode.Ask)
        {
            choices = await AskAsync(console, review, environmentUrl, ct);
        }
        else
        {
            choices = UnattendedChoices(c);
            var leftDifferent = c.DifferentAccount.Count;
            var leftNames = c.GeneratedNames.Count;
            if (leftDifferent + leftNames > 0)
                console.Info($"Left for a person: {Count(leftDifferent, "group", "groups")} on different accounts, " +
                             $"{Count(leftNames, "default name", "default names")}. Run '{TidyCommand}' at a terminal.");
        }

        if (choices.MergeSame.Count + choices.MergeDifferent.Count + choices.RemoveUnused.Count + choices.Renames.Count == 0)
        {
            console.Skip("Connection references: nothing to change");
            return;
        }

        var outcomes = await console.Status().FlowlineSpinner().StartAsync(
            "Tidying connection references...", _ =>
                ConnectionReferenceCleanup.ApplyAsync(service, solutionUniqueName, settingsFolder, review, choices, ct));

        foreach (var o in outcomes.Where(o => o.Status == CleanupStatus.Done))
            console.Ok(DoneLine(o));

        if (outcomes.Any(o => o.Status == CleanupStatus.Failed))
            throw StopException(outcomes);
    }

    /// <summary>The advice block a pull without the flag prints: one line of counts, one naming the fix. Empty when nothing is actionable.</summary>
    /// <remarks>Groups held back are left out: the flag cannot merge them either.</remarks>
    internal static IReadOnlyList<string> AdviceLines(Classification c)
    {
        var parts = new List<string>();
        if (c.SameConnection.Count > 0)
            parts.Add($"{Count(c.SameConnection.Count, "duplicate group", "duplicate groups")} on one connection");
        if (c.DifferentAccount.Count > 0)
            parts.Add($"{Count(c.DifferentAccount.Count, "group", "groups")} on different accounts");
        if (c.Unused.Count > 0)
            parts.Add($"{c.Unused.Count} unused");
        if (c.GeneratedNames.Count > 0)
            parts.Add($"{c.GeneratedNames.Count} with a default name");

        if (parts.Count == 0) return [];
        return
        [
            $"Connection references in DEV: {string.Join(", ", parts)}.",
            $"Run '{TidyCommand}' to untangle them.",
        ];
    }

    /// <summary>
    /// What an unattended run with the flag acts on: merges whose every environment has evidence, and unused
    /// references. Different accounts and renames need a person's judgment or a name.
    /// </summary>
    internal static CleanupChoices UnattendedChoices(Classification c) =>
        new(c.SameConnection.Where(g => g.MissingEvidence.Count == 0).ToList(), [], c.Unused, []);

    // ponytail: generated display names end in "<solution>-<5 hex>"; stripping that leaves the connector's
    // name. A display name that never had the suffix is used whole.
    static readonly Regex GeneratedSuffix = new(@"\s+\S+-[0-9a-f]{5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A starting point for a rename: the connector's name plus the label of the bound connection when the caller can see it.</summary>
    internal static string SuggestDisplayName(string currentDisplayName, string? connectionLabel)
    {
        var connector = GeneratedSuffix.Replace(currentDisplayName, "").Trim();
        return string.IsNullOrWhiteSpace(connectionLabel) ? connector : $"{connector} ({connectionLabel})";
    }

    /// <summary>The stop: what failed, what landed and what didn't, and how to finish.</summary>
    internal static FlowlineException StopException(IReadOnlyList<CleanupOutcome> outcomes)
    {
        var failed = outcomes.First(o => o.Status == CleanupStatus.Failed);
        var done = outcomes.Where(o => o.Status == CleanupStatus.Done).Select(Label).ToList();
        var notDone = outcomes.Where(o => o.Status == CleanupStatus.NotAttempted).Select(Label).ToList();

        var message = $"Connection reference cleanup stopped at {Label(failed)}: {failed.Detail?.TrimEnd('.') ?? "it failed"}.";
        if (done.Count > 0) message += $" Done: {string.Join(", ", done)}.";
        if (notDone.Count > 0) message += $" Not done: {string.Join(", ", notDone)}.";
        message += $" Nothing was exported. Fix it, then re-run '{TidyCommand}'.";
        return new FlowlineException(ExitCode.PartialSuccess, message);
    }

    static string Label(CleanupOutcome o) => o.Kind == "settings" ? "the settings files" : $"{o.Kind} '{o.Name}'";

    static string DoneLine(CleanupOutcome o)
    {
        var name = Markup.Escape(o.Name);
        var detail = o.Detail is null ? "" : $" ({Markup.Escape(o.Detail)})";
        return o.Kind switch
        {
            "flow" => $"Re-pointed flow [bold]{name}[/]",
            "settings" => $"Settings entries removed{detail}",
            "rename" => $"Renamed [bold]{name}[/]",
            _ => $"[bold]{name}[/]{detail}",
        };
    }

    static async Task<CleanupChoices> AskAsync(
        IAnsiConsole console, ConnectionReferenceReview review, string environmentUrl, CancellationToken ct)
    {
        var c = review.Classification;
        var mergeSame = new List<SameConnectionGroup>();
        var mergeDifferent = new List<DifferentAccountGroup>();
        var renameOffers = new List<ConnectionReference>();

        if (c.SameConnection.Count > 0)
        {
            console.Info("Same connection everywhere:");
            foreach (var g in c.SameConnection)
            {
                var missing = g.MissingEvidence.Count == 0 ? "" :
                    $" [dim](no settings file for {string.Join(", ", g.MissingEvidence)}, so not checked there)[/]";
                console.MarkupLine($"  {Names(g.Move)} into [bold]{Markup.Escape(g.Keeper.LogicalName)}[/], " +
                                   $"{Count(g.FlowsToMove.Count, "flow", "flows")} move{missing}");
            }

            if (await console.PromptAsync(new ConfirmationPrompt(FlowlineConsoleExtensions.Question(
                    $"Merge {(c.SameConnection.Count == 1 ? "it" : $"all {c.SameConnection.Count}")}?")), ct))
                mergeSame.AddRange(c.SameConnection);
        }

        foreach (var g in c.DifferentAccount)
        {
            var keeper = ConnectionReferenceClassifier.PickKeeper(g.Members);
            console.Info("Same connector, different accounts:");
            foreach (var m in g.Members)
            {
                var roles = string.Join(", ", m.Bindings.Where(b => b.Value.HasFile)
                    .Select(b => $"{b.Key}: {b.Value.ConnectionId ?? "unbound"}"));
                console.MarkupLine($"  [bold]{Markup.Escape(m.LogicalName)}[/] DEV: {Markup.Escape(m.DevConnectionId ?? "unbound")}" +
                                   (roles.Length > 0 ? $", {Markup.Escape(roles)}" : ""));
            }

            var merge = $"Merge into {keeper.LogicalName}";
            const string Keep = "Keep them separate";
            var answer = await console.PromptAsync(new SelectionPrompt<string>()
                .Title(FlowlineConsoleExtensions.Question("Merge them?"))
                .AddChoices(merge, Keep), ct);
            if (answer == Keep) renameOffers.AddRange(g.Members);
            else mergeDifferent.Add(g);
        }

        var removeUnused = new List<ConnectionReference>();
        if (c.Unused.Count > 0)
        {
            console.Info($"Nothing uses {Names(c.Unused)}.");
            if (await console.PromptAsync(new ConfirmationPrompt(FlowlineConsoleExtensions.Question(
                    $"Remove {(c.Unused.Count == 1 ? "it" : "them")}?")), ct))
                removeUnused.AddRange(c.Unused);
        }

        var moved = mergeSame.SelectMany(g => g.Move)
            .Concat(mergeDifferent.SelectMany(g => g.Members.Where(m => m != ConnectionReferenceClassifier.PickKeeper(g.Members))))
            .ToHashSet();
        renameOffers.AddRange(c.GeneratedNames);
        var offers = renameOffers.Where(r => !moved.Contains(r) && !removeUnused.Contains(r)).DistinctBy(r => r.Id).ToList();

        var renames = new List<Rename>();
        if (offers.Count > 0)
        {
            var labels = (await PacConnections.ListAsync(environmentUrl, ct))
                .ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);
            var taken = review.References.Select(r => r.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var r in offers)
            {
                var suggestion = SuggestDisplayName(r.DisplayName,
                    r.DevConnectionId is not null && labels.TryGetValue(r.DevConnectionId, out var l) ? l : null);
                var use = $"Rename to '{suggestion}'";
                const string Type = "Type a name";
                var keep = $"Keep '{r.DisplayName}'";
                var choices = suggestion == r.DisplayName || taken.Contains(suggestion) ? new[] { Type, keep } : [use, Type, keep];
                var answer = await console.PromptAsync(new SelectionPrompt<string>()
                    .Title(FlowlineConsoleExtensions.Question($"Rename {Markup.Escape(r.LogicalName)}?"))
                    .AddChoices(choices.Select(Markup.Escape)), ct);

                if (answer == Markup.Escape(keep)) continue;
                var name = answer == Markup.Escape(use) ? suggestion : await console.PromptAsync(
                    new TextPrompt<string>(FlowlineConsoleExtensions.Question("Display name:"))
                        .Validate(n => !string.IsNullOrWhiteSpace(n) && (n == r.DisplayName || !taken.Contains(n))
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Another connection reference already has that name.")), ct);

                if (name.Trim() == r.DisplayName) continue;
                taken.Add(name);
                renames.Add(new Rename(r, name.Trim()));
            }
        }

        return new CleanupChoices(mergeSame, mergeDifferent, removeUnused, renames);
    }

    static string Names(IEnumerable<ConnectionReference> refs) =>
        string.Join(", ", refs.Select(r => $"[bold]{Markup.Escape(r.LogicalName)}[/]"));

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
