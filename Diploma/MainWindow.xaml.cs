using Diploma.Crypto;
using Diploma.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // Observable collections for automatic UI updates without Refresh()
        private ObservableCollection<ChatItem> _chatItems;
        private ObservableCollection<FriendRequestItem> _friendRequests;
        private ObservableCollection<MessageDisplay> _messages = new ObservableCollection<MessageDisplay>();
        private bool _isLoadingMessages = false;
        private List<MessageDisplay> _pendingIncomingMessages = new List<MessageDisplay>();
        private readonly List<MessageDisplay> _pendingOutgoingMessages = new List<MessageDisplay>();
        public MainWindow(Guid userId, string username, string displayName,
                          string privateKey, string publicKey, Mutex userMutex)
        {
            InitializeComponent();
            MessagesListBox.ItemsSource = _messages;
            _currentUserId = userId;
            _currentUsername = username;
            _currentDisplayName = displayName;
            _currentPrivateKey = privateKey;
            _currentPublicKey = publicKey;
            _userMutex = userMutex;

            _api = new ApiService("https://localhost:5001", userId);
            _keyManager = new KeyManager(userId);

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

            _chatItems = new ObservableCollection<ChatItem>(chatItems);
            ChatListBox.ItemsSource = _chatItems;
            ResortChats();
            if (selectedUsername != null)
            {
                var previouslySelected = _chatItems.FirstOrDefault(ci => ci.Username == selectedUsername);
                if (previouslySelected != null)
                    ChatListBox.SelectedItem = previouslySelected;
            }
            ApplyOnlineStatusFromCache();
        }

        private async Task LoadPendingRequestsAsync()
        {
            var requests = await _api.GetPendingRequests();
            _friendRequests = new ObservableCollection<FriendRequestItem>(
                requests.Select(r => new FriendRequestItem
                {
                    FromUsername = r.FromUsername,
                    RequestId = r.ContactId
                })
            );
            FriendRequestsListBox.ItemsSource = _friendRequests;
        }

        private void ScrollMessagesToBottom()
        {
            if (VisualTreeHelper.GetChildrenCount(MessagesListBox) > 0)
            {
                var border = VisualTreeHelper.GetChild(MessagesListBox, 0) as Decorator;
                var scrollViewer = border?.Child as ScrollViewer;
                scrollViewer?.ScrollToEnd();
                MessagesListBox.Opacity = 1;
            }
        }

        private async void ChatListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChatListBox.SelectedItem is not ChatItem selected)
                return;

            _selectedChat = selected;
            _messages.Clear();
            if (selected.IsOnline)
            {
                OnlineStatusText.Text = "Online";
                OnlineStatusText.Foreground = new SolidColorBrush(Colors.LimeGreen);
                OnlineStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                OnlineStatusText.Text = "Offline";
                OnlineStatusText.Foreground = new SolidColorBrush(Colors.Gray);
                OnlineStatusText.Visibility = Visibility.Visible;
            }
            ChatHeaderText.Text = selected.ContactName;
            selected.UnreadCount = 0;
            // No Refresh() needed – UnreadCount property change will notify UI

            var conv = await _api.GetConversation(selected.Username);
            if (conv != null)
            {
                selected.ConversationId = conv.ConversationId;
                // Clear any pending messages from a previous load
                _pendingIncomingMessages.Clear();
                _isLoadingMessages = true;
                LoadMessages(selected.ConversationId);
            }
            else
            {
                MessageBox.Show("Conversation not found. Make sure you are friends.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                MessagesListBox.ItemsSource = null;
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ChatListBox.SelectedItem = null;
                _selectedChat = null;
                _messages.Clear();
                ChatHeaderText.Text = "Select a chat";
                OnlineStatusText.Visibility = Visibility.Collapsed;
                MessageTextBox.Clear();

                ChatListBox.Focusable = false;
                ChatListBox.Focusable = true;
                Keyboard.ClearFocus();
            }
        }

        private async void LoadMessages(Guid conversationId)
        {
            var targetConvId = conversationId;

            string contactPublicKey = _selectedChat?.PublicKey;
            if (contactPublicKey == null) return;

            // 1. Hide the chat while we load (no rapid scroll)
            MessagesListBox.Opacity = 0;

            _isLoadingMessages = true;
            _pendingIncomingMessages.Clear();
            _pendingOutgoingMessages.Clear();

            var serverMessages = await _api.GetMessages(targetConvId);

            var decryptedList = await Task.Run(() =>
            {
                var list = new List<MessageDisplay>();
                foreach (var msg in serverMessages)
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
                        IsMine = msg.SenderId == _currentUserId
                    });
                }
                return list;
            });

            if (_selectedChat == null || _selectedChat.ConversationId != targetConvId)
            {
                _isLoadingMessages = false;
                MessagesListBox.Opacity = 1;   // restore visibility if we abort
                return;
            }

            // 2. Clear and repopulate the persistent collection
            _messages.Clear();
            foreach (var msg in decryptedList)
                _messages.Add(msg);

            // Add any pending messages that arrived during loading
            foreach (var msg in _pendingIncomingMessages)
                _messages.Add(msg);
            _pendingIncomingMessages.Clear();

            foreach (var msg in _pendingOutgoingMessages)
                _messages.Add(msg);
            _pendingOutgoingMessages.Clear();

            _isLoadingMessages = false;

            // 3. Show the chat and scroll to the bottom
            Dispatcher.BeginInvoke(new Action(ScrollMessagesToBottom),
                                   System.Windows.Threading.DispatcherPriority.Loaded);

            // 4. Now the user can see the messages – mark them as read
            if (serverMessages.Any(m => m.SenderId != _currentUserId && m.Status != "Read"))
            {
                _ = Task.Run(() => _api.MarkAsRead(targetConvId));
            }
        }

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

            var displayMsg = new MessageDisplay
            {
                Text = text,
                SenderName = _currentDisplayName,
                Time = DateTime.Now.ToString("t"),
                StatusIcon = "✓",
                MessageId = messageId,
                IsMine = true
            };

            // If the chat is still loading, hold the message in the pending outgoing buffer
            if (_isLoadingMessages)
            {
                _pendingOutgoingMessages.Add(displayMsg);
            }
            else
            {
                _messages.Add(displayMsg);

                // Scroll to bottom
                Dispatcher.BeginInvoke(new Action(() => ScrollMessagesToBottom()),
                                       System.Windows.Threading.DispatcherPriority.Background);

            }

            // Update the chat list preview immediately (doesn't depend on _messages)
            _selectedChat.LastMessage = text.Length > 25 ? text.Substring(0, 25) + "..." : text;
            _selectedChat.LastMessageStatus = "Sent";
            _selectedChat.IsLastMessageFromMe = true;
            _selectedChat.LastMessageTimestamp = DateTime.Now;
            MoveChatToTop(_selectedChat);
            ChatListBox.ScrollIntoView(_selectedChat);

            MessageTextBox.Clear();

            // Fire-and-forget: actually send the message to the server
            _ = Task.Run(async () =>
            {
                bool success = await _api.SendMessage(messageId, _selectedChat.ConversationId, ciphertext);
                if (!success)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        displayMsg.StatusIcon = "⚠";
                    });
                }
            });
        }

        private void MessageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendButton_Click(sender, e);
            }
        }

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

        private async void AcceptRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Guid contactId)
            {
                await _api.RespondToRequest(contactId, true);

                // Remove from the observable collection directly
                var item = _friendRequests?.FirstOrDefault(r => r.RequestId == contactId);
                if (item != null)
                {
                    _friendRequests.Remove(item);
                }

                // The WebSocket "contact_added" handler will reload the chat list and set online status.
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
            if (_chatItems == null) return;
            foreach (var item in _chatItems)
            {
                item.IsOnline = _onlineUserIds.Contains(item.ContactUserId);
            }
            // No Refresh() – IsOnline property notifies automatically
        }
        private void ResortChats()
        {
            var sorted = _chatItems
                .OrderByDescending(c => c.LastMessageTimestamp ?? DateTime.MinValue)
                .ToList();

            _chatItems.Clear();
            foreach (var item in sorted)
                _chatItems.Add(item);
        }
        private void MoveChatToTop(ChatItem chat)
        {
            var index = _chatItems.IndexOf(chat);
            if (index <= 0) return;

            _chatItems.Move(index, 0);
        }
        private async void OnWebSocketMessage(string message)
        {
            var doc = System.Text.Json.JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "new_message":
                    var newConvId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());
                    string encryptedContentB64 = doc.RootElement.GetProperty("encryptedContent").GetString();
                    Guid senderId = Guid.Parse(doc.RootElement.GetProperty("senderId").GetString());

                    // If the conversation is currently open, decrypt and add to _messages
                    if (_selectedChat?.ConversationId == newConvId)
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                byte[] ciphertext = Convert.FromBase64String(encryptedContentB64);
                                byte[] decrypted = ECCryptoService.DecryptData(
                                    ciphertext, _currentPrivateKey, _selectedChat.PublicKey);
                                string plainText = Encoding.UTF8.GetString(decrypted);

                                // Safe timestamp parsing
                                DateTime messageTime = DateTime.Now;
                                if (doc.RootElement.TryGetProperty("timestamp", out var tsElement))
                                    messageTime = DateTime.Parse(tsElement.GetString(), null,
                                        System.Globalization.DateTimeStyles.RoundtripKind);

                                var displayMsg = new MessageDisplay
                                {
                                    Text = plainText,
                                    SenderName = _selectedChat.ContactName,
                                    Time = messageTime.ToLocalTime().ToString("t"),
                                    StatusIcon = "",
                                    MessageId = Guid.Parse(doc.RootElement.GetProperty("messageId").GetString()),
                                    IsMine = false
                                };

                                Application.Current.Dispatcher.BeginInvoke(() =>
                                {
                                    if (_isLoadingMessages)
                                    {
                                        _pendingIncomingMessages.Add(displayMsg);
                                    }
                                    else
                                    {
                                        _messages.Add(displayMsg);
                                        // Scroll to the newly added message (safe)
                                        // Scroll to bottom
                                        Dispatcher.BeginInvoke(new Action(() => ScrollMessagesToBottom()),
                                                               System.Windows.Threading.DispatcherPriority.Background);

                                    }

                                    // Mark as read
                                    _ = _api.MarkAsRead(newConvId);
                                });
                            }
                            catch { }
                        });
                    }

                    // Update last message preview in sidebar (unchanged)
                    var targetChat = _chatItems?.FirstOrDefault(c => c.ConversationId == newConvId);
                    if (targetChat != null)
                    {
                        try
                        {
                            byte[] ciphertext = Convert.FromBase64String(encryptedContentB64);
                            byte[] decrypted = ECCryptoService.DecryptData(ciphertext, _currentPrivateKey, targetChat.PublicKey);
                            string fullText = Encoding.UTF8.GetString(decrypted);
                            targetChat.LastMessage = fullText.Length > 25 ? fullText.Substring(0, 25) + "..." : fullText;
                        }
                        catch { targetChat.LastMessage = "[encrypted]"; }

                        targetChat.IsLastMessageFromMe = false;
                        targetChat.LastMessageStatus = "";

                        if (doc.RootElement.TryGetProperty("timestamp", out var tsElement))
                        {
                            targetChat.LastMessageTimestamp = DateTime.Parse(tsElement.GetString(), null,
                                System.Globalization.DateTimeStyles.RoundtripKind);
                        }

                        if (_selectedChat?.ConversationId != newConvId)
                            targetChat.UnreadCount++;
                    }
                    break;

                case "friend_request":
                    var reqId = Guid.Parse(doc.RootElement.GetProperty("requestId").GetString());
                    var fromUsername = doc.RootElement.GetProperty("fromUsername").GetString();

                    _friendRequests?.Add(new FriendRequestItem
                    {
                        RequestId = reqId,
                        FromUsername = fromUsername
                    });
                    break;

                case "contact_added":
                    var addedContactUserId = Guid.Parse(doc.RootElement.GetProperty("contactUserId").GetString());
                    bool addedIsOnline = doc.RootElement.GetProperty("isOnline").GetBoolean();

                    if (addedIsOnline)
                        _onlineUserIds.Add(addedContactUserId);
                    else
                        _onlineUserIds.Remove(addedContactUserId);

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
                    {
                        var msgId = Guid.Parse(doc.RootElement.GetProperty("messageId").GetString());
                        var newStatus = doc.RootElement.GetProperty("newStatus").GetString();
                        var statusConvId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());

                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            // Update the message in the open chat (if this conversation is open)
                            var msg = _messages.FirstOrDefault(m => m.MessageId == msgId);
                            if (msg != null)
                            {
                                msg.StatusIcon = newStatus switch
                                {
                                    "Sent" => "✓",
                                    "Read" => "✓✓",
                                    _ => msg.StatusIcon
                                };
                            }

                            // Update sidebar last message status
                            var chat = _chatItems.FirstOrDefault(c => c.ConversationId == statusConvId);
                            if (chat != null && chat.IsLastMessageFromMe)
                            {
                                chat.LastMessageStatus = newStatus;
                            }
                        });

                        break;
                    }

                case "presence":
                    var presUserId = Guid.Parse(doc.RootElement.GetProperty("userId").GetString());
                    var isOnline = doc.RootElement.GetProperty("isOnline").GetBoolean();

                    if (isOnline)
                        _onlineUserIds.Add(presUserId);
                    else
                        _onlineUserIds.Remove(presUserId);

                    var chat = _chatItems?.FirstOrDefault(c => c.ContactUserId == presUserId);
                    if (chat != null)
                    {
                        chat.IsOnline = isOnline;
                    }

                    if (_selectedChat?.ContactUserId == presUserId)
                    {
                        OnlineStatusText.Text = isOnline ? "Online" : "Offline";
                        OnlineStatusText.Foreground = isOnline
                            ? new SolidColorBrush(Colors.LimeGreen)
                            : new SolidColorBrush(Colors.Gray);
                        OnlineStatusText.Visibility = Visibility.Visible;
                    }
                    break;
            }
        }
    }

    // ---------- Display helper classes ----------
    public class ChatItem : INotifyPropertyChanged
    {
        private string _lastMessage;
        private string _lastMessageStatus;
        private int _unreadCount;
        private bool _isOnline;

        public string ContactName { get; set; }
        public string Username { get; set; }
        public Guid ContactUserId { get; set; }
        public string PublicKey { get; set; }
        public Guid ConversationId { get; set; }
        public bool IsLastMessageFromMe { get; set; }
        private DateTime? _lastMessageTimestamp;
        public DateTime? LastMessageTimestamp
        {
            get => _lastMessageTimestamp;
            set
            {
                if (_lastMessageTimestamp != value)
                {
                    _lastMessageTimestamp = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LastMessageTimeText));
                }
            }
        }

        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged(); }
        }

        public string LastMessage
        {
            get => _lastMessage;
            set { _lastMessage = value; OnPropertyChanged(); }
        }

        public string LastMessageStatus
        {
            get => _lastMessageStatus;
            set { _lastMessageStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastMessageStatusIcon)); }
        }

        public int UnreadCount
        {
            get => _unreadCount;
            set { _unreadCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUnread)); }
        }

        public bool HasUnread => UnreadCount > 0;

        public string LastMessageStatusIcon => IsLastMessageFromMe
            ? LastMessageStatus switch
            {
                "Sent" => "✓",
                "Read" => "✓✓",
                _ => ""
            }
            : "";

        public string LastMessageTimeText
        {
            get
            {
                if (LastMessageTimestamp == null) return "";
                var dt = LastMessageTimestamp.Value.ToLocalTime();
                var now = DateTime.Now;

                if (dt.Date == now.Date)
                    return dt.ToString("t");

                if (dt.Date > now.Date.AddDays(-7))
                    return dt.ToString("dddd");

                if (dt.Year == now.Year)
                    return dt.ToString("d MMMM");
                else
                    return dt.ToString("M/d/yyyy");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class FriendRequestItem
    {
        public string FromUsername { get; set; }
        public Guid RequestId { get; set; }
    }

    public class MessageDisplay : INotifyPropertyChanged
    {
        public Guid MessageId { get; set; }
        public string Text { get; set; }
        public string SenderName { get; set; }
        public string Time { get; set; }
        public bool IsMine { get; set; }

        private string _statusIcon;
        public string StatusIcon
        {
            get => _statusIcon;
            set
            {
                if (_statusIcon != value)
                {
                    _statusIcon = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}