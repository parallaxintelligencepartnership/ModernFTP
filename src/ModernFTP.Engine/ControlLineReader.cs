using System.Text;

namespace ModernFTP.Engine;

internal enum ControlLineKind
{
    Line,
    TooLong,
    EndOfStream,
}

internal readonly record struct ControlLine(ControlLineKind Kind, string Text);

/// <summary>
/// Reads CRLF (or bare LF) terminated command lines into a fixed buffer. A line longer than
/// <see cref="MaxLineLength"/> is discarded up to its terminator and reported once as
/// <see cref="ControlLineKind.TooLong"/>; memory use never grows with the input.
/// Telnet IAC sequences (used by some clients around ABOR) are stripped.
/// </summary>
internal sealed class ControlLineReader
{
    public const int MaxLineLength = 4096;

    private readonly byte[] _buffer = new byte[MaxLineLength];
    private int _start;
    private int _end;

    public void Reset()
    {
        _start = 0;
        _end = 0;
    }

    public async ValueTask<ControlLine> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var discarding = false;
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var lineEnd = newline;
                if (lineEnd > _start && _buffer[lineEnd - 1] == (byte)'\r')
                {
                    lineEnd--;
                }

                var line = discarding
                    ? new ControlLine(ControlLineKind.TooLong, string.Empty)
                    : new ControlLine(ControlLineKind.Line, Decode(_start, lineEnd));
                _start = newline + 1;
                return line;
            }

            if (_end - _start >= MaxLineLength)
            {
                discarding = true;
                _start = 0;
                _end = 0;
            }
            else if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return new ControlLine(ControlLineKind.EndOfStream, string.Empty);
            }

            _end += read;
        }
    }

    private string Decode(int start, int end)
    {
        Span<byte> filtered = stackalloc byte[end - start];
        var count = 0;
        for (var i = start; i < end; i++)
        {
            var b = _buffer[i];
            if (b == 0xFF)
            {
                i++; // IAC plus its command byte
                continue;
            }

            filtered[count++] = b;
        }

        return Encoding.UTF8.GetString(filtered[..count]);
    }
}
