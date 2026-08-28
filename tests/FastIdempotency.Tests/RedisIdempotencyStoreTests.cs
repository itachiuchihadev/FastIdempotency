using DotNet.Testcontainers.Builders;
using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Models;
using FastIdempotency.Storage.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace FastIdempotency.Tests;

/// <summary>
/// Integration tests for the Redis idempotency store.
/// Spins up a real Redis Docker container via Testcontainers — no mocking.
/// </summary>
public sealed class RedisIdempotencyStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine")
        .Build();

    private IIdempotencyStore _store = null!;
    private IdempotencyOptions _options = null!;

    // ── Setup & Teardown ────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var mux = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        _store = new RedisIdempotencyStore(mux);
        _options = new IdempotencyOptions
        {
            RetentionWindow = TimeSpan.FromMinutes(5),
            LockTimeout = TimeSpan.FromSeconds(10),
            EnableSmartPolling = false
        };
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "GetAsync returns null for an unknown key")]
    public async Task GetAsync_UnknownKey_ReturnsNull()
    {
        var result = await _store.GetAsync("idmp:nonexistent");
        Assert.Null(result);
    }

    [Fact(DisplayName = "TryAcquireLockAsync succeeds on first call")]
    public async Task TryAcquireLock_FirstCall_ReturnsTrue()
    {
        var key = $"idmp:test-lock-{Guid.NewGuid():N}";
        var acquired = await _store.TryAcquireLockAsync(key, 12345UL, "owner-1", _options);
        Assert.True(acquired);
    }

    [Fact(DisplayName = "TryAcquireLockAsync fails when lock is already held")]
    public async Task TryAcquireLock_AlreadyLocked_ReturnsFalse()
    {
        var key = $"idmp:test-lock-{Guid.NewGuid():N}";

        var first = await _store.TryAcquireLockAsync(key, 12345UL, "owner-1", _options);
        var second = await _store.TryAcquireLockAsync(key, 12345UL, "owner-2", _options);

        Assert.True(first, "First acquisition should succeed");
        Assert.False(second, "Second acquisition should fail — lock already held");
    }

    [Fact(DisplayName = "SaveCompletedAsync transitions key to Completed status")]
    public async Task SaveCompleted_SetsStatusToCompleted()
    {
        var key = $"idmp:test-complete-{Guid.NewGuid():N}";
        await _store.TryAcquireLockAsync(key, 99UL, "owner-x", _options);

        var response = new IdempotentResponse
        {
            StatusCode = 201,
            Body = """{"orderId":"ABC123"}"""u8.ToArray(),
            Headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
            ContentType = "application/json"
        };

        await _store.SaveCompletedAsync(key, response, _options);

        var record = await _store.GetAsync(key);
        Assert.NotNull(record);
        Assert.Equal(IdempotencyStatus.Completed, record.Status);
        Assert.NotNull(record.Response);
        Assert.Equal(201, record.Response.StatusCode);
    }

    [Fact(DisplayName = "GetAsync returns completed response body correctly")]
    public async Task GetAsync_CompletedKey_ReturnsCachedResponse()
    {
        var key = $"idmp:test-body-{Guid.NewGuid():N}";
        var expectedBody = """{"orderId":"XYZ789","status":"created"}"""u8.ToArray();

        await _store.TryAcquireLockAsync(key, 42UL, "owner-y", _options);
        await _store.SaveCompletedAsync(key, new IdempotentResponse
        {
            StatusCode = 200,
            Body = expectedBody,
            Headers = [],
            ContentType = "application/json"
        }, _options);

        var record = await _store.GetAsync(key);

        Assert.NotNull(record?.Response);
        Assert.Equal(expectedBody, record.Response.Body);
    }

    [Fact(DisplayName = "ReleaseLockAsync only releases if caller is the owner")]
    public async Task ReleaseLock_NonOwner_DoesNotRelease()
    {
        var key = $"idmp:test-release-{Guid.NewGuid():N}";

        await _store.TryAcquireLockAsync(key, 1UL, "real-owner", _options);

        // Impostor tries to release
        await _store.ReleaseLockAsync(key, "impostor-owner");

        // Key should still exist (impostor couldn't release it)
        var record = await _store.GetAsync(key);
        Assert.NotNull(record);
        Assert.Equal(IdempotencyStatus.InFlight, record.Status);
    }

    [Fact(DisplayName = "Concurrent duplicate requests — only one lock acquisition succeeds")]
    public async Task ConcurrentDuplicates_OnlyOneLockAcquired()
    {
        var key = $"idmp:test-concurrent-{Guid.NewGuid():N}";
        const int concurrency = 20;
        var results = new int[concurrency];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, concurrency),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (i, ct) =>
            {
                var acquired = await _store.TryAcquireLockAsync(
                    key, 999UL, $"owner-{i}", _options, ct);
                results[i] = acquired ? 1 : 0;
            });

        // Exactly ONE server should have won the lock
        Assert.Equal(1, results.Sum());
    }
}
