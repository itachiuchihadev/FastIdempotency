using System.Collections.Concurrent;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastIdempotency.AspNetCore;
using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Hashing;
using FastIdempotency.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastIdempotency.Benchmarks;

/// <summary>
/// End-to-end middleware benchmarks measuring HTTP pipeline overhead, cache hit replay speed,
/// first-time lock acquisition, and payload mismatch rejection performance.
/// </summary>
[SimpleJob]
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class MiddlewarePipelineBenchmarks
{
    private IdempotencyMiddleware _middleware = null!;
    private IServiceProvider _serviceProvider = null!;
    private readonly byte[] _requestBodyBytes = Encoding.UTF8.GetBytes("""{"orderId":"ord-123","amount":99.99,"currency":"USD"}""");
    private readonly byte[] _mismatchedBodyBytes = Encoding.UTF8.GetBytes("""{"orderId":"ord-123","amount":199.99,"currency":"EUR"}""");
    private readonly byte[] _responseBodyBytes = Encoding.UTF8.GetBytes("""{"status":"created","orderId":"ord-123"}""");

    private const string CachedKey = "cached-key-001";
    private const string MismatchKey = "mismatch-key-001";
    private const string MissKey = "miss-key-001";

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        var options = new IdempotencyOptions();
        var hasher = new XxHash3RequestHasher();
        var store = new InMemoryBenchmarkStore();

        // Seed a cached response for CacheHit
        var cachedHash = hasher.ComputeHash("POST", "/api/orders", _requestBodyBytes);
        store.SeedRecord($"idemp:{CachedKey}", new IdempotencyRecord
        {
            Key = $"idemp:{CachedKey}",
            RequestHash = cachedHash,
            Status = IdempotencyStatus.Completed,
            Response = new IdempotentResponse
            {
                StatusCode = 200,
                Body = _responseBodyBytes,
                ContentType = "application/json",
                Headers = new Dictionary<string, string[]>
                {
                    ["Content-Type"] = ["application/json"]
                }
            },
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        });

        // Seed record for mismatch benchmark
        store.SeedRecord($"idemp:{MismatchKey}", new IdempotencyRecord
        {
            Key = $"idemp:{MismatchKey}",
            RequestHash = cachedHash, // Matches original body, but benchmark will send mismatched body
            Status = IdempotencyStatus.Completed,
            Response = new IdempotentResponse
            {
                StatusCode = 200,
                Body = _responseBodyBytes,
                ContentType = "application/json",
                Headers = new Dictionary<string, string[]>
                {
                    ["Content-Type"] = ["application/json"]
                }
            },
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        });

        services.AddSingleton(options);
        services.AddSingleton<IRequestHasher>(hasher);
        services.AddSingleton<IIdempotencyStore>(store);

        _serviceProvider = services.BuildServiceProvider();

        // Pipeline with a downstream delegate simulating controller execution
        RequestDelegate next = async ctx =>
        {
            ctx.Response.StatusCode = 201;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(_responseBodyBytes, 0, _responseBodyBytes.Length);
        };

        _middleware = new IdempotencyMiddleware(next, NullLogger<IdempotencyMiddleware>.Instance);
    }

    [Benchmark(Description = "Passthrough (No Idempotency-Key Header)")]
    public async Task Passthrough_NoHeader()
    {
        var context = CreateContext(headerKey: null, _requestBodyBytes);
        await _middleware.InvokeAsync(context);
    }

    [Benchmark(Description = "Cache Hit (Short-Circuit & Replay Cached Response)")]
    public async Task CacheHit_Replayed()
    {
        var context = CreateContext(CachedKey, _requestBodyBytes);
        await _middleware.InvokeAsync(context);
    }

    [Benchmark(Description = "Payload Mismatch (Rejection with 422 Unprocessable)")]
    public async Task PayloadMismatch_Rejected()
    {
        var context = CreateContext(MismatchKey, _mismatchedBodyBytes);
        await _middleware.InvokeAsync(context);
    }

    [Benchmark(Description = "Cache Miss (First Execution + Lock + Buffer + Store)")]
    public async Task CacheMiss_FirstExecution()
    {
        var context = CreateContext(MissKey, _requestBodyBytes);
        await _middleware.InvokeAsync(context);
    }

    private DefaultHttpContext CreateContext(string? headerKey, byte[] body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = _serviceProvider
        };

        context.Request.Method = "POST";
        context.Request.Path = "/api/orders";
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;

        if (headerKey is not null)
        {
            context.Request.Headers["Idempotency-Key"] = headerKey;
        }

        context.Response.Body = Stream.Null;
        return context;
    }

    private sealed class InMemoryBenchmarkStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, IdempotencyRecord> _store = new();

        public void SeedRecord(string key, IdempotencyRecord record) => _store[key] = record;

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(key, out var record);
            return Task.FromResult(record);
        }

        public Task<bool> TryAcquireLockAsync(
            string key,
            ulong requestHash,
            string lockOwner,
            IdempotencyOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task SaveCompletedAsync(
            string key,
            IdempotentResponse response,
            IdempotencyOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ReleaseLockAsync(string key, string lockOwner, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
