using System;

namespace Flowline.Attributes;

/// <summary>
/// Marks an IPlugin class as a Dataverse Custom API.
/// Flowline uses this to register the customapi record in Dataverse.
/// </summary>
/// <param name="boundEntity">
/// Logical name of the entity to bind to (bindingtype = Entity).
/// Omit for a Global API. Use <see cref="EntityCollection"/> for EntityCollection binding.
/// </param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CustomApiAttribute(string? boundEntity = null) : Attribute
{
    /// <summary>Bound entity logical name → bindingtype = Entity (1).</summary>
    public string? BoundEntity { get; } = boundEntity;

    /// <summary>Bound entity logical name → bindingtype = EntityCollection (2).</summary>
    public string? EntityCollection { get; set; }

    /// <summary>Whether this Custom API is a Function (true) or Action (false). Default: false.</summary>
    public bool IsFunction { get; set; }

    /// <summary>Whether this Custom API is private (hidden from the API catalog). Default: false.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Which custom processing step types third parties are allowed to add. Default: None.</summary>
    public CustomApiStepType AllowedStepType { get; set; }

    /// <summary>Overrides the display name derived from the class name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Description shown in the solution explorer and API catalog.</summary>
    public string? Description { get; set; }

    /// <summary>Name of the privilege required to execute this Custom API.</summary>
    public string? ExecutePrivilege { get; set; }
}
