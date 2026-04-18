using System;

namespace Flowline.Attributes;

/// <summary>
/// Registers a pre- or post-image snapshot on the plugin step.
/// Stack multiple times to register both a PreImage and a PostImage.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ImageAttribute : Attribute
{
    public string Name { get; }
    public string Alias { get; }
    public ImageType ImageType { get; }
    public string[] Attributes { get; }

    /// <param name="name">Display name of the image in Plugin Registration Tool.</param>
    /// <param name="alias">Alias used to access the image in plugin code via <c>context.PreEntityImages["alias"]</c>.</param>
    /// <param name="imageType">Type of image snapshot. Defaults to <see cref="ImageType.PostImage"/>.</param>
    /// <param name="attributes">Attributes to include in the snapshot. Omit to include all attributes.</param>
    public ImageAttribute(string name, string alias,
        ImageType imageType = ImageType.PostImage, params string[] attributes)
    {
        Name = name;
        Alias = alias;
        ImageType = imageType;
        Attributes = attributes;
    }
}

public enum ImageType
{
    PreImage = 0,
    PostImage = 1,
    Both = 2
}
