using System;

namespace Flowline.Attributes;

/// <summary>
/// Specifies the secondary entity for plugin steps that involve two entities,
/// such as <c>Associate</c> and <c>Disassociate</c> messages.
/// Omit for all other messages where the secondary entity is "none".
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SecondaryEntityAttribute(string logicalName) : Attribute
{
    public string LogicalName { get; } = logicalName;
}
