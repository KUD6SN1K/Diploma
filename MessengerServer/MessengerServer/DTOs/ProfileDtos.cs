namespace MessengerServer.DTOs
{
    public class UpdateDisplayNameRequest
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; }
    }

    public class ChangePasswordRequest
    {
        public Guid UserId { get; set; }
        public string OldPasswordHash { get; set; }
        public string NewPasswordHash { get; set; }
    }
}