using System;

namespace Flowline.Attributes
{

/// <summary>
/// Declares an input parameter of a Dataverse Custom API for Flowline registration.
/// </summary>
/// <remarks>
/// <para>
/// When someone calls your Custom API — from Power Automate, a canvas app, a JavaScript web
/// resource, or another plugin — they pass named values as arguments. Each <c>[Input]</c>
/// declaration on the class registers one of those arguments in Dataverse.
/// </para>
/// <para>
/// Inside <c>Execute</c>, read the value from <c>context.InputParameters</c> using the unique
/// name you declared:
/// </para>
/// <code>
/// [CustomApi]
/// [Input("accountId", FieldType.EntityReference, Entity = "account")]
/// [Input("includeHistory", FieldType.Boolean, IsOptional = true)]
/// [Output("riskScore", FieldType.Integer)]
/// public class GetAccountRiskApi : IPlugin
/// {
///     public void Execute(IServiceProvider serviceProvider)
///     {
///         var ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
///         var accountId     = (EntityReference)ctx.InputParameters["accountId"];
///         var withHistory   = ctx.InputParameters.Contains("includeHistory")
///                             &amp;&amp; (bool)ctx.InputParameters["includeHistory"];
///         ctx.OutputParameters["riskScore"] = ComputeScore(accountId, withHistory);
///     }
/// }
/// </code>
/// <para>
/// <b>Optional parameters:</b> the caller may omit them. Always check
/// <c>context.InputParameters.Contains("name")</c> before reading an optional value.
/// </para>
/// <para>
/// <b>EntityReference parameters:</b> always set <see cref="Entity"/> to the logical name of
/// the referenced table. Dataverse requires this to validate the reference.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class InputAttribute : Attribute
{
    /// <summary>Represents metadata that can be applied to a class to specify its expected input parameters.</summary>
    /// <remarks>This attribute allows multiple usages on a single class.</remarks>
    /// <param name="name">The name of the parameter.</param>
    /// <param name="type">The type of the parameter.</param>
    public InputAttribute(string name, FieldType type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>
    /// The name used to identify this parameter in Dataverse and in <c>context.InputParameters</c>.
    /// Must be unique within the Custom API. Convention is camelCase: <c>"accountId"</c>,
    /// <c>"includeHistory"</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The Dataverse field type of this parameter.
    /// See <see cref="FieldType"/> for the full C# ↔ Dataverse type mapping.
    /// </summary>
    public FieldType Type { get; }

    /// <summary>
    /// Whether the caller may omit this parameter. Default is <c>false</c> (required).
    /// When <c>true</c>, always check <c>context.InputParameters.Contains("name")</c>
    /// before reading the value.
    /// </summary>
    public bool IsOptional { get; set; }

    /// <summary>
    /// Logical name of the Dataverse table this parameter references.
    /// Required when <see cref="Type"/> is <see cref="FieldType.EntityReference"/> or
    /// <see cref="FieldType.Entity"/>. Example: <c>"account"</c>, <c>"cr123_invoice"</c>.
    /// </summary>
    public string Entity { get; set; }

    /// <summary>
    /// Display name shown in the solution explorer and API catalog.
    /// Defaults to <see cref="Name"/> split on camelCase boundaries if omitted.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>Description shown in the solution explorer.</summary>
    public string Description { get; set; }
}
}
