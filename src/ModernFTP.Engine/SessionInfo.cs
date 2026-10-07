using System.Net;

namespace ModernFTP.Engine;

/// <summary>A point in time view of one control connection, for hosts that list sessions.</summary>
public sealed record SessionInfo
{
    /// <summary>The same id the session's events carry.</summary>
    public required long Id { get; init; }

    /// <summary>Null until the session logs in.</summary>
    public string? User { get; init; }

    public required IPAddress RemoteAddress { get; init; }

    public required DateTimeOffset ConnectedAt { get; init; }

    public required DateTimeOffset LastActivity { get; init; }

    /// <summary>Null when no data transfer is running.</summary>
    public TransferInfo? CurrentTransfer { get; init; }

    /// <summary>Data connection payload bytes sent to the client (downloads and listings).</summary>
    public required long BytesSent { get; init; }

    /// <summary>Data connection payload bytes received from the client (uploads).</summary>
    public required long BytesReceived { get; init; }
}

/// <summary>A point in time view of a running data transfer.</summary>
public sealed record TransferInfo
{
    public required string Path { get; init; }

    public required TransferDirection Direction { get; init; }

    public required long BytesDone { get; init; }

    /// <summary>Known for downloads and listings; null for uploads.</summary>
    public long? TotalBytes { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}
