using FastIdempotency.AspNetCore.Internals;
using FastIdempotency.Core.Abstractions;
using FastIdempotency.Core.Hashing;
using FastIdempotency.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastIdempotency.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that enforces idempotency semantics on HTTP requests.
///
/// Request lifecycle:
///   1. Check for the configured idempotency header. Skip silently if absent.
///   2. Buffer the request body so it can be read multiple times (EnableBuffering).
///   3. Compute XxHash3 over Method + Path + Body using ReadOnlySpan(byte) — zero-alloc.
///   4. Look up the key in the store:
///      a. Completed → Replay cached response immediately (skip controller).
///      b. In-Flight → Smart poll or return 409 Conflict.
///      c. Not Found → Acquire distributed lock, proceed to controller.
///      d. Hash Mismatch → Return 422 Unprocessable Entity.
///   5. Wrap HttpResponse.Body with BodyCaptureStream (RecyclableMemoryStream-backed).
///   6. After controller returns, persist the response and release the lock.
/// </summary>
public sealed class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IdempotencyOptions>();
        var store = context.RequestServices.GetRequiredService<IIdempotencyStore>();
        var hasher = context.RequestServices.GetRequiredService<IRequestHasher>();

        // 1. Only process applicable methods (POST, PUT, PATCH by default)
        if (!options.ApplicableMethods.Contains(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // 2. Check for idempotency header
        if (!context.Request.Headers.TryGetValue(options.HeaderName, out var rawKey)
            || string.IsNullOrWhiteSpace(rawKey))
        {
            await _next(context);
            return;
        }

        var userKey = rawKey.ToString().Trim();
        var storeKey = $"{options.KeyPrefix}{userKey}";

        // 3. Buffer the request body so we can read it for hashing without consuming it
        context.Request.EnableBuffering();
        var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        // Rewind so the controller can read the body normally
        context.Request.Body.Position = 0;

        // 4. Compute zero-allocation XxHash3 hash
        var requestHash = hasher.ComputeHash(
            context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            body);

        // 5. Check existing record
        var existing = await store.GetAsync(storeKey, context.RequestAborted);

        if (existing is not null)
        {
            // 5a. HASH MISMATCH — same key, different payload → reject
            if (existing.RequestHash != requestHash)
            {
                _logger.LogWarning(
                    "Idempotency key '{Key}' reused with a different payload. Rejecting with 422.",
                    userKey);
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                    title = "Idempotency Key Conflict",
                    status = 422,
                    detail = $"The idempotency key '{userKey}' was already used for a different request payload."
                }, context.RequestAborted);
                return;
            }

            // 5b. COMPLETED — replay cached response (controller is NOT invoked)
            if (existing.Status == IdempotencyStatus.Completed && existing.Response is not null)
            {
                _logger.LogDebug("Idempotency key '{Key}' hit — replaying cached response.", userKey);
                await ReplayCachedResponseAsync(context, existing.Response);
                return;
            }

            // 5c. IN-FLIGHT — another instance is processing this request right now
            if (existing.Status == IdempotencyStatus.InFlight)
            {
                if (options.EnableSmartPolling)
                {
                    var polled = await PollForCompletionAsync(store, storeKey, options, context.RequestAborted);
                    if (polled?.Response is not null)
                    {
                        await ReplayCachedResponseAsync(context, polled.Response);
                        return;
                    }
                }

                _logger.LogWarning("Idempotency key '{Key}' is in-flight. Returning 409.", userKey);
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Request In Progress",
                    status = 409,
                    detail = $"A request with key '{userKey}' is already being processed."
                }, context.RequestAborted);
                return;
            }
        }

        // 6. NOT FOUND — first-time request. Acquire the distributed lock.
        var lockOwner = Guid.NewGuid().ToString("N");
        var acquired = await store.TryAcquireLockAsync(
            storeKey, requestHash, lockOwner, options, context.RequestAborted);

        if (!acquired)
        {
            // Lost the race — another server just acquired the lock for this key.
            // Smart poll for the result.
            if (options.EnableSmartPolling)
            {
                var polled = await PollForCompletionAsync(store, storeKey, options, context.RequestAborted);
                if (polled?.Response is not null)
                {
                    await ReplayCachedResponseAsync(context, polled.Response);
                    return;
                }
            }

            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        // 7. We own the lock. Wrap the response body with a capture stream.
        var originalBody = context.Response.Body;
        await using var captureStream = new BodyCaptureStream(originalBody);
        context.Response.Body = captureStream;

        try
        {
            await _next(context);

            // 8. Controller executed. Persist the completed response.
            var capturedBody = captureStream.GetCapturedBytes();
            var capturedHeaders = context.Response.Headers
                .Where(h => !IsHopByHopHeader(h.Key))
                .ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? "").ToArray());

            var response = new IdempotentResponse
            {
                StatusCode = context.Response.StatusCode,
                Headers = capturedHeaders,
                Body = capturedBody,
                ContentType = context.Response.ContentType
            };

            await store.SaveCompletedAsync(storeKey, response, options, context.RequestAborted);
            _logger.LogDebug("Idempotency key '{Key}' completed and cached.", userKey);
        }
        catch
        {
            // On exception, release the lock so the request can be retried
            await store.ReleaseLockAsync(storeKey, lockOwner, context.RequestAborted);
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    // ── Private Helpers ──────────────────────────────────────────────────────

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        // Use RecyclableMemoryStream from pool to avoid LOH allocations and GC pressure
        await using var ms = BodyCaptureStream.PoolManager.GetStream("FastIdempotency.RequestBody");
        await request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static async Task ReplayCachedResponseAsync(HttpContext context, IdempotentResponse cached)
    {
        context.Response.StatusCode = cached.StatusCode;

        if (!string.IsNullOrEmpty(cached.ContentType))
            context.Response.ContentType = cached.ContentType;

        foreach (var (name, values) in cached.Headers)
        {
            if (!context.Response.Headers.ContainsKey(name))
                context.Response.Headers[name] = values;
        }

        // Add a header indicating this is a replayed idempotent response
        context.Response.Headers["X-Idempotency-Replayed"] = "true";

        if (cached.Body.Length > 0)
            await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted);
    }

    private static async Task<IdempotencyRecord?> PollForCompletionAsync(
        IIdempotencyStore store,
        string key,
        IdempotencyOptions options,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(options.SmartPollTimeout);

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(options.SmartPollInterval, ct);
            var record = await store.GetAsync(key, ct);
            if (record?.Status == IdempotencyStatus.Completed)
                return record;
        }

        return null;
    }

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Keep-Alive", "Proxy-Authenticate",
        "Proxy-Authorization", "TE", "Trailer", "Upgrade"
    };

    private static bool IsHopByHopHeader(string name) => HopByHopHeaders.Contains(name);
}
