using MessengerServer.Data;
using MessengerServer.Models;
using MessengerServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

[ApiController]
[Route("api/[controller]")]
public class ContactsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ConnectionManager _connMgr;

    public ContactsController(AppDbContext db, ConnectionManager connMgr)
    {
        _db = db;
        _connMgr = connMgr;
    }

    // Send friend request – push to target user
    [HttpPost("request")]
    public async Task<IActionResult> SendRequest(FriendRequestDto dto)
    {
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Username == dto.TargetUsername);
        if (target == null) return NotFound("User not found.");
        if (target.UserId == dto.SenderUserId) return BadRequest("Cannot add yourself.");

        bool exists = await _db.Contacts.AnyAsync(c =>
            (c.UserId == dto.SenderUserId && c.ContactUserId == target.UserId) ||
            (c.UserId == target.UserId && c.ContactUserId == dto.SenderUserId));
        if (exists) return Conflict("Contact or request already exists.");

        var contact = new Contact
        {
            UserId = dto.SenderUserId,
            ContactUserId = target.UserId,
            IsConfirmed = false,
            CreatedAt = DateTime.UtcNow
        };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        // Notify target user about incoming friend request
        var senderUsername = _db.Users.Find(dto.SenderUserId)?.Username;
        var notification = JsonSerializer.Serialize(new
        {
            type = "friend_request",
            requestId = contact.ContactId,
            fromUsername = senderUsername
        });
        await _connMgr.SendAsync(target.UserId, notification);

        return Ok();
    }

    // Accept/Decline – push to original requester on accept
    [HttpPost("respond")]
    public async Task<IActionResult> Respond(RespondRequestDto dto)
    {
        var contact = await _db.Contacts.FindAsync(dto.ContactId);
        if (contact == null || contact.ContactUserId != dto.UserId) return NotFound();

        if (dto.Accept)
        {
            contact.IsConfirmed = true;
            var user1 = contact.UserId;           // original requester
            var user2 = contact.ContactUserId;    // acceptor

            var conv = await _db.Conversations.FirstOrDefaultAsync(c =>
                (c.User1Id == user1 && c.User2Id == user2) ||
                (c.User1Id == user2 && c.User2Id == user1));
            if (conv == null)
            {
                conv = new Conversation
                {
                    User1Id = user1,
                    User2Id = user2,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Conversations.Add(conv);
            }
            await _db.SaveChangesAsync();

            // Get online statuses after conversation is created
            var onlineIds = _connMgr.GetOnlineUserIds();

            // Notify user1 (requester) that the request was accepted + user2's online status
            var notifyUser1 = JsonSerializer.Serialize(new
            {
                type = "contact_added",
                contactUserId = user2.ToString(),
                contactUsername = _db.Users.Find(user2)?.Username,
                isOnline = onlineIds.Contains(user2)
            });
            await _connMgr.SendAsync(user1, notifyUser1);

            // Notify user2 (accepter) that they now have a new contact + user1's online status
            var notifyUser2 = JsonSerializer.Serialize(new
            {
                type = "contact_added",
                contactUserId = user1.ToString(),
                contactUsername = _db.Users.Find(user1)?.Username,
                isOnline = onlineIds.Contains(user1)
            });
            await _connMgr.SendAsync(user2, notifyUser2);
        }
        else
        {
            _db.Contacts.Remove(contact);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    // List contacts (confirmed only)
    [HttpGet]
    public async Task<IActionResult> GetContacts([FromQuery] Guid userId)
    {
        var contacts = await _db.Contacts
            .Where(c => (c.UserId == userId || c.ContactUserId == userId) && c.IsConfirmed)
            .Select(c => new
            {
                OtherUserId = c.UserId == userId ? c.ContactUserId : c.UserId,
                c.ContactId,
                c.IsConfirmed,
                OtherUser = c.UserId == userId ? c.ContactUser : c.User
            })
            .Select(c => new
            {
                c.ContactId,
                UserId = c.OtherUserId,
                Username = c.OtherUser.Username,
                DisplayName = c.OtherUser.DisplayName,
                IsConfirmed = c.IsConfirmed,
                PublicKey = c.OtherUser.EccPublicKey,

                ConversationId = _db.Conversations
                    .Where(conv => (conv.User1Id == userId && conv.User2Id == c.OtherUserId) ||
                                   (conv.User1Id == c.OtherUserId && conv.User2Id == userId))
                    .Select(conv => conv.ConversationId)
                    .FirstOrDefault(),

                LastMessageEncrypted = _db.Messages
                    .Where(m => (m.Conversation.User1Id == userId && m.Conversation.User2Id == c.OtherUserId) ||
                                (m.Conversation.User1Id == c.OtherUserId && m.Conversation.User2Id == userId))
                    .OrderByDescending(m => m.Timestamp)
                    .Select(m => Convert.ToBase64String(m.EncryptedContent))
                    .FirstOrDefault(),

                LastMessageStatus = _db.Messages
                    .Where(m => (m.Conversation.User1Id == userId && m.Conversation.User2Id == c.OtherUserId) ||
                                (m.Conversation.User1Id == c.OtherUserId && m.Conversation.User2Id == userId))
                    .OrderByDescending(m => m.Timestamp)
                    .Select(m => m.Status)
                    .FirstOrDefault(),

                LastMessageSenderId = _db.Messages
                    .Where(m => (m.Conversation.User1Id == userId && m.Conversation.User2Id == c.OtherUserId) ||
                                (m.Conversation.User1Id == c.OtherUserId && m.Conversation.User2Id == userId))
                    .OrderByDescending(m => m.Timestamp)
                    .Select(m => m.SenderId)
                    .FirstOrDefault(),
                UnreadCount = _db.Messages
                    .Count(m => (m.Conversation.User1Id == userId && m.Conversation.User2Id == c.OtherUserId ||
                     m.Conversation.User1Id == c.OtherUserId && m.Conversation.User2Id == userId)
                    && m.SenderId != userId
                    && m.Status == "Sent"),
                LastMessageTimestamp = _db.Messages
                    .Where(m => (m.Conversation.User1Id == userId && m.Conversation.User2Id == c.OtherUserId) ||
                                (m.Conversation.User1Id == c.OtherUserId && m.Conversation.User2Id == userId))
                    .OrderByDescending(m => m.Timestamp)
                    .Select(m => (DateTime?)m.Timestamp)
                    .FirstOrDefault()
            })
            .ToListAsync();

        return Ok(contacts);
    }

    // Pending requests (for the current user)
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending([FromQuery] Guid userId)
    {
        var pending = await _db.Contacts
            .Where(c => c.ContactUserId == userId && !c.IsConfirmed)
            .Select(c => new
            {
                c.ContactId,
                FromUsername = _db.Users.FirstOrDefault(u => u.UserId == c.UserId).Username
            }).ToListAsync();
        return Ok(pending);
    }

}