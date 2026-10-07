using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ModernFTP.App;

/// <summary>Notification area icon with Show, Start, Stop and Exit. Double click restores the window.</summary>
public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _start;
    private readonly Forms.ToolStripMenuItem _stop;
    private readonly Drawing.Icon _image;

    public TrayController(Action show, Action start, Action stop, Action exit, Func<bool> isRunning)
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ModernFTP.ico", UriKind.Absolute))
            ?? throw new FileNotFoundException("Tray icon resource is missing.");
        using (resource.Stream)
        {
            _image = new Drawing.Icon(resource.Stream);
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.TrayShow, null, (_, _) => show());
        _start = new Forms.ToolStripMenuItem(Strings.TrayStart, null, (_, _) => start());
        _stop = new Forms.ToolStripMenuItem(Strings.TrayStop, null, (_, _) => stop());
        menu.Items.Add(_start);
        menu.Items.Add(_stop);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.TrayExit, null, (_, _) => exit());
        menu.Opening += (_, _) =>
        {
            var running = isRunning();
            _start.Enabled = !running;
            _stop.Enabled = running;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = Strings.TrayHint,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
