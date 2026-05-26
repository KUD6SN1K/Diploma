public class FriendRequestDto
{
    public Guid SenderUserId { get; set; }
    public string TargetUsername { get; set; }
}

public class RespondRequestDto
{
    public Guid UserId { get; set; }       // the user responding
    public Guid ContactId { get; set; }
    public bool Accept { get; set; }
}

public class ContactDto
{
    public Guid ContactId { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; }
    public string DisplayName { get; set; }
    public bool IsConfirmed { get; set; }
    public string PublicKey { get; set; }   // <-- add this
}