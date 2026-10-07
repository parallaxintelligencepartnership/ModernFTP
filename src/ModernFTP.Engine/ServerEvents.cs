using System.Net;

namespace ModernFTP.Engine;

/// <summary>Direction of a data connection transfer.</summary>
public enum TransferDirection
{
    Download,
    Upload,
    Listing,
}

/// <summary>Base type for everything the server reports to hosts (console, WPF shell, tests).</summary>
public abstract record ServerEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public required long SessionId { get; init; }

    public IPEndPoint? RemoteEndPoint { get; init; }

    public string? UserName { get; init; }

    /// <summary>Short kind label, for example "Connected".</summary>
    public string Kind
    {
        get
        {
            var name = GetType().Name;
            return name.EndsWith("Event", StringComparison.Ordinal) ? name[..^5] : name;
        }
    }

    /// <summary>One line human readable description, without timestamp or kind.</summary>
    public abstract string Describe();
}

public sealed record ConnectedEvent : ServerEvent
{
    public override string Describe() => $"connection from {RemoteEndPoint}";
}

public sealed record AuthenticatedEvent : ServerEvent
{
    public override string Describe() => $"user {UserName} logged in from {RemoteEndPoint}";
}

public sealed record CommandReceivedEvent : ServerEvent
{
    public required string Command { get; init; }

    /// <summary>Argument text; PASS arguments are always masked.</summary>
    public required string Argument { get; init; }

    public override string Describe() => Argument.Length == 0 ? Command : $"{Command} {Argument}";
}

public sealed record TransferStartedEvent : ServerEvent
{
    public required TransferDirection Direction { get; init; }

    public required string Path { get; init; }

    /// <summary>Local port of the data connection on the server side.</summary>
    public required int DataPort { get; init; }

    public required bool Secure { get; init; }

    public override string Describe() =>
        $"{Direction} {Path} on data port {DataPort}{(Secure ? " (TLS)" : string.Empty)}";
}

public sealed record TransferCompletedEvent : ServerEvent
{
    public required TransferDirection Direction { get; init; }

    public required string Path { get; init; }

    public required long Bytes { get; init; }

    public required TimeSpan Duration { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public override string Describe() =>
        $"{Direction} {Path} {(Success ? "completed" : "failed")} {Bytes} bytes in {Duration.TotalMilliseconds:0} ms"
        + (Error is null ? string.Empty : $" ({Error})");
}

public sealed record DisconnectedEvent : ServerEvent
{
    public required string Reason { get; init; }

    public override string Describe() => $"{RemoteEndPoint} disconnected: {Reason}";
}

public sealed record ErrorEvent : ServerEvent
{
    public required string Message { get; init; }

    public override string Describe() => Message;
}

/// <summary>Sent at most every 500 ms while a transfer moves data.</summary>
public sealed record TransferProgressEvent : ServerEvent
{
    public required TransferDirection Direction { get; init; }

    public required string Path { get; init; }

    public required long BytesDone { get; init; }

    /// <summary>Known for downloads and listings; null for uploads.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Server wide data bytes sent to clients so far.</summary>
    public required long TotalBytesSent { get; init; }

    /// <summary>Server wide data bytes received from clients so far.</summary>
    public required long TotalBytesReceived { get; init; }

    public required int ActiveConnections { get; init; }

    public override string Describe() =>
        $"{Direction} {Path} {BytesDone}{(TotalBytes is { } total ? $" of {total}" : string.Empty)} bytes";
}
