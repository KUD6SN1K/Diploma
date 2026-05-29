using Diploma.Crypto;
using Diploma.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
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
        private Mutex _userMutex;
        public MainWindow(Guid userId, string username, string displayName,
                          string privateKey, string publicKey, Mutex userMutex)
        {
            InitializeComponent();
          
            _currentUserId = userId;
            _currentUsername = username;
            _currentDisplayName = displayName;
            _currentPrivateKey = privateKey;
            _currentPublicKey = publicKey;
            _userMutex = userMutex;

            _api = new ApiService("https://localhost:5001", userId);
            _keyManager = new KeyManager(userId);
            // Wait until the window is fully loaded, then load data and connect
            this.Loaded += async (s, e) =>
            {
                await LoadContactsAsync();
                await LoadPendingRequestsAsync();
                _ = ConnectWebSocket();
            };
            this.Closed += async (s, e) =>
            {
                if (_wsService != null)
                    await _wsService.CloseAsync();
            };
        }

        // ========== Load contacts ==========
        private async Task LoadContactsAsync()
        {
            string selectedUsername = _selectedChat?.Username;

            var contacts = await _api.GetContacts();
            var chatItems = contacts.Select(c => {
                string preview = "";
                if (!string.IsNullOrEmpty(c.LastMessageEncrypted))
                {
                    try
                    {
                        byte[] ciphertext = Convert.FromBase64String(c.LastMessageEncrypted);
                        byte[] decrypted = ECCryptoService.DecryptData(ciphertext, _currentPrivateKey, c.PublicKey);
                        string fullText = Encoding.UTF8.GetString(decrypted);
                        preview = fullText.Length > 25 ? fullText.Substring(0, 25) + "..." : fullText;
                    }
                    catch { preview = "[encrypted]"; }
                }

                return new ChatItem
                {
                    ContactName = c.DisplayName,
                    Username = c.Username,
                    LastMessage = preview,
                    ContactUserId = c.UserId,
                    PublicKey = c.PublicKey,
                    ConversationId = c.ConversationId,
                    LastMessageStatus = c.LastMessageStatus ?? "",
                    IsLastMessageFromMe = c.LastMessageSenderId == _currentUserId,
                    UnreadCount = c.UnreadCount
                };
            }).ToList();

            ChatListBox.ItemsSource = chatItems;

            if (selectedUsername != null)
            {
                var previouslySelected = chatItems.FirstOrDefault(ci => ci.Username == selectedUsername);
                if (previouslySelected != null)
                    ChatListBox.SelectedItem = previouslySelected;
            }
        }

        // ========== Load pending friend requests ==========
        private async Task LoadPendingRequestsAsync()
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
            if (selected.IsOnline)
            {
                OnlineStatusText.Text = "Online";
                OnlineStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LimeGreen);
                OnlineStatusText.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                OnlineStatusText.Text = "Offline";
                OnlineStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
                OnlineStatusText.Visibility = System.Windows.Visibility.Visible;
            }
            ChatHeaderText.Text = selected.ContactName;
            // Reset unread count for the selected chat
            selected.UnreadCount = 0;
            ChatListBox.Items.Refresh();   // optional, can be done after conversation load
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
            var displayMessages = new ObservableCollection<MessageDisplay>();

            foreach (var msg in messages)
            {
                string plainText;
                try
                {
                    byte[] ciphertext = Convert.FromBase64String(msg.EncryptedContent);
                    byte[] decrypted = ECCryptoService.DecryptData(ciphertext, _currentPrivateKey, contactPublicKey);
                    plainText = Encoding.UTF8.GetString(decrypted);
                }
                catch
                {
                    plainText = "[decryption failed]";
                }

                string senderName = msg.SenderId == _currentUserId ? _currentDisplayName : _selectedChat.ContactName;

                string statusIcon = "";
                if (msg.SenderId == _currentUserId)
                {
                    statusIcon = msg.Status switch
                    {
                        "Sent" => "✓",
                        "Read" => "✓✓",
                        _ => ""
                    };
                }

                displayMessages.Add(new MessageDisplay
                {   
                    Text = plainText,
                    SenderName = senderName,
                    Time = msg.Timestamp.ToString("t"),
                    Alignment = msg.SenderId == _currentUserId ? "Right" : "Left",
                    BubbleColor = msg.SenderId == _currentUserId ? "#0078D7" : "#E0E0E0",
                    ShowSender = msg.SenderId == _currentUserId ? "Collapsed" : "Visible",
                    StatusIcon = statusIcon,
                    MessageId = msg.MessageId
                });
            }

            MessagesListBox.ItemsSource = displayMessages;

            // Mark unread messages as read (this triggers server to set Read and notify sender)
            if (messages.Any(m => m.SenderId != _currentUserId && m.Status != "Read"))
            {
                await _api.MarkAsRead(conversationId);
            }
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
                // Create and add the local message with status ✓
                var displayMsg = new MessageDisplay
                {
                    Text = text,
                    SenderName = _currentDisplayName,
                    Time = DateTime.Now.ToString("t"),
                    Alignment = "Right",
                    BubbleColor = "#0078D7",
                    ShowSender = "Collapsed",
                    StatusIcon = "✓",          // <-- one check
                    MessageId = messageId
                };

                // Add to the same ObservableCollection used by the ListBox
                var messages = MessagesListBox.ItemsSource as ObservableCollection<MessageDisplay>;
                messages?.Add(displayMsg);
                MessagesListBox.Items.Refresh();

                // Update the chat list preview
                _selectedChat.LastMessage = text.Length > 25 ? text.Substring(0, 25) + "..." : text;
                _selectedChat.LastMessageStatus = "Sent";
                _selectedChat.IsLastMessageFromMe = true;
                ChatListBox.Items.Refresh();

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
                await LoadPendingRequestsAsync();
                await LoadContactsAsync();
            }
        }

        private async void DeclineRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Guid contactId)
            {
                await _api.RespondToRequest(contactId, false);
                await LoadPendingRequestsAsync();
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
                    var newConvId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());
                    string encryptedContentB64 = doc.RootElement.GetProperty("encryptedContent").GetString();
                    Guid senderId = Guid.Parse(doc.RootElement.GetProperty("senderId").GetString());

                    // Load messages if this conversation is open
                    if (_selectedChat?.ConversationId == newConvId)
                        LoadMessages(newConvId);

                    // Update last message preview
                    var chatList = ChatListBox.ItemsSource as List<ChatItem>;
                    var targetChat = chatList?.FirstOrDefault(c => c.ConversationId == newConvId);
                    if (targetChat != null)
                    {
                        // Update preview text
                        try
                        {
                            byte[] ciphertext = Convert.FromBase64String(encryptedContentB64);
                            byte[] decrypted = ECCryptoService.DecryptData(ciphertext, _currentPrivateKey, targetChat.PublicKey);
                            string fullText = System.Text.Encoding.UTF8.GetString(decrypted);
                            targetChat.LastMessage = fullText.Length > 25 ? fullText.Substring(0, 25) + "..." : fullText;
                        }
                        catch { targetChat.LastMessage = "[encrypted]"; }

                        // The new message is from the other user, so clear any sender‑side marks
                        targetChat.IsLastMessageFromMe = false;
                        targetChat.LastMessageStatus = "";   // no check marks for the receiver

                        // Increment unread count if this conversation is NOT currently selected
                        if (_selectedChat?.ConversationId != newConvId)
                        {
                            targetChat.UnreadCount++;
                            // Do not set LastMessageStatus here; the status is already cleared above
                        }
                        ChatListBox.Items.Refresh();
                    }
                    break;

                case "friend_request":
                    _ = LoadPendingRequestsAsync();
                    break;

                case "contact_added":
                    _ = LoadContactsAsync();
                    _ = LoadPendingRequestsAsync();
                    break;
                case "message_status":
                    var msgId = Guid.Parse(doc.RootElement.GetProperty("messageId").GetString());
                    var newStatus = doc.RootElement.GetProperty("newStatus").GetString(); // "Read"
                    var statusConvId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());

                    // Update in open chat
                    if (_selectedChat?.ConversationId == statusConvId)
                    {
                        var displayList = MessagesListBox.ItemsSource as ObservableCollection<MessageDisplay>;
                        var msg = displayList?.FirstOrDefault(m => m.MessageId == msgId);
                        if (msg != null)
                        {
                            msg.StatusIcon = "✓✓";
                            MessagesListBox.Items.Refresh();
                        }
                    }

                    // Update sidebar last message icon
                    var sidebarChatList = ChatListBox.ItemsSource as List<ChatItem>;
                    var chatItem = sidebarChatList?.FirstOrDefault(c => c.ConversationId == statusConvId);
                    if (chatItem != null && chatItem.IsLastMessageFromMe)
                    {
                        chatItem.LastMessageStatus = newStatus;
                        ChatListBox.Items.Refresh();
                    }
                    break;
                case "presence":
                    var presUserId = Guid.Parse(doc.RootElement.GetProperty("userId").GetString());
                    var isOnline = doc.RootElement.GetProperty("isOnline").GetBoolean();
                    //MessageBox.Show($"Presence received: {presUserId} online={isOnline}", "Debug");
                    // Update chat list – rename to avoid conflict
                    var presenceChatList = ChatListBox.ItemsSource as List<ChatItem>;
                    var chat = presenceChatList?.FirstOrDefault(c => c.ContactUserId == presUserId);
                    if (chat != null)
                    {
                        chat.IsOnline = isOnline;
                        ChatListBox.Items.Refresh();
                    }

                    // Update header if this is the selected chat
                    if (_selectedChat?.ContactUserId == presUserId)
                    {
                        OnlineStatusText.Text = isOnline ? "Online" : "Offline";
                        OnlineStatusText.Foreground = isOnline
                            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LimeGreen)
                            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
                        OnlineStatusText.Visibility = System.Windows.Visibility.Visible;
                    }
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
        public bool IsOnline { get; set; }
        public string LastMessageStatus { get; set; }
        public bool IsLastMessageFromMe { get; set; }

        public string LastMessageStatusIcon => IsLastMessageFromMe
            ? LastMessageStatus switch
            {
                "Sent" => "✓",
                "Read" => "✓✓",
                _ => ""
            }
            : "";
        public int UnreadCount { get; set; }
        public bool HasUnread => UnreadCount > 0;
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
        public string StatusIcon { get; set; }   
        public Guid MessageId { get; set; }    
    }
}