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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media;
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
        private HashSet<Guid> _onlineUserIds = new HashSet<Guid>();

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
                    UnreadCount = c.UnreadCount,
                    LastMessageTimestamp = c.LastMessageTimestamp
                };
            }).ToList();

            ChatListBox.ItemsSource = chatItems;

            if (selectedUsername != null)
            {
                var previouslySelected = chatItems.FirstOrDefault(ci => ci.Username == selectedUsername);
                if (previouslySelected != null)
                    ChatListBox.SelectedItem = previouslySelected;
            }
            ApplyOnlineStatusFromCache();
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

 
        private void ScrollMessagesToBottom()
        {
            // Find the ScrollViewer inside the ListBox template
            if (VisualTreeHelper.GetChildrenCount(MessagesListBox) > 0)
            {
                var border = VisualTreeHelper.GetChild(MessagesListBox, 0) as Decorator;
                var scrollViewer = border?.Child as ScrollViewer;
                scrollViewer?.ScrollToEnd();
            }
        }
        // ========== Chat selection ==========
        private async void ChatListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
          
            if (ChatListBox.SelectedItem is not ChatItem selected)
                return;

            _selectedChat = selected;
            MessagesListBox.ItemsSource = null;
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

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                // Deselect the chat    
                ChatListBox.SelectedItem = null;
                _selectedChat = null;

                // Reset the right panel to the initial state
                ChatHeaderText.Text = "Select a chat";
                OnlineStatusText.Visibility = Visibility.Collapsed;
                MessagesListBox.ItemsSource = null;
                MessageTextBox.Clear();
                // Remove focus from the chat list and return it to the window
                ChatListBox.Focusable = false;
                ChatListBox.Focusable = true;
                Keyboard.ClearFocus();
            }
        }


        // ========== Load messages ==========
        private async void LoadMessages(Guid conversationId)
        {
            // Capture the conversation we are loading for
            var targetConvId = conversationId;

            string contactPublicKey = _selectedChat?.PublicKey;
            if (contactPublicKey == null) return;

            var messages = await _api.GetMessages(targetConvId);

            // Run decryption on a background thread
            var displayMessages = await Task.Run(() =>
            {
                var list = new ObservableCollection<MessageDisplay>();
                foreach (var msg in messages)
                {
                    string plainText;
                    try
                    {
                        byte[] ciphertext = Convert.FromBase64String(msg.EncryptedContent);
                        byte[] decrypted = ECCryptoService.DecryptData(
                            ciphertext, _currentPrivateKey, contactPublicKey);
                        plainText = Encoding.UTF8.GetString(decrypted);
                    }
                    catch
                    {
                        plainText = "[decryption failed]";
                    }

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

                    list.Add(new MessageDisplay
                    {
                        Text = plainText,
                        SenderName = msg.SenderId == _currentUserId ? _currentDisplayName : _selectedChat?.ContactName ?? "",
                        Time = msg.Timestamp.ToLocalTime().ToString("t"),
                        StatusIcon = statusIcon,
                        MessageId = msg.MessageId,
                        IsMine = msg.SenderId == _currentUserId   // <-- true for own messages
                    });
                }
                return list;
            });

            // ** Guard: only apply if the selected chat still matches **
            // Apply results on the UI thread
            if (_selectedChat == null || _selectedChat.ConversationId != targetConvId)
                return;

            // Reuse the same ObservableCollection if possible, to avoid full visual rebuild
            var currentCollection = MessagesListBox.ItemsSource as ObservableCollection<MessageDisplay>;
            if (currentCollection != null)
            {
                currentCollection.Clear();
                foreach (var msg in displayMessages)
                    currentCollection.Add(msg);
            }
            else
            {
                MessagesListBox.ItemsSource = displayMessages;
            }

            // Scroll after layout is done (low priority)
            Dispatcher.BeginInvoke(new Action(() => ScrollMessagesToBottom()),
                                   System.Windows.Threading.DispatcherPriority.Background);

            // Mark as read
            if (messages.Any(m => m.SenderId != _currentUserId && m.Status != "Read"))
            {
                await _api.MarkAsRead(targetConvId);
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

            // Encrypt the message
            byte[] ciphertext = ECCryptoService.EncryptData(
                Encoding.UTF8.GetBytes(text),
                _currentPrivateKey,
                _selectedChat.PublicKey);

            Guid messageId = Guid.NewGuid();

            // ---- 1. Show the message instantly ----
            var displayMsg = new MessageDisplay
            {
                Text = text,
                SenderName = _currentDisplayName,
                Time = DateTime.Now.ToString("t"),
                StatusIcon = "✓",
                MessageId = messageId,
                IsMine = true
            };

            if (MessagesListBox.ItemsSource is ObservableCollection<MessageDisplay> messages)
            {
                messages.Add(displayMsg);
                // REMOVED: MessagesListBox.Items.Refresh();  <-- ObservableCollection handles this
            }

            // Scroll to the new message AFTER the layout has finished, on a low priority
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessagesListBox.ScrollIntoView(displayMsg);
            }), System.Windows.Threading.DispatcherPriority.Background);

            // Update the chat list preview (the rest is the same, but we keep Refresh for now)
            _selectedChat.LastMessage = text.Length > 25 ? text.Substring(0, 25) + "..." : text;
            _selectedChat.LastMessageStatus = "Sent";
            _selectedChat.IsLastMessageFromMe = true;
            _selectedChat.LastMessageTimestamp = DateTime.Now;
            ChatListBox.Items.Refresh();

            MessageTextBox.Clear();

            // ---- 2. Send to server in background (fire-and-forget) ----
            _ = Task.Run(async () =>
            {
                bool success = await _api.SendMessage(messageId, _selectedChat.ConversationId, ciphertext);
                if (!success)
                {
                    // Mark the message as failed on the UI thread
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        displayMsg.StatusIcon = "⚠";
                        MessagesListBox.Items.Refresh();
                    });
                }
            });
        }

        private void MessageTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                // Prevent the "ding" sound or any default behavior
                e.Handled = true;
                // Trigger the send button click logic
                SendButton_Click(sender, e);
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

                // Immediately remove the request from the UI (no server reload needed)
                var pendingList = FriendRequestsListBox.ItemsSource as List<FriendRequestItem>;
                var item = pendingList?.FirstOrDefault(r => r.RequestId == contactId);
                if (item != null)
                {
                    pendingList.Remove(item);
                    FriendRequestsListBox.Items.Refresh();
                }

                // The WebSocket "contact_added" handler will reload the chat list and set online status.
                // Do NOT call LoadContactsAsync() or LoadPendingRequestsAsync() here.
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

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem &&
                menuItem.DataContext is MessageDisplay message)
            {
                Clipboard.SetText(message.Text);
            }
        }

        private async Task ConnectWebSocket()
        {
            _wsService = new WebSocketService($"wss://localhost:5001/ws?userId={_currentUserId}", OnWebSocketMessage);
            await _wsService.ConnectAsync();
        }

        private void ApplyOnlineStatusFromCache()
        {
            var list = ChatListBox.ItemsSource as List<ChatItem>;
            if (list == null) return;
            foreach (var item in list)
            {
                item.IsOnline = _onlineUserIds.Contains(item.ContactUserId);
            }
            ChatListBox.Items.Refresh();
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
                                                             // Update timestamp from the server notification
                        if (doc.RootElement.TryGetProperty("timestamp", out var tsElement))
                        {
                            targetChat.LastMessageTimestamp = DateTime.Parse(tsElement.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind);
                        }
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
                    var addedContactUserId = Guid.Parse(doc.RootElement.GetProperty("contactUserId").GetString());
                    bool addedIsOnline = doc.RootElement.GetProperty("isOnline").GetBoolean();

                    // Update the online cache immediately
                    if (addedIsOnline)
                        _onlineUserIds.Add(addedContactUserId);
                    else
                        _onlineUserIds.Remove(addedContactUserId);

                    // Reload contacts, then apply all cached online statuses
                    _ = LoadContactsAsync().ContinueWith(_ =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ApplyOnlineStatusFromCache();
                        });
                    });

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

                    // Keep the cache updated
                    if (isOnline)
                        _onlineUserIds.Add(presUserId);
                    else
                        _onlineUserIds.Remove(presUserId);

                    // Update the chat list item (if exists)
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
        public DateTime? LastMessageTimestamp { get; set; }

        public string LastMessageTimeText
        {
            get
            {
                if (LastMessageTimestamp == null) return "";
                var dt = LastMessageTimestamp.Value.ToLocalTime();
                var now = DateTime.Now;

                if (dt.Date == now.Date)
                    return dt.ToString("t");                     // 14:35

                if (dt.Date > now.Date.AddDays(-7))
                    return dt.ToString("dddd");                  // Monday

                // More than a week: show day + month if same year, else full date
                if (dt.Year == now.Year)
                    return dt.ToString("d MMMM");                // 6 May
                else
                    return dt.ToString("M/d/yyyy");              // 5/30/2026
            }
        }
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
        public string StatusIcon { get; set; }
        public Guid MessageId { get; set; }
        public bool IsMine { get; set; }   // <-- new
    }
}