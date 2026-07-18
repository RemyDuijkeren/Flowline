using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Flowline.Core.Models;

namespace Flowline.Core.WebResources;

// Fallback content-based type detection for local web resource files whose extension is
// missing or unrecognized (WebResourceType.Unknown after extension parsing). This is Tier 2 —
// a guess when no matching Dataverse record exists to adopt a type from (Tier 1, see
// WebResourceReader.BackfillUnresolvedTypes). Only signals rated >=90% confident are active;
// checks scoring lower on a 2026-07-18 review were disabled (kept below, commented out, for
// reference) rather than deleted — Svg (~80%, unanchored to the document root so any document
// containing an <svg> tag anywhere would match), Xml (~70%, a generic "<?xml" declaration is
// not enough to distinguish Xml from the separate Dataverse Xsl type), and Css (~55%, pure
// regex heuristics with no real CSS parser backing it, unlike the JS check below). A wrong
// webresourcetype written to Dataverse is worse than staying unresolved, so a disabled check
// stays disabled rather than guessing past its own confidence bar. This bar applies to Tier 2
// only — Tier 1 adopting an existing Dataverse record's Svg/Xml/Css type is authoritative, not
// a guess, and is unaffected.
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

    static readonly Regex HtmlRegex = new(@"^\s*<(!DOCTYPE\s+html|html\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex JsSignalRegex = new(
        @"\bfunction\s*\w*\s*\(|=>|\b(var|let|const)\s+\w+\s*=|Xrm\.Page|Xrm\.WebApi|formContext\.|executionContext\.|document\.getElementById\(|console\.log\(|module\.exports|require\(",
        RegexOptions.Compiled);

    // DISABLED (confidence ~80%, below the 90% bar) — unanchored, matches <svg anywhere in the
    // document, not just at the root.
    // static readonly Regex SvgTagRegex = new(@"<svg\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // DISABLED (confidence ~70%, below the 90% bar) — a generic XML declaration doesn't
    // distinguish Xml from the separate Dataverse Xsl type.
    // static readonly Regex XmlDeclarationRegex = new(@"^\s*<\?xml\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // DISABLED (confidence ~55%, below the 90% bar) — pure regex heuristics, no real CSS parser
    // to confirm a match the way Acornima confirms JS below.
    // static readonly Regex CssAtRuleRegex = new(@"@(media|import|font-face|keyframes|charset|supports)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // // Semicolon-terminated "property: value;" inside a brace pair — CSS declaration syntax.
    // // A comma-separated JS object literal (e.g. "{ color: 'red', margin: 10 }") has no
    // // semicolon inside the braces and must not match this.
    // static readonly Regex CssPropertyBlockRegex = new(@"\{[^{}]*[a-zA-Z-]+\s*:\s*[^;{}]+;[^{}]*\}", RegexOptions.Compiled);

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
        // if (SvgTagRegex.IsMatch(text)) return WebResourceType.Svg;
        // if (XmlDeclarationRegex.IsMatch(text)) return WebResourceType.Xml;
        if (JsSignalRegex.IsMatch(text) && IsValidJavaScript(content, text)) return WebResourceType.Js;
        // if (CssAtRuleRegex.IsMatch(text) || CssPropertyBlockRegex.IsMatch(text)) return WebResourceType.Css;

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
