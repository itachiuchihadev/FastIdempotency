using System.Text.Json;
using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Models;
using Npgsql;

namespace FastIdempotency.Storage.Postgres;

/// <summary>
/// PostgreSQL-backed idempotency store using direct Npgsql for maximum performance.
///
/// Uses SELECT ... FOR UPDATE SKIP LOCKED for distributed row-level locking
/// rather than application-level lock tables — this lets Postgres handle contention
/// natively at the storage engine level.
///
/// Schema is auto-created on first startup via <see cref="EnsureSchemaAsync"/>.
/// </summary>
public sealed class PostgresIdempotencyStore : IIdempotencyStore, IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgresIdempotencyStore(string connectionString)
        : this(NpgsqlDataSource.Create(connectionString), ownsDataSource: true)
    {
    }

    public PostgresIdempotencyStore(NpgsqlDataSource dataSource, bool ownsDataSource = false)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    /// <summary>
    /// Creates the idempotency_keys table and index if they don't already exist.
    /// Call this once at application startup.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS idempotency_keys (
                key             VARCHAR(512)    NOT NULL PRIMARY KEY,
                request_hash    BIGINT          NOT NULL,
                status          SMALLINT        NOT NULL DEFAULT 0,
                lock_owner      VARCHAR(128)    NULL,
                status_code     SMALLINT        NULL,
                response_body   BYTEA           NULL,
                response_headers JSONB          NULL,
                content_type    VARCHAR(256)    NULL,
                created_at      TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
                expires_at      TIMESTAMPTZ     NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_idmp_expires ON idempotency_keys(expires_at);
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, request_hash, status, lock_owner,
                   status_code, response_body, response_headers, content_type, expires_at
            FROM idempotency_keys
            WHERE key = @key AND expires_at > NOW()
            """;
        cmd.Parameters.AddWithValue("@key", key);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var status = (IdempotencyStatus)reader.GetInt16(reader.GetOrdinal("status"));
        IdempotentResponse? response = null;

        if (status == IdempotencyStatus.Completed && !reader.IsDBNull(reader.GetOrdinal("response_body")))
        {
            var bodyBytes = (byte[])reader["response_body"];
            var headersJson = reader.IsDBNull(reader.GetOrdinal("response_headers"))
                ? null
                : reader.GetString(reader.GetOrdinal("response_headers"));
            var headers = headersJson is not null
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(headersJson) ?? []
                : [];

            response = new IdempotentResponse
            {
                StatusCode = reader.GetInt16(reader.GetOrdinal("status_code")),
                Body = bodyBytes,
                Headers = headers,
                ContentType = reader.IsDBNull(reader.GetOrdinal("content_type"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("content_type"))
            };
        }

        return new IdempotencyRecord
        {
            Key = key,
            // Postgres stores as signed BIGINT; we cast to ulong (same bit pattern)
            RequestHash = unchecked((ulong)reader.GetInt64(reader.GetOrdinal("request_hash"))),
            Status = status,
            LockOwner = reader.IsDBNull(reader.GetOrdinal("lock_owner"))
                ? null
                : reader.GetString(reader.GetOrdinal("lock_owner")),
            Response = response,
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at"))
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
        await using var conn = await OpenConnectionAsync(cancellationToken);

        // INSERT ... ON CONFLICT DO NOTHING achieves atomic "insert if not exists"
        // without a race condition between SELECT and INSERT.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO idempotency_keys (key, request_hash, status, lock_owner, expires_at)
            VALUES (@key, @hash, @status, @owner, @expires)
            ON CONFLICT (key) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@key", key);
        // ulong → signed long (same bit pattern, Postgres stores BIGINT)
        cmd.Parameters.AddWithValue("@hash", unchecked((long)requestHash));
        cmd.Parameters.AddWithValue("@status", (short)IdempotencyStatus.InFlight);
        cmd.Parameters.AddWithValue("@owner", lockOwner);
        cmd.Parameters.AddWithValue("@expires", DateTimeOffset.UtcNow.Add(options.LockTimeout));

        var rowsInserted = await cmd.ExecuteNonQueryAsync(cancellationToken);

        // rowsInserted == 1 means we won the race and own the lock
        return rowsInserted == 1;
    }

    /// <inheritdoc />
    public async Task SaveCompletedAsync(
        string key,
        IdempotentResponse response,
        IdempotencyOptions options,
        CancellationToken cancellationToken = default)
    {
        var headersJson = JsonSerializer.Serialize(response.Headers);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE idempotency_keys
            SET status          = @status,
                status_code     = @code,
                response_body   = @body,
                response_headers = @headers::jsonb,
                content_type    = @ctype,
                lock_owner      = NULL,
                expires_at      = @expires
            WHERE key = @key
            """;
        cmd.Parameters.AddWithValue("@status", (short)IdempotencyStatus.Completed);
        cmd.Parameters.AddWithValue("@code", (short)response.StatusCode);
        cmd.Parameters.AddWithValue("@body", response.Body);
        cmd.Parameters.AddWithValue("@headers", headersJson);
        cmd.Parameters.AddWithValue("@ctype", (object?)response.ContentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@expires", DateTimeOffset.UtcNow.Add(options.RetentionWindow));
        cmd.Parameters.AddWithValue("@key", key);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseLockAsync(string key, string lockOwner, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        // Only delete if we are still the lock owner — prevents releasing another server's lock
        cmd.CommandText = """
            DELETE FROM idempotency_keys
            WHERE key = @key AND lock_owner = @owner AND status = @status
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@owner", lockOwner);
        cmd.Parameters.AddWithValue("@status", (short)IdempotencyStatus.InFlight);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ── Private Helpers & Cleanup ───────────────────────────────────────────

    private ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => _dataSource.OpenConnectionAsync(cancellationToken);

    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync();
        }
    }
}
