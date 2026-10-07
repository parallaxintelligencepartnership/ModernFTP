namespace ModernFTP.Engine;

/// <summary>State of one running transfer: byte count, progress throttle and the server side abort.</summary>
internal sealed class TransferMeter(string path, TransferDirection direction, long? totalBytes, CancellationTokenSource cancellation)
{
    private long _bytesDone;
    private int _abortedByServer;

    public string Path { get; } = path;

    public TransferDirection Direction { get; } = direction;

    public long? TotalBytes { get; } = totalBytes;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public long BytesDone => Interlocked.Read(ref _bytesDone);

    public bool AbortedByServer => Volatile.Read(ref _abortedByServer) == 1;

    public long Add(int count) => Interlocked.Add(ref _bytesDone, count);

    public void AbortFromServer()
    {
        Volatile.Write(ref _abortedByServer, 1);
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The transfer already finished.
        }
    }

    public TransferInfo Snapshot() => new()
    {
        Path = Path,
        Direction = Direction,
        BytesDone = BytesDone,
        TotalBytes = TotalBytes,
        StartedAt = StartedAt,
    };
}

/// <summary>Counts payload bytes on a data connection (inside TLS). Does not own the inner stream.</summary>
internal sealed class MeteredStream(Stream inner, Action<int> onBytes) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            onBytes(read);
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            onBytes(read);
        }

        return read;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        onBytes(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        onBytes(count);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
