using System;

namespace Flowline.Attributes;

/// <summary>
/// Specifies the Dataverse entity (table) logical name this plugin step is registered for.
/// Required on every IPlugin class for Flowline to detect and register the step.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class EntityAttribute : Attribute
{
    public string LogicalName { get; }

    public EntityAttribute(string logicalName) => LogicalName = logicalName;
}
