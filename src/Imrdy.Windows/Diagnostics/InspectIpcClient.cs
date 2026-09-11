using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Imrdy.Core;
using Imrdy.Core.Diagnostics;

namespace Imrdy.Windows.Diagnostics;

/// <summary>
/// Thin synchronous client for the <c>Local\ImrdyInspect</c> named-pipe IPC server.
/// Stateless — every call opens a fresh connection and disposes it before returning.
/// </summary>
internal static class InspectIpcClient
{
    private const int MaxRequestBodyBytes = 4096;
    private const int MaxResponseBodyBytes = 262_144; // 256 KiB

    /// <summary>
    /// Deadline for the exchange that follows a successful connect. This is a separate budget
    /// from the connect one and is deliberately larger: the connect budget answers "is anything
    /// listening", where the common production answer is no and a caller should not wait, while
    /// this one has to cover the server actually doing the work. Every handler is marshalled to
    /// the tray's UI thread under a two-second budget of its own, so anything below that would
    /// report a busy tray as an absent one.
    /// <para>
    /// Without a deadline here a server that accepts the connection and then never writes hangs
    /// the caller with no exit short of Ctrl-C — which matters most for <c>imrdy links</c>, the
    /// one verb an operator is invited to put in a shell script.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends <paramref name="request"/> to the tray's IPC server and returns the response.
    /// </summary>
    /// <param name="request">The request to send; serialized body must not exceed 4 096 bytes.</param>
    /// <param name="timeout">
    /// Budget for the <see cref="NamedPipeClientStream.Connect(int)"/> call only. The write and
    /// read that follow are bounded separately by <see cref="ExchangeTimeout"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no server is listening within <paramref name="timeout"/> (wraps the
    /// original <see cref="TimeoutException"/> with an actionable message).
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the server accepted the connection but the exchange did not complete within
    /// <see cref="ExchangeTimeout"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the serialized request body exceeds <see cref="MaxRequestBodyBytes"/>.
    /// </exception>
    public static InspectResponse Send(InspectRequest request, TimeSpan timeout)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, ImrdyJsonContext.Default.InspectRequest);
        if (body.Length > MaxRequestBodyBytes)
            throw new ArgumentException(
                $"Serialized request body is {body.Length} bytes, which exceeds the {MaxRequestBodyBytes}-byte maximum.",
                nameof(request));

        // PipeOptions.Asynchronous is what makes the exchange deadline real. Without it the
        // handle is opened for synchronous I/O, ReadAsync runs the blocking read on a pool
        // thread, and cancelling the token abandons the wait rather than the read — so the
        // process still would not exit until the server wrote something.
        using var client = new NamedPipeClientStream(
            ".", "ImrdyInspect", PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            client.Connect((int)timeout.TotalMilliseconds);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                "Tray not running. Start the tray with 'imrdy' (or check that diagnostics IPC is enabled in ~/.imrdy/config.json).",
                ex);
        }

        using var cts = new CancellationTokenSource(ExchangeTimeout);

        try
        {
            return ExchangeAsync(client, body, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The tray accepted the connection but did not complete the exchange within "
                + $"{ExchangeTimeout.TotalSeconds:0.#}s.",
                ex);
        }
    }

    private static async Task<InspectResponse> ExchangeAsync(
        NamedPipeClientStream client, byte[] body, CancellationToken cancellationToken)
    {
        // Write length-prefixed request. There is deliberately no WaitForPipeDrain here: it is
        // a synchronous, unbounded wait for the server to read, which is the same hang the
        // deadline exists to prevent, and the read below already fails if the request never
        // landed.
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, body.Length);
        await client.WriteAsync(lenBuf.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        await client.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Read length-prefixed response
        await ReadExactlyAsync(client, lenBuf, 4, cancellationToken).ConfigureAwait(false);
        var responseLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (responseLen <= 0 || responseLen > MaxResponseBodyBytes)
            throw new InvalidDataException(
                $"Server returned an invalid response length: {responseLen}");

        var responseBuf = new byte[responseLen];
        await ReadExactlyAsync(client, responseBuf, responseLen, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize(responseBuf, ImrdyJsonContext.Default.InspectResponse)
            ?? throw new InvalidDataException("Server returned a null response body");
    }

    private static async Task ReadExactlyAsync(
        Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Pipe closed before full response was received");
            offset += read;
        }
    }
}
