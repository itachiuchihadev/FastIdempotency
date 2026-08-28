namespace FastIdempotency.Core.Models;

/// <summary>
/// Represents a completed HTTP response that has been captured and stored for replay.
/// </summary>
public sealed class IdempotentResponse
{
    /// <summary>HTTP status code (e.g. 200, 201, 400).</summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Captured response headers. Excludes transfer-encoding and other hop-by-hop headers.
    /// </summary>
    public required Dictionary<string, string[]> Headers { get; init; }

    /// <summary>
    /// The raw response body bytes. Stored using pooled buffers to avoid LOH pressure.
    /// </summary>
    public required byte[] Body { get; init; }

    /// <summary>Content-Type of the response (e.g. application/json; charset=utf-8).</summary>
    public string? ContentType { get; init; }
}
