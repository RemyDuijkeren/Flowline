using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Flowline.Core.Models;

namespace Flowline.Core.WebResources;

// Fallback content-based type detection for local web resource files whose extension is
// missing or unrecognized (WebResourceType.Unknown after extension parsing). Signals are
// checked most-specific-first: RESX is an XML superset, so it's checked before the HTML/SVG/XML
// document-root checks. HtmlRegex is anchored to the start of the document (^\s*<...), so an
// HTML page containing a descendant <svg> element is still classified Html — only a document
// whose root element is <svg> falls through to the SVG check. CSS and JS both require a
// positive signal — never a "nothing else matched" default — since a wrong webresourcetype
// written to Dataverse is worse than staying unresolved. JS is checked before CSS and confirmed
// with a real parse (Acornima, the same parser FormEventFunctionResolver uses on built web
// resources) rather than trusting the signal regex alone — a regex can be fooled by an
// incidental "identifier: value;" pair (e.g. a switch/case "default: return 0;", or a CSS
// string embedded in a JS template literal) that reads as a CSS declaration but isn't one.
public static class WebResourceTypeSniffer
{
    const int TextPrefixBytes = 4096;

    static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];
    static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    static readonly byte[] GifMagic = "GIF8"u8.ToArray();
    static readonly byte[] IcoMagic = [0x00, 0x00, 0x01, 0x00];

    // Throwing fallback so malformed byte sequences actually raise DecoderFallbackException —
    // the default Encoding.UTF8 instance silently substitutes U+FFFD instead of throwing, which
    // would let binary garbage decode into text and risk a false-positive regex match below.
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
            text = StrictUtf8.GetString(content, 0, prefixLength);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (text.Contains("Microsoft ResX Schema", StringComparison.Ordinal)) return WebResourceType.Resx;
        if (HtmlRegex.IsMatch(text)) return WebResourceType.Html;
        if (SvgTagRegex.IsMatch(text)) return WebResourceType.Svg;
        if (XmlDeclarationRegex.IsMatch(text)) return WebResourceType.Xml;
        if (JsSignalRegex.IsMatch(text) && IsValidJavaScript(content, text)) return WebResourceType.Js;
        if (CssAtRuleRegex.IsMatch(text) || CssPropertyBlockRegex.IsMatch(text)) return WebResourceType.Css;

        return null;
    }

    // The signal regex only proves the prefix *contains* JS-shaped tokens — it can't tell a real
    // script from a CSS/text file that happens to embed one (e.g. a CSS string literal containing
    // "=>"). A successful parse is the authoritative check. Parses the full content, not just the
    // prefix used for the cheap regex checks above — parsing only a truncated prefix would make a
    // large, genuinely valid script fail on an unterminated statement at the truncation point.
    static bool IsValidJavaScript(byte[] content, string prefixText)
    {
        string text;
        if (content.Length <= TextPrefixBytes)
        {
            text = prefixText;
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(content);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        try
        {
            new Parser().ParseScript(text);
            return true;
        }
        catch (ParseErrorException)
        {
            return false;
        }
    }
}
