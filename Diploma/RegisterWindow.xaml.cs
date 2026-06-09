using Diploma.Services;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Json;   // for ReadFromJsonAsync
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;        // for JsonSerializer if needed
using System.Windows;
using System.Windows.Controls;

namespace Diploma
{
    public partial class RegisterWindow : Window
    {
        private static readonly HttpClient client = new HttpClient
        {
            BaseAddress = new Uri("https://localhost:5001") // adjust to your server address
        };

        public RegisterWindow() => InitializeComponent();

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text.Trim();
            string display = DisplayNameBox.Text.Trim();
            string pass = PasswordBox.Password;
            string confirm = ConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pass))
            {
                StatusText.Text = "Username and password are required.";
                return;
            }
            if (pass != confirm)
            {
                StatusText.Text = "Passwords do not match.";
                return;
            }
            if (pass.Length < 8)
            {
                StatusText.Text = "Password must be at least 8 characters.";
                return;
            }

            try
            {
                // 1. Hash password
                byte[] salt = new byte[16];
                RandomNumberGenerator.Fill(salt);
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(pass).Concat(salt).ToArray());
                byte[] hashWithSalt = salt.Concat(hash).ToArray();
                string passwordHashB64 = Convert.ToBase64String(hashWithSalt);

                // 2. Generate ECC key pair (in memory only)
                var (pubKey, privKey) = ECCryptoService.GenerateKeyPair();

                // 3. Call server
                var payload = new
                {
                    username = username,
                    password = passwordHashB64,
                    displayName = string.IsNullOrWhiteSpace(display) ? username : display,
                    eccPublicKey = pubKey
                };
                var response = await client.PostAsJsonAsync("api/auth/register", payload);

                if (response.IsSuccessStatusCode)
                {
                    // 4. Get real userId and save private key locally
                    var result = await response.Content.ReadFromJsonAsync<RegisterResponse>();
                    Guid realUserId = result.UserId;
                    var keyManager = new KeyManagerService(realUserId);
                    keyManager.SavePrivateKey(privKey);

                    MessageBox.Show("Registration successful!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    // 5. Clear all input fields
                    ClearFields();

                    // 6. Open login window and close this one
                    var loginWindow = new LoginWindow();
                    loginWindow.Show();
                    this.Close();
                }
                else
                {
                    string err = await response.Content.ReadAsStringAsync();
                    StatusText.Text = $"Error: {err}";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Connection error: {ex.Message}";
            }
        }
        private void ClearFields()
        {
            UsernameBox.Text = string.Empty;
            DisplayNameBox.Text = string.Empty;
            PasswordBox.Password = string.Empty;
            ConfirmPasswordBox.Password = string.Empty;
            StatusText.Text = string.Empty;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            //using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=localstorage.db");
            //conn.Open();
            //var cmd = conn.CreateCommand();
            //cmd.CommandText = "SELECT COUNT(*) FROM keypairs";
            //long count = (long)cmd.ExecuteScalar();
            //MessageBox.Show($"Keys stored: {count}");

            // Return to the login window
            var loginWindow = new LoginWindow();
            loginWindow.Show();
            this.Close();
        }
        public class RegisterResponse
        {
            public Guid UserId { get; set; }
        }

    }
}