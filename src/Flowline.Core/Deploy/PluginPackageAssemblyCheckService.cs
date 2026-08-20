using System.IO.Compression;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
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
    // KTD4: PluginAssemblyReader.AnalyzePackage prints an "analyzed" line per plugin-bearing DLL, and
    // the scanner it drives emits its own warnings — push-time output with no place in a deploy's
    // post-import summary. Shared with PluginPackageContentReader, the other consumer of the same
    // reflection walk, rather than declared once per consumer.
    readonly PluginAssemblyReader _assemblyReader = new(PluginPackageContentReader.DiscardConsole);
    readonly PluginReader _reader = new();

    // Instance (not const) so tests can shrink the budget instead of paying ~4 real seconds per
    // retry scenario — mirrors PluginService.PackageAssemblyCheckMaxAttempts/Delay. Production
    // callers never touch these.
    internal int PollMaxAttempts { get; set; } = 5;
    internal TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(1);

    // KTD1: no pre-import half by design — this check only has meaning once an import has happened.
    public Task RunPreImportAsync(PostDeployContext context, CancellationToken ct) => Task.CompletedTask;

    // FIX D: wraps the whole method (directory enumeration through the terminal verdict line) in the
    // same exception filter CheckPackageAsync uses below — an import that already committed must not
    // fail a deploy just because something on this verification-only path (a locked directory, a
    // console write) went wrong.
    public async Task<PostDeployOutcome> RunPostImportAsync(PostDeployContext context, CancellationToken ct)
    {
        try
        {
            var packagesRoot = Path.Combine(context.DataverseSolutionSrcRoot, "pluginpackages");
            if (!Directory.Exists(packagesRoot))
                return PostDeployOutcome.Clean; // R5/Fix C&D: solution carries no plug-in package — silent, not inconclusive.

            var packageDirs = Directory.GetDirectories(packagesRoot).OrderBy(d => d, StringComparer.Ordinal).ToList();
            if (packageDirs.Count == 0)
                return PostDeployOutcome.Clean;

            var findings = 0;
            // Step 7: the verdict line only claims "all registered" when every package directory was
            // fully evaluated — reflected at least one plugin-bearing assembly, was found in the target,
            // and the poll ran to completion without a fault. Any package that fell short of that (an
            // empty reflection, a package the target doesn't hold, or an R7 fault) suppresses the verdict
            // even when it printed nothing itself — a clean verdict that also covers what was skipped is
            // the false all-clear this check exists to remove.
            var allFullyEvaluated = true;
            var anyInconclusive = false;

            foreach (var packageDir in packageDirs)
            {
                var (packageFindings, fullyEvaluated, inconclusive) = await CheckPackageAsync(context, packageDir, ct).ConfigureAwait(false);
                findings += packageFindings;
                allFullyEvaluated &= fullyEvaluated;
                anyInconclusive |= inconclusive;
            }

            if (allFullyEvaluated)
                console.Ok("Plugin package assemblies are all registered.");

            return new PostDeployOutcome(findings, anyInconclusive, findings > 0 ? ExitCode.AssemblyNotRegistered : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            console.Warning($"Plugin package assembly check couldn't finish: {Markup.Escape(ex.Message)}. Verify plugin package registrations manually.");
            return new PostDeployOutcome(0, true, null);
        }
    }

    // R7 wraps the whole per-package body (steps 3, 4 and 5), not reflection alone — DeployCommand's
    // post-import loop has no try/catch of its own around a service's findings, and an import that
    // already committed must not fail a deploy just because verification couldn't complete.
    // AnalyzePackage also throws outright on a package holding a workflow activity type, with wording
    // aimed at a push-time author, so the catch has to be wide and this service's own warn text used.
    async Task<(int Findings, bool FullyEvaluated, bool Inconclusive)> CheckPackageAsync(PostDeployContext context, string packageDir, CancellationToken ct)
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
                return (0, false, true); // Fix C: couldn't inspect this package — inconclusive, not clean.
            }
            if (reflected.Count == 0)
            {
                // Fix C: AnalyzePackage returns an empty list both when the .nupkg has no DLLs at all and
                // when its DLLs are genuinely dependency-only (no IPlugin type) — only the first case means
                // this check couldn't verify anything. The second is a normal, silent result.
                return PackageHasAnyDll(packageDir) ? (0, false, false) : (0, false, true);
            }

            var packageId = await FindPackageIdAsync(context.Service, uniqueName, ct).ConfigureAwait(false);
            if (packageId == null)
                return (0, false, false); // R6: target doesn't hold this package at all — no finding, no output.

            var (missing, foundAssemblies) = await PollForMissingAsync(context.Service, packageId.Value, reflected, ct).ConfigureAwait(false);

            var findingMessages = missing.Select(metadata => BuildFindingMessage(metadata, uniqueName)).ToList();

            // Fix B: a pluginassembly row can exist under the package and still carry zero plugin types —
            // exactly the state the documented remedy passes through when the import hasn't written
            // content yet. That runs nothing, so it's a finding, not a clean result.
            var reflectedByName = reflected.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, assembly) in foundAssemblies)
            {
                var pluginTypes = await _reader.GetRegisteredPluginTypesAsync(context.Service, assembly.Id, ct).ConfigureAwait(false);
                if (pluginTypes.Count == 0)
                    findingMessages.Add(BuildNoPluginTypesMessage(reflectedByName[name], uniqueName));
            }

            if (findingMessages.Count == 0)
                return (0, true, false);

            foreach (var message in findingMessages)
                console.Warning(message);

            return (findingMessages.Count, false, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            console.Warning($"Plugin package assembly check couldn't finish for '{Markup.Escape(packageLabel)}': {Markup.Escape(ex.Message)}. Verify its registrations manually.");
            return (0, false, true); // Fix C: R7 — couldn't run to completion, inconclusive.
        }
    }

    // Fix C: distinguishes "nothing readable in the nupkg" from "DLLs present, none plugin-bearing" —
    // AnalyzePackage's returned list is empty either way, so this re-checks the .nupkg's own lib/
    // entries directly rather than trusting an absence it can't explain.
    static bool PackageHasAnyDll(string packageDir)
    {
        var nupkgDir = Path.Combine(packageDir, "package");
        var nupkgPath = Directory.Exists(nupkgDir) ? Directory.EnumerateFiles(nupkgDir, "*.nupkg").FirstOrDefault() : null;
        if (nupkgPath == null) return false;

        using var archive = ZipFile.OpenRead(nupkgPath);
        return archive.Entries.Any(e =>
            e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
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
    // Fix B: also returns the found pluginassembly Entity per name, so the caller can check its plugin
    // type count without a second poll — the missing/found split happens here, once.
    async Task<(List<PluginAssemblyMetadata> Missing, Dictionary<string, Entity> Found)> PollForMissingAsync(
        IOrganizationServiceAsync2 service,
        Guid packageId,
        List<PluginAssemblyMetadata> reflected,
        CancellationToken ct)
    {
        var stillMissing = reflected;
        var found = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 1; attempt <= PollMaxAttempts; attempt++)
        {
            var results = await Task.WhenAll(stillMissing.Select(async metadata =>
                (Metadata: metadata,
                    Assembly: await _reader.FindPackageAssemblyAsync(service, packageId, metadata.Name, ct).ConfigureAwait(false))))
                .ConfigureAwait(false);

            foreach (var result in results.Where(r => r.Assembly != null))
                found[result.Metadata.Name] = result.Assembly!;

            stillMissing = results.Where(r => r.Assembly == null).Select(r => r.Metadata).ToList();
            if (stillMissing.Count == 0 || attempt == PollMaxAttempts)
                break;

            await Task.Delay(PollDelay, ct).ConfigureAwait(false);
        }

        return (stillMissing, found);
    }

    // R3/remedy/Fix D: house shape — a warning line naming assembly, version, package and that it will
    // not run; a Fix it: line with the remedy the plan defines once; a line saying the finding and its
    // non-zero exit repeat on every later deploy until that remedy is done.
    static string BuildFindingMessage(PluginAssemblyMetadata metadata, string packageUniqueName) => string.Join(Environment.NewLine,
        $"'{Markup.Escape(metadata.Name)}' ({Markup.Escape(metadata.Version)}) in package '{Markup.Escape(packageUniqueName)}' has no registration in the target — it will not run.",
        "Fix it: create the pluginassembly record under that package with isolationmode sandbox and the assembly's own version, culture and public key token, then deploy again so the content write populates its plugin types.",
        "This finding and its non-zero exit repeat on every later deploy until that record exists.");

    // Fix B: same house shape as BuildFindingMessage, for the record-exists-but-empty case — the
    // record itself doesn't need creating here, only a content write, so the Fix it: line is narrower.
    static string BuildNoPluginTypesMessage(PluginAssemblyMetadata metadata, string packageUniqueName) => string.Join(Environment.NewLine,
        $"'{Markup.Escape(metadata.Name)}' ({Markup.Escape(metadata.Version)}) in package '{Markup.Escape(packageUniqueName)}' is registered under the package but carries no plugin types — nothing in it runs.",
        "Fix it: deploy again so the content write populates its plugin types.",
        "This finding and its non-zero exit repeat on every later deploy until it does.");
}
