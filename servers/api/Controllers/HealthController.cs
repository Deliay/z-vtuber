using Microsoft.AspNetCore.Mvc;

namespace Z.Vtuber.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public ActionResult<object> Get()
    {
        return Ok(new
        {
            status = "ok",
            service = "Z.Vtuber",
            timestamp = DateTimeOffset.UtcNow
        });
    }
}
