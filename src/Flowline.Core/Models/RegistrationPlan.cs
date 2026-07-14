using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Models;

public record UpsertAction(string Name, Entity Entity, bool IsCreate, string? SolutionName = null);
public record DeleteAction(string Name, string EntityLogicalName, Guid Id);
public record AddToSolutionAction(string Name, string EntityLogicalName, Guid Id, string SolutionName, int ComponentType);

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

    // Grouped per API for tree rendering only; execution uses the flat plans above.
    public List<CustomApiGroup> CustomApiGroups { get; } = new();

    public List<string> Warnings { get; } = new();

    public int TotalDeletes => PluginTypes.Deletes.Count + Steps.Deletes.Count + Images.Deletes.Count
                             + CustomApis.Deletes.Count + RequestParams.Deletes.Count + ResponseProps.Deletes.Count;

    public int TotalUpserts => PluginTypes.Upserts.Count + Steps.Upserts.Count + Images.Upserts.Count
                             + CustomApis.Upserts.Count + RequestParams.Upserts.Count + ResponseProps.Upserts.Count;
    public int TotalChanges => TotalDeletes + TotalUpserts;

    // KD2/KD4/KTD13: Flowline must never call DeleteAsync("plugintype", ...) — Dataverse's package sync
    // removes an emptied plugin type automatically. Callers on the package path that need "everything
    // else" this plan wants deleted, without touching plugin types, use this instead of hand-copying
    // each category (a 7th category added to RegistrationPlan would otherwise be silently dropped here
    // by omission — this method must be updated alongside any new category).
    public RegistrationPlan NonPluginTypeDeletes()
    {
        var subset = new RegistrationPlan();
        subset.Steps.Deletes.AddRange(Steps.Deletes);
        subset.CustomApis.Deletes.AddRange(CustomApis.Deletes);
        subset.Images.Deletes.AddRange(Images.Deletes);
        subset.RequestParams.Deletes.AddRange(RequestParams.Deletes);
        subset.ResponseProps.Deletes.AddRange(ResponseProps.Deletes);
        return subset;
    }
}

public record CustomApiGroup(string ApiName, ActionPlan Api, ActionPlan RequestParams, ActionPlan ResponseProps, string? PluginTypeName = null);

public class ActionPlan
{
    public List<UpsertAction> Upserts { get; } = [];
    public List<DeleteAction> Deletes { get; } = [];
    public List<AddToSolutionAction> AddSolutionComponents { get; } = [];

    public void Add(ActionPlan other)
    {
        Upserts.AddRange(other.Upserts);
        Deletes.AddRange(other.Deletes);
        AddSolutionComponents.AddRange(other.AddSolutionComponents);
    }
}
