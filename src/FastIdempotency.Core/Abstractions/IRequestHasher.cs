namespace FastIdempotency.Core.Abstractions;

/// <summary>
/// Computes a fast, non-cryptographic hash of an HTTP request
/// to detect payload mismatches on idempotency key reuse.
/// </summary>
public interface IRequestHasher
{
    /// <summary>
    /// Computes a 64-bit hash over the HTTP method, path, and raw body bytes.
    /// Implementations must use zero-allocation techniques (Span&lt;byte&gt;) for high throughput.
    /// </summary>
    /// <param name="httpMethod">HTTP method (e.g. "POST").</param>
    /// <param name="path">Request path including query string (e.g. "/api/checkout?v=1").</param>
    /// <param name="body">Raw request body as a read-only byte span (zero-copy).</param>
    /// <returns>A 64-bit hash value uniquely fingerprinting this request.</returns>
    ulong ComputeHash(string httpMethod, string path, ReadOnlySpan<byte> body);
}
