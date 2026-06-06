using MessengerServer.Data;
using MessengerServer.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using MessengerServer.Services;
namespace MessengerServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConversationsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ConnectionManager _connMgr;
        public ConversationsController(AppDbContext db, ConnectionManager connMgr)
        {
            _db = db;
            _connMgr = connMgr;                       
        }

        // Get conversation between current user and another user (by username)
        [HttpGet]
        public async Task<IActionResult> GetConversation([FromQuery] Guid userId, [FromQuery] string withUsername)
        {
            var other = await _db.Users.FirstOrDefaultAsync(u => u.Username == withUsername);
            if (other == null) return NotFound("User not found.");

            var conv = await _db.Conversations.FirstOrDefaultAsync(c =>
                (c.User1Id == userId && c.User2Id == other.UserId) ||
                (c.User1Id == other.UserId && c.User2Id == userId));
            if (conv == null) return NotFound("No conversation yet.");

            return Ok(new ConversationDto
            {
                ConversationId = conv.ConversationId,
                OtherUserId = other.UserId,
                OtherUsername = other.Username,
                OtherDisplayName = other.DisplayName
            });
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

            // Notify the other user
            var otherUserId = conv.User1Id == userId ? conv.User2Id : conv.User1Id;
            var notification = JsonSerializer.Serialize(new
            {
                type = "clear_history",
                conversationId = conversationId.ToString()
            });
            await _connMgr.SendAsync(otherUserId, notification);

            return Ok();
        }
    }
}
