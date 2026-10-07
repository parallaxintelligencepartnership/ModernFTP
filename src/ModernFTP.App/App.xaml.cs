using System.IO;
using System.Windows;

namespace ModernFTP.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var captureIndex = Array.IndexOf(e.Args, "--capture");
        if (captureIndex >= 0)
        {
            await RunCaptureAsync(captureIndex + 1 < e.Args.Length ? e.Args[captureIndex + 1] : null);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        if (!window.StartHidden)
        {
            window.Show();
        }

        window.WarmUp();
        if (window.StartServerOnLaunch)
        {
            await window.StartServerOnLaunchAsync();
        }
    }

    private async Task RunCaptureAsync(string? directory)
    {
        var code = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("--capture needs a directory argument.");
            }

            MainWindow = new Window { Visibility = Visibility.Hidden, ShowInTaskbar = false, Width = 1, Height = 1 };
            await CaptureRunner.RunAsync(Path.GetFullPath(directory));
        }
        catch (Exception ex)
        {
            code = 1;
            try
            {
                var target = string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory;
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "capture-error.txt"), ex.ToString());
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done; the exit code still reports the failure.
            }
        }

        Environment.ExitCode = code;
        Shutdown(code);
    }
}
