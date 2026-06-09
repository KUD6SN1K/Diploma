using Diploma.Models;
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
        private const string LocalConnectionString = "Data Source=localstorage.db";
        private static readonly HttpClient client = new HttpClient
        {
            BaseAddress = new Uri("https://localhost:5001")
        };

        public LoginWindow()
        {
            InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                var creds = LoadCredentials();
                if (creds.HasValue)
                {
                    // Attempt auto-login
                    var (savedUsername, savedBlob) = creds.Value;
                    var success = await TryAutoLogin(savedUsername, savedBlob);
                    if (success) return;   // main window already opened
                                           // Auto-login failed – clear credentials and let user login manually
                    DeleteCredentials();
                }
            };
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
                    string displayName = userData.DisplayName;
                    string publicKey = userData.EccPublicKey;

                    // Load private key from local DB
                    var keyManager = new KeyManager(userId);
                    string privateKey = keyManager.LoadPrivateKey(userId);  // as you defined

                    // --- Prevent multiple logins for the same user ---
                    string mutexName = $"DiplomaMessenger_User_{username}";
                    var mutex = new System.Threading.Mutex(true, mutexName, out bool createdNew);
                    if (!createdNew)
                    {
                        MessageBox.Show("This user is already logged in on another instance.",
                                        "Login denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                        mutex.Dispose();
                        return;
                    }

                    MessageBox.Show($"Login successful! Welcome, {displayName}.", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    if (RememberMeCheckBox.IsChecked == true)
                    {
                        SaveCredentials(username, blobB64);
                    }
                    // Open main window with all the user data, plus the mutex
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
        private void SaveCredentials(string username, string passwordBlobBase64)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(username + "|" + passwordBlobBase64);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

            using var conn = new SqliteConnection(LocalConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS saved_credentials (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            encrypted_data BLOB NOT NULL
        );
        INSERT OR REPLACE INTO saved_credentials (id, encrypted_data) VALUES (1, @data);";
            cmd.Parameters.AddWithValue("@data", encrypted);
            cmd.ExecuteNonQuery();
        }

        private (string username, string passwordBlob)? LoadCredentials()
        {
            using var conn = new SqliteConnection(LocalConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS saved_credentials (id INTEGER PRIMARY KEY CHECK (id = 1), encrypted_data BLOB NOT NULL);";
            cmd.ExecuteNonQuery();   // ensure table exists

            cmd.CommandText = "SELECT encrypted_data FROM saved_credentials WHERE id = 1;";
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return null;

            try
            {
                byte[] encrypted = (byte[])result;
                byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                string data = Encoding.UTF8.GetString(plain);
                var parts = data.Split('|', 2);
                if (parts.Length == 2) return (parts[0], parts[1]);
            }
            catch { }
            return null;
        }

        private void DeleteCredentials()
        {
            using var conn = new SqliteConnection(LocalConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM saved_credentials;";
            cmd.ExecuteNonQuery();
        }
        private async Task<bool> TryAutoLogin(string username, string blobB64)
        {
            try
            {
                var loginPayload = new { username = username, password = blobB64 };
                var response = await client.PostAsJsonAsync("api/auth/login", loginPayload);

                if (response.IsSuccessStatusCode)
                {
                    var userData = await response.Content.ReadFromJsonAsync<LoginResponse>();
                    Guid userId = userData.UserId;
                    string displayName = userData.DisplayName;
                    string publicKey = userData.EccPublicKey;

                    var keyManager = new KeyManager(userId);
                    string privateKey = keyManager.LoadPrivateKey(userId);

                    string mutexName = $"DiplomaMessenger_User_{username}";
                    var mutex = new Mutex(true, mutexName, out bool createdNew);
                    if (!createdNew)
                    {
                        MessageBox.Show("This user is already logged in.", "Login denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                        mutex.Dispose();
                        return false;
                    }

                    var mainWindow = new MainWindow(userId, username, displayName, privateKey, publicKey, mutex);
                    mainWindow.Show();
                    this.Close();
                    return true;
                }
            }
            catch { }
            return false;
        }

    }

    // DTOs for JSON responses
    public class SaltResponse { public string Salt { get; set; } }
    public class LoginResponse { public Guid UserId { get; set; } public string DisplayName { get; set; } public string EccPublicKey { get; set; } }
}