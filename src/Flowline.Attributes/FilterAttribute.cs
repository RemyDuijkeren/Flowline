using System;

namespace Flowline.Attributes;

/// <summary>
/// Limits this plugin step to fire only when at least one of the listed columns is included
/// in the operation.
/// </summary>
/// <remarks>
/// <para>
/// Without <c>[Filter]</c>, the step fires on <b>every</b> update to the table — even when
/// the columns your plugin cares about haven't changed. This wastes CPU and can cause
/// unexpected side effects. Always add a filter on Update steps unless you genuinely need
/// to react to any column change.
/// </para>
/// <para>
/// Dataverse evaluates the filter <b>before</b> invoking your plugin. If none of the listed
/// columns appear in the update payload, your plugin is never called — no instantiation, no
/// round-trip.
/// </para>
/// <para>
/// Pass the logical names of the columns to watch:
/// </para>
/// <code>
/// [Step("account")]
/// [Filter("name", "creditlimit")]
/// public class AccountPreUpdatePlugin : IPlugin { ... }
/// </code>
/// <para>
/// Use <c>nameof</c> with early-bound entity classes for compile-time safety:
/// </para>
/// <code>
/// [Step("account")]
/// [Filter(nameof(Account.name), nameof(Account.creditlimit))]
/// public class AccountPreUpdatePlugin : IPlugin { ... }
/// </code>
/// <para>
/// <c>[Filter]</c> is most useful on <c>Update</c> steps. It has no effect on <c>Create</c>
/// or <c>Delete</c> steps because those messages have no column list to filter against.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FilterAttribute(params string[] columns) : Attribute
{
    /// <summary>
    /// Logical names of the columns that trigger this step.
    /// The step fires when the operation includes at least one of these columns.
    /// </summary>
    public string[] Columns { get; } = columns;
}
