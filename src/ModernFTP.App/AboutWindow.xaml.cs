using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace ModernFTP.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        RepoLink.NavigateUri = new Uri(Strings.AboutRepoUrl);
        VersionText.Text = string.Format(CultureInfo.CurrentCulture, Strings.AboutVersionFormat, AppVersion());
        LicenseText.Text = string.Format(CultureInfo.CurrentCulture, Strings.AboutLicenseFormat, Strings.AboutLicenseName);
    }

    public static string AppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutWindow).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateLinkBlock.Visibility = Visibility.Collapsed;
        UpdateText.Text = Strings.AboutChecking;
        try
        {
            var result = await UpdateChecker.CheckAsync(AppVersion());
            switch (result.State)
            {
                case UpdateState.UpToDate:
                    UpdateText.Text = Strings.AboutUpToDate;
                    break;
                case UpdateState.Available:
                    UpdateText.Text = string.Format(CultureInfo.CurrentCulture, Strings.AboutUpdateAvailableFormat, result.Version);
                    UpdateLink.NavigateUri = new Uri(result.Url);
                    UpdateLinkBlock.Visibility = Visibility.Visible;
                    break;
                default:
                    UpdateText.Text = Strings.AboutUpdateFailed;
                    break;
            }
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
