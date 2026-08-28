using FastIdempotency.AspNetCore;
using FastIdempotency.Storage.Redis;

// ────────────────────────────────────────────────────────────────────────────
// FastIdempotency.NET — Sample API
// Demonstrates using the library with a Redis backend.
//
// To run:
//   docker run -p 6379:6379 redis:7-alpine
//   dotnet run --project samples/FastIdempotency.Sample.Api
//
// Then test with curl:
//   # First call — executes the order
//   curl -X POST http://localhost:5000/api/orders \
//        -H "Content-Type: application/json" \
//        -H "Idempotency-Key: order-abc-001" \
//        -d '{"item":"Keyboard","price":50}'
//
//   # Retry with same key & body — returns cached 201, NOT re-executed
//   curl -X POST http://localhost:5000/api/orders \
//        -H "Content-Type: application/json" \
//        -H "Idempotency-Key: order-abc-001" \
//        -d '{"item":"Keyboard","price":50}'
//
//   # Same key but different payload — returns 422
//   curl -X POST http://localhost:5000/api/orders \
//        -H "Content-Type: application/json" \
//        -H "Idempotency-Key: order-abc-001" \
//        -d '{"item":"MacBook","price":2500}'
// ────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// 1. Register FastIdempotency with Redis backend
builder.Services.AddFastIdempotencyRedis(
    redisConnectionString: builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379",
    configureOptions: opts =>
    {
        opts.RetentionWindow = TimeSpan.FromHours(24);
        opts.LockTimeout = TimeSpan.FromSeconds(30);
        opts.EnableSmartPolling = true;
        opts.SmartPollTimeout = TimeSpan.FromSeconds(5);
    });

// 2. Register core FastIdempotency services (XxHash3 hasher)
builder.Services.AddFastIdempotency();

builder.Services.AddControllers();

var app = builder.Build();

// 3. Add the idempotency middleware to the pipeline
//    Place AFTER auth middleware, BEFORE endpoint mapping
app.UseFastIdempotency();

app.MapControllers();

app.Run();

// Expose Program for WebApplicationFactory in integration tests
public partial class Program { }
