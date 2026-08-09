using Microsoft.Crm.Sdk.Messages;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.Deploy;

// Read-only preflight: hands the packed solution to RetrieveMissingComponents and blocks the deploy
// when the target lacks anything it needs. KTD6: blocks on every missing component regardless of
// origin — including first-party apps — and treats managed/unmanaged targets identically, since
// neither branches this service's logic. Never writes to Dataverse.
public class MissingComponentCheckService(IAnsiConsole console) : IPostDeployService
{
    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        RetrieveMissingComponentsResponse response;
        try
        {
            var zipBytes = await File.ReadAllBytesAsync(context.PackagePath, ct).ConfigureAwait(false);
            response = (RetrieveMissingComponentsResponse)await console.Status().FlowlineSpinner()
                .StartAsync("Checking target for missing components...",
                    _ => context.Service.ExecuteAsync(new RetrieveMissingComponentsRequest { CustomizationFile = zipBytes }, ct))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // R12/KTD7: a preflight that can't run fails distinctly from one that ran and found
            // components missing — different ExitCode, different wording, names the skip flag.
            // The filter also consults the token, not just the exception type: a client-side timeout
            // surfaces as OperationCanceledException/TaskCanceledException without ever signalling the
            // caller's token, and that must classify as "check couldn't run", not "user cancelled".
            throw new FlowlineException(ExitCode.ConnectionFailed,
                $"Missing-component check couldn't run against the target ({ex.Message}) — use --skip-component-check to deploy without it.", ex);
        }

        var results = MapMissingComponents(response.MissingComponents);

        if (results.Count == 0)
        {
            MissingComponentReport.ClearReport(context.PackagePath, context.Solution.EnvironmentUrl);
            console.Ok("No missing components.");
            return;
        }

        var reportPath = MissingComponentReport.Write(context.PackagePath, context.Solution.EnvironmentUrl, context.Solution.Name, results);
        throw new FlowlineException(ExitCode.ValidationFailed, MissingComponentReport.RenderFailureMessage(results, reportPath));
    }

    public Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct) => Task.FromResult(0);

    // Never surfaces RequiredComponent.Id/ParentId/ParentDisplayName/ParentSchemaName — a live probe
    // against a real environment found SchemaName/DisplayName/Solution reliably populated on
    // RequiredComponent, but Id came back Guid.Empty and ParentDisplayName blank; DependentComponent's
    // Solution came back empty too. Omitting Id entirely guarantees a bare GUID never reaches the report.
    internal static List<MissingComponentResult> MapMissingComponents(MissingComponent[]? missingComponents) =>
        (missingComponents ?? [])
            .Select(m => new MissingComponentResult(
                Blank(m.RequiredComponent?.SchemaName),
                Blank(m.RequiredComponent?.DisplayName),
                Blank(m.RequiredComponent?.Solution),
                m.RequiredComponent?.Type ?? 0,
                Blank(m.DependentComponent?.SchemaName),
                Blank(m.DependentComponent?.DisplayName)))
            .ToList();

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
