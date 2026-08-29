using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FastIdempotency.Storage.Redis;

/// <summary>
/// DI registration extensions for the Redis storage provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RedisIdempotencyStore"/> as the <see cref="IIdempotencyStore"/> implementation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="redisConnectionString">Redis connection string (e.g. "localhost:6379").</param>
    /// <param name="configureOptions">Optional: configure idempotency options.</param>
    public static IServiceCollection AddFastIdempotencyRedis(
        this IServiceCollection services,
        string redisConnectionString,
        Action<IdempotencyOptions>? configureOptions = null)
    {
        var options = new IdempotencyOptions();
        configureOptions?.Invoke(options);

        services.AddSingleton(options);

        // IConnectionMultiplexer is registered via factory so connection is deferred until first resolution
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var config = ConfigurationOptions.Parse(redisConnectionString);
            config.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();

        return services;
    }
}
