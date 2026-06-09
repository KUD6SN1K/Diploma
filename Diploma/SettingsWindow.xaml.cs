using Diploma.Helpers;
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
        private bool _initialAcceptRequests;
        public string NewDisplayName => _newDisplayName;

        public SettingsWindow(Guid userId, string username, string currentDisplayName, ApiService api)
        {
            InitializeComponent();

            _userId = userId;
            _username = username;
            _api = api;
            DisplayNameTextBox.Text = currentDisplayName;
            LoadProfileAsync();
        }
        private async void LoadProfileAsync()
        {
            var profile = await _api.GetProfile(_userId);
            if (profile != null)
            {
                _initialAcceptRequests = profile.AcceptFriendRequests;
                BlockRequestsToggle.IsOn = !_initialAcceptRequests;   // On = blocked
            }
        }
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. Validate display name
            string newDisplayName = DisplayNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newDisplayName))
            {
                StatusText.Text = "Display name cannot be empty.";
                return;
            }

            // 2. Password change (optional)
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

                // Fetch stored salt
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

                // Build new blob with fresh salt
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

            // 3. Update friend request setting if changed
            bool newBlocked = BlockRequestsToggle.IsOn;
            bool newAccept = !newBlocked;
            if (newAccept != _initialAcceptRequests)
            {
                bool toggleOk = await _api.ToggleFriendRequests(_userId, newAccept);
                if (!toggleOk)
                {
                    StatusText.Text = "Failed to update friend request setting.";
                    return;
                }
            }

            // 4. Update display name
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
        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to log out?",
                "Log out",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                CredentialsStorage.DeleteCredentials();
                string exePath = Environment.ProcessPath!;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
                Application.Current.Shutdown();
            }
        }
    }
}