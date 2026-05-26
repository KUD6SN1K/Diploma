using Diploma.Crypto;
using Diploma.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Diploma
{
    public partial class MainWindow : Window
    {
        private Guid _currentUserId;
        private string _currentUsername;
        private string _currentDisplayName;
        private string _currentPrivateKey;
        private string _currentPublicKey;
        private ApiService _api;
        private KeyManager _keyManager;
        private WebSocketService _wsService;
        private ChatItem _selectedChat;

        public MainWindow(Guid userId, string username, string displayName,
                          string privateKey, string publicKey)
        {
            InitializeComponent();
            this.Closed += async (s, e) =>
            {
                if (_wsService != null)
                    await _wsService.CloseAsync();
            };
            _currentUserId = userId;
            _currentUsername = username;
            _currentDisplayName = displayName;
            _currentPrivateKey = privateKey;
            _currentPublicKey = publicKey;

            _api = new ApiService("https://localhost:5001", userId);
            _keyManager = new KeyManager(userId);
            _ = ConnectWebSocket();
            LoadContacts();
            LoadPendingRequests();
        }

        // ========== Load contacts ==========
        private async void LoadContacts()
        {
            var contacts = await _api.GetContacts();
            ChatListBox.ItemsSource = contacts.Select(c => new ChatItem
            {
                ContactName = c.DisplayName,
                Username = c.Username,
                LastMessage = "",
                ContactUserId = c.UserId,
                PublicKey = c.PublicKey,
                ConversationId = Guid.Empty
            }).ToList();
        }

        // ========== Load pending friend requests ==========
        private async void LoadPendingRequests()
        {
            var requests = await _api.GetPendingRequests();
            FriendRequestsListBox.ItemsSource = requests.Select(r => new FriendRequestItem
            {
                FromUsername = r.FromUsername,
                RequestId = r.ContactId
            }).ToList();
        }

        // ========== Chat selection ==========
        private async void ChatListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChatListBox.SelectedItem is not ChatItem selected)
                return;

            _selectedChat = selected;
            ChatHeaderText.Text = selected.ContactName;

            var conv = await _api.GetConversation(selected.Username);
            if (conv != null)
            {
                selected.ConversationId = conv.ConversationId;
                LoadMessages(selected.ConversationId);
            }
            else
            {
                MessageBox.Show("Conversation not found. Make sure you are friends.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                MessagesListBox.ItemsSource = null;
            }
        }

        // ========== Load messages ==========
        private async void LoadMessages(Guid conversationId)
        {
            if (_selectedChat == null) return;

            string contactPublicKey = _selectedChat.PublicKey;
            var messages = await _api.GetMessages(conversationId);
            var displayMessages = new List<MessageDisplay>();

            foreach (var msg in messages)
            {
                string plainText;
                try
                {
                    byte[] ciphertext = Convert.FromBase64String(msg.EncryptedContent);
                    byte[] decrypted = ECCryptoService.DecryptData(
                        ciphertext,
                        _currentPrivateKey,
                        contactPublicKey);
                    plainText = Encoding.UTF8.GetString(decrypted);
                }
                catch
                {
                    plainText = "[decryption failed]";
                }

                string senderName = msg.SenderId == _currentUserId
                    ? _currentDisplayName
                    : _selectedChat.ContactName;

                displayMessages.Add(new MessageDisplay
                {
                    Text = plainText,
                    SenderName = senderName,
                    Time = msg.Timestamp.ToString("t"),
                    Alignment = msg.SenderId == _currentUserId ? "Right" : "Left",
                    BubbleColor = msg.SenderId == _currentUserId ? "#0078D7" : "#E0E0E0",
                    ShowSender = msg.SenderId == _currentUserId ? "Collapsed" : "Visible"
                });
            }
            MessagesListBox.ItemsSource = displayMessages;
        }

        // ========== Send message ==========
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedChat == null)
            {
                MessageBox.Show("Select a chat first.");
                return;
            }

            string text = MessageTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (_selectedChat.ConversationId == Guid.Empty)
            {
                MessageBox.Show("Conversation not ready. Try selecting the chat again.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte[] ciphertext = ECCryptoService.EncryptData(
                Encoding.UTF8.GetBytes(text),
                _currentPrivateKey,
                _selectedChat.PublicKey);

            Guid messageId = Guid.NewGuid();
            bool success = await _api.SendMessage(messageId, _selectedChat.ConversationId, ciphertext);

            if (success)
            {
                var displayMsg = new MessageDisplay
                {
                    Text = text,
                    SenderName = _currentDisplayName,
                    Time = DateTime.Now.ToString("t"),
                    Alignment = "Right",
                    BubbleColor = "#0078D7",
                    ShowSender = "Collapsed"
                };

                var list = MessagesListBox.ItemsSource as List<MessageDisplay>;
                list?.Add(displayMsg);
                MessagesListBox.Items.Refresh();
                MessageTextBox.Clear();
            }
            else
            {
                MessageBox.Show("Failed to send message.");
            }
        }

        // ========== Add friend ==========
        private async void AddFriendButton_Click(object sender, RoutedEventArgs e)
        {
            string username = FriendUsernameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(username)) return;
            bool ok = await _api.SendFriendRequest(username);
            if (ok)
            {
                MessageBox.Show("Request sent.");
                FriendUsernameBox.Clear();
            }
            else
                MessageBox.Show("Failed to send request.");
        }

        // ========== Accept/decline friend request ==========
        private async void AcceptRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Guid contactId)
            {
                await _api.RespondToRequest(contactId, true);
                LoadPendingRequests();
                LoadContacts();
            }
        }

        private async void DeclineRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Guid contactId)
            {
                await _api.RespondToRequest(contactId, false);
                LoadPendingRequests();
            }
        }

        // ========== Settings ==========
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Settings will be implemented later.");
        }
        private async Task ConnectWebSocket()
        {
            _wsService = new WebSocketService($"wss://localhost:5001/ws?userId={_currentUserId}", OnWebSocketMessage);
            await _wsService.ConnectAsync();
        }

        private void OnWebSocketMessage(string message)
        {
            // Parse simple JSON manually (you can use System.Text.Json if you prefer)
            var doc = System.Text.Json.JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "new_message":
                    var convId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());
                    if (_selectedChat?.ConversationId == convId)
                        LoadMessages(convId);
                    else
                        LoadContacts(); // Update last message preview (optional)
                    break;

                case "friend_request":
                    LoadPendingRequests();
                    break;

                case "contact_added":
                    LoadContacts();
                    LoadPendingRequests();
                    break;
            }
        }
    }

    // ---------- Display helper classes ----------
    public class ChatItem
    {
        public string ContactName { get; set; }
        public string Username { get; set; }
        public string LastMessage { get; set; }
        public Guid ContactUserId { get; set; }
        public string PublicKey { get; set; }
        public Guid ConversationId { get; set; }
    }

    public class FriendRequestItem
    {
        public string FromUsername { get; set; }
        public Guid RequestId { get; set; }
    }

    public class MessageDisplay
    {
        public string Text { get; set; }
        public string SenderName { get; set; }
        public string Time { get; set; }
        public string Alignment { get; set; }
        public string BubbleColor { get; set; }
        public string ShowSender { get; set; }
    }


}