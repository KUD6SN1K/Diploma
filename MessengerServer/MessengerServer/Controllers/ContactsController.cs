using MessengerServer.Data;
using MessengerServer.Models;
using MessengerServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        var notification = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "friend_request",
            fromUsername = _db.Users.Find(dto.SenderUserId)?.Username
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
            var user1 = contact.UserId;
            var user2 = contact.ContactUserId;
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

            // Notify original requester that their request was accepted
            var notification = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "contact_added",
                contactUsername = _db.Users.Find(contact.ContactUserId)?.Username
            });
            await _connMgr.SendAsync(contact.UserId, notification);
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
            .Select(c => new ContactDto
            {
                ContactId = c.ContactId,
                UserId = c.UserId == userId ? c.ContactUserId : c.UserId,
                Username = c.UserId == userId ? c.ContactUser.Username : c.User.Username,
                DisplayName = c.UserId == userId ? c.ContactUser.DisplayName : c.User.DisplayName,
                IsConfirmed = c.IsConfirmed,
                PublicKey = c.UserId == userId ? c.ContactUser.EccPublicKey : c.User.EccPublicKey
            }).ToListAsync();
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