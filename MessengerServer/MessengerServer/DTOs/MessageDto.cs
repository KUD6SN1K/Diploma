namespace MessengerServer.DTOs
{
    public class SendMessageRequest
    {
        public Guid MessageId { get; set; }
        public Guid SenderId { get; set; }
        public Guid ConversationId { get; set; }
        public string EncryptedContent { get; set; }
    }
}
