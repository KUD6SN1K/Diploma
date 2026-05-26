namespace MessengerServer.Models
{
    public class Message
    {
        public Guid MessageId { get; set; }
        public Guid ConversationId { get; set; }
        public Guid SenderId { get; set; }
        public byte[] EncryptedContent { get; set; }
        public DateTime Timestamp { get; set; }
        public string Status { get; set; }

        public Conversation Conversation { get; set; }
        public User Sender { get; set; }
    }
}
