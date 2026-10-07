using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App;

public partial class MainWindow : Window
{
    private readonly ServerHost _host = new();
    private readonly UsersPageViewModel _users = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sampleMode;
    private int _sampleConnections;
    private long _sampleSent;
    private long _sampleReceived;
    private readonly TrayController? _tray;
    private readonly LogView _log;
    private AppSettings _settings = new();
    private bool _exiting;

    public MainWindow()
        : this(enableTray: true)
    {
    }

    /// <param name="enableTray">False in capture mode: no notification icon and no settings read from disk.</param>
    public MainWindow(bool enableTray)
    {
        InitializeComponent();
        _log = new LogView(LogBox, () => ThemeHelper.IsDark(this));
        if (enableTray)
        {
            _settings = AppSettings.Load();
            _tray = new TrayController(
                ShowFromTray,
                async () => await StartServerAsync(),
                async () => await StopServerAsync(),
                ExitApplication,
                () => _host.IsRunning);
        }

        UsersList.ItemsSource = _users.Rows;
        _host.EventsReceived += OnServerEvents;
        _host.StateChanged += UpdateStatus;
        _timer.Tick += (_, _) =>
        {
            RefreshSessions();
            UpdateStatus();
        };
        _timer.Start();
        UpdateStatus();
    }

    public ServerHost Host => _host;

    /// <summary>True when launch settings ask for the window to stay hidden in the tray.</summary>
    public bool StartHidden => _tray is not null && _settings.StartMinimizedToTray;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    public bool IsServerRunning => _host.IsRunning;

    /// <summary>True when the app settings ask for the server to start as soon as the app opens.</summary>
    public bool StartServerOnLaunch => _tray is not null && _settings.StartServerOnLaunch;

    /// <summary>
    /// Starts the server when the app opens. A failure is logged and, while the window is hidden in the tray,
    /// reported with a tray notification instead of a dialog nobody would see.
    /// </summary>
    public async Task StartServerOnLaunchAsync()
    {
        if (IsVisible)
        {
            await StartServerAsync();
            return;
        }

        var error = await TryStartServerAsync();
        if (error is not null)
        {
            _tray?.ShowWarning(Strings.StartFailedTitle, error);
        }
    }

