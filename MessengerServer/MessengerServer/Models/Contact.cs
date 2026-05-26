namespace MessengerServer.Models
{
    public class Contact
    {
        public Guid ContactId { get; set; }
        public Guid UserId { get; set; }
        public Guid ContactUserId { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime CreatedAt { get; set; }

        public User User { get; set; }
        public User ContactUser { get; set; }
    }
}
