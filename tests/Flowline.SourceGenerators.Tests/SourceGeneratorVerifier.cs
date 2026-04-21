using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Flowline.SourceGenerators.Tests;

public static class SourceGeneratorVerifier
{
    public static (ImmutableArray<Diagnostic> Diagnostics, string Output) RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "System.Runtime").Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.Xrm.Sdk.IPluginExecutionContext).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Flowline.Attributes.RequestParameterAttribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "Tests",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new CustomApiMappingGenerator();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();
        var result = runResult.Results[0];
        var output = result.GeneratedSources.Length > 0 
            ? result.GeneratedSources[0].SourceText.ToString() 
            : "";

        return (diagnostics, output);
    }
}
