using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
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
    // The whole packed zip goes inline in one org-service message — `pac solution import` avoids this
    // by chunked-uploading large solutions, so this check can fail at the transport layer above some
    // size while the import it guards would still succeed. That true ceiling has never been measured;
    // this is a conservative diagnostic trigger for the failure message below, not a verified limit.
    const long LargePayloadBytes = 32L * 1024 * 1024; // 32 MB

    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        RetrieveMissingComponentsResponse response;
        var payloadBytes = 0L;
        try
        {
            var zipBytes = await File.ReadAllBytesAsync(context.PackagePath, ct).ConfigureAwait(false);
            payloadBytes = zipBytes.Length;
            response = (RetrieveMissingComponentsResponse)await console.Status().FlowlineSpinner()
                .StartAsync("Checking target for missing components...",
                    _ => context.Service.ExecuteAsync(new RetrieveMissingComponentsRequest { CustomizationFile = zipBytes }, ct))
                .ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault> ex) when (IsPrivilegeFault(ex))
        {
            // Distinct from the transport failure below: a script/agent retrying on ConnectionFailed
            // would loop forever against a permanent privilege problem, so this needs its own ExitCode
            // and wording.
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"Missing-component check needs a privilege this account doesn't have ({ex.Message}) — use --skip-component-check to deploy without it.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // R12/KTD7: a preflight that can't run fails distinctly from one that ran and found
            // components missing — different ExitCode, different wording, names the skip flag.
            // The filter also consults the token, not just the exception type: a client-side timeout
            // surfaces as OperationCanceledException/TaskCanceledException without ever signalling the
            // caller's token, and that must classify as "check couldn't run", not "user cancelled".
            throw new FlowlineException(ExitCode.ConnectionFailed, BuildConnectionFailedMessage(ex.Message, payloadBytes), ex);
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

    // FIX B: names the payload size only once it's plausibly the cause — below the threshold, the
    // fault is far more likely a real connectivity/auth problem, and appending a size line there would
    // point every failure at the wrong culprit.
    internal static string BuildConnectionFailedMessage(string exceptionMessage, long payloadBytes)
    {
        var sizeNote = payloadBytes >= LargePayloadBytes
            ? $" Payload was {payloadBytes / (1024.0 * 1024.0):0.#} MB — a large solution may exceed the service's inline message limit."
            : "";
        return $"Missing-component check couldn't run against the target ({exceptionMessage}).{sizeNote} Use --skip-component-check to deploy without it.";
    }

    // FIX C: PrivilegeDenied (0x80040220) is the documented Dataverse SDK code for "missing prvXxx
    // privilege" faults; the message-text checks catch the access-denied variants that don't surface
    // under that code. Mirrors OrphanCleanupService.IsDependencyError's error-code + message-text pattern.
    internal static bool IsPrivilegeFault(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail?.ErrorCode == unchecked((int)0x80040220) ||
        (ex.Message?.Contains("privilege", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (ex.Message?.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ?? false);

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
