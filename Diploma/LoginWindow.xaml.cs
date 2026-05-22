using Diploma.Models;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Diploma
{
    public partial class LoginWindow : Window
    {
        private static readonly HttpClient client = new HttpClient
        {
            BaseAddress = new Uri("https://localhost:5001")
        };

        public LoginWindow() => InitializeComponent();

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
                // Step 1: get salt from server
                var saltResponse = await client.GetAsync($"api/auth/salt/{username}");
                if (!saltResponse.IsSuccessStatusCode)
                {
                    StatusText.Text = "User not found.";
                    return;
                }
                var saltData = await saltResponse.Content.ReadFromJsonAsync<SaltResponse>();
                byte[] salt = Convert.FromBase64String(saltData.Salt);

                // Step 2: compute the same salt+hash blob as during registration
                byte[] passwordBytes = Encoding.UTF8.GetBytes(pass);
                byte[] hash = SHA256.HashData(passwordBytes.Concat(salt).ToArray());   // password first, then salt
                byte[] blob = salt.Concat(hash).ToArray();
                string blobB64 = Convert.ToBase64String(blob);

                // Step 3: login
                var loginPayload = new
                {
                    username = username,
                    password = blobB64
                };
                var loginResponse = await client.PostAsJsonAsync("api/auth/login", loginPayload);

                if (loginResponse.IsSuccessStatusCode)
                {
                    var userData = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
                    Guid userId = userData.UserId;

                    // Step 4: load private key from local DB
                    var keyManager = new KeyManager(userId);
                    // we need to retrieve the encrypted key bytes from the local DB
                    // we'll add a method for that in KeyManager
                    string privateKey = keyManager.LoadPrivateKey(userId);  // we'll fix KeyManager to read by userId

                    MessageBox.Show($"Login successful! Welcome, {userData.DisplayName}.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    // Step 5: open main window with user info
                    var mainWindow = new MainWindow();
                    // you can pass the user data and private key to MainWindow later
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
    }

    // DTOs for JSON responses
    public class SaltResponse { public string Salt { get; set; } }
    public class LoginResponse { public Guid UserId { get; set; } public string DisplayName { get; set; } public string EccPublicKey { get; set; } }
}