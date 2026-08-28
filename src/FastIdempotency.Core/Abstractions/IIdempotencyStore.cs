using FastIdempotency.Core.Models;

namespace FastIdempotency.Core.Abstractions;

/// <summary>
/// Abstraction over the backend storage provider (Redis / PostgreSQL).
/// Implementations must be thread-safe and support distributed concurrent access.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Attempts to retrieve an existing idempotency record for the given key.
    /// Returns null if the key has never been seen before.
    /// </summary>
    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to atomically acquire an exclusive distributed lock for the given key.
    /// Uses SET NX EX in Redis or SELECT FOR UPDATE in Postgres.
    /// </summary>
    /// <param name="key">The full idempotency key (prefix + user key).</param>
    /// <param name="requestHash">Hash of the incoming request for mismatch detection on future calls.</param>
    /// <param name="lockOwner">A unique token identifying this server instance as the lock owner.</param>
    /// <param name="options">Idempotency options (lock TTL, retention window).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the lock was acquired; false if another instance already holds it.</returns>
    Task<bool> TryAcquireLockAsync(
        string key,
        ulong requestHash,
        string lockOwner,
        IdempotencyOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the completed response, transitions the record to Completed status,
    /// and releases the lock atomically.
    /// </summary>
    Task SaveCompletedAsync(
        string key,
        IdempotentResponse response,
        IdempotencyOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the distributed lock for the given key.
    /// Only releases if the caller is the current lock owner (prevents releasing another instance's lock).
    /// </summary>
    Task ReleaseLockAsync(string key, string lockOwner, CancellationToken cancellationToken = default);
}
