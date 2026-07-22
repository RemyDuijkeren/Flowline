using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup;

// Shared fault-tolerant query wrapper for orphan-detection queries — a business fault (the table
// genuinely has no matching rows) degrades silently to `fallback`; any other fault (network/auth/
// throttling) additionally warns via console before degrading, since that's a real failure the
// operator should see. Consolidates the same catch/catch pair independently duplicated across every
// OrphanCleanup handler and OrphanCleanupService.
public static class DataverseFaultTolerance
{
    public static async Task<T> TryQueryAsync<T>(
        Func<Task<T>> query, T fallback, IAnsiConsole console, Func<string, string> warning, Action? onFault = null)
    {
        try
        {
            return await query().ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            onFault?.Invoke();
            return fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onFault?.Invoke();
            console.Warning(warning(Markup.Escape(ex.Message)));
            return fallback;
        }
    }
}
