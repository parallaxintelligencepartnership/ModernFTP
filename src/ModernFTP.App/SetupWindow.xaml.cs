using System.Globalization;
using System.IO;
using System.Windows;
using ModernFTP.Config;

namespace ModernFTP.App;

public partial class SetupWindow : Window
{
    private readonly ModernFtpConfig _config;
    private readonly AppSettings _settings;
    private readonly string? _configPath;

    /// <param name="configPath">Where Save writes the config. Null disables writing (capture mode).</param>
    public SetupWindow(ModernFtpConfig config, AppSettings settings, string? configPath)
    {
        InitializeComponent();
        _config = config;
        _settings = settings;
        _configPath = configPath;

        BindBox.Text = config.ListenAddress;
        PortBox.Text = Num(config.Port);
        PasvMinBox.Text = Num(config.PassivePortMin);
        PasvMaxBox.Text = Num(config.PassivePortMax);
        PasvIpBox.Text = config.PassivePublicAddress ?? string.Empty;
        MaxConnBox.Text = Num(config.MaxConnections);
        MaxIpBox.Text = Num(config.MaxConnectionsPerIp);
        IdleBox.Text = Num(config.IdleTimeoutSeconds);
        WelcomeBox.Text = config.WelcomeMessage;
        GoodbyeBox.Text = config.GoodbyeMessage;
        HideNameBox.IsChecked = config.HideServerName;
        StartMinBox.IsChecked = settings.StartMinimizedToTray;
        StartWinBox.IsChecked = settings.StartWithWindows;
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private bool TryNumber(System.Windows.Controls.TextBox box, string label, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        ErrorText.Text = string.Format(CultureInfo.CurrentCulture, Strings.SetupNumberFormat, label.Replace("_", string.Empty, StringComparison.Ordinal));
        box.Focus();
        return false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (!TryNumber(PortBox, Strings.SetupPort, out var port)
            || !TryNumber(PasvMinBox, Strings.SetupPasvRange, out var pasvMin)
            || !TryNumber(PasvMaxBox, Strings.SetupPasvRange, out var pasvMax)
            || !TryNumber(MaxConnBox, Strings.SetupMaxConnections, out var maxConn)
            || !TryNumber(MaxIpBox, Strings.SetupMaxPerIp, out var maxIp)
            || !TryNumber(IdleBox, Strings.SetupIdleTimeout, out var idle))
        {
            return;
        }

        var candidate = new ModernFtpConfig
        {
            ListenAddress = BindBox.Text.Trim(),
            Port = port,
            PassivePortMin = pasvMin,
            PassivePortMax = pasvMax,
            PassivePublicAddress = string.IsNullOrWhiteSpace(PasvIpBox.Text) ? null : PasvIpBox.Text.Trim(),
            AllowActiveMode = _config.AllowActiveMode,
            MaxConnections = maxConn,
            MaxConnectionsPerUser = _config.MaxConnectionsPerUser,
            MaxConnectionsPerIp = maxIp,
            IdleTimeoutSeconds = idle,
            WelcomeMessage = WelcomeBox.Text,
            GoodbyeMessage = GoodbyeBox.Text,
            HideServerName = HideNameBox.IsChecked == true,
            AllowAnonymous = _config.AllowAnonymous,
            AllowPlaintextPasswords = _config.AllowPlaintextPasswords,
            Tls = _config.Tls,
            Users = _config.Users,
            BannedAddresses = _config.BannedAddresses,
        };

        if (_configPath is null)
        {
            return;
        }

        var errors = ConfigLoader.Validate(candidate, Path.GetDirectoryName(_configPath)!);
        if (errors.Count > 0)
        {
            ErrorText.Text = errors[0];
            return;
        }

        try
        {
            ConfigLoader.Save(candidate, _configPath);
            _settings.StartMinimizedToTray = StartMinBox.IsChecked == true;
            _settings.StartWithWindows = StartWinBox.IsChecked == true;
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
