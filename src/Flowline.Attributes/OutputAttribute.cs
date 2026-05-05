using System;

namespace Flowline.Attributes
{

/// <summary>
/// Declares a return value of a Dataverse Custom API for Flowline registration.
/// </summary>
/// <remarks>
/// <para>
/// When your Custom API finishes executing, it can send named values back to the caller. Each
/// <c>[Output]</c> declaration on the class registers one of those return values in Dataverse.
/// </para>
/// <para>
/// Inside <c>Execute</c>, write the value to <c>context.OutputParameters</c> using the unique
/// name you declared:
/// </para>
/// <code>
/// [CustomApi]
/// [Input("accountId", FieldType.EntityReference, Entity = "account")]
/// [Output("riskScore", FieldType.Integer)]
/// [Output("riskLabel", FieldType.String)]
/// public class GetAccountRiskApi : IPlugin
/// {
///     public void Execute(IServiceProvider serviceProvider)
///     {
///         var ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
///         var accountId = (EntityReference)ctx.InputParameters["accountId"];
///         ctx.OutputParameters["riskScore"] = ComputeScore(accountId);
///         ctx.OutputParameters["riskLabel"] = "High";
///     }
/// }
/// </code>
/// <para>
/// <b>EntityReference outputs:</b> always set <see cref="Entity"/> to the logical name of the
/// referenced table. Dataverse requires this to validate the reference.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class OutputAttribute : Attribute
{
    /// <summary>
    /// Specifies the output metadata for a class. This attribute can be applied to a class
    /// to define the name and type of an output.
    /// </summary>
    /// <remarks>
    /// This attribute allows multiple usages on a single class.
    /// </remarks>
    /// <param name="name">The name of the output.</param>
    /// <param name="type">The type of the output.</param>
    public OutputAttribute(string name, FieldType type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>
    /// The name used to identify this return value in Dataverse and in
    /// <c>context.OutputParameters</c>. Must be unique within the Custom API.
    /// Convention is camelCase: <c>"riskScore"</c>, <c>"errorMessage"</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The Dataverse field type of this return value.
    /// See <see cref="FieldType"/> for the full C# ↔ Dataverse type mapping.
    /// </summary>
    public FieldType Type { get; }

    /// <summary>
    /// Logical name of the Dataverse table this return value references.
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
