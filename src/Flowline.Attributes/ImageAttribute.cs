using System;

namespace Flowline.Attributes;

/// <summary>Registers a pre-image snapshot on the plugin step.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PreImageAttribute(params string[] attributes) : Attribute
{
    public string Alias { get; set; } = "preimage";
    public string Name => Alias;
    public string[] Attributes { get; } = attributes;
}

/// <summary>Registers a post-image snapshot on the plugin step.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PostImageAttribute(params string[] attributes) : Attribute
{
    public string Alias { get; set; } = "postimage";
    public string Name => Alias;
    public string[] Attributes { get; } = attributes;
}
