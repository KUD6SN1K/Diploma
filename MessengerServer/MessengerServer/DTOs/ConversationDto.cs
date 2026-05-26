namespace MessengerServer.DTOs
{
    public class ConversationDto
    {
        public Guid ConversationId { get; set; }
        public Guid OtherUserId { get; set; }
        public string OtherUsername { get; set; }
        public string OtherDisplayName { get; set; }
    }
}
