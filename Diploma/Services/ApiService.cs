using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Diploma.Services
{
    public class ApiService
    {
        private readonly HttpClient _http;
        private readonly Guid _userId;

        public ApiService(string baseUrl, Guid userId)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _userId = userId;
        }

        // Contacts
        public async Task<bool> SendFriendRequest(string targetUsername)
        {
            var payload = new { SenderUserId = _userId, TargetUsername = targetUsername };
            var response = await _http.PostAsJsonAsync("api/contacts/request", payload);
            return response.IsSuccessStatusCode;
        }

        public async Task<List<ContactDto>> GetContacts()
        {
            var response = await _http.GetAsync($"api/contacts?userId={_userId}");
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<List<ContactDto>>();
            return new List<ContactDto>();
        }

        public async Task<List<PendingRequestDto>> GetPendingRequests()
        {
            var response = await _http.GetAsync($"api/contacts/pending?userId={_userId}");
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<List<PendingRequestDto>>();
            return new List<PendingRequestDto>();
        }

        public async Task RespondToRequest(Guid contactId, bool accept)
        {
            var payload = new { UserId = _userId, ContactId = contactId, Accept = accept };
            await _http.PostAsJsonAsync("api/contacts/respond", payload);
        }

        // Conversations
        public async Task<ConversationDto?> GetConversation(string withUsername)
        {
            var response = await _http.GetAsync($"api/conversations?userId={_userId}&withUsername={withUsername}");
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<ConversationDto>();
            return null;
        }

        // Messages
        public async Task<bool> SendMessage(Guid messageId, Guid conversationId, byte[] encryptedContent)
        {
            var payload = new
            {
                MessageId = messageId,
                SenderId = _userId,
                ConversationId = conversationId,
                EncryptedContent = Convert.ToBase64String(encryptedContent)
            };
            var response = await _http.PostAsJsonAsync("api/messages", payload);
            return response.IsSuccessStatusCode;
        }

        public async Task<List<MessageDto>> GetMessages(Guid conversationId)
        {
            var response = await _http.GetAsync($"api/messages?conversationId={conversationId}&userId={_userId}");
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<List<MessageDto>>();
            return new List<MessageDto>();
        }
        public async Task MarkAsRead(Guid conversationId)
        {
            var payload = new { UserId = _userId, ConversationId = conversationId };
            await _http.PostAsJsonAsync("api/messages/read", payload);
        }
    }

    // DTOs matching server responses
    public class ContactDto
    {
        public Guid ContactId { get; set; }
        public Guid UserId { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public bool IsConfirmed { get; set; }
        public string PublicKey { get; set; }
        public Guid ConversationId { get; set; }
        public string LastMessageEncrypted { get; set; }
        public string LastMessageStatus { get; set; }  
        public Guid? LastMessageSenderId { get; set; }
        public int UnreadCount { get; set; }
    }

    public class PendingRequestDto
    {
        public Guid ContactId { get; set; }
        public string FromUsername { get; set; }
    }

    public class ConversationDto
    {
        public Guid ConversationId { get; set; }
        public Guid OtherUserId { get; set; }
        public string OtherUsername { get; set; }
        public string OtherDisplayName { get; set; }
    }

    public class MessageDto
    {
        public Guid MessageId { get; set; }
        public Guid SenderId { get; set; }
        public string EncryptedContent { get; set; }   // base64
        public DateTime Timestamp { get; set; }
        public string Status { get; set; }
        
    }
}