using MessengerServer.Data;
using MessengerServer.DTOs;
using MessengerServer.Models;
using MessengerServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MessengerServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MessagesController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ConnectionManager _connMgr;

        public MessagesController(AppDbContext db, ConnectionManager connMgr)
        {
            _db = db;
            _connMgr = connMgr;
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage(SendMessageRequest dto)
        {
            var msg = new Message
            {
                MessageId = dto.MessageId,
                ConversationId = dto.ConversationId,
                SenderId = dto.SenderId,
                EncryptedContent = Convert.FromBase64String(dto.EncryptedContent),
                Timestamp = DateTime.UtcNow,
                Status = "Sent"
            };
            _db.Messages.Add(msg);
            await _db.SaveChangesAsync();

            // Find the recipient (the other participant in the conversation)
            var conversation = await _db.Conversations.FindAsync(dto.ConversationId);
            var recipientId = conversation.User1Id == dto.SenderId ? conversation.User2Id : conversation.User1Id;

            // Notify the recipient via WebSocket
            var notification = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "new_message",
                conversationId = dto.ConversationId.ToString(),
                messageId = dto.MessageId.ToString(),
                encryptedContent = dto.EncryptedContent,   // the new message’s encrypted data (base64)
                senderId = dto.SenderId.ToString(),
                timestamp = msg.Timestamp.ToString("o")   // ISO 8601 (e.g. 2026-01-01T12:30:45.1234567Z)
            });
            await _connMgr.SendAsync(recipientId, notification);

            return Ok();
        }

        // Get messages for a conversation
        [HttpGet]
        public async Task<IActionResult> GetMessages(
        [FromQuery] Guid conversationId,
        [FromQuery] Guid userId,
        [FromQuery] int count = 50,
        [FromQuery] DateTime? before = null)
        {
            IQueryable<Message> query = _db.Messages
                .Where(m => m.ConversationId == conversationId);

            if (before.HasValue)
                query = query.Where(m => m.Timestamp < before.Value);

            var messages = await query
                .OrderByDescending(m => m.Timestamp)   // newest first (for Take)
                .Take(count)
                .OrderBy(m => m.Timestamp)             // flip to chronological
                .Select(m => new
                {
                    m.MessageId,
                    m.SenderId,
                    EncryptedContent = Convert.ToBase64String(m.EncryptedContent),
                    m.Timestamp,
                    m.Status
                })
                .ToListAsync();

            return Ok(messages);
        }

        [HttpPost("read")]
        public async Task<IActionResult> MarkAsRead(MarkReadRequest dto)
        {
            var unread = await _db.Messages
                .Where(m => m.ConversationId == dto.ConversationId
                            && m.SenderId != dto.UserId
                            && m.Status != "Read")
                .ToListAsync();

            foreach (var msg in unread)
            {
                msg.Status = "Read";
                var notification = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "message_status",
                    messageId = msg.MessageId.ToString(),
                    conversationId = dto.ConversationId.ToString(),
                    newStatus = "Read"
                });
                await _connMgr.SendAsync(msg.SenderId, notification);
            }

            if (unread.Any())
                await _db.SaveChangesAsync();

            return Ok();
        }
        
        [HttpDelete("{messageId}")]
        public async Task<IActionResult> DeleteMessage(Guid messageId, [FromQuery] Guid userId)
        {
            var msg = await _db.Messages.FindAsync(messageId);
            if (msg == null) return NotFound();
            if (msg.SenderId != userId) return Forbid();   // only sender can delete

            var conversationId = msg.ConversationId;
            _db.Messages.Remove(msg);
            await _db.SaveChangesAsync();

            // Notify the other user
            var conversation = await _db.Conversations.FindAsync(conversationId);
            var otherUserId = conversation.User1Id == userId ? conversation.User2Id : conversation.User1Id;
            var notification = JsonSerializer.Serialize(new
            {
                type = "delete_message",
                messageId = messageId.ToString(),
                conversationId = conversationId.ToString()
            });
            await _connMgr.SendAsync(otherUserId, notification);

            return Ok();
        }

        [HttpDelete("{conversationId}/messages")]
        public async Task<IActionResult> ClearHistory(Guid conversationId, [FromQuery] Guid userId)
        {
            var conv = await _db.Conversations.FindAsync(conversationId);
            if (conv == null) return NotFound();
            if (conv.User1Id != userId && conv.User2Id != userId) return Forbid();

            var messages = await _db.Messages.Where(m => m.ConversationId == conversationId).ToListAsync();
            _db.Messages.RemoveRange(messages);
            await _db.SaveChangesAsync();

            var otherUserId = conv.User1Id == userId ? conv.User2Id : conv.User1Id;
            var notification = JsonSerializer.Serialize(new
            {
                type = "clear_history",
                conversationId = conversationId.ToString()
            });
            await _connMgr.SendAsync(otherUserId, notification);

            return Ok();
        }
        
        [HttpGet("last")]
        public async Task<IActionResult> GetLastMessage([FromQuery] Guid conversationId, [FromQuery] Guid userId)
        {
            var lastMsg = await _db.Messages
                .Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefaultAsync();

            if (lastMsg == null)
                return Ok(new { exists = false });

            return Ok(new
            {
                exists = true,
                encryptedContent = Convert.ToBase64String(lastMsg.EncryptedContent),
                senderId = lastMsg.SenderId,
                status = lastMsg.Status,
                timestamp = lastMsg.Timestamp
            });
        }
        public class MarkReadRequest
        {
            public Guid UserId { get; set; }
            public Guid ConversationId { get; set; }
        }
    }
}
