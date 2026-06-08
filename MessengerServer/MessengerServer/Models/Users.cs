public class User
{
    public Guid UserId { get; set; }
    public string Username { get; set; }
    public byte[] PasswordHash { get; set; }
    public string DisplayName { get; set; }
    public string EccPublicKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool AcceptFriendRequests { get; set; } = true;
}