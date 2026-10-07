using System.IO;
using System.Windows;
using ModernFTP.Config;

namespace ModernFTP.App;

public partial class SetupWindow : Window
{
    private readonly SetupViewModel _model;
    private readonly AppSettings _settings;
    private readonly string? _configPath;

    /// <param name="configPath">Where Save writes the config. Null disables writing (capture mode).</param>
    public SetupWindow(ModernFtpConfig config, AppSettings settings, string? configPath)
    {
        InitializeComponent();
        _model = new SetupViewModel(config, settings);
        _settings = settings;
        _configPath = configPath;

        BindBox.Text = _model.ListenAddress;
        PortBox.Text = _model.Port;
        PasvMinBox.Text = _model.PassivePortMin;
        PasvMaxBox.Text = _model.PassivePortMax;
        PasvIpBox.Text = _model.PassivePublicAddress;
        MaxConnBox.Text = _model.MaxConnections;
        MaxIpBox.Text = _model.MaxConnectionsPerIp;
        IdleBox.Text = _model.IdleTimeoutSeconds;
        WelcomeBox.Text = _model.WelcomeMessage;
        GoodbyeBox.Text = _model.GoodbyeMessage;
        HideNameBox.IsChecked = _model.HideServerName;
        StartMinBox.IsChecked = _model.StartMinimizedToTray;
        StartWinBox.IsChecked = _model.StartWithWindows;
        FirewallButton.IsEnabled = OperatingSystem.IsWindows() && configPath is not null;
    }

    private async void OnAddFirewall(object sender, RoutedEventArgs e)
    {
        FirewallButton.IsEnabled = false;
        FirewallStatusText.Text = Strings.SetupFirewallRunning;
        try
        {
            FirewallStatusText.Text = await Task.Run(() => FirewallHelper.Run(_configPath!));
        }
        finally
        {
            FirewallButton.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        _model.ListenAddress = BindBox.Text;
        _model.Port = PortBox.Text;
        _model.PassivePortMin = PasvMinBox.Text;
        _model.PassivePortMax = PasvMaxBox.Text;
        _model.PassivePublicAddress = PasvIpBox.Text;
        _model.MaxConnections = MaxConnBox.Text;
        _model.MaxConnectionsPerIp = MaxIpBox.Text;
        _model.IdleTimeoutSeconds = IdleBox.Text;
        _model.WelcomeMessage = WelcomeBox.Text;
        _model.GoodbyeMessage = GoodbyeBox.Text;
        _model.HideServerName = HideNameBox.IsChecked == true;
        _model.StartMinimizedToTray = StartMinBox.IsChecked == true;
        _model.StartWithWindows = StartWinBox.IsChecked == true;

        if (_configPath is null)
        {
            return;
        }

        try
        {
            var error = _model.Save(_configPath);
            if (error is not null)
            {
                ErrorText.Text = error;
                (_model.ErrorField switch
                {
                    nameof(SetupViewModel.Port) => PortBox,
                    nameof(SetupViewModel.PassivePortMin) => PasvMinBox,
                    nameof(SetupViewModel.PassivePortMax) => PasvMaxBox,
                    nameof(SetupViewModel.MaxConnections) => MaxConnBox,
                    nameof(SetupViewModel.MaxConnectionsPerIp) => MaxIpBox,
                    nameof(SetupViewModel.IdleTimeoutSeconds) => IdleBox,
                    _ => null,
                })?.Focus();
                return;
            }

            _settings.Save();
            StartupRegistration.Apply(_settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(this, ex.Message, Strings.SetupSaveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
