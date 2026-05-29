namespace Flowline.Attributes
{
    /// <summary>
    /// Identifies the execution stage and mode of a plugin step.
    /// Used as the <c>stage</c> parameter in <see cref="HandlesAttribute"/>.
    /// </summary>
    /// <remarks>
    /// Integer values (0–3) do not match the internal <c>ProcessingStage</c> ints (10/20/40).
    /// <c>PluginAssemblyReader</c> maps each value explicitly via a switch statement.
    /// <para>
    /// <c>PostOperationAsync</c> folds execution mode into the stage value —
    /// it maps to <c>ProcessingStage.PostOperation (40)</c> + <c>ProcessingMode.Asynchronous (1)</c>.
    /// </para>
    /// </remarks>
    public enum Stage
    {
        /// <summary>
        /// Runs before the platform validation step, before the main database transaction.
        /// Synchronous. Suitable for input validation that should reject the operation early.
        /// </summary>
        PreValidation = 0,

        /// <summary>
        /// Runs inside the main database transaction, before the core platform operation.
        /// Synchronous. Suitable for modifying input or preparing related records.
        /// </summary>
        PreOperation = 1,

        /// <summary>
        /// Runs inside the main database transaction, after the core platform operation.
        /// Synchronous. Suitable for side effects that must be complete within the same transaction.
        /// </summary>
        PostOperation = 2,

        /// <summary>
        /// Runs after the transaction commits, in a background queue.
        /// Asynchronous. Use <see cref="StepAttribute.DeleteJobOnSuccess"/> to control job cleanup.
        /// </summary>
        PostOperationAsync = 3,
    }
}
