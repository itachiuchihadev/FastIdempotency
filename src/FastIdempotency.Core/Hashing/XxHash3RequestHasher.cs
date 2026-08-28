using System.IO.Hashing;
using System.Text;
using FastIdempotency.Core.Abstractions;

namespace FastIdempotency.Core.Hashing;

/// <summary>
/// High-performance, zero-allocation request hasher using XxHash3.
///
/// WHY XxHash3 instead of SHA256:
///   - XxHash3 uses SIMD (AVX2/SSE) vectorized instructions, running at 10+ GB/sec.
///   - SHA256 runs at ~400 MB/sec and is designed for cryptographic collision resistance.
///   - Idempotency does NOT require cryptographic security — only fast, collision-resistant
///     fingerprinting to detect payload mismatches. XxHash3 is 10-15x faster.
///   - XxHash3 accepts ReadOnlySpan&lt;byte&gt; directly — zero heap allocations on hot path.
/// </summary>
public sealed class XxHash3RequestHasher : IRequestHasher
{
    // Separator bytes used between hash segments (avoids "POST/foo" == "POS T/foo" collision).
    private static ReadOnlySpan<byte> Separator => "\x1F"u8; // ASCII Unit Separator

    /// <inheritdoc />
    public ulong ComputeHash(string httpMethod, string path, ReadOnlySpan<byte> body)
    {
        // We chain three XxHash3 computations over method + path + body.
        // All Append() calls operate on ReadOnlySpan<byte> — no heap allocation.

        var hasher = new XxHash3();

        // 1. Hash the HTTP method (e.g. "POST" = 4 bytes max — fits on stack)
        Span<byte> methodBytes = stackalloc byte[Encoding.UTF8.GetMaxByteCount(httpMethod.Length)];
        int methodLen = Encoding.UTF8.GetBytes(httpMethod, methodBytes);
        hasher.Append(methodBytes[..methodLen]);
        hasher.Append(Separator);

        // 2. Hash the request path (stack-alloc for paths up to 512 chars, heap-alloc otherwise)
        int maxPathBytes = Encoding.UTF8.GetMaxByteCount(path.Length);
        byte[]? rentedPathBuffer = null;
        Span<byte> pathBytes = maxPathBytes <= 1024
            ? stackalloc byte[maxPathBytes]
            : (rentedPathBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(maxPathBytes));

        try
        {
            int pathLen = Encoding.UTF8.GetBytes(path, pathBytes);
            hasher.Append(pathBytes[..pathLen]);
            hasher.Append(Separator);
        }
        finally
        {
            if (rentedPathBuffer is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedPathBuffer);
        }

        // 3. Hash the request body (already a ReadOnlySpan<byte> — zero copy)
        hasher.Append(body);

        return hasher.GetCurrentHashAsUInt64();
    }
}
