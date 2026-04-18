using System;

namespace Flowline.Attributes;

/// <summary>
/// Registers a pre- or post-image snapshot on the plugin step.
/// Stack multiple times to register both a PreImage and a PostImage.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ImageAttribute : Attribute
{
    /// <summary>Key used in plugin code: <c>context.PreEntityImages["alias"]</c>.</summary>
    public string Alias { get; }

    /// <summary>
    /// Display name shown in the Plugin Registration Tool. Defaults to "Pre Image", "Post Image", or "Image".
    /// Not used in plugin code — override only when you need a custom label.
    /// </summary>
    public string Name { get; set; }

    /// <summary>Type of image snapshot.</summary>
    public ImageType ImageType { get; }

    /// <summary>Attributes to include. Omit to include all attributes.</summary>
    public string[] Attributes { get; }

    /// <param name="imageType">Type of image snapshot.</param>
    /// <param name="attributes">Attributes to include. Omit to include all attributes.</param>
    public ImageAttribute(ImageType imageType, params string[] attributes)
        : this(DefaultAlias(imageType), imageType, attributes) { }

    /// <param name="alias">Key used in plugin code: <c>context.PreEntityImages["alias"]</c>. Use when the default alias conflicts.</param>
    /// <param name="imageType">Type of image snapshot.</param>
    /// <param name="attributes">Attributes to include. Omit to include all attributes.</param>
    public ImageAttribute(string alias, ImageType imageType, params string[] attributes)
    {
        Alias = alias;
        ImageType = imageType;
        Attributes = attributes;
        Name = DefaultName(imageType);
    }

    private static string DefaultAlias(ImageType t) => t switch
    {
        ImageType.PreImage => "preimage",
        ImageType.PostImage => "postimage",
        _ => "image"
    };

    private static string DefaultName(ImageType t) => t switch
    {
        ImageType.PreImage => "Pre Image",
        ImageType.PostImage => "Post Image",
        _ => "Image"
    };
}

public enum ImageType
{
    PreImage = 0,
    PostImage = 1,
    Both = 2
}
