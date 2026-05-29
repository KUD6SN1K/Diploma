using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MessengerServer.Services
{
    public class ConnectionManager
    {
        private readonly ConcurrentDictionary<Guid, WebSocket> _connections = new();

        public void AddConnection(Guid userId, WebSocket socket)
            => _connections[userId] = socket;

        public void RemoveConnection(Guid userId)
            => _connections.TryRemove(userId, out _);

        public async Task SendAsync(Guid userId, string message)
        {
            if (_connections.TryGetValue(userId, out var socket) && socket.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        public List<Guid> GetOnlineUserIds()
        {
            return _connections.Keys.ToList();
        }
    }
}