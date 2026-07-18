using System.Text;
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
    public void TrySniff_SvgRootTag_WithXmlDeclaration_ResolvesSvg()
    {
        var content = Bytes("""<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg"></svg>""");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Svg);
    }

    [Fact]
    public void TrySniff_SvgRootTag_WithoutXmlDeclaration_ResolvesSvg()
    {
        var content = Bytes("""<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0"/></svg>""");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Svg);
    }

    [Fact]
    public void TrySniff_BareXmlDeclaration_NoResxOrSvgMarker_ResolvesXml()
    {
        var content = Bytes("""<?xml version="1.0"?><config><setting>1</setting></config>""");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Xml);
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
    public void TrySniff_CssAtRule_ResolvesCss()
    {
        var content = Bytes("@media (min-width: 768px) { .foo { display: block; } }");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Css);
    }

    [Fact]
    public void TrySniff_CssPropertyBlock_NoAtRule_ResolvesCss()
    {
        var content = Bytes(".foo { color: red; margin: 10px; }");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Css);
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
    public void TrySniff_RespectsPriority_XmlDeclarationWithSvgTag_ResolvesSvgNotXml()
    {
        var content = Bytes("""<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg"></svg>""");

        WebResourceTypeSniffer.TrySniff(content).Should().Be(WebResourceType.Svg);
    }

    [Fact]
    public void TrySniff_JsObjectLiteral_ResolvesJs_NotCss()
    {
        // Comma-separated, no semicolon inside the braces — must not trip the CSS property-block signal.
        var content = Bytes("const config = { color: 'red', margin: 10 };");

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
}
