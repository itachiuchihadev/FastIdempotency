using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FastIdempotency.AspNetCore;
using FastIdempotency.Storage.Redis;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace FastIdempotency.Tests;

/// <summary>
/// End-to-end middleware integration tests using WebApplicationFactory.
/// Spins up a real Redis container and a real in-process ASP.NET Core server.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Redis")]
public sealed class IdempotencyMiddlewareTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Replace the default Redis connection with our test container
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(IConnectionMultiplexer));
                    if (descriptor is not null) services.Remove(descriptor);

                    services.AddSingleton<IConnectionMultiplexer>(
                        ConnectionMultiplexer.Connect(_redis.GetConnectionString()));
                });
            });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _redis.DisposeAsync();
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "First POST with Idempotency-Key returns 201 Created")]
    public async Task FirstPost_WithIdempotencyKey_Returns201()
    {
        var response = await PostOrderAsync("e2e-key-001", """{"item":"Keyboard","price":50}""");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact(DisplayName = "Second identical POST returns same cached 201 without re-executing controller")]
    public async Task SecondPost_SameKey_ReturnsCachedResponse()
    {
        var key = $"e2e-key-{Guid.NewGuid():N}";
        const string body = """{"item":"Mouse","price":25}""";

        // First call — controller executes
        var first = await PostOrderAsync(key, body);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();

        // Second call — should be replayed from cache
        var second = await PostOrderAsync(key, body);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // The X-Idempotency-Replayed header must be present on the second response
        Assert.True(second.Headers.Contains("X-Idempotency-Replayed"),
            "Second response must carry the X-Idempotency-Replayed header");

        // Both responses must have the SAME orderId — controller ran exactly once
        Assert.Equal(
            firstBody.GetProperty("orderId").GetString(),
            secondBody.GetProperty("orderId").GetString());
    }

    [Fact(DisplayName = "POST without Idempotency-Key passes through normally")]
    public async Task PostWithoutKey_PassesThrough()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("""{"item":"Monitor","price":300}""", Encoding.UTF8, "application/json")
        };
        // No Idempotency-Key header

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact(DisplayName = "Same key with different payload returns 422 Unprocessable Entity")]
    public async Task SameKeyDifferentPayload_Returns422()
    {
        var key = $"e2e-key-mismatch-{Guid.NewGuid():N}";

        // First call
        await PostOrderAsync(key, """{"item":"Keyboard","price":50}""");

        // Second call — same key but different body
        var second = await PostOrderAsync(key, """{"item":"MacBook","price":2500}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact(DisplayName = "Concurrent requests with same key — controller executes exactly once")]
    public async Task ConcurrentRequests_SameKey_ControllerExecutesOnce()
    {
        var key = $"e2e-key-concurrent-{Guid.NewGuid():N}";
        const string body = """{"item":"Headphones","price":150}""";
        const int concurrency = 10;

        // Fire 10 concurrent identical requests
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => PostOrderAsync(key, body))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        // All responses should be 201 or 409 (if smart polling is off)
        // With smart polling ON, they should all eventually get 201
        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(successCount >= 1, "At least one request must succeed");

        // Check execution count — controller should have run ONCE
        var countResponse = await _client.GetFromJsonAsync<JsonElement>("/api/orders/execution-count");
        Assert.Equal(1, countResponse.GetProperty("count").GetInt32());
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostOrderAsync(string idempotencyKey, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _client.SendAsync(request);
    }
}
