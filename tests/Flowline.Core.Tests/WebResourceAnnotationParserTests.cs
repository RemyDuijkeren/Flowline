using Flowline.Core.Services;

namespace Flowline.Core.Tests;

public class WebResourceAnnotationParserTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public WebResourceAnnotationParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    string Write(string filename, string content)
    {
        var path = Path.Combine(_dir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParseAnnotations_OneDependsLine_ReturnsThatName()
    {
        var path = Write("form.js", "// flowline:depends av_sol/lib/utils.js\nconsole.log('hi');");
        var result = WebResourceAnnotationParser.ParseAnnotations(path);
        Assert.Equal(["av_sol/lib/utils.js"], result);
    }

    [Fact]
    public void ParseAnnotations_TwoDependsLines_ReturnsBoth()
    {
        var path = Write("form.js",
            "// flowline:depends av_sol/lib/jquery.js\n// flowline:depends av_sol/lib/utils.js\ncode();");
        var result = WebResourceAnnotationParser.ParseAnnotations(path);
        Assert.Equal(["av_sol/lib/jquery.js", "av_sol/lib/utils.js"], result);
    }

    [Fact]
    public void ParseAnnotations_NoDependsLines_ReturnsEmpty()
    {
        var path = Write("form.js", "// just a regular comment\nconsole.log('hi');");
        var result = WebResourceAnnotationParser.ParseAnnotations(path);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAnnotations_DependsAfterCode_NotCollected()
    {
        var path = Write("form.js", "var x = 1;\n// flowline:depends av_sol/lib.js");
        var result = WebResourceAnnotationParser.ParseAnnotations(path);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAnnotations_OtherCommentsBetweenDepends_AllCollected()
    {
        var path = Write("form.js",
            "// flowline:depends av_sol/a.js\n// other comment\n// flowline:depends av_sol/b.js\ncode();");
        var result = WebResourceAnnotationParser.ParseAnnotations(path);
        Assert.Equal(["av_sol/a.js", "av_sol/b.js"], result);
    }

    [Fact]
    public void ParseAnnotations_BlankLinesBetweenDepends_AllCollected()
    {
        var path = Write("form.js",
            "\n// flowline:depends av_sol/a.js\n\n// flowline:depends av_sol/b.js\ncode();");
        var result = WebResourceAnnotationParser.ParseAnnotations(path);
        Assert.Equal(["av_sol/a.js", "av_sol/b.js"], result);
    }

    [Fact]
    public void CollectAllReferences_MultipleJsFiles_UnionOfAllAnnotations()
    {
        Write("a.js", "// flowline:depends av_sol/lib1.js\ncode();");
        Write("b.js", "// flowline:depends av_sol/lib2.js\ncode();");
        Write("c.js", "// no annotations\ncode();");

        var result = WebResourceAnnotationParser.CollectAllReferences(_dir);

        Assert.Contains("av_sol/lib1.js", result);
        Assert.Contains("av_sol/lib2.js", result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CollectAllReferences_SameReferenceInMultipleFiles_Deduplicated()
    {
        Write("a.js", "// flowline:depends av_sol/shared.js\ncode();");
        Write("b.js", "// flowline:depends av_sol/shared.js\ncode();");

        var result = WebResourceAnnotationParser.CollectAllReferences(_dir);

        Assert.Single(result);
        Assert.Contains("av_sol/shared.js", result);
    }

    [Fact]
    public void CollectAllReferences_NonExistentDirectory_ReturnsEmpty()
    {
        var result = WebResourceAnnotationParser.CollectAllReferences(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(result);
    }
}
