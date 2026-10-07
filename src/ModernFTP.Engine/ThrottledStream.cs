using System.Diagnostics;

namespace ModernFTP.Engine;

/// <summary>
/// Write side rate limiter for data transfers. Writes are split into chunks of about a tenth of a
/// second worth of bytes and delayed so the running average never exceeds the cap. Does not own
/// the inner stream.
/// </summary>
internal sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly long _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _written;

    private readonly Action? _onChunk;

    /// <param name="onChunk">Called after each chunk is written, so a slow but live transfer counts as activity.</param>
    public ThrottledStream(Stream inner, long bytesPerSecond, Action? onChunk = null)
    {
        _onChunk = onChunk;
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerSecond, 1);
        _inner = inner;
        _bytesPerSecond = bytesPerSecond;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private int ChunkSize => (int)Math.Clamp(_bytesPerSecond / 10, 1, 64 * 1024);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(buffer.Length, ChunkSize)];
            await _inner.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            buffer = buffer[chunk.Length..];
            _written += chunk.Length;
            _onChunk?.Invoke();
            var delay = TimeSpan.FromSeconds(_written / (double)_bytesPerSecond) - _clock.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
