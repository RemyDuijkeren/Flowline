using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Models;

public record UpsertAction(string Name, Entity Entity, bool IsCreate, string? SolutionName = null);
public record DeleteAction(string Name, string EntityLogicalName, Guid Id);
public record AddToSolutionAction(string Name, string EntityLogicalName, Guid Id, string SolutionName);

// Delete order: Images | ResponseProps | RequestParams, CustomApis | Steps, PluginTypes
// Upsert order: PluginTypes, Steps | CustomApis, Images | ResponseProps | RequestParams, AddSolutionComponents
public class RegistrationPlan
{
    public ActionPlan PluginTypes   { get; } = new();
    public ActionPlan Steps         { get; } = new();
    public ActionPlan Images        { get; } = new();
    public ActionPlan CustomApis    { get; } = new();
    public ActionPlan RequestParams { get; } = new();
    public ActionPlan ResponseProps { get; } = new();

    public int TotalDeletes => PluginTypes.Deletes.Count + Steps.Deletes.Count + Images.Deletes.Count
                             + CustomApis.Deletes.Count + RequestParams.Deletes.Count + ResponseProps.Deletes.Count;

    public int TotalUpserts => PluginTypes.Upserts.Count + Steps.Upserts.Count + Images.Upserts.Count
                             + CustomApis.Upserts.Count + RequestParams.Upserts.Count + ResponseProps.Upserts.Count;
}

public class ActionPlan
{
    public Dictionary<string, UpsertAction> Upserts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DeleteAction> Deletes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AddToSolutionAction> AddSolutionComponents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(ActionPlan other)
    {
        foreach (var (key, value) in other.Upserts) Upserts[key] = value;
        foreach (var (key, value) in other.Deletes) Deletes[key] = value;
        foreach (var (key, value) in other.AddSolutionComponents) AddSolutionComponents[key] = value;
    }
}
