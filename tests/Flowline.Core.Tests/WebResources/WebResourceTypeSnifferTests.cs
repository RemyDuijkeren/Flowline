using System.Text;
using System.Linq;
using Flowline.Core.Models;
using Flowline.Core.WebResources;
using FluentAssertions;

namespace Flowline.Core.Tests.WebResources;

public class WebResourceTypeSnifferTests
{
    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, WebResourceType.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, WebResourceType.Jpg)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, WebResourceType.Gif)]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 }, WebResourceType.Ico)]
    public void TrySniff_MagicBytes_ResolvesExpectedType(byte[] content, WebResourceType expected)
    {
        WebResourceTypeSniffer.TrySniff(content).Should().Be(expected);
    }

    [Fact]
    public void TrySniff_ResxSchemaMarker_ResolvesResx()
    {
        var content = Bytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <!--
                Microsoft ResX Schema
              -->
              <data name="Greeting"><value>Hello</value></data>
            </root>
            """);

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Resx);
    }

    [Fact]
    public void TrySniff_SvgRootTag_WithXmlDeclaration_ReturnsNull_SvgSniffingDisabled()
    {
        // Svg sniffing is disabled (~80% confidence, below the 90% bar) — stays Unknown rather
        // than guessing. Tier 1 (an existing Dataverse record) is unaffected and still resolves
        // real Svg files correctly.
        var content = Bytes("""<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg"></svg>""");

        WebResourceTypeSniffer.TrySniff(content).Should().BeNull();
    }

    [Fact]
    public void TrySniff_SvgRootTag_WithoutXmlDeclaration_ReturnsNull_SvgSniffingDisabled()
    {
        var content = Bytes("""<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0"/></svg>""");

        WebResourceTypeSniffer.TrySniff(content).Should().BeNull();
    }

    [Fact]
    public void TrySniff_BareXmlDeclaration_ReturnsNull_XmlSniffingDisabled()
    {
        // Xml sniffing is disabled (~70% confidence, below the 90% bar) — stays Unknown rather
        // than guessing (a generic "<?xml" declaration can't distinguish Xml from Xsl).
        var content = Bytes("""<?xml version="1.0"?><config><setting>1</setting></config>""");

        WebResourceTypeSniffer.TrySniff(content).Should().BeNull();
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Hi</body></html>")]
    [InlineData("<html><body>Hi</body></html>")]
    [InlineData("  <HTML><body>Hi</body></html>")]
    public void TrySniff_HtmlDoctypeOrTag_ResolvesHtml(string text)
    {
        WebResourceTypeSniffer.TrySniff(Bytes(text)).Should().Be(WebResourceType.Html);
    }

    [Fact]
    public void TrySniff_CssAtRule_ReturnsNull_CssSniffingDisabled()
    {
        // Css sniffing is disabled (~55% confidence, below the 90% bar) — no real CSS parser
        // backs this check (unlike the Acornima-validated Js check), so it stays Unknown.
        var content = Bytes("@media (min-width: 768px) { .foo { display: block; } }");

        WebResourceTypeSniffer.TrySniff(content).Should().BeNull();
    }

    [Fact]
    public void TrySniff_CssPropertyBlock_NoAtRule_ReturnsNull_CssSniffingDisabled()
    {
        var content = Bytes(".foo { color: red; margin: 10px; }");

        WebResourceTypeSniffer.TrySniff(content).Should().BeNull();
    }

    [Theory]
    [InlineData("function hide() {\n  var value = 1;\n}")]
    [InlineData("const handler = () => { console.log('hi'); };")]
    [InlineData("var x = 1;")]
    [InlineData("let x = 1;")]
    [InlineData("const x = 1;")]
    [InlineData("Xrm.Page.getAttribute('foo').setValue(1);")]
    [InlineData("formContext.getAttribute('foo').setValue(1);")]
    public void TrySniff_JsSignal_ResolvesJs(string text)
    {
        WebResourceTypeSniffer.TrySniff(Bytes(text)).Should().Be(WebResourceType.Js);
    }

    [Fact]
    public void TrySniff_JsObjectLiteral_ResolvesJs_NotCss()
    {
        // Comma-separated, no semicolon inside the braces — must not trip the CSS property-block signal.
        var content = Bytes("const config = { color: 'red', margin: 10 };");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Js);
    }

    [Fact]
    public void TrySniff_JsSwitchStatement_ResolvesJs_NotCss()
    {
        // "default: return 0;" inside a brace pair looks like a CSS declaration to a naive regex.
        var content = Bytes("function f(x) {\n  switch (x) {\n    case 1: return 1;\n    default: return 0;\n  }\n}");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Js);
    }

    [Fact]
    public void TrySniff_JsWithCssTemplateLiteral_ResolvesJs_NotCss()
    {
        var content = Bytes("const css = `.foo { color: red; }`;\nconst handler = () => {};");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Js);
    }

    [Fact]
    public void TrySniff_HtmlWithInlineSvg_ResolvesHtml()
    {
        var content = Bytes("<!DOCTYPE html><html><body><svg><path d=\"M0 0\"/></svg></body></html>");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Html);
    }

    [Fact]
    public void TrySniff_CssContainingArrowLikeStringLiteral_ReturnsNull_NotJs()
    {
        // The JS signal regex matches "=>" anywhere, including inside a CSS string value — the
        // real-parse guard rejects this as JS since it isn't valid JavaScript syntax. Css
        // sniffing itself is disabled, so this now stays Unknown rather than resolving to Css.
        var content = Bytes(".foo:after { content: \"=>\"; }");

        WebResourceTypeSniffer.TrySniff(content).Should().BeNull();
    }

    [Fact]
    public void TrySniff_LargeValidJsFile_ResolvesJs()
    {
        // Content longer than the 4096-byte regex-check prefix must still parse-validate against
        // the FULL content, not a truncated prefix that would fail on an unterminated statement.
        // The JS signal itself sits within the prefix; the padding after it pushes total length past it.
        var padding = string.Concat(Enumerable.Repeat("// padding comment to exceed the prefix length\n", 200));
        var content = Bytes("function widget() {\n  console.log('hi');\n}\n" + padding);

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Js);
    }

    [Fact]
    public void TrySniff_EmptyByteArray_ReturnsNull()
    {
        WebResourceTypeSniffer.TrySniff([]).Should().BeNull();
    }

    [Fact]
    public void TrySniff_PlainTextNoSignals_ReturnsNull()
    {
        WebResourceTypeSniffer.TrySniff(Bytes("hello world, nothing to see here")).Should().BeNull();
    }

    [Fact]
    public void TrySniff_InvalidUtf8Bytes_ReturnsNull()
    {
        // Lone continuation byte (0x80) is not valid UTF-8 on its own — no magic-byte match, so
        // this must be rejected by the decoder rather than silently decoded with replacement chars.
        byte[] content = [0x80, 0x81, 0x82, 0x83];

        WebResourceTypeSniffer.TrySniff(content).Should().BeNull();
    }
}
