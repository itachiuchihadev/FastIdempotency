namespace FastIdempotency.Core.Models;

/// <summary>
/// Represents a stored idempotency record (either in-flight or completed).
/// </summary>
public sealed class IdempotencyRecord
{
    /// <summary>The idempotency key (from request header).</summary>
    public required string Key { get; init; }

    /// <summary>
    /// XxHash3 hash of (HttpMethod + Path + RequestBody) — used to detect payload mismatches.
    /// </summary>
    public required ulong RequestHash { get; init; }

    /// <summary>Current processing status of this key.</summary>
    public required IdempotencyStatus Status { get; init; }

    /// <summary>The lock owner token — used to prevent another server instance from releasing a lock it doesn't own.</summary>
    public string? LockOwner { get; init; }

    /// <summary>Completed response (only populated when Status == Completed).</summary>
    public IdempotentResponse? Response { get; init; }

    /// <summary>When this record was first created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this record expires and should be cleaned up.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
