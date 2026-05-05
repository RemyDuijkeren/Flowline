using System;

namespace Flowline.Attributes
{

/// <summary>
/// Registers a pre-image on this plugin step — a snapshot of the record's column values
/// <b>before</b> the current operation runs.
/// </summary>
/// <remarks>
/// <para>
/// A <b>pre-image</b> lets you read what the record looked like before the triggering operation.
/// This is essential on Update steps when you need to compare old values against new ones, and
/// on Delete steps when you need to read the record that is about to be removed.
/// </para>
/// <para>
/// <b>Availability by message and stage:</b>
/// </para>
/// <list type="table">
///   <listheader><term>Message</term><description>Pre-image available?</description></listheader>
///   <item><term>Create</term><description>No — the record did not exist before this operation.</description></item>
///   <item><term>Update</term><description>Yes — available in PreValidation, PreOperation, and PostOperation.</description></item>
///   <item><term>Delete</term><description>Yes — available in PreValidation, PreOperation, and PostOperation.</description></item>
/// </list>
/// <para>
/// Pass the column logical names you need. Omit arguments to receive all columns (expensive —
/// prefer listing only what your plugin actually reads):
/// </para>
/// <code>
/// [Step("account")]
/// [PreImage("name", "creditlimit")]          // only these two columns
/// public class AccountPreUpdatePlugin : IPlugin
/// {
///     public void Execute(IServiceProvider sp)
///     {
///         var ctx      = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
///         var preImage = ctx.PreEntityImages["preimage"];
///         var oldName  = preImage.GetAttributeValue&lt;string&gt;("name");
///     }
/// }
/// </code>
/// <para>
/// The image is always retrieved with the key <c>"preimage"</c> unless you override
/// <see cref="Alias"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PreImageAttribute : Attribute
{
    /// <summary>
    /// Specifies an attribute to define a pre-image for a specific entity.
    /// This is commonly utilized in plugins to capture the state of an entity prior to an operation or event execution.
    /// </summary>
    /// <param name="columns">The logical names of the columns to include in the snapshot.</param>
    public PreImageAttribute(params string[] columns)
    {
        Columns = columns;
    }

    /// <summary>
    /// The key used to retrieve this image from <c>context.PreEntityImages</c>.
    /// Default is <c>"preimage"</c>. Override only when migrating from a manually registered
    /// step that used a different alias, so existing code does not break.
    /// </summary>
    public string Alias { get; set; } = "preimage";

    /// <summary>Same as <see cref="Alias"/>. Used internally by Flowline.</summary>
    public string Name => Alias;

    /// <summary>
    /// Logical names of the columns to include in the snapshot.
    /// Omit to include all columns (use sparingly — fetch only what you need).
    /// </summary>
    public string[] Columns { get; }
}

/// <summary>
/// Registers a post-image on this plugin step — a snapshot of the record's column values
/// <b>after</b> the current operation completes.
/// </summary>
/// <remarks>
/// <para>
/// A <b>post-image</b> lets you read what the record looks like after the triggering operation,
/// including columns your plugin did not modify. This is useful on PostOperation steps when you
/// need to see the full final state of the record without making a separate Retrieve call.
/// </para>
/// <para>
/// <b>Availability by message and stage:</b>
/// </para>
/// <list type="table">
///   <listheader><term>Message</term><description>Post-image available?</description></listheader>
///   <item><term>Create</term><description>Yes — but only in PostOperation stage (sync or async).</description></item>
///   <item><term>Update</term><description>Yes — but only in PostOperation stage (sync or async).</description></item>
///   <item><term>Delete</term><description>No — the record no longer exists after this operation.</description></item>
/// </list>
/// <para>
/// Post-images are <b>not available</b> in PreValidation or PreOperation stages — the operation
/// has not completed yet at those points.
/// </para>
/// <para>
/// Pass the column logical names you need. Omit arguments to receive all columns (expensive —
/// prefer listing only what your plugin actually reads):
/// </para>
/// <code>
/// [Step("account")]
/// [Filter("name")]
/// [PreImage("name")]
/// [PostImage("name")]
/// public class AccountPostUpdatePlugin : IPlugin
/// {
///     public void Execute(IServiceProvider sp)
///     {
///         var ctx       = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
///         var preImage  = ctx.PreEntityImages["postimage"];
///         var postImage = ctx.PostEntityImages["postimage"];
///         var oldName   = preImage.GetAttributeValue&lt;string&gt;("name");
///         var newName   = postImage.GetAttributeValue&lt;string&gt;("name");
///         if (oldName != newName) { /* name changed */ }
///     }
/// }
/// </code>
/// <para>
/// The image is always retrieved with the key <c>"postimage"</c> unless you override
/// <see cref="Alias"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PostImageAttribute : Attribute
{
    public PostImageAttribute(params string[] columns)
    {
        Columns = columns;
    }

    /// <summary>
    /// The key used to retrieve this image from <c>context.PostEntityImages</c>.
    /// Default is <c>"postimage"</c>. Override only when migrating from a manually registered
    /// step that used a different alias, so existing code does not break.
    /// </summary>
    public string Alias { get; set; } = "postimage";

    /// <summary>Same as <see cref="Alias"/>. Used internally by Flowline.</summary>
    public string Name => Alias;

    /// <summary>
    /// Logical names of the columns to include in the snapshot.
    /// Omit to include all columns (use sparingly — fetch only what you need).
    /// </summary>
    public string[] Columns { get; }
}
}
