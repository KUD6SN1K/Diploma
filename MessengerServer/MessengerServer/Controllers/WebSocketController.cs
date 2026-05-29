using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MessengerServer.Data;
using MessengerServer.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class WebSocketController : ControllerBase
{
    private readonly ConnectionManager _manager;
    private readonly AppDbContext _db;

    public WebSocketController(ConnectionManager manager, AppDbContext db)
    {
        _manager = manager;
        _db = db;
    }

    [HttpGet("ws")]
    public async Task Get()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        if (!Guid.TryParse(HttpContext.Request.Query["userId"], out var userId))
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        _manager.AddConnection(userId, socket);
        // Send all currently online contacts to the new user
        var onlineContacts = _db.Contacts
            .Where(c => (c.UserId == userId || c.ContactUserId == userId) && c.IsConfirmed)
            .Select(c => c.UserId == userId ? c.ContactUserId : c.UserId)
            .Distinct()
            .ToList();

        foreach (var contactId in onlineContacts)
        {
            if (_manager.GetOnlineUserIds().Contains(contactId))
            {
                var presenceMsg = JsonSerializer.Serialize(new
                {
                    type = "presence",
                    userId = contactId.ToString(),
                    isOnline = true
                });
                await _manager.SendAsync(userId, presenceMsg);
            }
        }
        // Notify contacts that this user is online
        await BroadcastPresence(userId, true);

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
        finally
        {
            _manager.RemoveConnection(userId);
            // Notify contacts that this user is offline
            await BroadcastPresence(userId, false);

            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
    }

    private async Task BroadcastPresence(Guid userId, bool isOnline)
    {
        var contactIds = await Task.Run(() =>
            _db.Contacts
                .Where(c => (c.UserId == userId || c.ContactUserId == userId) && c.IsConfirmed)
                .Select(c => c.UserId == userId ? c.ContactUserId : c.UserId)
                .Distinct() 
                .ToList());

        var notification = JsonSerializer.Serialize(new
        {
            type = "presence",
            userId = userId.ToString(),
            isOnline
        });

        foreach (var contactId in contactIds)
            await _manager.SendAsync(contactId, notification);

        Console.WriteLine($"Broadcasting presence for {userId}: {isOnline}. Sending to {contactIds.Count} contacts.");
    }
}