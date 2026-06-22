using System;

namespace Flowline.Attributes
{
    /// <summary>
    /// Explicitly declares the Dataverse message and stage for this plugin step, overriding the
    /// class-name naming convention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flowline normally derives the message (Create, Update, Delete …) and pipeline stage
    /// (PreValidation, PreOperation, PostOperation, Async) from the class name:
    /// <c>AccountPreUpdatePlugin</c> → Update, PreOperation. When a class name does not follow
    /// this convention (brownfield code, third-party bases, generated names), apply
    /// <c>[Handles]</c> to declare the values explicitly.
    /// </para>
    /// <para>
    /// <b>This attribute is an escape hatch.</b> Prefer renaming the class to follow the convention —
    /// the name then serves as self-documenting intent and the attribute is no longer needed.
    /// </para>
    /// <para>
    /// Use the <c>Message</c> enum overload for built-in Dataverse messages:
    /// </para>
    /// <code>
    /// [Step("account")]
    /// [Handles(Message.Update, Stage.PreOperation)]
    /// public class AccountPlugin : IPlugin { ... }
    /// </code>
    /// <para>
    /// Use the <c>string</c> overload for Custom API messages:
    /// </para>
    /// <code>
    /// [Step("account")]
    /// [Handles("mynamespace_MyAction", Stage.PostOperation)]
    /// public class AccountPlugin : IPlugin { ... }
    /// </code>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class HandlesAttribute : Attribute
    {
        /// <summary>
        /// Declares message and stage for a built-in Dataverse message.
        /// </summary>
        /// <param name="on">The Dataverse message this step fires on.</param>
        /// <param name="stage">The pipeline stage and execution mode.</param>
        public HandlesAttribute(Message on, Stage stage)
        {
            On = on.ToString();
            Stage = stage;
            IsCustomMessage = false;
        }

        /// <summary>
        /// Declares message and stage for a Custom API message.
        /// </summary>
        /// <param name="on">
        /// The unique name of the Custom API message, e.g. <c>"mynamespace_MyAction"</c>.
        /// </param>
        /// <param name="stage">The pipeline stage and execution mode.</param>
        public HandlesAttribute(string on, Stage stage)
        {
            if (on == null) throw new ArgumentNullException(nameof(on));
            On = on;
            Stage = stage;
            IsCustomMessage = true;
        }

        /// <summary>
        /// The message name this step fires on.
        /// For built-in messages this is the <see cref="Message"/> member name (e.g. <c>"Update"</c>).
        /// For Custom API messages this is the unique name supplied at decoration time.
        /// </summary>
        public string On { get; }

        /// <summary>
        /// The pipeline stage and execution mode.
        /// </summary>
        public Stage Stage { get; }

        /// <summary>
        /// <see langword="true"/> when this instance was constructed with a <c>string</c> message
        /// name (Custom API); <see langword="false"/> when constructed with a <see cref="Message"/>
        /// enum value.
        /// </summary>
        public bool IsCustomMessage { get; }
    }
}
