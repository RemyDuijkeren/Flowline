using System;

namespace Flowline.Attributes;

/// <summary>
/// Declares a Custom API request parameter for Flowline registration.
/// Can be placed on the class (explicit) or on a property (deduced).
/// </summary>
/// <remarks>
/// <b>Class-level</b>: specify <paramref name="uniqueName"/> and <paramref name="type"/> explicitly.
/// <b>Property-level</b>: uniqueName, type, and IsOptional are deduced from the property name,
/// C# type, and nullability annotation. Use <see cref="EntityName"/> for EntityReference/Entity types.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true)]
public sealed class RequestParameterAttribute : Attribute
{
    /// <summary>Class-level: explicit unique name and type.</summary>
    public RequestParameterAttribute(string uniqueName, CustomApiFieldType type)
    {
        UniqueName = uniqueName;
        FieldType = type;
    }

    /// <summary>Property-level: unique name, type, and IsOptional are deduced.</summary>
    public RequestParameterAttribute() { }

    /// <summary>Unique name of the parameter (set explicitly for class-level; deduced from property name otherwise).</summary>
    public string? UniqueName { get; }

    /// <summary>Data type (set explicitly for class-level; deduced from C# type otherwise).</summary>
    public CustomApiFieldType FieldType { get; }

    /// <summary>Whether the parameter is optional (class-level only; deduced from nullability for property-level).</summary>
    public bool IsOptional { get; set; }

    /// <summary>Logical entity name — required when FieldType is EntityReference or Entity.</summary>
    public string? EntityName { get; set; }

    /// <summary>Overrides the display name derived from the unique name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Description shown in the solution explorer.</summary>
    public string? Description { get; set; }
}
