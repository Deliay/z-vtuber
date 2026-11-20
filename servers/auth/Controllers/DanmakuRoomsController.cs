using Microsoft.AspNetCore.Mvc;
using Z.Vtuber.Auth.Configuration;
using Z.Vtuber.Auth.Contracts;
using Z.Vtuber.Auth.Danmaku;

namespace Z.Vtuber.Auth.Controllers;

[ApiController]
[Route("api/auth/danmaku")]
public sealed class DanmakuRoomsController(
    DanmakuHealthRegistry registry,
    IOptions<DanmakuOptions> options) : ControllerBase
{
    [HttpGet("rooms")]
    public IActionResult Rooms()
    {
        var rooms = registry.GetSnapshots(options.Value.RoomIds)
            .Select(x => new RoomDto
            {
                RoomId = x.RoomId,
                Role = x.Role,
                Connected = x.Connected,
                Available = x.Available,
                LastHeartbeatAt = x.LastHeartbeatAt,
                LastEventAt = x.LastEventAt
            })
            .ToList();

        return Ok(new RoomListResponse { Rooms = rooms });
    }
}
