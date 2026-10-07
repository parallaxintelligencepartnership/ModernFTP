using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using ModernFTP.Engine;

namespace ModernFTP.App;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 2000;

    private readonly ServerHost _host = new();
    private readonly ObservableCollection<SessionRow> _sessions = [];
    private readonly Dictionary<long, SessionRow> _byId = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _bytesSent;
    private long _bytesReceived;
    private readonly TrayController? _tray;
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

        UsersList.ItemsSource = _sessions;
        _host.EventReceived += OnServerEvent;
        _host.StateChanged += UpdateStatus;
        _timer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.Now;
            foreach (var row in _sessions)
            {
                row.RefreshIdle(now);
            }
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

    public async Task StartServerAsync()
    {
        if (_host.IsRunning)
        {
            return;
        }

        var error = await _host.StartAsync();
        if (error is not null)
        {
            AppendLine([new LogSegment(Strings.StartFailedTitle + ": " + error, LogTag.Text)]);
            MessageBox.Show(this, error, Strings.StartFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AppendLine([new LogSegment(string.Format(CultureInfo.CurrentCulture, Strings.ServerStartedFormat, _host.ListeningOn), LogTag.Text)]);
    }

    public async Task StopServerAsync()
    {
        if (!_host.IsRunning)
        {
            return;
        }

        await _host.StopAsync();
        _sessions.Clear();
        _byId.Clear();
        UpdateStatus();
        AppendLine([new LogSegment(Strings.ServerStopped, LogTag.Text)]);
    }

    public void ShowLogPage() => Tabs.SelectedItem = LogTab;

    public void ShowUsersPage() => Tabs.SelectedItem = UsersTab;

    /// <summary>Adds one engine event to the log and updates the session list and counters.</summary>
    public void OnServerEvent(ServerEvent e)
    {
        var now = DateTimeOffset.Now;
        _byId.TryGetValue(e.SessionId, out var row);
        switch (e)
        {
            case ConnectedEvent when row is null:
                row = new SessionRow(e.SessionId, e.RemoteEndPoint?.Address.ToString() ?? "-");
                _byId[e.SessionId] = row;
                _sessions.Add(row);
                break;
            case AuthenticatedEvent when row is not null:
                row.User = e.UserName ?? "-";
                break;
            case TransferStartedEvent t when row is not null:
                row.Transfer = t.Path[(t.Path.LastIndexOf('/') + 1)..];
                row.Progress = 0;
                row.TimeLeft = "-";
                break;
            case TransferCompletedEvent t:
                if (t.Direction == TransferDirection.Download)
                {
                    _bytesSent += t.Bytes;
                }
                else if (t.Direction == TransferDirection.Upload)
                {
                    _bytesReceived += t.Bytes;
                }

                if (row is not null)
                {
                    row.Transfer = string.Empty;
                    row.Progress = 0;
                    row.TimeLeft = string.Empty;
                }

                break;
            case DisconnectedEvent when row is not null:
                _sessions.Remove(row);
                _byId.Remove(e.SessionId);
                row = null;
                break;
        }

        row?.Touch(now);
        AppendLine(LogFormatter.Format(e));
        UpdateStatus();
    }

    /// <summary>Replaces the session list and counters with fixed sample data (capture mode).</summary>
    public void SetSampleState(IEnumerable<SessionRow> rows, long sent, long received, bool running)
    {
        _sessions.Clear();
        _byId.Clear();
        foreach (var row in rows)
        {
            _sessions.Add(row);
            _byId[row.Id] = row;
        }

        _bytesSent = sent;
        _bytesReceived = received;
        _timer.Stop();
        StateText.Text = running ? Strings.StatusRunning : Strings.StatusStopped;
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
        _tray?.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void AppendLine(IReadOnlyList<LogSegment> segments)
    {
        var dark = ThemeHelper.IsDark(this);
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var segment in segments)
        {
            paragraph.Inlines.Add(new Run(segment.Text) { Foreground = LogPalette.Get(segment.Tag, dark) });
        }

        var document = LogBox.Document;
        document.Blocks.Add(paragraph);
        while (document.Blocks.Count > MaxLogLines)
        {
            document.Blocks.Remove(document.Blocks.FirstBlock);
        }

        LogBox.ScrollToEnd();
    }

    private void UpdateStatus() => UpdateStatus(_host.IsRunning);

    private void UpdateStatus(bool running)
    {
        StateText.Text = running ? Strings.StatusRunning : Strings.StatusStopped;
        ConnectionsText.Text = string.Format(CultureInfo.CurrentCulture, Strings.StatusConnectionsFormat, _sessions.Count);
        SentText.Text = string.Format(CultureInfo.CurrentCulture, Strings.StatusSentFormat, FormatBytes(_bytesSent));
        ReceivedText.Text = string.Format(CultureInfo.CurrentCulture, Strings.StatusReceivedFormat, FormatBytes(_bytesReceived));
        StartItem.IsEnabled = !running;
        StopItem.IsEnabled = running;
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await StartServerAsync();

    private async void OnStop(object sender, RoutedEventArgs e) => await StopServerAsync();

    private void OnExit(object sender, RoutedEventArgs e) => ExitApplication();

    private void OnSetup(object sender, RoutedEventArgs e)
    {
        try
        {
            new SetupWindow(_host.LoadConfig(), _settings, _host.ConfigPath) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, Strings.SetupSaveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void OnMinimizeToTray(object sender, RoutedEventArgs e) => Hide();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = UsersList.SelectedItem as SessionRow;
        DisconnectButton.IsEnabled = selected is not null;
        AbortButton.IsEnabled = selected?.HasTransfer == true;
        BanButton.IsEnabled = selected is not null;
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (UsersList.SelectedItem is SessionRow row)
        {
            Report(_host.Controller.Disconnect(row.Id), null);
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
        if (UsersList.SelectedItem is SessionRow row && IPAddress.TryParse(row.Ip, out var address))
        {
            Report(_host.Controller.BanIp(address), string.Format(CultureInfo.CurrentCulture, Strings.BannedFormat, row.Ip));
        }
    }

    private void Report(bool ok, string? success) =>
        MessageText.Text = ok ? success ?? string.Empty : Strings.NotSupportedYet;
}
