using System;

namespace Flowline.Attributes;

/// <summary>
/// Declares a Custom API response property for Flowline registration.
/// Can be placed on the class (explicit) or on a property (deduced).
/// </summary>
/// <remarks>
/// <b>Class-level</b>: specify <paramref name="uniqueName"/> and <paramref name="type"/> explicitly.
/// <b>Property-level</b>: uniqueName and type are deduced from the property name and C# type.
/// Use <see cref="EntityName"/> for EntityReference/Entity types.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true)]
public sealed class ResponsePropertyAttribute : Attribute
{
    /// <summary>Class-level: explicit unique name and type.</summary>
    public ResponsePropertyAttribute(string uniqueName, CustomApiFieldType type)
    {
        UniqueName = uniqueName;
        FieldType = type;
    }

    /// <summary>Property-level: unique name and type are deduced.</summary>
    public ResponsePropertyAttribute() { }

    /// <summary>Unique name of the response property (deduced from property name when property-level).</summary>
    public string? UniqueName { get; }

    /// <summary>Data type (deduced from C# type when property-level).</summary>
    public CustomApiFieldType FieldType { get; }

    /// <summary>Logical entity name — required when FieldType is EntityReference or Entity.</summary>
    public string? EntityName { get; set; }

    /// <summary>Overrides the display name derived from the unique name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Description shown in the solution explorer.</summary>
    public string? Description { get; set; }
}
