using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>
/// Capture mode (--capture dir): renders the main, setup and about windows in light then dark theme to PNG files
/// and writes contact-sheet.md. This is the only screenshot mechanism of the app.
/// </summary>
public static class CaptureRunner
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var sheet = new StringBuilder("# ModernFTP UI capture\n\n");
        var number = 0;
        foreach (var (mode, label) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
        {
            ThemeHelper.Apply(Application.Current.MainWindow!, mode);
            var main = new MainWindow(enableTray: false);
            ThemeHelper.Apply(main, mode);
            Application.Current.MainWindow = main;
            main.Show();
            LoadSample(main);
            main.ShowLogPage();
            await Save(main, directory, sheet, ++number, "main-log", label, Strings.CaptureLogPageDescription);
            main.ShowUsersPage();
            await Save(main, directory, sheet, ++number, "main-users", label, Strings.CaptureUsersPageDescription);
            main.Hide();

            var setup = new SetupWindow(new ModernFtpConfig(), new AppSettings(), configPath: null);
            ThemeHelper.Apply(setup, mode);
            setup.Show();
            await Save(setup, directory, sheet, ++number, "setup", label, Strings.CaptureSetupDescription);
            setup.Hide();

            var about = new AboutWindow();
            ThemeHelper.Apply(about, mode);
            about.Show();
            await Save(about, directory, sheet, ++number, "about", label, Strings.CaptureAboutDescription);
            about.Hide();
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "contact-sheet.md"), sheet.ToString());
    }

    private static async Task Save(Window window, string directory, StringBuilder sheet, int number, string name, string theme, string description)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);
        window.UpdateLayout();

        // The first visual child of a Window is its themed chrome (background plus content), client area only.
        var root = VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement chrome
            ? chrome
            : (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);

        // The system backdrop (Mica) is not part of the visual tree, so paint an opaque base first.
        var dark = ThemeHelper.IsDark(window);
        var baseBrush = window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush
            ?? new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var surface = new DrawingVisual();
        using (var dc = surface.RenderOpen())
        {
            dc.DrawRectangle(baseBrush, null, bounds);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, bounds);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(surface);

        var file = string.Create(CultureInfo.InvariantCulture, $"{number:00}-{name}-{theme}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var stream = File.Create(Path.Combine(directory, file)))
        {
            encoder.Save(stream);
        }

        sheet.Append("- ").Append(file).Append(": ").Append(description).Append(" Theme: ").Append(theme).Append(".\n");
    }

    private static void LoadSample(MainWindow main)
    {
        var day = DateTime.Today;
        DateTimeOffset At(int h, int m, int s) => new(day.AddHours(h).AddMinutes(m).AddSeconds(s));
        var alice = new IPEndPoint(IPAddress.Parse("203.0.113.24"), 51724);
        var bob = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40312);

        ServerEvent[] events =
        [
            new ConnectedEvent { SessionId = 1, RemoteEndPoint = alice, Timestamp = At(14, 2, 11) },
            new CommandReceivedEvent { SessionId = 1, RemoteEndPoint = alice, Command = "USER", Argument = "alice", Timestamp = At(14, 2, 11) },
            new AuthenticatedEvent { SessionId = 1, RemoteEndPoint = alice, UserName = "alice", Timestamp = At(14, 2, 12) },
            new CommandReceivedEvent { SessionId = 1, RemoteEndPoint = alice, UserName = "alice", Command = "CWD", Argument = "/projects/reports", Timestamp = At(14, 2, 15) },
            new TransferStartedEvent { SessionId = 1, RemoteEndPoint = alice, UserName = "alice", Direction = TransferDirection.Download, Path = "/projects/reports/q3-summary.pdf", DataPort = 50012, Secure = true, Timestamp = At(14, 2, 16) },
            new TransferCompletedEvent { SessionId = 1, RemoteEndPoint = alice, UserName = "alice", Direction = TransferDirection.Download, Path = "/projects/reports/q3-summary.pdf", Bytes = 2_457_600, Duration = TimeSpan.FromMilliseconds(840), Success = true, Timestamp = At(14, 2, 17) },
            new ConnectedEvent { SessionId = 2, RemoteEndPoint = bob, Timestamp = At(14, 3, 40) },
            new AuthenticatedEvent { SessionId = 2, RemoteEndPoint = bob, UserName = "bob", Timestamp = At(14, 3, 41) },
            new CommandReceivedEvent { SessionId = 2, RemoteEndPoint = bob, UserName = "bob", Command = "STOR", Argument = "/uploads/site-backup.zip", Timestamp = At(14, 3, 44) },
            new TransferStartedEvent { SessionId = 2, RemoteEndPoint = bob, UserName = "bob", Direction = TransferDirection.Upload, Path = "/uploads/site-backup.zip", DataPort = 50013, Secure = true, Timestamp = At(14, 3, 44) },
        ];
        foreach (var e in events)
        {
            main.OnServerEvent(e);
        }

        var first = new SessionRow(1, "203.0.113.24") { User = "alice" };
        first.SetIdleText("00:00:41");
        var second = new SessionRow(2, "198.51.100.7") { User = "bob", Transfer = "site-backup.zip", Progress = 42, TimeLeft = "00:01:12" };
        second.SetIdleText("00:00:03");
        main.SetSampleState([first, second], sent: 19_300_000, received: 6_500_000, running: true);
    }
}
