using System;

namespace Flowline.Attributes;

/// <summary>
/// Specifies which Dataverse attributes trigger this plugin step.
/// The step only fires when at least one listed attribute is included in the operation.
/// Omit to fire on all attribute changes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class FilterAttribute(params string[] attributes) : Attribute
{
    public string[] Attributes { get; } = attributes;
}