    public async Task StartServerAsync()
    {
        var error = await TryStartServerAsync();
        if (error is not null)
        {
            MessageBox.Show(this, error, Strings.StartFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Starts the server and logs the outcome. Returns the error message, or null on success.</summary>
    private async Task<string?> TryStartServerAsync()
    {
        if (_host.IsRunning)
        {
            return null;
        }

        var error = await _host.StartAsync();
        if (error is not null)
        {
            AppendLine([new LogSegment(Strings.StartFailedTitle + ": " + error, LogTag.Text)]);
            return error;
        }

        AppendLine([new LogSegment(string.Format(CultureInfo.CurrentCulture, Strings.ServerStartedFormat, _host.ListeningOn), LogTag.Text)]);
        return null;
    }

    public async Task StopServerAsync()
    {
        if (!_host.IsRunning)
        {
            return;
        }

        await _host.StopAsync();
        _users.Clear();
        UpdateStatus();
        AppendLine([new LogSegment(Strings.ServerStopped, LogTag.Text)]);
    }

    public void ShowLogPage() => Tabs.SelectedItem = LogTab;

    public void ShowUsersPage() => Tabs.SelectedItem = UsersTab;

    /// <summary>
    /// Adds one batch of engine events to the log (with a drop notice when events were dropped) and refreshes
    /// the session list and counters once for the whole batch.
    /// </summary>
    public void OnServerEvents(IReadOnlyList<ServerEvent> events, long dropped)
    {
        _log.Append(LogView.Lines(events, dropped));
        RefreshSessions();
        UpdateStatus();
    }

    /// <summary>Rebuilds the session rows from the server's snapshot. Runs every second and once per event batch.</summary>
    private void RefreshSessions()
    {
        if (_sampleMode)
        {
            return;
        }

        _users.Refresh(_host.Controller.Sessions, DateTimeOffset.Now);
        UpdateButtons();
    }

    /// <summary>Shows fixed sample sessions and counters through the same path as live data (capture mode).</summary>
    public void LoadSnapshots(IReadOnlyList<(DateTimeOffset At, IReadOnlyList<SessionInfo> Sessions)> snapshots, long sent, long received, bool running)
    {
        _sampleMode = true;
        _timer.Stop();
        _users.Clear();
        foreach (var (at, sessions) in snapshots)
        {
            _users.Refresh(sessions, at);
        }

        _sampleConnections = snapshots[^1].Sessions.Count;
        _sampleSent = sent;
        _sampleReceived = received;
        UpdateStatus(running);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {units[unit]}");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _tray is not null && _settings.StartMinimizedToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _host.DisposeEvents();
        _tray?.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void AppendLine(IReadOnlyList<LogSegment> segments) => _log.AppendLine(segments);

    private void UpdateStatus() => UpdateStatus(_host.IsRunning);

    private void UpdateStatus(bool running)
    {
        StateText.Text = running ? Strings.StatusRunning : Strings.StatusStopped;
        var connections = _sampleMode ? _sampleConnections : _host.ActiveConnections;
        var sent = _sampleMode ? _sampleSent : _host.TotalBytesSent;
        var received = _sampleMode ? _sampleReceived : _host.TotalBytesReceived;
        ConnectionsText.Text = string.Format(CultureInfo.CurrentCulture, Strings.StatusConnectionsFormat, connections);
        SentText.Text = string.Format(CultureInfo.CurrentCulture, Strings.StatusSentFormat, FormatBytes(sent));
        ReceivedText.Text = string.Format(CultureInfo.CurrentCulture, Strings.StatusReceivedFormat, FormatBytes(received));
        StartItem.IsEnabled = !running;
        StopItem.IsEnabled = running;
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await StartServerAsync();

    private async void OnStop(object sender, RoutedEventArgs e) => await StopServerAsync();

    private void OnExit(object sender, RoutedEventArgs e) => ExitApplication();

    private void OnSetup(object sender, RoutedEventArgs e) =>
        ShowConfigWindow(config => new SetupWindow(config, _settings, _host.ConfigPath));

    private void OnUsers(object sender, RoutedEventArgs e) =>
        ShowConfigWindow(config => new UserSetupWindow(config, _host.ConfigPath));

    private void OnIpRestriction(object sender, RoutedEventArgs e) =>
        ShowConfigWindow(config => new IpRestrictionWindow(config, _host.ConfigPath));

    private void ShowConfigWindow(Func<ModernFtpConfig, Window> create)
    {
        ModernFtpConfig config;
        bool saved;
        try
        {
            config = _host.LoadConfig();
            var window = create(config);
            window.Owner = this;
            saved = window.ShowDialog() == true;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, Strings.SetupSaveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (saved)
        {
            ApplySavedConfig(config);
        }
    }

    /// <summary>
    /// Sends a config that a window just saved to the running server. Shows the restart note only when some
    /// changed settings cannot apply until the server restarts.
    /// </summary>
    private void ApplySavedConfig(ModernFtpConfig config)
    {
        IReadOnlyList<string>? restart;
        try
        {
            restart = _host.ApplyConfig(config);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            var failed = string.Format(CultureInfo.CurrentCulture, Strings.ApplyFailedFormat, ex.Message);
            AppendLine([new LogSegment(failed, LogTag.Text)]);
            MessageBox.Show(this, failed, Strings.SetupTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (restart is null)
        {
            return;
        }

        AppendLine([new LogSegment(Strings.SettingsApplied, LogTag.Text)]);
        if (restart.Count > 0)
        {
            var note = string.Format(CultureInfo.CurrentCulture, Strings.RestartNeededFormat, Strings.SetupRestartNote, string.Join(", ", restart));
            AppendLine([new LogSegment(note, LogTag.Text)]);
            MessageBox.Show(this, note, Strings.SetupTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void OnMinimizeToTray(object sender, RoutedEventArgs e) => Hide();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var selected = UsersList.SelectedItem as SessionRow;
        DisconnectButton.IsEnabled = selected is not null;
        AbortButton.IsEnabled = selected is not null;
        BanButton.IsEnabled = selected is not null;
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is SessionRow row)
        {
            Report(_host.Controller.Disconnect(row.Id), null);
            RefreshSessions();
        }
    }

    private void OnAbort(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is SessionRow row)
        {
            Report(_host.Controller.AbortTransfer(row.Id), null);
        }
    }

    private void OnBan(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is not SessionRow row || !IPAddress.TryParse(row.Ip, out var address))
        {
            return;
        }

        try
        {
            var banned = _host.Controller.BanIp(address);
            Report(banned, string.Format(CultureInfo.CurrentCulture, Strings.BannedFormat, row.Ip));
            if (banned)
            {
                _host.Controller.Disconnect(row.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _host.Controller.Disconnect(row.Id);
            MessageText.Text = string.Format(CultureInfo.CurrentCulture, Strings.BanSaveFailedFormat, row.Ip, ex.Message);
        }
    }

    private void Report(bool ok, string? success) =>
        MessageText.Text = ok ? success ?? string.Empty : Strings.ActionFailed;
}
