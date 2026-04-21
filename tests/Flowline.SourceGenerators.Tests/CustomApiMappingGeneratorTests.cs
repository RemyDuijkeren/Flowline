using FluentAssertions;
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace Flowline.SourceGenerators.Tests;

public class CustomApiMappingGeneratorTests
{
    [Fact]
    public void Generator_ShouldGenerateCode_ForClassWithAttributes()
    {
        // Arrange
        var source = @"
using Flowline.Attributes;

namespace MyNamespace;

public partial class MyCustomApi
{
    [RequestParameter]
    public string? MyInput { get; set; }

    [ResponseProperty]
    public string? MyOutput { get; set; }
}";

        // Act
        var (diagnostics, output) = SourceGeneratorVerifier.RunGenerator(source);

        // Assert
        diagnostics.Should().BeEmpty();
        output.Should().Contain("public static void LoadRequestParameters(this IPluginExecutionContext context, MyCustomApi target)");
        output.Should().Contain("if (context.InputParameters.Contains(\"myInput\"))");
        output.Should().Contain("target.MyInput = (string?)context.InputParameters[\"myInput\"];");
        output.Should().Contain("public static void StoreResponseProperties(this IPluginExecutionContext context, MyCustomApi target)");
        output.Should().Contain("context.OutputParameters[\"myOutput\"] = target.MyOutput;");
    }

    [Fact]
    public void Generator_ShouldDoNothing_ForClassWithoutAttributes()
    {
        // Arrange
        var source = @"
namespace MyNamespace;

public partial class MyClass
{
    public string? MyInput { get; set; }
}";

        // Act
        var (diagnostics, output) = SourceGeneratorVerifier.RunGenerator(source);

        // Assert
        diagnostics.Should().BeEmpty();
        output.Should().BeEmpty();
    }

    [Fact]
    public void Generator_ShouldHandleMultipleClasses()
    {
        // Arrange
        var source = @"
using Flowline.Attributes;

namespace Namespace1
{
    public partial class Api1
    {
        [RequestParameter] public string? In1 { get; set; }
    }
}

namespace Namespace2
{
    public partial class Api2
    {
        [ResponseProperty] public int Out1 { get; set; }
    }
}";

        // Act
        var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(System.AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "System.Runtime").Location),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Microsoft.Xrm.Sdk.IPluginExecutionContext).Assembly.Location),
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Flowline.Attributes.RequestParameterAttribute).Assembly.Location),
        };

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "Tests",
            new[] { syntaxTree },
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        var generator = new CustomApiMappingGenerator();
        Microsoft.CodeAnalysis.GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        var runResult = driver.GetRunResult();

        // Assert
        diagnostics.Should().BeEmpty();
        runResult.Results[0].GeneratedSources.Length.Should().Be(2);
        
        var api1Source = runResult.Results[0].GeneratedSources.First(s => s.HintName.Contains("Api1")).SourceText.ToString();
        var api2Source = runResult.Results[0].GeneratedSources.First(s => s.HintName.Contains("Api2")).SourceText.ToString();

        api1Source.Should().Contain("namespace Namespace1");
        api1Source.Should().Contain("class Api1GeneratedExtensions");
        
        api2Source.Should().Contain("namespace Namespace2");
        api2Source.Should().Contain("class Api2GeneratedExtensions");
    }

    [Fact]
    public void Generator_ShouldIgnorePropertiesWithoutSetterOrGetter()
    {
        // Arrange
        var source = @"
using Flowline.Attributes;

namespace MyNamespace;

public partial class MyCustomApi
{
    [RequestParameter]
    public string? ReadOnlyInput { get; }

    [ResponseProperty]
    public string? WriteOnlyOutput { set { } }
}";

        // Act
        var (diagnostics, output) = SourceGeneratorVerifier.RunGenerator(source);

        // Assert
        diagnostics.Should().BeEmpty();
        output.Should().NotContain("ReadOnlyInput");
        output.Should().NotContain("WriteOnlyOutput");
    }
}
