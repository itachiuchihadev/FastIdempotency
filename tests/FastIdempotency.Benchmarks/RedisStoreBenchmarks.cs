using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastIdempotency.AspNetCore;
using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Hashing;
using FastIdempotency.Core.Models;
using FastIdempotency.Storage.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace FastIdempotency.Benchmarks;

/// <summary>
/// Benchmarks for RedisIdempotencyStore operations and end-to-end middleware execution with a real Redis instance.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class RedisStoreBenchmarks
{
    private ConnectionMultiplexer _redis = null!;
    private RedisIdempotencyStore _store = null!;
    private IdempotencyOptions _options = null!;
    private IdempotencyMiddleware _middleware = null!;
    private IServiceProvider _serviceProvider = null!;

    private readonly byte[] _requestBodyBytes = Encoding.UTF8.GetBytes("""{"orderId":"ord-123","amount":99.99,"currency":"USD"}""");
    private readonly byte[] _responseBodyBytes = Encoding.UTF8.GetBytes("""{"status":"created","orderId":"ord-123"}""");

    private const string CachedKey = "bench:redis:cached-001";
    private const string ReleaseKey = "bench:redis:release-001";
    private const string LockKeyPrefix = "bench:redis:lock-";
    private const string SaveKeyPrefix = "bench:redis:save-";
    private const string MissKeyPrefix = "bench:redis:miss-";
    private long _counter;

    [GlobalSetup]
    public void Setup()
    {
        var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "localhost:6379";
        _redis = ConnectionMultiplexer.Connect(redisConnectionString);
        _store = new RedisIdempotencyStore(_redis);

        _options = new IdempotencyOptions
        {
            RetentionWindow = TimeSpan.FromMinutes(30),
            LockTimeout = TimeSpan.FromSeconds(30),
            EnableSmartPolling = false
        };

        var hasher = new XxHash3RequestHasher();
        var cachedHash = hasher.ComputeHash("POST", "/api/orders", _requestBodyBytes);

        var cachedResponse = new IdempotentResponse
        {
            StatusCode = 200,
            Body = _responseBodyBytes,
            ContentType = "application/json",
            Headers = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"]
            }
        };

        // Seed a cached record in Redis for CacheHit benchmark
        _store.TryAcquireLockAsync(CachedKey, cachedHash, "seed-owner", _options).GetAwaiter().GetResult();
        _store.SaveCompletedAsync(CachedKey, cachedResponse, _options).GetAwaiter().GetResult();

        // Seed record for ReleaseLock benchmark
        _store.TryAcquireLockAsync(ReleaseKey, cachedHash, "release-owner", _options).GetAwaiter().GetResult();

        // Setup DI & Middleware
        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddSingleton<IRequestHasher>(hasher);
        services.AddSingleton<IIdempotencyStore>(_store);
        _serviceProvider = services.BuildServiceProvider();

        RequestDelegate next = async ctx =>
        {
            ctx.Response.StatusCode = 201;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(_responseBodyBytes, 0, _responseBodyBytes.Length);
        };

        _middleware = new IdempotencyMiddleware(next, NullLogger<IdempotencyMiddleware>.Instance);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _redis.Dispose();
    }

    [Benchmark(Description = "Redis Store: TryAcquireLockAsync (Lua atomic lock)")]
    public async Task<bool> Store_TryAcquireLock()
    {
        var key = $"{LockKeyPrefix}{Interlocked.Increment(ref _counter)}";
        return await _store.TryAcquireLockAsync(key, 99999UL, "bench-owner", _options);
    }

    [Benchmark(Description = "Redis Store: GetAsync (Cache Hit fetch)")]
    public async Task<IdempotencyRecord?> Store_GetCacheHit()
    {
        return await _store.GetAsync(CachedKey);
    }

    [Benchmark(Description = "Redis Store: SaveCompletedAsync (Batch HashSet + Expire)")]
    public async Task Store_SaveCompleted()
    {
        var key = $"{SaveKeyPrefix}{Interlocked.Increment(ref _counter)}";
        var response = new IdempotentResponse
        {
            StatusCode = 201,
            Body = _responseBodyBytes,
            ContentType = "application/json",
            Headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] }
        };
        await _store.SaveCompletedAsync(key, response, _options);
    }

    [Benchmark(Description = "Pipeline: Cache Hit with Redis (Replay response)")]
    public async Task Pipeline_CacheHit_WithRedis()
    {
        var context = CreateContext(CachedKey, _requestBodyBytes);
        await _middleware.InvokeAsync(context);
    }

    [Benchmark(Description = "Pipeline: Cache Miss with Redis (Lock + Execute + Cache)")]
    public async Task Pipeline_CacheMiss_WithRedis()
    {
        var key = $"{MissKeyPrefix}{Interlocked.Increment(ref _counter)}";
        var context = CreateContext(key, _requestBodyBytes);
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
}
