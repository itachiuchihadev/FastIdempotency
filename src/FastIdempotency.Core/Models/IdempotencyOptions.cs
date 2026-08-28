namespace FastIdempotency.Core.Models;

/// <summary>
/// Configuration options for the FastIdempotency middleware.
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>
    /// The HTTP header name to look for the idempotency key.
    /// Default: "Idempotency-Key" (IETF RFC draft standard).
    /// </summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>
    /// How long a completed idempotency record is retained in the store.
    /// After expiry, the same key can be reused for a fresh request.
    /// Default: 24 hours.
    /// </summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long to hold the distributed lock for an in-flight request
    /// before it auto-expires (prevents zombie locks on server crash).
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, a duplicate request arriving while the original is in-flight
    /// will wait (poll) for the result rather than immediately returning 409 Conflict.
    /// Default: true.
    /// </summary>
    public bool EnableSmartPolling { get; set; } = true;

    /// <summary>
    /// Maximum time to wait when smart polling for an in-flight result.
    /// Only used when <see cref="EnableSmartPolling"/> is true.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan SmartPollTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Polling interval when checking if an in-flight request has completed.
    /// Default: 200ms.
    /// </summary>
    public TimeSpan SmartPollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// HTTP methods that should be subject to idempotency checking.
    /// Default: POST, PUT, PATCH (safe methods GET/DELETE are inherently idempotent).
    /// </summary>
    public HashSet<string> ApplicableMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "PATCH"
    };

    /// <summary>
    /// Optional prefix for all idempotency keys stored in the backend.
    /// Useful for multi-tenant isolation. Default: "idmp:".
    /// </summary>
    public string KeyPrefix { get; set; } = "idmp:";
}
