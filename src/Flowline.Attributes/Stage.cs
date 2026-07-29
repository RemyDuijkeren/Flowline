namespace Flowline.Attributes
{
    /// <summary>
    /// Identifies the execution stage and mode of a plugin step.
    /// Used as the <c>stage</c> parameter in <see cref="HandlesAttribute"/>.
    /// </summary>
    /// <remarks>
    /// Each value carries both the pipeline stage and the execution mode, which is why
    /// <c>[Handles]</c> needs no separate asynchronous flag.
    /// <para>
    /// <see cref="PostOperationAsync"/> is not a stage of its own — it is
    /// <see cref="PostOperation"/> running in the background, after the transaction commits.
    /// Dataverse allows asynchronous execution at PostOperation only; asking for it at any other
    /// stage is an error Flowline reports during <c>flowline push</c>.
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
