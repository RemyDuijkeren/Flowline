using System;

namespace Flowline.Attributes;

/// <summary>
/// Controls whether third-party developers can extend a Custom API by registering their own
/// plugin steps on it, and if so, which processing modes they may use.
/// </summary>
/// <remarks>
/// Set via <see cref="CustomApiAttribute.AllowedStepType"/>. The default is <see cref="None"/>,
/// which means only your implementation runs — no external extensions possible. Change this only
/// if you are intentionally building an extensible API that other developers can hook into.
/// </remarks>
public enum AllowedStepType
{
    /// <summary>
    /// No custom processing steps allowed on this API (default).
    /// Only your implementation runs when the API is called.
    /// </summary>
    None         = 0,

    /// <summary>
    /// Third-party developers may register asynchronous processing steps only.
    /// Their steps run after your implementation completes, in the background.
    /// </summary>
    AsyncOnly    = 1,

    /// <summary>
    /// Third-party developers may register both synchronous and asynchronous processing steps.
    /// Synchronous steps run within the same transaction as your implementation.
    /// </summary>
    SyncAndAsync = 2,
}
