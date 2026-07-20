using System;

namespace Flowline.Attributes
{
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

        /// <summary>
        /// Logical names of the columns to include in the snapshot.
        /// Omit to include all columns (use sparingly — fetch only what you need).
        /// </summary>
        public string[] Columns { get; }
    }
}
