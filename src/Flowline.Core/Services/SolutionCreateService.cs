using System.Security.Cryptography;
using System.ServiceModel;
using System.Text;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Services;

/// <summary>
/// Creates the empty unmanaged solution for <c>flowline init</c>'s (and <c>clone</c>'s create-new
/// path's) Dataverse-side step: resolves or creates the publisher for a prefix (R5), refuses a
/// solution unique-name collision (R15), and creates the solution (R4). Takes an already-connected
/// <see cref="IOrganizationServiceAsync2"/> -- callers obtain it from
/// <see cref="DataverseConnector.ConnectViaPacAsync"/> -- this class never connects itself, so tests
/// can mock the org service directly (U3).
/// </summary>
public class SolutionCreateService
{
    /// <summary>
    /// Resolves/creates the publisher, refuses an existing solution unique name, then creates the
    /// empty unmanaged solution. Names must already be valid (<see cref="SolutionNameValidator"/>) --
    /// re-validated here so a caller that skips validation still fails clearly instead of hitting a
    /// raw Dataverse fault.
    /// </summary>
    public async Task<SolutionCreateResult> CreateAsync(
        IOrganizationServiceAsync2 service,
        string solutionUniqueName,
        string solutionDisplayName,
        string publisherPrefix,
        string? publisherFriendlyName = null,
        CancellationToken cancellationToken = default)
    {
        SolutionNameValidator.EnsureSolutionUniqueName(solutionUniqueName);
        SolutionNameValidator.EnsureSolutionDisplayName(solutionDisplayName);
        SolutionNameValidator.EnsurePublisherPrefix(publisherPrefix);

        var (publisherId, publisherCreated) = await ResolvePublisherAsync(
            service, publisherPrefix, publisherFriendlyName, cancellationToken).ConfigureAwait(false);

        await EnsureSolutionNameAvailableAsync(service, solutionUniqueName, cancellationToken).ConfigureAwait(false);

        var solutionId = await CreateSolutionAsync(
            service, solutionUniqueName, solutionDisplayName, publisherId, cancellationToken).ConfigureAwait(false);

        return new SolutionCreateResult(publisherId, publisherPrefix, publisherCreated, solutionId);
    }

    /// <summary>
    /// Lists existing publishers (prefix + friendly name) for the interactive publisher picker
    /// (R5/AE4) — the flag path never calls this, so a normal <c>--publisher-prefix</c> run does
    /// one fewer round trip.
    /// </summary>
    public async Task<List<PublisherSummary>> ListPublishersAsync(
        IOrganizationServiceAsync2 service, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("publisher")
        {
            ColumnSet = new ColumnSet("customizationprefix", "friendlyname")
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Entities
            .Select(e => new PublisherSummary(
                e.GetAttributeValue<string>("customizationprefix"),
                e.GetAttributeValue<string>("friendlyname")))
            .Where(p => !string.IsNullOrWhiteSpace(p.Prefix))
            .OrderBy(p => p.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reuses an existing publisher matching <paramref name="prefix"/>'s customizationprefix, or creates one (R5/KTD3).</summary>
    async Task<(Guid PublisherId, bool Created)> ResolvePublisherAsync(
        IOrganizationServiceAsync2 service,
        string prefix,
        string? friendlyName,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("publisher")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("publisherid"),
            Criteria = { Conditions = { new ConditionExpression("customizationprefix", ConditionOperator.Equal, prefix) } }
        };

        var existing = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existingPublisher = existing.Entities.FirstOrDefault();
        if (existingPublisher != null)
            return (existingPublisher.Id, false);

        var entity = new Entity("publisher")
        {
            ["uniquename"] = prefix,
            ["friendlyname"] = friendlyName ?? prefix,
            ["customizationprefix"] = prefix,
            ["customizationoptionvalueprefix"] = DeriveOptionValuePrefix(prefix)
        };

        Guid publisherId;
        try
        {
            publisherId = await service.CreateAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            throw WrapPrivilegeFault(ex, "Publisher");
        }

        return (publisherId, true);
    }

    /// <summary>Refuses a solution unique-name collision before anything is created (R15, extending R14's check-before-writing pattern).</summary>
    async Task EnsureSolutionNameAvailableAsync(
        IOrganizationServiceAsync2 service,
        string uniqueName,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("solution")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("solutionid"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, uniqueName) } }
        };

        var existing = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        if (existing.Entities.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Solution '{uniqueName}' already exists in this environment. Choose a different name.");
    }

    async Task<Guid> CreateSolutionAsync(
        IOrganizationServiceAsync2 service,
        string uniqueName,
        string displayName,
        Guid publisherId,
        CancellationToken cancellationToken)
    {
        var entity = new Entity("solution")
        {
            ["uniquename"] = uniqueName,
            ["friendlyname"] = displayName,
            ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId)
        };

        try
        {
            return await service.CreateAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            throw WrapPrivilegeFault(ex, "Solution");
        }
    }

    // R18: a raw FaultException<OrganizationServiceFault> from Create (typically a missing prvCreate*
    // privilege) must not escape -- named to the record type Flowline was trying to create so the user
    // knows what to ask an admin for, with the SDK's own detail message kept for diagnosis.
    static FlowlineException WrapPrivilegeFault(FaultException<OrganizationServiceFault> ex, string recordType) =>
        new(ExitCode.ValidationFailed,
            $"Missing permission to create {recordType} records in this environment. " +
            $"Ask a Dataverse admin to grant the Create privilege for {recordType}, then retry. " +
            $"Detail: {ex.Message}", ex);

    // KTD3(c): customizationoptionvalueprefix is SystemRequired, 10000-99999, and must be derived
    // deterministically from the prefix (same prefix -> same value every time, no RNG) so repeated
    // runs against the same prefix don't drift. SHA-256 (not string.GetHashCode -- randomized per
    // process since .NET Core) over the prefix's UTF-8 bytes gives a stable, well-distributed source.
    internal static int DeriveOptionValuePrefix(string prefix)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prefix));
        var value = BitConverter.ToUInt32(hash, 0);
        return (int)(value % 90000) + 10000;
    }
}

/// <summary>Publisher and solution identifiers a caller can log after <see cref="SolutionCreateService.CreateAsync"/> succeeds.</summary>
public record SolutionCreateResult(Guid PublisherId, string PublisherPrefix, bool PublisherCreated, Guid SolutionId);

/// <summary>An existing publisher's prefix and friendly name, for the interactive picker (R5/AE4).</summary>
public record PublisherSummary(string Prefix, string? FriendlyName);
