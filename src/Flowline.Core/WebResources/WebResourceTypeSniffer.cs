using System.Text;
using System.Text.RegularExpressions;
using Flowline.Core.Models;

namespace Flowline.Core.WebResources;

// Fallback content-based type detection for local web resource files whose extension is
// missing or unrecognized (WebResourceType.Unknown after extension parsing). Signals are
// checked most-specific-first: RESX and SVG are XML supersets, so both are checked before
// the generic XML declaration to avoid misclassifying either as plain Xml. CSS and JS both
// require a positive signal — never a "nothing else matched" default — since a wrong
// webresourcetype written to Dataverse is worse than staying unresolved.
public static class WebResourceTypeSniffer
{
    const int TextPrefixBytes = 4096;

    static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];
    static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    static readonly byte[] GifMagic = "GIF8"u8.ToArray();
    static readonly byte[] IcoMagic = [0x00, 0x00, 0x01, 0x00];

    static readonly Regex SvgTagRegex = new(@"<svg\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex XmlDeclarationRegex = new(@"^\s*<\?xml\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex HtmlRegex = new(@"^\s*<(!DOCTYPE\s+html|html\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CssAtRuleRegex = new(@"@(media|import|font-face|keyframes|charset|supports)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Semicolon-terminated "property: value;" inside a brace pair — CSS declaration syntax.
    // A comma-separated JS object literal (e.g. "{ color: 'red', margin: 10 }") has no
    // semicolon inside the braces and must not match this.
    static readonly Regex CssPropertyBlockRegex = new(@"\{[^{}]*[a-zA-Z-]+\s*:\s*[^;{}]+;[^{}]*\}", RegexOptions.Compiled);
    static readonly Regex JsSignalRegex = new(
        @"\bfunction\s*\w*\s*\(|=>|\b(var|let|const)\s+\w+\s*=|Xrm\.Page|Xrm\.WebApi|formContext\.|executionContext\.|document\.getElementById\(|console\.log\(|module\.exports|require\(",
        RegexOptions.Compiled);

    public static WebResourceType? TrySniff(byte[] content)
    {
        if (content.Length == 0) return null;

        if (content.AsSpan().StartsWith(PngMagic)) return WebResourceType.Png;
        if (content.AsSpan().StartsWith(JpegMagic)) return WebResourceType.Jpg;
        if (content.AsSpan().StartsWith(GifMagic)) return WebResourceType.Gif;
        if (content.AsSpan().StartsWith(IcoMagic)) return WebResourceType.Ico;

        string text;
        try
        {
            var prefixLength = Math.Min(content.Length, TextPrefixBytes);
            text = Encoding.UTF8.GetString(content, 0, prefixLength);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (text.Contains("Microsoft ResX Schema", StringComparison.Ordinal)) return WebResourceType.Resx;
        if (SvgTagRegex.IsMatch(text)) return WebResourceType.Svg;
        if (XmlDeclarationRegex.IsMatch(text)) return WebResourceType.Xml;
        if (HtmlRegex.IsMatch(text)) return WebResourceType.Html;
        if (CssAtRuleRegex.IsMatch(text) || CssPropertyBlockRegex.IsMatch(text)) return WebResourceType.Css;
        if (JsSignalRegex.IsMatch(text)) return WebResourceType.Js;

        return null;
    }
}
