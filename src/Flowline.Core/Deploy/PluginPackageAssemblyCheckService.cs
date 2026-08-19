using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Plugins;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.Deploy;

// KTD1: post-import-only IPostDeployService — observes whether the platform registered every
// plugin-bearing assembly it just imported, and never predicts (Key Decisions: "Observe after the
// import; never predict before it"). KTD2: reads the package from context.DataverseSolutionSrcRoot,
// the unpack of whatever zip was actually imported, not the local checkout. KTD3: runs after orphan
// cleanup so it sees the state deploy actually leaves behind. Never writes to Dataverse — "Report,
// never repair".
public class PluginPackageAssemblyCheckService(IAnsiConsole console) : IPostDeployService
{
    // KTD4: no discard console exists in Flowline.Core today. PluginAssemblyReader.AnalyzePackage
    // prints an "analyzed" line per plugin-bearing DLL, and the scanner it drives emits its own
    // warnings — push-time output with no place in a deploy's post-import summary.
    static readonly IAnsiConsole DiscardConsole =
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(TextWriter.Null) });

    readonly PluginAssemblyReader _assemblyReader = new(DiscardConsole);
    readonly PluginReader _reader = new();

    // Instance (not const) so tests can shrink the budget instead of paying ~4 real seconds per
    // retry scenario — mirrors PluginService.PackageAssemblyCheckMaxAttempts/Delay. Production
    // callers never touch these.
    internal int PollMaxAttempts { get; set; } = 5;
    internal TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(1);

    // KTD1: no pre-import half by design — this check only has meaning once an import has happened.
    public Task RunPreImportAsync(PostDeployContext context, CancellationToken ct) => Task.CompletedTask;

    public async Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct)
    {
        var packagesRoot = Path.Combine(context.DataverseSolutionSrcRoot, "pluginpackages");
        if (!Directory.Exists(packagesRoot))
            return 0; // R5: solution carries no plug-in package — no output, no exit-code change.

        var packageDirs = Directory.GetDirectories(packagesRoot).OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (packageDirs.Count == 0)
            return 0;

        var findings = 0;
        // Step 7: the verdict line only claims "all registered" when every package directory was
        // fully evaluated — reflected at least one plugin-bearing assembly, was found in the target,
        // and the poll ran to completion without a fault. Any package that fell short of that (an
        // empty reflection, a package the target doesn't hold, or an R7 fault) suppresses the verdict
        // even when it printed nothing itself — a clean verdict that also covers what was skipped is
        // the false all-clear this check exists to remove.
        var allFullyEvaluated = true;

        foreach (var packageDir in packageDirs)
        {
            var (packageFindings, fullyEvaluated) = await CheckPackageAsync(context, packageDir, ct).ConfigureAwait(false);
            findings += packageFindings;
            allFullyEvaluated &= fullyEvaluated;
        }

        if (allFullyEvaluated)
            console.Ok("Plugin package assemblies are all registered.");

        return findings;
    }

    // R7 wraps the whole per-package body (steps 3, 4 and 5), not reflection alone — DeployCommand's
    // post-import loop has no try/catch of its own around a service's findings, and an import that
    // already committed must not fail a deploy just because verification couldn't complete.
    // AnalyzePackage also throws outright on a package holding a workflow activity type, with wording
    // aimed at a push-time author, so the catch has to be wide and this service's own warn text used.
    async Task<(int Findings, bool FullyEvaluated)> CheckPackageAsync(PostDeployContext context, string packageDir, CancellationToken ct)
    {
        var packageLabel = Path.GetFileName(packageDir);
        try
        {
            var uniqueName = PluginPackageContentReader.ReadPackageUniqueName(packageDir);
            packageLabel = uniqueName;

            // U5/KTD7: shared walk with the orphan classifier's package-content exclusion check — see
            // PluginPackageContentReader. KTD4: the reader's discarding console keeps its push-time
            // "analyzed" lines and the scanner's own warnings out of deploy output.
            var reflected = PluginPackageContentReader.ReflectPackageContent(packageDir, _assemblyReader);
            if (reflected == null)
            {
                console.Warning($"Package '{Markup.Escape(packageLabel)}' has no .nupkg under its unpacked package folder. Its assembly registrations couldn't be checked.");
                return (0, false);
            }
            if (reflected.Count == 0)
                return (0, false); // nothing plugin-bearing reflected — can't claim a clean result for it.

            var packageId = await FindPackageIdAsync(context.Service, uniqueName, ct).ConfigureAwait(false);
            if (packageId == null)
                return (0, false); // R6: target doesn't hold this package at all — no finding, no output.

            var missing = await PollForMissingAsync(context.Service, packageId.Value, reflected, ct).ConfigureAwait(false);
            if (missing.Count == 0)
                return (0, true);

            foreach (var metadata in missing)
                console.Warning(BuildFindingMessage(metadata, uniqueName));

            return (missing.Count, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            console.Warning($"Plugin package assembly check couldn't finish for '{Markup.Escape(packageLabel)}': {Markup.Escape(ex.Message)}. Verify its registrations manually.");
            return (0, false);
        }
    }

    // Mirrors PluginService.cs's inline pluginpackage-by-uniquename query — the only such query today,
    // and one call site here doesn't justify extracting a shared helper (KTD4).
    static async Task<Guid?> FindPackageIdAsync(
        IOrganizationServiceAsync2 service, string uniqueName, CancellationToken ct)
    {
        var query = new QueryExpression("pluginpackage")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("pluginpackageid"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, uniqueName) } }
        };
        var result = await service.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
        return result.Entities.FirstOrDefault()?.Id;
    }

    // Step 5: the same bounded poll push's LoadPackageSnapshotsWithRetryAsync uses — five attempts,
    // one second apart, re-querying only the assemblies still missing on each round. The deploy path
    // needs this more than push does, not less: import runs as an async job, so the write that would
    // create the record is further from this read than on push's direct write.
    async Task<List<PluginAssemblyMetadata>> PollForMissingAsync(
        IOrganizationServiceAsync2 service,
        Guid packageId,
        List<PluginAssemblyMetadata> reflected,
        CancellationToken ct)
    {
        var stillMissing = reflected;
        for (var attempt = 1; attempt <= PollMaxAttempts; attempt++)
        {
            var results = await Task.WhenAll(stillMissing.Select(async metadata =>
                (Metadata: metadata,
                    Found: await _reader.FindPackageAssemblyAsync(service, packageId, metadata.Name, ct).ConfigureAwait(false) != null)))
                .ConfigureAwait(false);

            stillMissing = results.Where(r => !r.Found).Select(r => r.Metadata).ToList();
            if (stillMissing.Count == 0 || attempt == PollMaxAttempts)
                break;

            await Task.Delay(PollDelay, ct).ConfigureAwait(false);
        }

        return stillMissing;
    }

    // R3/remedy: names the assembly, its version and its package, states it will not run, names the
    // remedy the plan defines once, and says the finding and its non-zero exit repeat on every later
    // deploy until that remedy is done.
    static string BuildFindingMessage(PluginAssemblyMetadata metadata, string packageUniqueName) =>
        $"'{Markup.Escape(metadata.Name)}' ({Markup.Escape(metadata.Version)}) in package '{Markup.Escape(packageUniqueName)}' has no registration in the target, so it will not run. " +
        "Create the pluginassembly record under that package with isolationmode sandbox and the assembly's own version, culture and public key token, then deploy again so the content write populates its plugin types. " +
        "This finding and its non-zero exit repeat on every later deploy until that record exists.";
}
