using System;
using System.Collections.Generic;
using System.Windows;

namespace Diploma
{
    public partial class MainWindow : Window
    {
        private string _myUsername = "Alice";

        public MainWindow()
        {
            InitializeComponent();
            LoadSampleMessages();
        }

        private void LoadSampleMessages()
        {
            var messages = new List<MessageDisplay>
            {
                new MessageDisplay
                {
                    Text = "Hello!",
                    SenderName = "Bob",
                    Time = "10:30 AM",
                    Alignment = "Left",
                    BubbleColor = "#E0E0E0",
                    ShowSender = "Visible"
                },
                new MessageDisplay
                {
                    Text = "Hi Bob, how are you?",
                    SenderName = _myUsername,
                    Time = "10:31 AM",
                    Alignment = "Right",
                    BubbleColor = "#0078D7",
                    ShowSender = "Collapsed"
                }
            };

            MessagesListBox.ItemsSource = messages;
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            string text = MessageTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var message = new MessageDisplay
            {
                Text = text,
                SenderName = _myUsername,
                Time = DateTime.Now.ToString("t"),
                Alignment = "Right",
                BubbleColor = "#0078D7",
                ShowSender = "Collapsed"
            };

            var list = MessagesListBox.ItemsSource as List<MessageDisplay>;
            list?.Add(message);
            MessagesListBox.Items.Refresh();
            MessageTextBox.Clear();
        }
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