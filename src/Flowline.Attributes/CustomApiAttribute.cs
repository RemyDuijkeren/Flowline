using System;

namespace Flowline.Attributes
{

/// <summary>
/// Marks an <c>IPlugin</c> class as a Dataverse Custom API.
/// </summary>
/// <remarks>
/// <para>
/// A <b>Custom API</b> is a custom endpoint you define in Dataverse. Callers invoke it explicitly
/// by name — from Power Automate, a JavaScript web resource, an external app via the Web API, or
/// another plugin. This is different from a regular plugin step, which fires automatically when a
/// record is created, updated, or deleted.
/// </para>
/// <para>
/// Flowline uses this attribute to register the Custom API record in Dataverse when you run
/// <c>flowline push</c>. The class name (minus the <c>Api</c>, <c>CustomApi</c>, or <c>Plugin</c>
/// suffix) becomes the unique name, prefixed with the solution's publisher prefix.
/// </para>
/// <para>
/// <b>Action vs. Function:</b> an Action performs an operation and is called via HTTP POST. A
/// Function reads data without side effects and is called via HTTP GET. Use <see cref="IsFunction"/>
/// only if your API truly has no side effects and must support GET semantics. Default is Action.
/// </para>
/// <para>
/// <b>Global API (no entity binding):</b> the API applies to no specific record type. Use this
/// for operations like sending a notification, triggering a batch job, or computing a value.
/// </para>
/// <code>
/// [CustomApi]
/// public partial class SendNotificationApi : IPlugin { ... }
/// </code>
/// <para>
/// <b>Entity-bound API:</b> the API operates on a specific record. Pass the table logical name.
/// Dataverse automatically adds a <c>Target</c> <see cref="Microsoft.Xrm.Sdk.EntityReference"/>
/// parameter — the caller must provide the record ID when invoking the API.
/// </para>
/// <code>
/// [CustomApi("salesorder")]
/// public partial class ApproveOrderApi : IPlugin
/// {
///     public void FlowlineExecute(IServiceProvider sp)
///     {
///         var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
///         var target = (EntityReference)ctx.InputParameters["Target"]; // auto-provided by Dataverse
///     }
/// }
/// </code>
/// <para>
/// <b>Entity collection-bound API:</b> like entity binding but for a collection of records.
/// Use <see cref="EntityCollection"/> instead of the constructor parameter.
/// </para>
/// <code>
/// [CustomApi(EntityCollection = "invoice")]
/// public partial class BulkApproveApi : IPlugin { ... }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CustomApiAttribute : Attribute
{
    /// <summary>Marks a class as a Dataverse Custom API.</summary>
    public CustomApiAttribute()
        : this(null)
    {
    }

    /// <summary>Marks a class as a Dataverse Custom API.</summary>
    /// <param name="entity">
    /// Logical name of the table to bind this API to. Omit for a global (unbound) API.
    /// </param>
    public CustomApiAttribute(string entity)
    {
        Entity = entity;
    }

    /// <summary>
    /// Logical name of the table this API is bound to, e.g. <c>"account"</c> or <c>"cr123_invoice"</c>.
    /// When set, Dataverse automatically provides a <c>Target</c> EntityReference parameter containing
    /// the record the API was invoked on. Omit for a global (unbound) API.
    /// </summary>
    public string Entity { get; }

    /// <summary>
    /// Logical name of the table for entity collection binding.
    /// Use instead of <see cref="Entity"/> when the API operates on a set of records rather than
    /// a single record. Dataverse provides a <c>Target</c> EntityCollection parameter.
    /// </summary>
    public string EntityCollection { get; set; }

    /// <summary>
    /// When <c>true</c>, this API is a Function: it must return a value and has no side effects.
    /// Functions are called via HTTP GET. Default is <c>false</c> (Action, called via HTTP POST).
    /// Only set this if your API is genuinely read-only and idempotent.
    /// </summary>
    public bool IsFunction { get; set; }

    /// <summary>
    /// When <c>true</c>, this API is hidden from the public API catalog and cannot be discovered
    /// through OData metadata. Use for internal APIs not meant for external callers.
    /// Default is <c>false</c>.
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Controls whether third-party developers can register their own plugin steps on this API
    /// to extend its behaviour. Default is <see cref="AllowedStepType.None"/> (no extensions allowed).
    /// </summary>
    public AllowedStepType AllowedStepType { get; set; }

    /// <summary>
    /// Display name shown in the solution explorer and API catalog.
    /// Defaults to the class name (without suffix) split on PascalCase boundaries if omitted.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>Description shown in the solution explorer and API catalog.</summary>
    public string Description { get; set; }

    /// <summary>
    /// Name of the Dataverse privilege required to call this API.
    /// When set, only users with this privilege can invoke the API.
    /// Omit allowing any authenticated user.
    /// </summary>
    public string ExecutePrivilege { get; set; }
}
}
