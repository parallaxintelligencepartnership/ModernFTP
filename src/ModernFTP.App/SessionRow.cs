using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ModernFTP.App;

/// <summary>One row of the Users page, filled from an engine session snapshot.</summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    private string _user = "-";
    private string _transfer = string.Empty;
    private double _progress;
    private bool _isIndeterminate;
    private string _timeLeft = string.Empty;
    private string _idle = "00:00:00";

    public SessionRow(long id, string ip)
    {
        Id = id;
        Ip = ip;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; }

    public string Ip { get; }

    public string User { get => _user; set => Set(ref _user, value); }

    /// <summary>File name of the running transfer; empty when none.</summary>
    public string Transfer { get => _transfer; set => Set(ref _transfer, value); }

    /// <summary>Percent done, 0 to 100.</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    /// <summary>True while a transfer runs whose total size is unknown (uploads).</summary>
    public bool IsIndeterminate { get => _isIndeterminate; set => Set(ref _isIndeterminate, value); }

    public string TimeLeft { get => _timeLeft; set => Set(ref _timeLeft, value); }

    public string Idle { get => _idle; set => Set(ref _idle, value); }

    public bool HasTransfer => _transfer.Length > 0;

    public static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Transfer))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTransfer)));
        }
    }
}
