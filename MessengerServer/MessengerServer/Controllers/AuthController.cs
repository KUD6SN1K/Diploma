using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;               // <-- add this
using MessengerServer.Data;
using MessengerServer.DTOs;
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;

    public AuthController(AppDbContext db) => _db = db;

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        if (await _db.Users.AnyAsync(u => u.Username == request.Username))
            return Conflict("Username already exists.");

        // Password hash is done on the client side; here we just store it.
        // The client sends the hash as a base64 string, we decode to byte[].
        byte[] hashBytes = Convert.FromBase64String(request.Password);

        var user = new User
        {
            Username = request.Username,
            PasswordHash = hashBytes,
            DisplayName = request.DisplayName ?? request.Username,
            EccPublicKey = request.EccPublicKey,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { userId = user.UserId });
    }
    [HttpGet("salt/{username}")]
    public async Task<IActionResult> GetSalt(string username)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null)
            return NotFound("User not found.");

        // The first 16 bytes of PasswordHash are the salt
        byte[] salt = user.PasswordHash.Take(16).ToArray();
        return Ok(new { salt = Convert.ToBase64String(salt) });
    }
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
        if (user == null)
            return Unauthorized("Invalid username or password.");

        byte[] receivedBlob = Convert.FromBase64String(request.Password);
        // Compare the full blob (salt+hash) with the stored blob
        if (!user.PasswordHash.SequenceEqual(receivedBlob))
            return Unauthorized("Invalid username or password.");

        return Ok(new
        {
            user.UserId,
            user.DisplayName,
            user.EccPublicKey
        });
    }
}