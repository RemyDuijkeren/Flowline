using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class PluginReader
{
    public async Task<RegistrationSnapshot> LoadSnapshotAsync(
        IOrganizationServiceAsync2 service,
        Guid assemblyId,
        PluginAssemblyMetadata metadata,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        // Round 1: all independent queries in parallel
        var prefixTask      = GetPublisherPrefixAsync(service, solutionName, cancellationToken);
        var pluginTypesTask = GetRegisteredPluginTypesAsync(service, assemblyId, cancellationToken);
        var stepsTask       = GetRegisteredStepsAsync(service, assemblyId, cancellationToken);
        var imagesTask      = GetRegisteredImagesAsync(service, assemblyId, cancellationToken);
        var messageIdsTask  = LookupAllSdkMessageIdsAsync(service, metadata, cancellationToken);

        var prefix     = await prefixTask.ConfigureAwait(false);
        var messageIds = await messageIdsTask.ConfigureAwait(false);

        // Round 2: queries dependent on round 1 results
        var customApisTask    = GetRegisteredCustomApisAsync(service, prefix, cancellationToken);
        var requestParamsTask = GetRegisteredRequestParametersAsync(service, prefix, cancellationToken);
        var responsePropsTask = GetRegisteredResponsePropertiesAsync(service, prefix, cancellationToken);
        var filterIdsTask     = LookupAllFilterIdsAsync(service, metadata, messageIds, cancellationToken);
        var systemUserIdsTask = LookupAllSystemUserIdsAsync(service, metadata, cancellationToken);

        await Task.WhenAll(pluginTypesTask, stepsTask, imagesTask,
            customApisTask, requestParamsTask, responsePropsTask, filterIdsTask, systemUserIdsTask).ConfigureAwait(false);

        // Round 3: one bulk query for solution membership + component types, using all known IDs
        var allComponentIds = new[] { assemblyId }
            .Concat(pluginTypesTask.Result.Values.Select(e => e.Id))
            .Concat(stepsTask.Result.Select(e => e.Id))
            .Concat(imagesTask.Result.Select(e => e.Id))
            .Concat(customApisTask.Result.Select(e => e.Id))
            .Concat(requestParamsTask.Result.Select(e => e.Id))
            .Concat(responsePropsTask.Result.Select(e => e.Id));

        var (membership, componentTypes) = await GetComponentSolutionMembershipAsync(service, allComponentIds, cancellationToken).ConfigureAwait(false);

        return new RegistrationSnapshot(
            pluginTypesTask.Result,
            stepsTask.Result,
            imagesTask.Result,
            customApisTask.Result,
            requestParamsTask.Result,
            responsePropsTask.Result,
            messageIds,
            filterIdsTask.Result,
            systemUserIdsTask.Result,
            prefix,
            membership,
            componentTypes);
    }

    async Task<(IReadOnlyDictionary<Guid, IReadOnlyList<string>> Membership, IReadOnlyDictionary<Guid, int> ComponentTypes)>
        GetComponentSolutionMembershipAsync(
            IOrganizationServiceAsync2 service,
            IEnumerable<Guid> componentIds,
            CancellationToken cancellationToken)
    {
        var ids = componentIds.Distinct().Where(id => id != Guid.Empty).ToList();
        if (ids.Count == 0)
            return (new Dictionary<Guid, IReadOnlyList<string>>().AsReadOnly(), new Dictionary<Guid, int>().AsReadOnly());

        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid", "componenttype"),
            Criteria =
            {
                Conditions = { new ConditionExpression("objectid", ConditionOperator.In, ids.Select(id => (object)id).ToArray()) }
            },
            LinkEntities =
            {
                new LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", JoinOperator.Inner)
                {
                    Columns = new ColumnSet("uniquename"),
                    EntityAlias = "sol"
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);

        var membership    = new Dictionary<Guid, List<string>>();
        var componentTypes = new Dictionary<Guid, int>();

        foreach (var entity in result.Entities)
        {
            var objectId = entity.GetAttributeValue<Guid>("objectid");
            if (objectId == Guid.Empty) continue;

            var solutionName = entity.GetAttributeValue<AliasedValue>("sol.uniquename")?.Value as string;
            if (!string.IsNullOrEmpty(solutionName))
            {
                if (!membership.TryGetValue(objectId, out var sols))
                    membership[objectId] = sols = [];
                sols.Add(solutionName);
            }

            if (!componentTypes.ContainsKey(objectId))
            {
                var ct = entity.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
                if (ct.HasValue)
                    componentTypes[objectId] = ct.Value;
            }
        }

        return (
            membership.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value.AsReadOnly()).AsReadOnly(),
            componentTypes.AsReadOnly()
        );
    }

    async Task<IReadOnlyDictionary<string, Guid>> LookupAllSdkMessageIdsAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        CancellationToken cancellationToken)
    {
        var messageNames = metadata.Plugins
            .Where(p => !p.IsWorkflow && !p.IsCustomApi)
            .SelectMany(p => p.Steps)
            .Select(s => s.Message)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (messageNames.Count == 0)
            return new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

        var tasks = messageNames.Select(async name =>
        {
            var id = await LookupSdkMessageIdAsync(service, name, cancellationToken).ConfigureAwait(false);
            return (name, id);
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .Where(r => r.id.HasValue)
            .ToDictionary(r => r.name, r => r.id!.Value, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();
    }

    async Task<IReadOnlyDictionary<(Guid, string?, string?), Guid?>> LookupAllFilterIdsAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        IReadOnlyDictionary<string, Guid> messageIds,
        CancellationToken cancellationToken)
    {
        var stepKeys = metadata.Plugins
            .Where(p => !p.IsWorkflow && !p.IsCustomApi)
            .SelectMany(p => p.Steps)
            .Select(s => (s.Message, s.EntityName, s.SecondaryEntity))
            .Distinct()
            .ToList();

        if (stepKeys.Count == 0)
            return new Dictionary<(Guid, string?, string?), Guid?>().AsReadOnly();

        var tasks = stepKeys.Select(async key =>
        {
            if (!messageIds.TryGetValue(key.Message, out var messageId))
                return ((Guid.Empty, key.EntityName, key.SecondaryEntity), (Guid?)null);
            // null EntityName means "any entity" — no message filter exists for it
            if (key.EntityName == null || string.Equals(key.EntityName, "none", StringComparison.OrdinalIgnoreCase))
                return ((messageId, key.EntityName, key.SecondaryEntity), (Guid?)null);
            var filterId = await LookupSdkMessageFilterIdAsync(service, messageId, key.EntityName, key.SecondaryEntity, cancellationToken).ConfigureAwait(false);
            return ((messageId, key.EntityName, key.SecondaryEntity), filterId);
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(r => r.Item1, r => r.Item2).AsReadOnly();
    }

    async Task<IReadOnlySet<Guid>> LookupAllSystemUserIdsAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        CancellationToken cancellationToken)
    {
        var runAsIds = metadata.Plugins
            .Where(p => !p.IsWorkflow && !p.IsCustomApi)
            .SelectMany(p => p.Steps)
            .Select(s => s.RunAs)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        if (runAsIds.Count == 0)
            return new HashSet<Guid>();

        var tasks = runAsIds.Select(async id =>
            await SystemUserExistsAsync(service, id, cancellationToken).ConfigureAwait(false)
                ? (Guid?)id
                : null);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OfType<Guid>().ToHashSet();
    }

    async Task<IReadOnlyDictionary<string, Entity>> GetRegisteredPluginTypesAsync(
        IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "name", "isworkflowactivity"),
            Criteria =
            {
                Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities
            .ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredStepsAsync(
        IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        // category = 'CustomAPI' and stage = 30 are Custom API internal steps — exclude them
        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "name", "description", "plugintypeid", "stage", "mode", "rank",
                "filteringattributes", "configuration", "asyncautodelete", "impersonatinguserid", "statecode", "category", "sdkmessageid", "solutionid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("category", ConditionOperator.NotEqual, "CustomAPI"),
                    new ConditionExpression("stage", ConditionOperator.NotEqual, 30)
                }
            },
            LinkEntities =
            {
                new LinkEntity("sdkmessageprocessingstep", "plugintype", "plugintypeid", "plugintypeid", JoinOperator.Inner)
                {
                    LinkCriteria =
                    {
                        Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId) }
                    }
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredImagesAsync(
        IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepimageid", "sdkmessageprocessingstepid", "name", "entityalias", "imagetype", "attributes"),
        };

        var stepLink = query.AddLink("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "sdkmessageprocessingstepid");
        var typeLink = stepLink.AddLink("plugintype", "plugintypeid", "plugintypeid");
        typeLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredCustomApisAsync(
        IOrganizationServiceAsync2 service, string prefix, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet(
                "uniquename", "name", "displayname", "description", "bindingtype", "boundentitylogicalname", "isfunction",
                "isprivate", "allowedcustomprocessingsteptype", "executeprivilegename", "plugintypeid"),
            Criteria =
            {
                Conditions = { new ConditionExpression("uniquename", ConditionOperator.BeginsWith, $"{prefix}_") }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredRequestParametersAsync(
        IOrganizationServiceAsync2 service, string prefix, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapirequestparameter")
        {
            ColumnSet = new ColumnSet("customapirequestparameterid", "customapiid", "uniquename", "name", "displayname", "description", "type", "isoptional", "logicalentityname")
        };

        var apiLink = query.AddLink("customapi", "customapiid", "customapiid");
        apiLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.BeginsWith, $"{prefix}_");

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredResponsePropertiesAsync(
        IOrganizationServiceAsync2 service, string prefix, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapiresponseproperty")
        {
            ColumnSet = new ColumnSet("customapiresponsepropertyid", "customapiid", "uniquename", "name", "displayname", "description", "type", "logicalentityname")
        };

        var apiLink = query.AddLink("customapi", "customapiid", "customapiid");
        apiLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.BeginsWith, $"{prefix}_");

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<string> GetPublisherPrefixAsync(
        IOrganizationServiceAsync2 service, string solutionName, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("solution")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("publisherid"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, solutionName) } },
            LinkEntities =
            {
                new LinkEntity("solution", "publisher", "publisherid", "publisherid", JoinOperator.Inner)
                {
                    Columns = new ColumnSet("customizationprefix"),
                    EntityAlias = "pub"
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var solution = result.Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{solutionName}' not found in Dataverse.");

        return solution.GetAttributeValue<AliasedValue>("pub.customizationprefix")?.Value as string
            ?? throw new InvalidOperationException($"Could not read publisher prefix for solution '{solutionName}'.");
    }

    async Task<Guid?> LookupSdkMessageIdAsync(
        IOrganizationServiceAsync2 service, string messageName, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessage")
        {
            ColumnSet = new ColumnSet("sdkmessageid"),
            Criteria =
            {
                Conditions = { new ConditionExpression("name", ConditionOperator.Equal, messageName) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.FirstOrDefault()?.Id;
    }

    async Task<Guid?> LookupSdkMessageFilterIdAsync(
        IOrganizationServiceAsync2 service, Guid messageId, string entityName, string? secondaryEntity = null, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessagefilter")
        {
            ColumnSet = new ColumnSet("sdkmessagefilterid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("sdkmessageid", ConditionOperator.Equal, messageId),
                    new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, entityName)
                }
            }
        };

        if (secondaryEntity != null)
            query.Criteria.AddCondition("secondaryobjecttypecode", ConditionOperator.Equal, secondaryEntity);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.FirstOrDefault()?.Id;
    }

    async Task<bool> SystemUserExistsAsync(
        IOrganizationServiceAsync2 service,
        Guid systemUserId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("systemuser")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet(false),
            Criteria =
            {
                Conditions = { new ConditionExpression("systemuserid", ConditionOperator.Equal, systemUserId) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.Count > 0;
    }
}
