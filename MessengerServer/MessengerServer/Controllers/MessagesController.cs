using MessengerServer.Data;
using MessengerServer.DTOs;
using MessengerServer.Models;
using MessengerServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
                senderId = dto.SenderId.ToString()
            });
            await _connMgr.SendAsync(recipientId, notification);

            return Ok();
        }

        // Get messages for a conversation
        [HttpGet]
        public async Task<IActionResult> GetMessages([FromQuery] Guid conversationId, [FromQuery] Guid userId)
        {
            var messages = await _db.Messages
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.Timestamp)
                .Select(m => new
                {
                    m.MessageId,
                    m.SenderId,
                    EncryptedContent = Convert.ToBase64String(m.EncryptedContent),
                    m.Timestamp,
                    m.Status
                }).ToListAsync();
            return Ok(messages);
        }
    }
}
