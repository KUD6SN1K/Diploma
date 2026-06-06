using MessengerServer.Data;
using MessengerServer.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessengerServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConversationsController : ControllerBase
    {
        private readonly AppDbContext _db;
        public ConversationsController(AppDbContext db) => _db = db;

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
    }
}
