namespace MessengerServer.Models
{
    public class Conversation
    {
        public Guid ConversationId { get; set; }
        public Guid User1Id { get; set; }
        public Guid User2Id { get; set; }
        public DateTime CreatedAt { get; set; }

        public User User1 { get; set; }
        public User User2 { get; set; }
        public List<Message> Messages { get; set; }
    }
}
