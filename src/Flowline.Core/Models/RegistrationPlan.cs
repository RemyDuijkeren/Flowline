namespace Flowline.Core.Models;

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

}

public class ActionPlan
{
    // string holds the name of the entity that is being upserted or deleted or added to solution
    public Dictionary<string, Func<Task>> Upserts { get; } = new();
    public Dictionary<string, Func<Task>> Deletes { get; } = new();
    public Dictionary<string, Func<Task>> AddSolutionComponents { get; } = new();

    // Add all actions from another plan to this one
    public void Add(ActionPlan other)
    {
        foreach (var (key, value) in other.Upserts)
            Upserts[key] = value;
        foreach (var (key, value) in other.Deletes)
            Deletes[key] = value;
        foreach (var (key, value) in other.AddSolutionComponents)
            AddSolutionComponents[key] = value;
    }
}
