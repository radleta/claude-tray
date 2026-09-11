using System.Text;
using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

public class WireLineReaderTests
{
    private static WireLineReader Reader(string payload, int max = WireProtocol.MaxLineBytes) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(payload)), max);

    private static string Text(WireReadResult result) => Encoding.UTF8.GetString(result.Line.Span);

    [Fact]
    public async Task ReadsOneLine()
    {
        var result = await Reader("first\n").ReadLineAsync(CancellationToken.None);

        result.Status.Should().Be(WireReadStatus.Line);
        Text(result).Should().Be("first");
    }

    [Fact]
    public async Task ReadsSeveralLinesArrivingInOneChunk()
    {
        // The normal case on a busy link: one read carries several frames, and the reader must
        // keep the tail rather than drop it.
        var reader = Reader("a\nb\nc\n");

        Text(await reader.ReadLineAsync(CancellationToken.None)).Should().Be("a");
        Text(await reader.ReadLineAsync(CancellationToken.None)).Should().Be("b");
        Text(await reader.ReadLineAsync(CancellationToken.None)).Should().Be("c");
        (await reader.ReadLineAsync(CancellationToken.None)).Status.Should().Be(WireReadStatus.EndOfStream);
    }

    [Fact]
    public async Task StripsACarriageReturnBeforeTheNewline()
    {
        Text(await Reader("first\r\n").ReadLineAsync(CancellationToken.None)).Should().Be("first");
    }

    [Fact]
    public async Task EmptyStream_IsEndOfStream()
    {
        (await Reader("").ReadLineAsync(CancellationToken.None)).Status.Should().Be(WireReadStatus.EndOfStream);
    }

    [Fact]
    public async Task PartialLineAtEof_IsDroppedAsATornFrame()
    {
        (await Reader("no terminator").ReadLineAsync(CancellationToken.None))
            .Status.Should().Be(WireReadStatus.EndOfStream);
    }

    [Fact]
    public async Task LineLongerThanTheCap_IsRefused()
    {
        var result = await Reader(new string('x', 40) + "\n", max: 16).ReadLineAsync(CancellationToken.None);

        result.Status.Should().Be(WireReadStatus.LineTooLong);
    }

    [Fact]
    public async Task LineLongerThanTheInitialBufferButUnderTheCap_IsRead()
    {
        // Exercises the grow path: the buffer starts at 4 KiB and the cap is 64 KiB.
        var payload = new string('x', 10_000);

        var result = await Reader(payload + "\n").ReadLineAsync(CancellationToken.None);

        result.Status.Should().Be(WireReadStatus.Line);
        Text(result).Should().HaveLength(10_000);
    }
}
