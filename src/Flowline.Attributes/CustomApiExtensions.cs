using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;

namespace Flowline.Attributes;

/// <summary>
/// Extension methods for populating Custom API parameters and properties.
/// </summary>
public static class CustomApiExtensions
{
    /// <summary>
    /// Populates properties marked with [RequestParameter] from the context's InputParameters.
    /// </summary>
    public static void LoadRequestParameters(this IPluginExecutionContext context, object target)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (target == null) throw new ArgumentNullException(nameof(target));

        foreach (var prop in target.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.CanWrite && p.GetCustomAttribute<RequestParameterAttribute>() != null))
        {
            var key = ToCamelCase(prop.Name);
            if (context.InputParameters.Contains(key))
            {
                prop.SetValue(target, context.InputParameters[key]);
            }
        }
    }

    /// <summary>
    /// Writes properties marked with [ResponseProperty] to the context's OutputParameters.
    /// </summary>
    public static void StoreResponseProperties(this IPluginExecutionContext context, object target)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (target == null) throw new ArgumentNullException(nameof(target));

        foreach (var prop in target.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.CanRead && p.GetCustomAttribute<ResponsePropertyAttribute>() != null))
        {
            var key = ToCamelCase(prop.Name);
            context.OutputParameters[key] = prop.GetValue(target);
        }
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
