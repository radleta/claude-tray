namespace Imrdy.Core.Publishing;

/// <summary>How a read ended.</summary>
public enum WireReadStatus
{
    /// <summary>A complete line was read.</summary>
    Line,

    /// <summary>The peer closed. Any bytes buffered without a terminating newline are dropped.</summary>
    EndOfStream,

    /// <summary>The peer sent more than <see cref="WireProtocol.MaxLineBytes"/> without a newline.</summary>
    LineTooLong,
}

/// <param name="Status">How the read ended.</param>
/// <param name="Line">The line without its terminator, meaningful only when <paramref name="Status"/> is <see cref="WireReadStatus.Line"/>.</param>
public readonly record struct WireReadResult(WireReadStatus Status, ReadOnlyMemory<byte> Line);

/// <summary>
/// Reads newline-delimited frames off a socket under a hard length cap.
/// <para>
/// The cap is the reason this exists rather than a <c>StreamReader</c>: a peer that never sends
/// a newline would otherwise grow the receiver's buffer without bound. Because the sender
/// refuses to emit an over-length line at all, a line that trips the cap here means a peer that
/// is not this protocol, and the caller closes the connection with a logged reason.
/// </para>
/// </summary>
public sealed class WireLineReader
{
    private const int InitialCapacity = 4096;

    private readonly Stream _stream;
    private readonly int _maxLineBytes;

    private byte[] _buffer = new byte[InitialCapacity];
    private int _count;

    public WireLineReader(Stream stream, int maxLineBytes = WireProtocol.MaxLineBytes)
    {
        _stream = stream;
        _maxLineBytes = maxLineBytes;
    }

    public async Task<WireReadResult> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', 0, _count);
            if (newline >= 0)
            {
                // Measured with the terminator, the way the cap is defined. The check comes
                // before the take: a complete-but-oversize line is still an illegal line.
                return newline + 1 > _maxLineBytes
                    ? new WireReadResult(WireReadStatus.LineTooLong, default)
                    : new WireReadResult(WireReadStatus.Line, TakeLine(newline));
            }

            if (_count >= _maxLineBytes)
            {
                return new WireReadResult(WireReadStatus.LineTooLong, default);
            }

            if (_count == _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, _maxLineBytes));
            }

            var read = await _stream
                .ReadAsync(_buffer.AsMemory(_count, _buffer.Length - _count), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                // A partial line at EOF is a torn frame, not a short one: dropping it is the
                // same tolerance a torn state file already gets.
                return new WireReadResult(WireReadStatus.EndOfStream, default);
            }

            _count += read;
        }
    }

    /// <summary>
    /// Copies out the line and shifts whatever followed it — a single read commonly carries
    /// several frames — down to the front of the buffer.
    /// </summary>
    private ReadOnlyMemory<byte> TakeLine(int newline)
    {
        var end = newline > 0 && _buffer[newline - 1] == (byte)'\r' ? newline - 1 : newline;
        var line = _buffer.AsSpan(0, end).ToArray();

        var remaining = _count - (newline + 1);
        Array.Copy(_buffer, newline + 1, _buffer, 0, remaining);
        _count = remaining;

        return line;
    }
}
