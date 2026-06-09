using Diploma.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Diploma
{
    public partial class LoginWindow : Window
    {
        private static readonly HttpClient client = new HttpClient
        {
            BaseAddress = new Uri("https://localhost:5001")
        };

        public LoginWindow()
        {
            InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text.Trim();
            string pass = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pass))
            {
                StatusText.Text = "Username and password are required.";
                return;
            }

            try
            {
                // Get salt from server
                var saltResponse = await client.GetAsync($"api/auth/salt/{username}");
                if (!saltResponse.IsSuccessStatusCode)
                {
                    StatusText.Text = "User not found.";
                    return;
                }
                var saltData = await saltResponse.Content.ReadFromJsonAsync<SaltResponse>();
                byte[] salt = Convert.FromBase64String(saltData.Salt);

                // Compute blob
                byte[] passwordBytes = Encoding.UTF8.GetBytes(pass);
                byte[] hash = SHA256.HashData(passwordBytes.Concat(salt).ToArray());
                byte[] blob = salt.Concat(hash).ToArray();
                string blobB64 = Convert.ToBase64String(blob);

                // Login
                var loginPayload = new { username, password = blobB64 };
                var loginResponse = await client.PostAsJsonAsync("api/auth/login", loginPayload);

                if (loginResponse.IsSuccessStatusCode)
                {
                    var userData = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
                    Guid userId = userData.UserId;
                    string displayName = userData.DisplayName;
                    string publicKey = userData.EccPublicKey;

                    // Load private key from local DB
                    var keyManager = new KeyManagerService(userId);
                    string privateKey = keyManager.LoadPrivateKey(userId);

                    // Prevent multiple logins for same user
                    string mutexName = $"DiplomaMessenger_User_{username}";
                    var mutex = new Mutex(true, mutexName, out bool createdNew);
                    if (!createdNew)
                    {
                        MessageBox.Show("This user is already logged in on another instance.",
                            "Login denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                        mutex.Dispose();
                        return;
                    }

                    // Save credentials if "Remember me" is checked
                    if (RememberMeCheckBox.IsChecked == true)
                        CredentialsStorageService.SaveCredentials(username, blobB64);

                    MessageBox.Show($"Login successful! Welcome, {displayName}.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    var mainWindow = new MainWindow(userId, username, displayName, privateKey, publicKey, mutex);
                    mainWindow.Show();
                    this.Close();
                }
                else
                {
                    string err = await loginResponse.Content.ReadAsStringAsync();
                    StatusText.Text = $"Login failed: {err}";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Connection error: {ex.Message}";
            }
        }

        private void CreateAccountButton_Click(object sender, RoutedEventArgs e)
        {
            var registerWindow = new RegisterWindow();
            registerWindow.Show();
            this.Close();
        }

        private void PasswordTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                LoginButton_Click(sender, e);
            }
        }
        private void UsernameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                LoginButton_Click(sender, e);
            }
        }
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {          
                Keyboard.ClearFocus();
            }
        }
    }

    // DTOs for JSON responses
    public class SaltResponse { public string Salt { get; set; } }
    public class LoginResponse { public Guid UserId { get; set; } public string DisplayName { get; set; } public string EccPublicKey { get; set; } }
}