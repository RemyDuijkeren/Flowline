using System;

namespace Flowline.Attributes;

/// <summary>
/// Specifies the secondary table for plugin steps that involve two records, such as
/// <c>Associate</c> and <c>Disassociate</c> messages.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Associate</c> and <c>Disassociate</c> messages fire when a many-to-many relationship
/// between two records is created or removed — for example, when a contact is associated with
/// an account, or when a team member is removed from a team.
/// </para>
/// <para>
/// These messages always involve two tables. <c>[Entity]</c> specifies the primary table and
/// <c>[SecondaryEntity]</c> specifies the related table. Use <c>"none"</c> on
/// <c>[SecondaryEntity]</c> to match the message regardless of which table is on the other side.
/// </para>
/// <code>
/// // Fires when ANY record is associated with a contact
/// [Entity("contact")]
/// [SecondaryEntity("none")]
/// public class ContactAssociatePlugin : IPlugin { ... }
///
/// // Fires only when a contact is associated with an account specifically
/// [Entity("contact")]
/// [SecondaryEntity("account")]
/// public class ContactAccountAssociatePlugin : IPlugin { ... }
/// </code>
/// <para>
/// Inside <c>Execute</c>, read the relationship details from the plugin context:
/// </para>
/// <code>
/// var ctx          = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
/// var target       = (EntityReference)ctx.InputParameters["Target"];       // primary record
/// var relatedEntities = (EntityReferenceCollection)ctx.InputParameters["RelatedEntities"];
/// var relationship = (Relationship)ctx.InputParameters["Relationship"];
/// </code>
/// <para>
/// For all other messages (Create, Update, Delete, ...) where only one table is involved,
/// omit <c>[SecondaryEntity]</c> entirely. Dataverse uses <c>"none"</c> automatically.
/// </para>
/// </remarks>
/// <param name="logicalName">
/// Logical name of the secondary table, e.g. <c>"account"</c>.
/// Use <c>"none"</c> to match all secondary tables.
/// </param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SecondaryEntityAttribute(string logicalName) : Attribute
{
    /// <summary>
    /// Logical name of the secondary Dataverse table involved in the relationship operation.
    /// Use <c>"none"</c> to match any secondary table.
    /// </summary>
    public string LogicalName { get; } = logicalName;
}
