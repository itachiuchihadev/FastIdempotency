using System.Reflection;
using System.Text.Json;
using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Models;
using StackExchange.Redis;

namespace FastIdempotency.Storage.Redis;

/// <summary>
/// Redis-backed idempotency store.
///
/// Uses atomic Lua scripts (sent as raw strings to ScriptEvaluate) to guarantee
/// race-free distributed locking across multiple server instances.
///
/// Data layout per key (stored as Redis Hash):
///   status   : "InFlight" | "Completed"
///   owner    : lock owner token (unique per server instance + request)
///   hash     : ulong request hash (string representation)
///   code     : HTTP status code (only when Completed)
///   body     : base64-encoded response body bytes (only when Completed)
///   headers  : JSON-serialized response headers (only when Completed)
///   ctype    : Content-Type header value (only when Completed)
/// </summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IDatabase _db;

    // Lua scripts as strings — executed atomically on the Redis server
    private readonly string _acquireLuaScript;
    private readonly string _releaseLuaScript;

    public RedisIdempotencyStore(IConnectionMultiplexer connection)
    {
        _db = connection.GetDatabase();
        _acquireLuaScript = LoadEmbeddedScript("acquire_lock.lua");
        _releaseLuaScript = LoadEmbeddedScript("release_lock.lua");
    }

    /// <inheritdoc />
    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var fields = await _db.HashGetAllAsync(key).ConfigureAwait(false);
        if (fields.Length == 0) return null;

        var dict = fields.ToDictionary(
            f => f.Name.ToString(),
            f => f.Value.ToString());

        var status = dict.GetValueOrDefault("status") == "Completed"
            ? IdempotencyStatus.Completed
            : IdempotencyStatus.InFlight;

        IdempotentResponse? response = null;
        if (status == IdempotencyStatus.Completed && dict.TryGetValue("body", out var bodyB64))
        {
            var headers = dict.TryGetValue("headers", out var hdrsJson)
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(hdrsJson) ?? []
                : new Dictionary<string, string[]>();

            response = new IdempotentResponse
            {
                StatusCode = int.Parse(dict.GetValueOrDefault("code", "200")),
                Body = Convert.FromBase64String(bodyB64),
                Headers = headers,
                ContentType = dict.GetValueOrDefault("ctype")
            };
        }

        return new IdempotencyRecord
        {
            Key = key,
            RequestHash = ulong.Parse(dict.GetValueOrDefault("hash", "0")),
            Status = status,
            LockOwner = dict.GetValueOrDefault("owner"),
            Response = response,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) // Redis TTL is authoritative
        };
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireLockAsync(
        string key,
        ulong requestHash,
        string lockOwner,
        IdempotencyOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await _db.ScriptEvaluateAsync(
            _acquireLuaScript,
            keys: [new RedisKey(key)],
            values:
            [
                new RedisValue(lockOwner),
                new RedisValue(((long)options.LockTimeout.TotalMilliseconds).ToString()),

                new RedisValue(requestHash.ToString())
            ]).ConfigureAwait(false);

        return (long)result == 1L;
    }

    /// <inheritdoc />
    public async Task SaveCompletedAsync(
        string key,
        IdempotentResponse response,
        IdempotencyOptions options,
        CancellationToken cancellationToken = default)
    {
        var headersJson = JsonSerializer.Serialize(response.Headers);
        var bodyBase64 = Convert.ToBase64String(response.Body);

        var batch = _db.CreateBatch();
        _ = batch.HashSetAsync(key,
        [
            new HashEntry("status",  "Completed"),
            new HashEntry("code",    response.StatusCode.ToString()),
            new HashEntry("body",    bodyBase64),
            new HashEntry("headers", headersJson),
            new HashEntry("ctype",   response.ContentType ?? string.Empty),
        ]);
        _ = batch.KeyExpireAsync(key, options.RetentionWindow);
        batch.Execute();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseLockAsync(string key, string lockOwner, CancellationToken cancellationToken = default)
    {
        await _db.ScriptEvaluateAsync(
            _releaseLuaScript,
            keys: [new RedisKey(key)],
            values: [new RedisValue(lockOwner)]).ConfigureAwait(false);
    }

    // ── Private Helpers ──────────────────────────────────────────────────────

    private static string LoadEmbeddedScript(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(filename, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded Lua script '{filename}' not found. " +
                $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
