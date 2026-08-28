using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FastIdempotency.Storage.Postgres;

/// <summary>
/// DI registration extensions for the PostgreSQL storage provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresIdempotencyStore"/> as the <see cref="IIdempotencyStore"/> implementation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="configureOptions">Optional: configure idempotency options.</param>
    /// <param name="autoMigrate">
    /// When true (default), the idempotency_keys table is created on startup if it doesn't exist.
    /// </param>
    public static IServiceCollection AddFastIdempotencyPostgres(
        this IServiceCollection services,
        string connectionString,
        Action<IdempotencyOptions>? configureOptions = null,
        bool autoMigrate = true)
    {
        var options = new IdempotencyOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        var store = new PostgresIdempotencyStore(connectionString);

        if (autoMigrate)
        {
            // Run schema creation synchronously at registration time (startup phase)
            store.EnsureSchemaAsync().GetAwaiter().GetResult();
        }

        services.AddSingleton<IIdempotencyStore>(store);
        return services;
    }
}
