using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;

namespace Flowline.Attributes;

/// <summary>
/// Optional base class for Custom API plugins.
/// Automatically populates [RequestParameter] properties from InputParameters before Execute()
/// and writes [ResponseProperty] properties to OutputParameters after Execute().
/// Exposes Context, Service, and Tracing as protected helpers.
/// </summary>
public abstract class CustomApiBase : IPlugin
{
    protected IPluginExecutionContext Context { get; private set; } = null!;
    protected IOrganizationService Service { get; private set; } = null!;
    protected ITracingService Tracing { get; private set; } = null!;

    public void Execute(IServiceProvider serviceProvider)
    {
        Context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
        Service = factory.CreateOrganizationService(Context.UserId);
        Tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

        PopulateInputs();
        Execute();
        PopulateOutputs();
    }

    protected abstract void Execute();

    private void PopulateInputs()
    {
        foreach (var prop in GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.CanWrite && p.GetCustomAttribute<RequestParameterAttribute>() != null))
        {
            var key = ToCamelCase(prop.Name);
            if (Context.InputParameters.Contains(key))
                prop.SetValue(this, Context.InputParameters[key]);
        }
    }

    private void PopulateOutputs()
    {
        foreach (var prop in GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.CanRead && p.GetCustomAttribute<ResponsePropertyAttribute>() != null))
        {
            Context.OutputParameters[ToCamelCase(prop.Name)] = prop.GetValue(this);
        }
    }

    private static string ToCamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
