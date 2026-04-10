using System.Reflection;
using System.Runtime.InteropServices;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public interface IAssemblyAnalysisService
{
    PluginAssemblyMetadata Analyze(string dllPath, IsolationMode isolationMode);
}

public class AssemblyAnalysisService : IAssemblyAnalysisService
{
    public PluginAssemblyMetadata Analyze(string dllPath, IsolationMode isolationMode)
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var paths = Directory.GetFiles(runtimeDir, "*.dll").ToList();
        paths.Add(dllPath);
        var resolver = new PathAssemblyResolver(paths);
        using var mlc = new MetadataLoadContext(resolver);

        var assembly = mlc.LoadFromAssemblyPath(dllPath);
        var assemblyName = assembly.GetName();
        var content = File.ReadAllBytes(dllPath);

        var plugins = new List<PluginTypeMetadata>();

        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.IsPublic))
        {
            var isPlugin = IsDerivedFrom(type, "Microsoft.Xrm.Sdk.IPlugin");
            var isWorkflow = IsDerivedFrom(type, "System.Activities.CodeActivity");

            if (isPlugin || isWorkflow)
            {
                var steps = new List<PluginStepMetadata>();
                
                var stepAttrs = type.GetCustomAttributesData()
                    .Where(a => a.AttributeType.FullName == "Flowline.Core.Models.StepAttribute");

                foreach (var stepData in stepAttrs)
                {
                    var message = (string)stepData.ConstructorArguments[0].Value!;
                    var entityName = (string)stepData.ConstructorArguments[1].Value!;
                    var stage = (int)stepData.ConstructorArguments[2].Value!;
                    var mode = (int)stepData.ConstructorArguments[3].Value!;
                    
                    var order = 1;
                    var filteringAttributes = (string?)null;
                    var configuration = (string?)null;

                    foreach (var namedArg in stepData.NamedArguments)
                    {
                        if (namedArg.MemberName == "Order") order = (int)namedArg.TypedValue.Value!;
                        if (namedArg.MemberName == "FilteringAttributes") filteringAttributes = (string?)namedArg.TypedValue.Value;
                        if (namedArg.MemberName == "Configuration") configuration = (string?)namedArg.TypedValue.Value;
                    }

                    var images = new List<PluginImageMetadata>();
                    // Images are typically linked to steps in a real implementation, 
                    // but here we'll simplify and just look for all ImageAttributes on the class
                    var imageAttrs = type.GetCustomAttributesData()
                        .Where(a => a.AttributeType.FullName == "Flowline.Core.Models.ImageAttribute");

                    foreach (var imageData in imageAttrs)
                    {
                        var imgName = (string)imageData.ConstructorArguments[0].Value!;
                        var imgAlias = (string)imageData.ConstructorArguments[1].Value!;
                        var imgType = (int)imageData.ConstructorArguments[2].Value!;
                        
                        var imgAttributes = (string?)null;
                        foreach (var namedArg in imageData.NamedArguments)
                        {
                            if (namedArg.MemberName == "Attributes") imgAttributes = (string?)namedArg.TypedValue.Value;
                        }
                        
                        images.Add(new PluginImageMetadata(imgName, imgAlias, imgType, imgAttributes));
                    }

                    steps.Add(new PluginStepMetadata(
                        $"{type.FullName}: {message} of {entityName}",
                        message, entityName, stage, mode, order, filteringAttributes, configuration, images));
                }

                plugins.Add(new PluginTypeMetadata(type.Name, type.FullName!, steps));
            }
        }

        return new PluginAssemblyMetadata(
            assemblyName.Name!,
            assemblyName.FullName,
            content,
            assemblyName.Version!.ToString(),
            isolationMode,
            plugins);
    }

    private bool IsDerivedFrom(Type type, string targetTypeName)
    {
        var current = type;
        while (current != null)
        {
            if (current.FullName == targetTypeName) return true;
            if (current.GetInterfaces().Any(i => i.FullName == targetTypeName)) return true;
            current = current.BaseType;
        }
        return false;
    }
}
