using Diploma.Helpers;
using System.Windows;

namespace Diploma
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set theme
            ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;

            // Try auto-login if credentials exist
            var creds = CredentialsStorage.LoadCredentials();
            if (creds.HasValue)
            {
                var (username, blob) = creds.Value;
                var mainWindow = await AutoLoginService.TryAutoLoginAsync(username, blob);
                if (mainWindow != null)
                {
                    mainWindow.Show();
                    return;  // Skip showing LoginWindow
                }
                // Auto-login failed – clear invalid credentials
                CredentialsStorage.DeleteCredentials();
            }

            // Fallback: show LoginWindow
            var loginWindow = new LoginWindow();
            loginWindow.Show();
        }
    }
}