using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MessengerServer.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class WebSocketController : ControllerBase
{
    private readonly ConnectionManager _manager;

    public WebSocketController(ConnectionManager manager) => _manager = manager;

    [HttpGet("ws")]
    public async Task Get()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        // Get userId from query string (e.g., ws?userId=...)
        if (!Guid.TryParse(HttpContext.Request.Query["userId"], out var userId))
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        _manager.AddConnection(userId, socket);

        try
        {
            // Keep the connection open until the client closes it
            try
            {
                var buffer = new byte[1024];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch (WebSocketException)
            {
                // client disconnected without close handshake – ignore
            }
        }
        finally
        {
            _manager.RemoveConnection(userId);
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
    }
}