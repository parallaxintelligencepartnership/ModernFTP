using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ModernFTP.App;

/// <summary>One row of the Users page. Built from engine events because the engine offers no session enumeration.</summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    private string _user = "-";
    private string _transfer = string.Empty;
    private double _progress;
    private string _timeLeft = string.Empty;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
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

    public string Transfer { get => _transfer; set => Set(ref _transfer, value); }

    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public string TimeLeft { get => _timeLeft; set => Set(ref _timeLeft, value); }

    public string Idle { get => _idle; private set => Set(ref _idle, value); }

    public DateTimeOffset LastActivity => _lastActivity;

    public bool HasTransfer => _transfer.Length > 0;

    public void Touch(DateTimeOffset now)
    {
        _lastActivity = now;
        RefreshIdle(now);
    }

    public void RefreshIdle(DateTimeOffset now)
    {
        var idle = now - _lastActivity;
        if (idle < TimeSpan.Zero)
        {
            idle = TimeSpan.Zero;
        }

        Idle = idle.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    public void SetIdleText(string text) => Idle = text;

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
