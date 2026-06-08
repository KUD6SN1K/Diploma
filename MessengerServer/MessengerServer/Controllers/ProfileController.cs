using MessengerServer.Data;
using MessengerServer.DTOs;
using MessengerServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MessengerServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProfileController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ConnectionManager _connMgr;

        public ProfileController(AppDbContext db, ConnectionManager connMgr)
        {
            _db = db;
            _connMgr = connMgr;
        }

        [HttpPut("displayname")]
        public async Task<IActionResult> UpdateDisplayName(UpdateDisplayNameRequest request)
        {
            var user = await _db.Users.FindAsync(request.UserId);
            if (user == null) return NotFound();

            user.DisplayName = request.DisplayName;
            await _db.SaveChangesAsync();

            var contacts = await _db.Contacts
                .Where(c => (c.UserId == user.UserId || c.ContactUserId == user.UserId) && c.IsConfirmed)
                .Select(c => c.UserId == user.UserId ? c.ContactUserId : c.UserId)
                .Distinct().ToListAsync();

            var notification = JsonSerializer.Serialize(new
            {
                type = "display_name_changed",
                userId = user.UserId.ToString(),
                newDisplayName = user.DisplayName
            });
            foreach (var id in contacts)
                await _connMgr.SendAsync(id, notification);

            return Ok();
        }

        [HttpPut("password")]
        public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
        {
            var user = await _db.Users.FindAsync(request.UserId);
            if (user == null) return NotFound();

            byte[] oldBlob = Convert.FromBase64String(request.OldPasswordHash);
            if (!user.PasswordHash.SequenceEqual(oldBlob))
                return Unauthorized("Current password is incorrect.");

            user.PasswordHash = Convert.FromBase64String(request.NewPasswordHash);
            await _db.SaveChangesAsync();
            return Ok();
        }
        [HttpGet]
        public async Task<IActionResult> GetProfile([FromQuery] Guid userId)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user == null) return NotFound();
            return Ok(new
            {
                user.DisplayName,
                user.AcceptFriendRequests
            });
        }

        [HttpPut("toggle-friend-requests")]
        public async Task<IActionResult> ToggleFriendRequests(ToggleFriendRequestsRequest request)
        {
            var user = await _db.Users.FindAsync(request.UserId);
            if (user == null) return NotFound();
            user.AcceptFriendRequests = request.AcceptFriendRequests;
            await _db.SaveChangesAsync();
            return Ok();
        }

        public class ToggleFriendRequestsRequest
        {
            public Guid UserId { get; set; }
            public bool AcceptFriendRequests { get; set; }
        }
    }
}