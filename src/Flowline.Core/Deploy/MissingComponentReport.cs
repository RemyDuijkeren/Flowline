namespace Flowline.Core.Deploy;

// Rendering and report-file lifecycle for the missing-component gate (U3) — kept separate from
// MissingComponentCheckService so the message shape and file I/O are testable without a Dataverse call.
public static class MissingComponentReport
{
    const int TerminalPreviewCount = 5;

    // Beside the packed artifact, same directory-composition technique as
    // src/Flowline/Services/SolutionCheckService.cs:14-15. Qualified by target so the same packed
    // artifact — reused across DTAP stages via the artifact cache — never has one target's report
    // clobbered or cleared by another's run. Path.GetFullPath so a relative/bare --path package still
    // resolves a directory (Path.GetDirectoryName("sol.zip") is "", not null, so the temp fallback
    // never fires without this).
    public static string GetReportPath(string packagePath, string targetUrl) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? Path.GetTempPath(), $"missing-components-{TargetSlug(targetUrl)}.txt");

    // Full host, dots replaced — not just the first label — so two regions of the same org
    // (contoso.crm4.dynamics.com vs contoso.crm11.dynamics.com) never collide on the same slug.
    // `?? ""` guards a null/unparseable targetUrl rather than throwing out of a report-path lookup.
    static string TargetSlug(string targetUrl)
    {
        var host = Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) ? uri.Host : targetUrl;
        var label = (host ?? "").Replace('.', '-');
        foreach (var c in Path.GetInvalidFileNameChars())
            label = label.Replace(c, '-');
        return label.Length > 0 ? label : "target";
    }

    // KTD4/R8: written only when the gate finds something — the caller only calls this on a non-empty
    // result set. Returns null when the report couldn't be written: the verdict is what blocks the
    // deploy, so a failure here degrades the message rather than masking the components behind an
    // unrelated IO error.
    public static string? Write(string packagePath, string targetUrl, string solutionName, IReadOnlyList<MissingComponentResult> results)
    {
        var reportPath = GetReportPath(packagePath, targetUrl);
        try
        {
            var header = $"# {solutionName} -> {targetUrl} ({DateTime.UtcNow:u})";
            File.WriteAllLines(reportPath, [header, ..results.Select((r, i) => $"{i + 1}. {FormatComponentLine(r)}")]);
            return reportPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // R8: a clean run removes any report an earlier blocked run left behind, so the file's presence
    // always describes the latest outcome. Best-effort — a stale report is a stale signal, not a
    // reason to fail a deploy that passed. Only clears the report for the target just checked.
    public static void ClearReport(string packagePath, string targetUrl)
    {
        var reportPath = GetReportPath(packagePath, targetUrl);
        try
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Intentional: the deploy succeeded; a leftover report must not turn that into a failure.
        }
    }

    // R7/R9: verdict line, first five components, a pointer to the full report, then both remedy
    // routes — install the dependency in the target, or drop it from the solution in DEV and re-sync.
    public static string RenderFailureMessage(IReadOnlyList<MissingComponentResult> results, string? reportPath)
    {
        var lines = new List<string>
        {
            $"Target is missing {results.Count} required component{(results.Count == 1 ? "" : "s")} — deploy stopped before import."
        };

        foreach (var result in results.Take(TerminalPreviewCount))
            lines.Add($"  {FormatComponentLine(result)}");

        if (results.Count > TerminalPreviewCount)
            lines.Add($"  ...and {results.Count - TerminalPreviewCount} more");

        lines.Add(reportPath != null
            ? $"Full list: {reportPath}"
            : "Couldn't write the full report — the components above are the first five of the set.");
        lines.Add("Fix it: install the missing solution or application in the target, or remove the dependent component from the solution in DEV and run 'flowline sync'.");
        lines.Add("Last resort: --skip-component-check deploys without this check.");

        return string.Join(Environment.NewLine, lines);
    }

    // Degrades per field: an absent owning solution or dependent drops its whole clause rather than
    // printing an empty artifact ("in ''", ", required by "). Never falls back to a GUID — the schema
    // name / display name are all this ever prints for identity.
    internal static string FormatComponentLine(MissingComponentResult result)
    {
        var name = FormatIdentifier(result.RequiredSchemaName, result.RequiredDisplayName) ?? "(unnamed component)";
        var typeSuffix = ComponentTypeLabels.TryGetValue(result.RequiredComponentType, out var label) ? $" ({label})" : "";
        var solutionSuffix = result.RequiredSolution != null ? $" — in '{result.RequiredSolution}'" : "";
        var dependent = FormatIdentifier(result.DependentSchemaName, result.DependentDisplayName);
        var dependentSuffix = dependent != null ? $", required by {dependent}" : "";

        return $"{name}{typeSuffix}{solutionSuffix}{dependentSuffix}";
    }

    static string? FormatIdentifier(string? schemaName, string? displayName) =>
        schemaName != null && displayName != null && !schemaName.Equals(displayName, StringComparison.Ordinal)
            ? $"{displayName} ({schemaName})"
            : schemaName ?? displayName;

    // solutioncomponent.componenttype labels (learn.microsoft.com/power-apps/developer/data-platform/
    // reference/entities/solutioncomponent) — not exhaustive; an unmapped type renders without a label
    // rather than a guessed one. Mirrors OrphanCleanupService.ManualTypeLabels.
    static readonly Dictionary<int, string> ComponentTypeLabels = new()
    {
        [1]   = "Entity",
        [2]   = "Attribute",
        [3]   = "Relationship",
        [9]   = "OptionSet",
        [20]  = "Role",
        [24]  = "Form",
        [26]  = "View",
        [29]  = "Workflow",
        [31]  = "Report",
        [59]  = "Chart",
        [60]  = "SystemForm",
        [61]  = "WebResource",
        [62]  = "SiteMap",
        [63]  = "ConnectionRole",
        [66]  = "CustomControl",
        [90]  = "PluginType",
        [91]  = "PluginAssembly",
        [92]  = "SdkMessageProcessingStep",
        [93]  = "SdkMessageProcessingStepImage",
        [95]  = "ServiceEndpoint",
        [150] = "RoutingRule",
        [152] = "SLA",
        [161] = "MobileOfflineProfile",
        [165] = "SimilarityRule",
        [166] = "DataSourceMapping",
        [208] = "ImportMap",
        [300] = "CanvasApp",
        [371] = "Connector",
        [372] = "Connector",
        [380] = "EnvironmentVariableDefinition",
        [381] = "EnvironmentVariableValue",
    };
}
