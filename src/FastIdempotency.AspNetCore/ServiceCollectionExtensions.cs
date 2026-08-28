using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Hashing;
using FastIdempotency.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FastIdempotency.AspNetCore;

/// <summary>
/// DI and pipeline registration extensions for FastIdempotency.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the FastIdempotency core services (hasher, options).
    /// Must be called alongside a storage provider registration
    /// (e.g. AddFastIdempotencyRedis or AddFastIdempotencyPostgres).
    /// </summary>
    public static IServiceCollection AddFastIdempotency(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configureOptions = null)
    {
        // Only register options if not already registered by a storage provider
        if (!services.Any(d => d.ServiceType == typeof(IdempotencyOptions)))
        {
            var options = new IdempotencyOptions();
            configureOptions?.Invoke(options);
            services.AddSingleton(options);
        }

        // Register the zero-allocation XxHash3 hasher as a singleton (stateless)
        services.AddSingleton<IRequestHasher, XxHash3RequestHasher>();

        return services;
    }

    /// <summary>
    /// Adds the idempotency middleware to the pipeline.
    /// Place this AFTER UseRouting() and UseAuthentication() but BEFORE UseAuthorization() and MapControllers().
    /// </summary>
    public static IApplicationBuilder UseFastIdempotency(this IApplicationBuilder app)
        => app.UseMiddleware<IdempotencyMiddleware>();
}
