using System;

namespace Flowline.Attributes;

/// <summary>
/// Specifies the Dataverse entity (table) logical name this plugin step is registered for.
/// Required on every IPlugin class for Flowline to detect and register the step.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class EntityAttribute(string logicalName) : Attribute
{
    public string LogicalName { get; } = logicalName;

    /// <summary>
    /// Controls the execution order when multiple plugins are registered on the same step.
    /// Lower numbers run first. Default is 1.
    /// </summary>
    public int Order { get; set; } = 1;

    /// <summary>
    /// Specifies which user context the plugin runs under. Controls <c>context.UserId</c> inside <c>Execute</c>.
    /// Default is <see cref="ExecuteAs.CallingUser"/>.
    /// </summary>
    public ExecuteAs As { get; set; } = ExecuteAs.CallingUser;

    /// <summary>
    /// Optional configuration string passed to the plugin constructor as the first parameter.
    /// Use this to supply endpoint URLs, feature flags, or serialized JSON config without hardcoding them.
    /// Retrieve it in the constructor: <c>public MyPlugin(string unsecureConfig) { ... }</c>
    /// Maps to Unsecure Configuration in Plugin Registration Tool.
    /// Secure Configuration is intentionally not supported — secrets should not be committed to source code.
    /// </summary>
    public string? Configuration { get; set; }
}

/// <summary>
/// Controls which user context the plugin runs under, affecting <c>context.UserId</c> inside <c>Execute</c>.
/// </summary>
public enum ExecuteAs
{
    /// <summary>
    /// The plugin runs as the user who directly triggered the operation (the API caller).
    /// This is the default and covers most scenarios.
    /// </summary>
    CallingUser = 0,

    /// <summary>
    /// The plugin runs as the user who started the original chain of events.
    /// Use this when a Flow or workflow triggers your plugin and you need the human user's
    /// context rather than the service account that owns the automation.
    /// </summary>
    InitiatingUser = 1
}

