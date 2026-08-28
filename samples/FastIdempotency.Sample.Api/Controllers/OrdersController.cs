using Microsoft.AspNetCore.Mvc;

namespace FastIdempotency.Sample.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController : ControllerBase
{
    private static int _executionCount = 0;

    /// <summary>
    /// Creates an order.
    /// With the Idempotency-Key header, this endpoint can be safely retried —
    /// the controller body executes exactly once per unique key.
    /// </summary>
    [HttpPost]
    public IActionResult CreateOrder([FromBody] CreateOrderRequest request)
    {
        // Track how many times this controller actually executes.
        // On retries with the same Idempotency-Key, this counter should NOT increment.
        var count = Interlocked.Increment(ref _executionCount);

        var orderId = Guid.NewGuid().ToString("N")[..8].ToUpper();

        return StatusCode(201, new
        {
            OrderId = orderId,
            Item = request.Item,
            Price = request.Price,
            Status = "Created",
            ControllerExecutionCount = count,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>Returns how many times the controller body has actually executed.</summary>
    [HttpGet("execution-count")]
    public IActionResult GetExecutionCount() => Ok(new { Count = _executionCount });
}

public sealed record CreateOrderRequest(string Item, decimal Price);
