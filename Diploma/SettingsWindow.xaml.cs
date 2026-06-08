using Diploma.Services;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Diploma
{
    public partial class SettingsWindow : Window
    {
        private readonly Guid _userId;
        private readonly string _username;
        private readonly ApiService _api;
        private string _newDisplayName;

        public string NewDisplayName => _newDisplayName;

        public SettingsWindow(Guid userId, string username, string currentDisplayName, ApiService api)
        {
            InitializeComponent();

            _userId = userId;
            _username = username;
            _api = api;
            DisplayNameTextBox.Text = currentDisplayName;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string newDisplayName = DisplayNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newDisplayName))
            {
                StatusText.Text = "Display name cannot be empty.";
                return;
            }

            // --- Password change (optional) ---
            string curPass = CurrentPasswordBox.Password;
            string newPass = NewPasswordBox.Password;
            string confirm = ConfirmNewPasswordBox.Password;

            if (!string.IsNullOrWhiteSpace(curPass))
            {
                if (newPass != confirm)
                {
                    StatusText.Text = "New passwords do not match.";
                    return;
                }
                if (newPass.Length < 8)
                {
                    StatusText.Text = "New password must be at least 8 characters.";
                    return;
                }

                // Fetch the stored salt from the server
                string saltBase64 = await _api.GetSalt(_username);
                if (saltBase64 == null)
                {
                    StatusText.Text = "Failed to retrieve user info.";
                    return;
                }

                // Build old blob: salt + SHA256(password || salt)
                byte[] salt = Convert.FromBase64String(saltBase64);
                byte[] oldHash = SHA256.HashData(Encoding.UTF8.GetBytes(curPass).Concat(salt).ToArray());
                byte[] oldBlob = salt.Concat(oldHash).ToArray();
                string oldBlobB64 = Convert.ToBase64String(oldBlob);

                // Build new blob with a fresh salt
                byte[] newSalt = new byte[16];
                RandomNumberGenerator.Fill(newSalt);
                byte[] newHash = SHA256.HashData(Encoding.UTF8.GetBytes(newPass).Concat(newSalt).ToArray());
                byte[] newBlob = newSalt.Concat(newHash).ToArray();
                string newBlobB64 = Convert.ToBase64String(newBlob);

                bool pwOk = await _api.ChangePassword(_userId, oldBlobB64, newBlobB64);
                if (!pwOk)
                {
                    StatusText.Text = "Current password is incorrect or change failed.";
                    return;
                }
            }

            // --- Update display name ---
            bool displayOk = await _api.UpdateDisplayName(_userId, newDisplayName);
            if (!displayOk)
            {
                StatusText.Text = "Failed to update display name.";
                return;
            }

            _newDisplayName = newDisplayName;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}