using Microsoft.AspNetCore.Mvc;

namespace Z.Vtuber.Cookie.Controllers;

[ApiController]
[Route("api/cookie")]
public class HealthController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult GetHealth() => Ok("Healthy");
}