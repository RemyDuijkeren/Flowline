using System;

namespace Flowline.Attributes;

/// <summary>Controls which custom processing step types are allowed on this Custom API.</summary>
public enum CustomApiStepType
{
    /// <summary>No custom processing steps allowed.</summary>
    None        = 0,
    /// <summary>Only asynchronous custom processing steps allowed.</summary>
    AsyncOnly   = 1,
    /// <summary>Both synchronous and asynchronous custom processing steps allowed.</summary>
    SyncAndAsync = 2,
}
