using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Flowline.Core.Models;

namespace Flowline.Core.Plugins;

// The one place that creates a package-owned pluginassembly record. Two callers with different
// reasons to reach it — push, when Dataverse didn't register an assembly it just uploaded, and
// deploy, when the target is missing a record the import needs before it can bind plugin types —
// so the field set lives here rather than in either of them. Neither writes console output from
// here: push and deploy say different things about the same create.
internal static class PackageAssemblyRegistrar
{
    // KTD3/KTD4: smallest field set the create accepts, arrived at against a live environment by starting
    // from the minimum and adding only what Dataverse rejected the create without. It uses the same direct
    // request-plus-solution-name pattern GetOrRegisterAssemblyAsync uses.
    //
    // Two rejections shaped this set. Without isolationmode: "'<assembly>' is not allowed to be registered
    // in full-trust mode, assembly must be registered in isolation." Then, with only name/package/isolation:
    // "Unable to load plug-in assembly." A package-owned row carries no content of its own — the bytes live
    // in the package — so Dataverse resolves which DLL the row refers to from the assembly's full identity.
    // Name alone doesn't identify it; version, culture and public key token do. That is why this sets
    // identity the classic path deliberately leaves unset: there, Dataverse reads identity out of the
    // uploaded content field, and here there is no such field to read.
    //
    // PublicKeyToken is nullable on the metadata (an unsigned assembly reflects to null) and is passed
    // straight through either way — this is the field set measured against a live environment, and
    // second-guessing it per-caller would give two behaviours where there is one create.
    //
    // R6: no --force specifier gates this on either caller — creating a record is additive, and both
    // callers are acting on an assembly the package content already carries.
    internal static async Task CreateAsync(
        IOrganizationServiceAsync2 service,
        Guid packageId,
        PluginAssemblyMetadata metadata,
        string solutionName,
        CancellationToken cancellationToken)
    {
        var entity = new Entity("pluginassembly")
        {
            ["name"]           = metadata.Name,
            ["packageid"]      = new EntityReference("pluginpackage", packageId),
            ["isolationmode"]  = new OptionSetValue(2), // 2 = Sandbox (cloud only)
            ["version"]        = metadata.Version,
            ["culture"]        = metadata.Culture,
            ["publickeytoken"] = metadata.PublicKeyToken
        };

        try
        {
            await service.ExecuteAsync(
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Carries only why Dataverse refused. Each caller composes its own user-facing message
            // around this — push aggregates every failed assembly into one line (R8), deploy names the
            // one it couldn't create and imports anyway.
            throw new FlowlineException(ExitCode.ValidationFailed, ex.Message, ex);
        }
    }
}
