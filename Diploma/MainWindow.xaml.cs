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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
        private Dictionary<Guid, HashSet<Guid>> _unreadMessageIds = new Dictionary<Guid, HashSet<Guid>>();
        // Observable collections for automatic UI updates without Refresh()
        private ObservableCollection<ChatItem> _chatItems;
        private ObservableCollection<FriendRequestItem> _friendRequests;
        private ObservableCollection<object> _messages = new ObservableCollection<object>();
        private bool _isLoadingMessages = false;
        private List<MessageDisplay> _pendingIncomingMessages = new List<MessageDisplay>();
        private readonly List<MessageDisplay> _pendingOutgoingMessages = new List<MessageDisplay>();
        private readonly List<Guid> _pendingReadReceipts = new List<Guid>();
        private bool _isClickPending = false;
        private object _clickedItem = null;
        private Point _clickPoint;
        private CancellationTokenSource _loadCts = new();
        private const int MaxMessageLength = 10000;
        private ScrollViewer _messagesScrollViewer;    // for scroll detection
        private bool _hasMoreMessages = true;
        private bool _loadingOlderMessages = false;
        private DateTime? _oldestMessageTimestamp;     // timestamp of the oldest loaded message
        private const int PageSize = 50;
        private bool _suppressScrollEvents = false;
        private DispatcherTimer _scrollSuppressTimer;
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

        private void AttachScrollViewer()
        {
            if (_messagesScrollViewer != null)
                return;   // already attached

            // Find the ScrollViewer inside the ListBox
            _messagesScrollViewer = FindVisualChild<ScrollViewer>(MessagesListBox);
            if (_messagesScrollViewer != null)
                _messagesScrollViewer.ScrollChanged += MessagesScrollChanged;
        }

        // Reuse the helper from ScrollMessagesToBottom – already exists
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
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
                    ContactId = c.ContactId,
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
            AttachScrollViewer();
            if (_messagesScrollViewer != null)
            {
                _suppressScrollEvents = true;

                // Create / reset a timer that will re-enable scroll events after 300 ms
                if (_scrollSuppressTimer == null)
                {
                    _scrollSuppressTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(300)
                    };
                    _scrollSuppressTimer.Tick += (s, e) =>
                    {
                        _suppressScrollEvents = false;
                        _scrollSuppressTimer.Stop();
                    };
                }
                else
                {
                    _scrollSuppressTimer.Stop();
                }
                _scrollSuppressTimer.Start();

                _messagesScrollViewer.ScrollToEnd();
                MessagesListBox.Opacity = 1;
            }
        }

        private async void ChatListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChatListBox.SelectedItem is not ChatItem selected)
                return;
            // Cancel any previous LoadMessages that might still be running
            _loadCts.Cancel();
            _loadCts = new CancellationTokenSource();
            var currentToken = _loadCts.Token;
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
                LoadMessages(selected.ConversationId, currentToken);
                MessageTextBox.Focus();
            }
            else
            {
                MessageBox.Show("Conversation not found. Make sure you are friends.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                MessagesListBox.ItemsSource = null;
            }
        }
        private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (MessageTextBox.Text.Length > MaxMessageLength)
            {
                MessageTextBox.BorderBrush = new SolidColorBrush(Colors.Red);
                MessageTextBox.BorderThickness = new Thickness(1);
                MessageTextBox.ToolTip = $"Message is too long. Maximum is {MaxMessageLength} characters.";
            }
            else
            {
                // Restore normal style
                MessageTextBox.ClearValue(TextBox.BorderBrushProperty);
                MessageTextBox.ClearValue(TextBox.BorderThicknessProperty);
                MessageTextBox.ClearValue(TextBox.ToolTipProperty);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _loadCts.Cancel();
                _loadCts = new CancellationTokenSource();
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

        private async void LoadMessages(Guid conversationId, CancellationToken cancellationToken)
        {
            var targetConvId = conversationId;

            string contactPublicKey = _selectedChat?.PublicKey;
            if (contactPublicKey == null) return;
            // Mark all messages in this conversation as read (clear unread tracker)
            if (_unreadMessageIds.ContainsKey(targetConvId))
                _unreadMessageIds[targetConvId].Clear();
            _isLoadingMessages = true;
            _pendingIncomingMessages.Clear();
            _pendingOutgoingMessages.Clear();
            _pendingReadReceipts.Clear();
            MessagesListBox.Opacity = 0;

            var serverMessages = await _api.GetMessages(targetConvId, PageSize, null);

            if (cancellationToken.IsCancellationRequested)
            {
                _isLoadingMessages = false;
                MessagesListBox.Opacity = 1;
                return;
            }

            _hasMoreMessages = (serverMessages.Count == PageSize);

            var decryptedList = await Task.Run(() =>
            {
                var list = new List<MessageDisplay>();
                foreach (var msg in serverMessages)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    string plainText;
                    try
                    {
                        byte[] ciphertext = Convert.FromBase64String(msg.EncryptedContent);
                        byte[] decrypted = ECCryptoService.DecryptData(
                            ciphertext, _currentPrivateKey, contactPublicKey);
                        plainText = Encoding.UTF8.GetString(decrypted);
                    }
                    catch { plainText = "[decryption failed]"; }

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
                        Timestamp = msg.Timestamp,
                        StatusIcon = statusIcon,
                        MessageId = msg.MessageId,
                        IsMine = msg.SenderId == _currentUserId
                    });
                }
                return list;
            }, cancellationToken);

            // Double‑check we are still on the same chat and not cancelled
            if (cancellationToken.IsCancellationRequested ||
                _selectedChat == null ||
                _selectedChat.ConversationId != targetConvId)
            {
                _isLoadingMessages = false;
                MessagesListBox.Opacity = 1;
                return;
            }

            _oldestMessageTimestamp = serverMessages.FirstOrDefault()?.Timestamp;

            // Build final list with date separators for the loaded messages
            var finalList = InsertDateSeparators(decryptedList);

            // Add pending messages with date separators
            foreach (var msg in _pendingIncomingMessages)
                AddMessageWithDateSeparator(msg, finalList);
            _pendingIncomingMessages.Clear();

            foreach (var msg in _pendingOutgoingMessages)
                AddMessageWithDateSeparator(msg, finalList);
            _pendingOutgoingMessages.Clear();

            // Replace the collection
            _messages = new ObservableCollection<object>(finalList);
            MessagesListBox.ItemsSource = _messages;

            if (_pendingReadReceipts.Count > 0)
            {
                var uniqueConvIds = _pendingReadReceipts.Distinct().ToList();
                _pendingReadReceipts.Clear();
                foreach (var convId in uniqueConvIds)
                    _ = Task.Run(() => _api.MarkAsRead(convId));
            }

            _isLoadingMessages = false;

            Dispatcher.BeginInvoke(new Action(ScrollMessagesToBottom),
                                   System.Windows.Threading.DispatcherPriority.Loaded);

            if (serverMessages.Any(m => m.SenderId != _currentUserId && m.Status != "Read"))
                _ = Task.Run(() => _api.MarkAsRead(targetConvId));
        }
        private async void MessagesScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 1. Ignore events that were caused programmatically (ScrollToEnd, etc.)
            if (_suppressScrollEvents)
                return;

            // 2. Ignore layout-only events (resize, minimise, maximise)
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
                return;

            // 3. Normal guard conditions
            if (_loadingOlderMessages || !_hasMoreMessages)
                return;

            if (_messagesScrollViewer.VerticalOffset <= 10)
                await LoadOlderMessages();
        }
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedChat == null)
            {
                MessageBox.Show("Select a chat first.");
                return;
            }

            string text = MessageTextBox.Text.Trim();
            if (text.Length > MaxMessageLength)
            {
                // Show error on the text box
                MessageTextBox.BorderBrush = new SolidColorBrush(Colors.Red);
                MessageTextBox.BorderThickness = new Thickness(1);
                MessageTextBox.ToolTip = $"Message is too long. Maximum is {MaxMessageLength} characters.";
                return;
            }
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
                IsMine = true,
                Timestamp = DateTime.Now
            };

            // If the chat is still loading, hold the message in the pending outgoing buffer
            if (_isLoadingMessages)
            {
                _pendingOutgoingMessages.Add(displayMsg);
            }
            else
            {
                AddMessageWithDateSeparator(displayMsg);

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
        private async Task LoadOlderMessages()
        {
            if (_selectedChat == null || !_hasMoreMessages || _loadingOlderMessages)
                return;

            if (!_oldestMessageTimestamp.HasValue)
                return;

            _loadingOlderMessages = true;

            double previousExtentHeight = _messagesScrollViewer?.ExtentHeight ?? 0;

            var olderMessages = await _api.GetMessages(
                _selectedChat.ConversationId,
                PageSize,
                _oldestMessageTimestamp.Value);

            if (olderMessages.Count < PageSize)
                _hasMoreMessages = false;

            if (olderMessages.Count == 0)
            {
                _loadingOlderMessages = false;
                return;
            }

            _oldestMessageTimestamp = olderMessages.First().Timestamp;

            string contactPublicKey = _selectedChat.PublicKey;

            // Decrypt on background thread
            var decryptedOlder = await Task.Run(() =>
            {
                var list = new List<MessageDisplay>();
                foreach (var msg in olderMessages)
                {
                    string plainText;
                    try
                    {
                        byte[] ciphertext = Convert.FromBase64String(msg.EncryptedContent);
                        byte[] decrypted = ECCryptoService.DecryptData(
                            ciphertext, _currentPrivateKey, contactPublicKey);
                        plainText = Encoding.UTF8.GetString(decrypted);
                    }
                    catch { plainText = "[decryption failed]"; }

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
                        Timestamp = msg.Timestamp,
                        StatusIcon = statusIcon,
                        MessageId = msg.MessageId,
                        IsMine = msg.SenderId == _currentUserId
                    });
                }
                return list;
            });

            // ----- Smart insertion with correct date separators -----
            // 1. Find the date of the currently oldest MESSAGE (skip leading separators)
            DateTime? lastDate = null;
            for (int i = 0; i < _messages.Count; i++)
            {
                if (_messages[i] is MessageDisplay firstMsg)
                {
                    lastDate = firstMsg.Timestamp.Date;
                    break;
                }
            }

            // 2. Build the list of older items (messages + separators) using the existing lastDate
            var toInsert = new List<object>();
            foreach (var msg in decryptedOlder)
            {
                if (lastDate == null || msg.Timestamp.Date != lastDate.Value)
                {
                    toInsert.Add(new DateSeparator { Text = FormatDateLabel(msg.Timestamp.Date) });
                    lastDate = msg.Timestamp.Date;
                }
                toInsert.Add(msg);
            }

            // 3. Insert the whole older block at the beginning (oldest first)
            for (int i = toInsert.Count - 1; i >= 0; i--)
                _messages.Insert(0, toInsert[i]);

            // 4. Remove any accidental duplicate separators (safety)
            RemoveDuplicateDateSeparators();
            RemoveRedundantDateSeparators();
            // Restore scroll position
            await Dispatcher.InvokeAsync(() =>
            {
                _messagesScrollViewer?.UpdateLayout();
                double newExtentHeight = _messagesScrollViewer?.ExtentHeight ?? 0;
                _messagesScrollViewer?.ScrollToVerticalOffset(newExtentHeight - previousExtentHeight);
            }, System.Windows.Threading.DispatcherPriority.Background);

            _loadingOlderMessages = false;
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
        private void FriendUsernameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddFriendButton_Click(sender, e);
            }
        }
        private void ChatListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                e.Handled = true;   // block arrow keys from changing selection
            }
        }

        private async void AcceptRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Guid contactId)
            {
                await _api.RespondToRequest(contactId, true);

                // Immediately remove the request from the UI
                var item = _friendRequests?.FirstOrDefault(r => r.RequestId == contactId);
                if (item != null)
                    _friendRequests.Remove(item);
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
        private void ChatListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Hit‑test where the user clicked
            var hit = VisualTreeHelper.HitTest(ChatListBox, e.GetPosition(ChatListBox));
            var hitElement = hit?.VisualHit as DependencyObject;

            // Check if the click is on the scrollbar – if so, let the event pass through
            if (FindVisualParent<ScrollBar>(hitElement) != null)
            {
                _isClickPending = false;
                _clickedItem = null;
                return;    // do NOT mark e.Handled, scrollbar works normally
            }

            // Find the clicked chat item (if any)
            var item = FindVisualParent<ListBoxItem>(hitElement)?.DataContext;

            if (item is ChatItem)
            {
                _clickedItem = item;
                _clickPoint = e.GetPosition(null);
                _isClickPending = true;
                e.Handled = true;   // block only when we’re going to handle the click ourselves
            }
            else
            {
                // Clicked empty space – ignore, but don’t block the event
                _isClickPending = false;
                _clickedItem = null;
            }
        }

        private void ChatListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isClickPending || _clickedItem == null) return;

            // Make sure the mouse is still over the same chat item
            var hit = VisualTreeHelper.HitTest(ChatListBox, e.GetPosition(ChatListBox));
            var item = FindVisualParent<ListBoxItem>(hit?.VisualHit as DependencyObject)?.DataContext;

            if (item == _clickedItem)
            {
                ChatListBox.SelectedItem = _clickedItem;
            }

            _isClickPending = false;
            _clickedItem = null;
        }
        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null && !(child is T))
                child = VisualTreeHelper.GetParent(child);
            return child as T;
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
                    Guid newMsgId = Guid.Parse(doc.RootElement.GetProperty("messageId").GetString());

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

                                DateTime messageTime = DateTime.Now;
                                if (doc.RootElement.TryGetProperty("timestamp", out var tsElement))
                                    messageTime = DateTime.Parse(tsElement.GetString(), null,
                                        System.Globalization.DateTimeStyles.RoundtripKind);

                                var displayMsg = new MessageDisplay
                                {
                                    Text = plainText,
                                    SenderName = _selectedChat.ContactName,
                                    Time = messageTime.ToLocalTime().ToString("t"),
                                    Timestamp = messageTime,
                                    StatusIcon = "",
                                    MessageId = newMsgId,
                                    IsMine = false
                                };

                                Application.Current.Dispatcher.BeginInvoke(() =>
                                {
                                    if (_isLoadingMessages)
                                    {
                                        _pendingIncomingMessages.Add(displayMsg);
                                        // Defer the read receipt until the load finishes
                                        _pendingReadReceipts.Add(newConvId);
                                    }
                                    else
                                    {
                                        _messages.Add(displayMsg);
                                        Dispatcher.BeginInvoke(new Action(() => ScrollMessagesToBottom()),
                                                               System.Windows.Threading.DispatcherPriority.Background);

                                        // Chat is open and visible → mark as read immediately
                                        _ = Task.Run(() => _api.MarkAsRead(newConvId));
                                    }
                                });
                            }
                            catch { }
                        });
                    }

                    // Update last message preview in sidebar
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
                        {
                            targetChat.UnreadCount++;
                            if (!_unreadMessageIds.ContainsKey(newConvId))
                                _unreadMessageIds[newConvId] = new HashSet<Guid>();
                            _unreadMessageIds[newConvId].Add(newMsgId);
                        }

                        MoveChatToTop(targetChat);
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
                    var addedContactId = Guid.Parse(doc.RootElement.GetProperty("contactId").GetString());   // <-- new
                    var addedContactUserId = Guid.Parse(doc.RootElement.GetProperty("contactUserId").GetString());
                    string addedContactUsername = doc.RootElement.GetProperty("contactUsername").GetString();
                    string addedContactDisplayName = doc.RootElement.GetProperty("contactDisplayName").GetString();
                    string addedContactPublicKey = doc.RootElement.GetProperty("publicKey").GetString();
                    Guid addedConversationId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());
                    bool addedIsOnline = doc.RootElement.GetProperty("isOnline").GetBoolean();

                    if (addedIsOnline)
                        _onlineUserIds.Add(addedContactUserId);
                    else
                        _onlineUserIds.Remove(addedContactUserId);

                    var newChat = new ChatItem
                    {
                        ContactName = addedContactDisplayName,
                        Username = addedContactUsername,
                        ContactUserId = addedContactUserId,
                        PublicKey = addedContactPublicKey,
                        ConversationId = addedConversationId,
                        IsOnline = addedIsOnline,
                        ContactId = addedContactId,   // <-- now correctly set
                        LastMessage = "",
                        LastMessageStatus = "",
                        IsLastMessageFromMe = false,
                        UnreadCount = 0,
                        LastMessageTimestamp = null
                    };

                    _chatItems.Add(newChat);
                    break;

                case "message_status":
                    {
                        var msgId = Guid.Parse(doc.RootElement.GetProperty("messageId").GetString());
                        var newStatus = doc.RootElement.GetProperty("newStatus").GetString();
                        var statusConvId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());

                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            // Update the message in the open chat (if this conversation is open)
                            var msg = _messages.OfType<MessageDisplay>().FirstOrDefault(m => m.MessageId == msgId);
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
                case "delete_message":
                    var delMsgId = Guid.Parse(doc.RootElement.GetProperty("messageId").GetString());
                    var delConvId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());

                    // Remove from UI if the conversation is open
                    if (_selectedChat?.ConversationId == delConvId)
                    {
                        var msgToRemove = _messages.OfType<MessageDisplay>().FirstOrDefault(m => m.MessageId == delMsgId);
                        if (msgToRemove != null)
                        {
                            int idx = _messages.IndexOf(msgToRemove);
                            _messages.RemoveAt(idx);
                            RemoveRedundantDateSeparators();
                            RemoveOrphanedDateSeparators();
                        }
                    }
                    else
                    {
                        // If the deleted message was tracked as unread, decrement the unread count
                        if (_unreadMessageIds.TryGetValue(delConvId, out var unreadSet) && unreadSet.Remove(delMsgId))
                        {
                            var delChat = _chatItems?.FirstOrDefault(c => c.ConversationId == delConvId);
                            if (delChat != null && delChat.UnreadCount > 0)
                                delChat.UnreadCount--;
                        }
                    }

                    await UpdateChatPreviewFromServer(delConvId);
                    break;

                case "clear_history":
                    var clearConvId = Guid.Parse(doc.RootElement.GetProperty("conversationId").GetString());
                    if (_selectedChat?.ConversationId == clearConvId)
                    {
                        _messages.Clear();
                    }
                    _unreadMessageIds.Remove(clearConvId);
                    // Update sidebar preview
                    var clearChat = _chatItems?.FirstOrDefault(c => c.ConversationId == clearConvId);
                    if (clearChat != null)
                    {
                        clearChat.LastMessage = "";
                        clearChat.LastMessageTimestamp = null;
                        clearChat.LastMessageStatus = "";
                        clearChat.IsLastMessageFromMe = false;
                        clearChat.UnreadCount = 0;
                    }
                    await UpdateChatPreviewFromServer(clearConvId);
                    break;

                case "delete_friend":
                    var delContactId = Guid.Parse(doc.RootElement.GetProperty("contactId").GetString());
                    var friendChat = _chatItems?.FirstOrDefault(c => c.ContactId == delContactId);
                    if (friendChat != null)
                    {
                        _chatItems.Remove(friendChat);
                        if (_selectedChat == friendChat)
                        {
                            _selectedChat = null;
                            ChatHeaderText.Text = "Select a chat";
                            OnlineStatusText.Visibility = Visibility.Collapsed;
                            _messages.Clear();
                        }
                    }
                    break;
            }
        }
        private string FormatDateLabel(DateTime date)
        {
            if (date.Year == DateTime.Now.Year)
                return date.ToString("d MMMM");            // e.g., "5 June"
            return date.ToString("d MMMM yyyy");           // e.g., "5 June 2025"
        }
        private List<object> InsertDateSeparators(List<MessageDisplay> messages)
        {
            var result = new List<object>();
            DateTime? lastDate = null;

            foreach (var msg in messages)
            {
                var msgDate = msg.Timestamp.Date;
                if (lastDate == null || msgDate != lastDate.Value)
                {
                    result.Add(new DateSeparator { Text = FormatDateLabel(msgDate) });
                    lastDate = msgDate;
                }
                result.Add(msg);
            }
            return result;
        }
        private void AddMessageWithDateSeparator(MessageDisplay msg, List<object> targetList = null)
        {
            var list = targetList ?? (_messages as IList<object>);
            if (list == null) return;

            // Find the last MessageDisplay in the list
            DateTime? lastDate = null;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is MessageDisplay lastMsg)
                {
                    lastDate = lastMsg.Timestamp.Date;
                    break;
                }
            }

            if (lastDate == null || msg.Timestamp.Date != lastDate.Value)
            {
                list.Add(new DateSeparator { Text = FormatDateLabel(msg.Timestamp.Date) });
            }
            list.Add(msg);
        }
        private void RemoveDuplicateDateSeparators()
        {
            for (int i = 1; i < _messages.Count; i++)
            {
                if (_messages[i] is DateSeparator d1 && _messages[i - 1] is DateSeparator d2 && d1.Text == d2.Text)
                {
                    _messages.RemoveAt(i);
                    i--; // stay at the same index since we removed one
                }
            }
        }
        private void RemoveRedundantDateSeparators()
        {
            for (int i = _messages.Count - 2; i >= 0; i--)
            {
                if (_messages[i] is DateSeparator &&
                    i > 0 && i < _messages.Count - 1 &&
                    _messages[i - 1] is MessageDisplay left &&
                    _messages[i + 1] is MessageDisplay right &&
                    left.Timestamp.Date == right.Timestamp.Date)
                {
                    _messages.RemoveAt(i);
                }
            }
        }
        private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is Guid messageId)
            {
                bool success = await _api.DeleteMessage(messageId);
                if (success)
                {
                    // Remove the message locally
                    var msgToRemove = _messages.OfType<MessageDisplay>().FirstOrDefault(m => m.MessageId == messageId);
                    if (msgToRemove != null)
                    {
                        int index = _messages.IndexOf(msgToRemove);
                        _messages.RemoveAt(index);
                        RemoveRedundantDateSeparators();
                        RemoveOrphanedDateSeparators();
                        if (_selectedChat != null)
                            await UpdateChatPreviewFromServer(_selectedChat.ConversationId);
                    }
                }
            }
        }
        private void RemoveOrphanedDateSeparators()
        {
            // Remove any separator that is followed by another separator or is at the end (no message after)
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i] is DateSeparator)
                {
                    bool isLast = i == _messages.Count - 1;
                    bool followedBySeparator = !isLast && _messages[i + 1] is DateSeparator;
                    bool followedByNothing = isLast;
                    if (followedBySeparator || followedByNothing)
                    {
                        _messages.RemoveAt(i);
                    }
                }
            }
        }
        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is Guid conversationId)
            {
                var result = MessageBox.Show("Clear all messages in this chat? This cannot be undone.",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;

                bool success = await _api.ClearHistory(conversationId);
                if (success)
                {
                    if (_unreadMessageIds.ContainsKey(conversationId))
                        _unreadMessageIds.Remove(conversationId);
                    // If this conversation is currently open, clear the messages
                    if (_selectedChat?.ConversationId == conversationId)
                    {
                        _messages.Clear();
                    }
                    // Update the chat preview
                    await UpdateChatPreviewFromServer(conversationId);
                }
            }
        }

        private async void DeleteFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is Guid contactId)
            {
                var result = MessageBox.Show("Delete this friend? All messages will be lost.",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;

                bool success = await _api.DeleteContact(contactId);
                if (success)
                {
                    // Remove from sidebar
                    var chat = _chatItems?.FirstOrDefault(c => c.ContactId == contactId);
                    if (chat != null)
                    {
                        _chatItems.Remove(chat);
                        if (_selectedChat == chat)
                        {
                            _selectedChat = null;
                            ChatHeaderText.Text = "Select a chat";
                            OnlineStatusText.Visibility = Visibility.Collapsed;
                            _messages.Clear();
                        }
                    }
                }
            }
        }
        private void UpdateChatPreviewAfterDelete(Guid conversationId)
        {
            var chat = _chatItems?.FirstOrDefault(c => c.ConversationId == conversationId);
            if (chat == null) return;

            // Get remaining messages (only MessageDisplay) from the open chat if it's selected,
            // otherwise we need to fetch from server. But we can reconstruct from _messages if open.
            List<MessageDisplay> remaining = null;
            if (_selectedChat?.ConversationId == conversationId)
            {
                remaining = _messages.OfType<MessageDisplay>().ToList();
            }
            else
            {
                // Not open – we must reload from server? For simplicity, just clear preview.
                chat.LastMessage = "";
                chat.LastMessageTimestamp = null;
                chat.LastMessageStatus = "";
                chat.IsLastMessageFromMe = false;
                chat.UnreadCount = 0;
                return;
            }

            if (remaining.Count > 0)
            {
                var last = remaining.Last();
                chat.LastMessage = last.Text.Length > 25 ? last.Text.Substring(0, 25) + "..." : last.Text;
                chat.LastMessageTimestamp = last.Timestamp;
                chat.LastMessageStatus = last.IsMine ? (last.StatusIcon == "✓✓" ? "Read" : "Sent") : "";
                chat.IsLastMessageFromMe = last.IsMine;
            }
            else
            {
                chat.LastMessage = "";
                chat.LastMessageTimestamp = null;
                chat.LastMessageStatus = "";
                chat.IsLastMessageFromMe = false;
            }
            // Unread count remains (it's not affected by deletion, unless we deleted unread messages – but we only allow sender to delete).
        }
        private void ChatListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Prevent the right‑click from selecting the item
            e.Handled = true;
        }
        private async Task UpdateChatPreviewFromServer(Guid conversationId)
        {
            var chat = _chatItems?.FirstOrDefault(c => c.ConversationId == conversationId);
            if (chat == null) return;

            var lastMsgDto = await _api.GetLastMessage(conversationId);
            if (lastMsgDto == null || !lastMsgDto.Exists)
            {
                // No messages left – clear the preview
                chat.LastMessage = "";
                chat.LastMessageTimestamp = null;
                chat.LastMessageStatus = "";
                chat.IsLastMessageFromMe = false;
                chat.UnreadCount = 0;   // safe for clear history; for delete message this might be wrong, but we'll handle separately
                return;
            }

            // Decrypt preview
            string preview = "";
            try
            {
                byte[] ciphertext = Convert.FromBase64String(lastMsgDto.EncryptedContent);
                byte[] decrypted = ECCryptoService.DecryptData(ciphertext, _currentPrivateKey, chat.PublicKey);
                string fullText = Encoding.UTF8.GetString(decrypted);
                preview = fullText.Length > 25 ? fullText.Substring(0, 25) + "..." : fullText;
            }
            catch { preview = "[encrypted]"; }

            chat.LastMessage = preview;
            chat.LastMessageTimestamp = lastMsgDto.Timestamp;
            chat.LastMessageStatus = lastMsgDto.SenderId == _currentUserId ? lastMsgDto.Status : "";
            chat.IsLastMessageFromMe = lastMsgDto.SenderId == _currentUserId;
            // Unread count stays as is (not affected by this update)
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
        public Guid ContactId { get; set; }
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
        public DateTime Timestamp { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    public class DateSeparator
    {
        public string Text { get; set; }
    }
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate MessageTemplate { get; set; }
        public DataTemplate DateSeparatorTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is DateSeparator)
                return DateSeparatorTemplate;
            return MessageTemplate;
        }
    }
}