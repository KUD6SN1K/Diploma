using System.Configuration;
using System.Data;
using System.Windows;
using ModernWpf;

namespace Diploma
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
        }
    }
}