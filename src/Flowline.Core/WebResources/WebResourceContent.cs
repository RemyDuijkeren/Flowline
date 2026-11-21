using Flowline.Core.Models;

namespace Flowline.Core.WebResources;

// Change detection for web resource content. Text types compare with CRLF folded to LF and a leading
// UTF-8 BOM dropped, so a Windows checkout (git autocrlf / `* text=auto`) and a Linux checkout of the
// same commit don't read as a content change. Binary types compare byte-exact. Upload stays byte-exact:
// this is only ever used to decide *whether* something changed, never to produce what gets sent.
public static class WebResourceContent
{
    static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static bool IsText(WebResourceType type) => type is
        WebResourceType.Html or WebResourceType.Css or WebResourceType.Js or WebResourceType.Xml or
        WebResourceType.Xsl or WebResourceType.Svg or WebResourceType.Resx;

    /// <summary>The type for a file name by extension, or <see cref="WebResourceType.Unknown"/>.</summary>
    public static WebResourceType TypeFromPath(string path) =>
        Enum.TryParse<WebResourceType>(Path.GetExtension(path).TrimStart('.'), true, out var type)
            ? type
            : WebResourceType.Unknown;

    /// <summary>Compares two base64 contents as <paramref name="type"/> would see them.</summary>
    public static bool Base64Equals(string? a, string? b, WebResourceType type)
    {
        if (a == b) return true;
        if (a is null || b is null || !IsText(type)) return false;
        return Normalize(Convert.FromBase64String(a)).AsSpan()
            .SequenceEqual(Normalize(Convert.FromBase64String(b)));
    }

    /// <summary>The bytes a comparison should see: text normalized, binary untouched.</summary>
    public static byte[] ForComparison(byte[] bytes, WebResourceType type) =>
        IsText(type) ? Normalize(bytes) : bytes;

    static byte[] Normalize(byte[] bytes)
    {
        var start = bytes.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        var result = new List<byte>(bytes.Length - start);
        for (var i = start; i < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n') continue;
            result.Add(bytes[i]);
        }
        return [.. result];
    }
}
