using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Tests;

// Shared across PluginAssemblyReaderTests, PluginPackageAssemblyCheckServiceTests,
// PluginAssemblyFamilyHandlerTests, and OrphanCleanupServiceTests — all four need genuine DLLs on disk
// for MetadataLoadContext-based reflection (AnalyzePackage / PluginPackageContentReader), not just
// in-memory mock types. PersistedAssemblyBuilder (System.Reflection.Emit, no new package) builds tiny
// real DLLs at test time, each referencing nothing but corelib and Microsoft.Xrm.Sdk (for the real
// IPlugin interface) — deliberately minimal so resolving them doesn't require a wide dependency closure.
internal static class PluginDllFixtures
{
    // Builds a minimal real assembly on disk with one public class implementing IPlugin, and optionally
    // a second class deriving from a fake System.Activities.CodeActivity (workflowTypeName) — used to
    // test AnalyzePackage's CodeActivity rejection. IsDerivedFrom matches by FullName string, so a
    // same-named local type (defined in the same dynamic module) is sufficient without a real
    // System.Activities package reference.
    public static string BuildPluginDll(string dir, string assemblyName, string pluginTypeName, string? workflowTypeName = null)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");

        var pluginTb = mb.DefineType(pluginTypeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object), [typeof(IPlugin)]);
        var executeMethod = typeof(IPlugin).GetMethod(nameof(IPlugin.Execute))!;
        var methodBuilder = pluginTb.DefineMethod(nameof(IPlugin.Execute),
            MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), [typeof(IServiceProvider)]);
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
        pluginTb.DefineMethodOverride(methodBuilder, executeMethod);
        pluginTb.CreateType();

        if (workflowTypeName != null)
        {
            var codeActivityType = mb.DefineType("System.Activities.CodeActivity", TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();
            mb.DefineType(workflowTypeName, TypeAttributes.Public | TypeAttributes.Class, codeActivityType).CreateType();
        }

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // Builds a minimal real assembly on disk with one public class that does NOT implement IPlugin —
    // a stand-in for a pure-dependency DLL (e.g. Newtonsoft.Json) that AnalyzePackage must skip.
    public static string BuildDependencyDll(string dir, string assemblyName, string typeName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");
        mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // Zips the given DLLs into a .nupkg under lib/<tfm>/, mirroring the real OPC package layout.
    public static string BuildNupkg(string dir, params string[] dllPaths)
    {
        var nupkgPath = Path.Combine(dir, $"{Guid.NewGuid():N}.nupkg");
        using var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);
        foreach (var dllPath in dllPaths)
            archive.CreateEntryFromFile(dllPath, $"lib/net10.0/{Path.GetFileName(dllPath)}");
        return nupkgPath;
    }
}
