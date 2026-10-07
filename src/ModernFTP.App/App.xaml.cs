using System.Windows;

namespace ModernFTP.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        if (!window.StartHidden)
        {
            window.Show();
        }
    }
}
