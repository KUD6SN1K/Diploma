using Diploma.Crypto;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Diploma.Helpers
{
    public static class AutoLoginService
    {
        private static readonly HttpClient client = new HttpClient
        {
            BaseAddress = new Uri("https://localhost:5001")
        };

        public static async Task<MainWindow> TryAutoLoginAsync(string username, string passwordBlobBase64)
        {
            try
            {
                var loginPayload = new { username, password = passwordBlobBase64 };
                var response = await client.PostAsJsonAsync("api/auth/login", loginPayload);

                if (!response.IsSuccessStatusCode)
                    return null;

                var userData = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (userData == null) return null;

                Guid userId = userData.UserId;
                string displayName = userData.DisplayName;
                string publicKey = userData.EccPublicKey;

                // Load private key from local DB
                var keyManager = new KeyManager(userId);
                string privateKey = keyManager.LoadPrivateKey(userId);

                // Enforce single instance per user
                string mutexName = $"DiplomaMessenger_User_{username}";
                var mutex = new Mutex(true, mutexName, out bool createdNew);
                if (!createdNew)
                {
                    MessageBox.Show("This user is already logged in.", "Login denied",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    mutex.Dispose();
                    return null;
                }

                return new MainWindow(userId, username, displayName, privateKey, publicKey, mutex);
            }
            catch
            {
                return null;
            }
        }

        private class LoginResponse
        {
            public Guid UserId { get; set; }
            public string DisplayName { get; set; }
            public string EccPublicKey { get; set; }
        }
    }
}