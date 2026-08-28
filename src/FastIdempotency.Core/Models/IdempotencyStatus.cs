namespace FastIdempotency.Core.Models;

/// <summary>
/// Represents the current state of an idempotency key in the store.
/// </summary>
public enum IdempotencyStatus : byte
{
    /// <summary>
    /// Key does not exist yet — first-time request.
    /// </summary>
    NotFound = 0,

    /// <summary>
    /// Key is locked — a request is currently being processed.
    /// </summary>
    InFlight = 1,

    /// <summary>
    /// Key is completed — a cached response is available.
    /// </summary>
    Completed = 2
}
