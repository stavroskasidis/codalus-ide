using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController : ControllerBase
{
    [HttpGet]
    public IEnumerable<string> List() => ["A-1", "A-2"]; // [orders-list]

    [HttpGet("{id}")]
    public string Get(string id) => id; // [orders-get]

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) => NoContent(); // [orders-delete]

    [HttpGet("/status")]
    public string Status() => "ok"; // [orders-status]
}
