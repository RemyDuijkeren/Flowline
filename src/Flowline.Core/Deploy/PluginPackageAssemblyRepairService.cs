using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Plugins;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.Deploy;

// Pre-import counterpart to PluginPackageAssemblyCheckService. Dataverse doesn't create a
// pluginassembly record for an assembly added to a package the target already holds, and the record
// is what the import needs before it can bind plugin types. Measured 2026-08-24: with the record
// present and zero plugin types, the import's own content write creates the types and the step. So
// this creates the record and nothing else — no content upload, nothing the import wasn't already
// going to write.
//
// Why pre-import rather than in the check service: when a step is bound to the missing assembly the
// import fails outright (exit 13, rolled back, "A record for PluginType with hash value ... is not
// found"), and every post-import service is skipped. A repair placed after the import can never run
// on the case that needs it most.
//
// This never throws and never blocks. A repair that can't run lets the import proceed and fail on
// its own terms — Flowline doesn't refuse a deploy on a prediction that the platform will reject it,
// and the day the platform registers these itself this service simply finds nothing to do.
public class PluginPackageAssemblyRepairService(IAnsiConsole console) : IPostDeployService
{
    // Same discarding console the check service uses: AnalyzePackage's per-DLL "analyzed" lines and the
    // scanner's warnings are push-time output with no place in a deploy. Reflection results are cached
    // by PluginPackageContentReader, so the check service's later walk over the same packages is free.
    readonly PluginAssemblyReader _assemblyReader = new(PluginPackageContentReader.DiscardConsole);
    readonly PluginReader _reader = new();

    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        try
        {
            var packagesRoot = Path.Combine(context.DataverseSolutionSrcRoot, "pluginpackages");
            if (!Directory.Exists(packagesRoot))
                return;

            foreach (var packageDir in Directory.GetDirectories(packagesRoot).OrderBy(d => d, StringComparer.Ordinal))
                await RepairPackageAsync(context, packageDir, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Nothing on this path is worth failing a deploy that hasn't started yet. The import runs,
            // and the post-import check still reports whatever is left unregistered.
            console.Warning($"Plugin package assembly check couldn't run before the import: {Markup.Escape(ex.Message)}. Importing anyway.");
        }
    }

    // The post-import half is PluginPackageAssemblyCheckService's job — it already verifies what the
    // import left behind, including anything this service created.
    public Task<PostDeployOutcome> RunPostImportAsync(PostDeployContext context, CancellationToken ct) =>
        Task.FromResult(PostDeployOutcome.Clean);

    async Task RepairPackageAsync(PostDeployContext context, string packageDir, CancellationToken ct)
    {
        var packageLabel = Path.GetFileName(packageDir);
        try
        {
            var uniqueName = PluginPackageContentReader.ReadPackageUniqueName(packageDir);
            packageLabel = uniqueName;

            // Null (no .nupkg) and empty (no plugin-bearing DLL) both mean there is nothing to register.
            // Neither is reported here — the post-import check distinguishes them and owns that verdict.
            var reflected = PluginPackageContentReader.ReflectPackageContent(packageDir, _assemblyReader);
            if (reflected == null || reflected.Count == 0)
                return;

            var packageId = await _reader.FindPackageIdAsync(context.Service, uniqueName, ct).ConfigureAwait(false);
            if (packageId == null)
                return; // Target doesn't hold this package. A first import creates it and registers its content.

            foreach (var metadata in reflected)
            {
                var existing = await _reader.FindPackageAssemblyAsync(context.Service, packageId.Value, metadata.Name, ct).ConfigureAwait(false);
                if (existing != null)
                    continue; // Registered. A row with no plugin types needs no create either — the import's content write populates them.

                await RegisterAsync(context, packageId.Value, uniqueName, metadata, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            console.Warning($"Couldn't check package '{Markup.Escape(packageLabel)}' for missing assembly registrations: {Markup.Escape(ex.Message)}. Importing anyway.");
        }
    }

    // Observation runs for every target; only the write is gated. Both refusing branches still say what
    // they found — a managed import fails on a bound step exactly as an unmanaged one does, and when it
    // does the post-import check never runs, so this is the only place that can name the cause.
    async Task RegisterAsync(PostDeployContext context, Guid packageId, string packageUniqueName, PluginAssemblyMetadata metadata, CancellationToken ct)
    {
        var label = $"[bold]{Markup.Escape(metadata.Name)}[/] ({Markup.Escape(metadata.Version)}) in package [bold]{Markup.Escape(packageUniqueName)}[/]";

        // Creating an unmanaged record under managed components leaves an unmanaged layer that outlives
        // an uninstall, and managed imports of a package in this state are unmeasured. Report only.
        if (context.Solution.IncludeManaged)
        {
            // One Warning carrying every line, matching PluginPackageAssemblyCheckService's house shape —
            // two calls would print two glyphs for one finding.
            console.Warning(string.Join(Environment.NewLine,
                $"{label} has no registration in the target — it won't run, and the import fails if anything is bound to it.",
                "Fix it: create the pluginassembly record under that package with isolationmode sandbox and the assembly's own version, culture and public key token.",
                "Flowline creates that record itself on an unmanaged target, but never on a managed one."));
            return;
        }

        if (context.Mode.IsReportOnly())
        {
            console.Skip($"{label} has no registration in the target — would create it, skipping (report-only run).");
            return;
        }

        try
        {
            await PackageAssemblyRegistrar.CreateAsync(context.Service, packageId, metadata, context.Solution.Name, ct).ConfigureAwait(false);
            console.Ok($"{label} had no registration in the target — created it, so the import can bind its plugin types.");
        }
        catch (FlowlineException ex)
        {
            console.Warning($"{label} has no registration in the target and Flowline couldn't create one: {Markup.Escape(ex.Message)}. Importing anyway — the import fails if anything is bound to it.");
        }
    }
}
