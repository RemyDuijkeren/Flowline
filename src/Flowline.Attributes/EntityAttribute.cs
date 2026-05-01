using System;

namespace Flowline.Attributes;

/// <summary>
/// Registers this <c>IPlugin</c> class as a Dataverse plugin step on the specified table.
/// </summary>
/// <remarks>
/// <para>
/// A <b>plugin step</b> is code that Dataverse calls automatically when a specific operation
/// (message) happens on a table — for example, when an account record is created, updated, or
/// deleted. Without <c>[Entity]</c>, Flowline ignores the class entirely.
/// </para>
/// <para>
/// The table logical name is the only required argument. The message (Create, Update, Delete ...)
/// and pipeline stage (PreValidation, Pre, Post, Async) are encoded in the class name:
/// </para>
/// <code>
/// {DescriptiveName}{Stage}{Message}[Async][Plugin]
///
/// AccountPostCreatePlugin      → PostOperation, Create, account, synchronous
/// InvoicePreUpdatePlugin       → PreOperation,  Update, cr123_invoice, synchronous
/// ContactValidationDeletePlugin → PreValidation, Delete, contact, synchronous
/// OrderPostUpdateAsyncPlugin   → PostOperation, Update, salesorder, asynchronous
/// </code>
/// <para>
/// <b>Choosing a stage:</b>
/// </para>
/// <list type="table">
///   <listheader><term>Stage keyword</term><description>When it runs and what to use it for</description></listheader>
///   <item>
///     <term><c>Validation</c></term>
///     <description>
///       Runs <b>before the database transaction opens</b>. Throw
///       <c>InvalidPluginExecutionException</c> here to reject the operation cleanly — nothing
///       is rolled back because nothing was written yet. Best for validation that blocks the
///       operation (e.g. credit limit checks, required field rules).
///     </description>
///   </item>
///   <item>
///     <term><c>Pre</c></term>
///     <description>
///       Runs <b>inside the transaction, before the record is saved</b>. Modifications to the
///       <c>Target</c> entity in <c>InputParameters</c> are automatically included in the save —
///       use this to enrich or correct incoming data before it hits the database.
///     </description>
///   </item>
///   <item>
///     <term><c>Post</c> (synchronous)</term>
///     <description>
///       Runs <b>inside the transaction, after the record is saved</b>. The save has completed
///       but the transaction is still open — an unhandled exception still rolls everything back.
///       Use for follow-up writes that must be atomic with the triggering operation.
///     </description>
///   </item>
///   <item>
///     <term><c>Post</c> + <c>Async</c></term>
///     <description>
///       Runs <b>outside the transaction, in the background</b>. The user's operation completes
///       first; your plugin runs later via the async service. Use for notifications, external API
///       calls, or long-running work that must not block the user.
///     </description>
///   </item>
/// </list>
/// </remarks>
/// <param name="logicalName">
/// The logical name of the Dataverse table (entity) to register the step on.
/// Use the schema name in lowercase: <c>"account"</c>, <c>"contact"</c>,
/// <c>"cr123_invoice"</c>. Found in the maker portal under Table → Properties → Name.
/// </param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EntityAttribute(string logicalName) : Attribute
{
    /// <summary>
    /// Logical name of the Dataverse table this step is registered on.
    /// </summary>
    public string LogicalName { get; } = logicalName;

    /// <summary>
    /// Controls the execution order when multiple plugin steps are registered for the same
    /// message and stage on the same table. Lower numbers run first. Default is <c>1</c>.
    /// </summary>
    /// <remarks>
    /// Only relevant when you have more than one plugin that fires on the same event.
    /// Use this to guarantee ordering between them — for example, a validation plugin at
    /// order 1 and an enrichment plugin at order 2 on the same PreOperation Update step.
    /// </remarks>
    public int Order { get; set; } = 1;

    /// <summary>
    /// Specifies which user's identity the plugin runs under, controlling
    /// <c>context.UserId</c> inside <c>Execute</c>. Default is <see cref="ExecuteAs.CallingUser"/>.
    /// </summary>
    /// <remarks>
    /// Use <see cref="ExecuteAs.InitiatingUser"/> when a Power Automate flow or workflow
    /// triggers your plugin and you need the original human user's identity rather than
    /// the flow's service account. Most plugins should use the default.
    /// </remarks>
    public ExecuteAs As { get; set; } = ExecuteAs.CallingUser;

    /// <summary>
    /// An optional string passed to your plugin's constructor as the first parameter
    /// (<c>unsecureConfig</c>). Use it to supply endpoint URLs, feature flags, or
    /// serialized JSON settings without hardcoding them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retrieve it in a constructor overload that accepts <c>string unsecureConfig</c>:
    /// </para>
    /// <code>
    /// [Entity("account", Configuration = "{\"endpoint\":\"https://api.example.com\"}")]
    /// public class AccountPostCreatePlugin : IPlugin
    /// {
    ///     private readonly string _endpoint;
    ///
    ///     public AccountPostCreatePlugin(string unsecureConfig)
    ///     {
    ///         _endpoint = JsonSerializer.Deserialize&lt;Config&gt;(unsecureConfig)!.Endpoint;
    ///     }
    ///
    ///     public void Execute(IServiceProvider sp) { ... }
    /// }
    /// </code>
    /// <para>
    /// This string is visible in the solution XML and source control.
    /// <b>Do not store secrets here.</b> Use environment variables or Azure Key Vault instead.
    /// Secure Configuration (which encrypts the string) is intentionally not supported —
    /// it encourages storing secrets in source control, which Flowline avoids by design.
    /// </para>
    /// </remarks>
    public string? Configuration { get; set; }

    /// <summary>
    /// When <see langword="true"/>, Dataverse automatically deletes the
    /// <c>AsyncOperation</c> (system job) record after this step completes successfully.
    /// Keeps the job queue clean without a separate cleanup flow.
    /// </summary>
    /// <remarks>
    /// Only applies to asynchronous post-operation steps — those are the only steps that
    /// create an <c>AsyncOperation</c> record. Setting this on a synchronous step is silently
    /// ignored by Dataverse; Flowline will emit a warning during <c>flowline push</c>.
    /// </remarks>
    public bool DeleteJobOnSuccess { get; set; } = false;
}

/// <summary>
/// Controls which user's identity the plugin runs under, affecting <c>context.UserId</c>
/// inside <c>Execute</c>.
/// </summary>
public enum ExecuteAs
{
    /// <summary>
    /// The plugin runs as the user who directly triggered the Dataverse operation — the
    /// API caller or the user who submitted the form. This is the default and is correct
    /// for the vast majority of plugins.
    /// </summary>
    CallingUser = 0,

    /// <summary>
    /// The plugin runs as the user who originally started the chain of events, even if the
    /// immediate trigger was a service account (e.g. a Power Automate flow).
    /// Use this when a flow or workflow triggers your plugin and you need the human user's
    /// identity for auditing or row-level security checks.
    /// </summary>
    InitiatingUser = 1,
}
