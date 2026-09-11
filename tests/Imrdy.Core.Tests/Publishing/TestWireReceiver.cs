using System.Net;
using System.Net.Sockets;
using System.Text;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

/// <summary>
/// A minimal loopback stand-in for the receiver, so <see cref="TcpSink"/> is exercised against
/// a real socket rather than a mocked stream — the framing, the dial and the connect snapshot
/// are exactly the parts a mock would not prove.
/// </summary>
internal sealed class TestWireReceiver : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<WireFrame> _frames = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly bool _refuseImmediately;
    private int _accepts;

    /// <param name="refuseImmediately">
    /// Accept the socket and close it at once, without reading a frame — what
    /// <see cref="WireListener"/> does to a publisher whose auth key or schema major does not
    /// match. It is the shape that separates "a peer took our connection" from "a peer kept
    /// it", which is what the dial loop's backoff has to tell apart.
    /// </param>
    public TestWireReceiver(bool refuseImmediately = false)
    {
        _refuseImmediately = refuseImmediately;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>How many times a publisher has dialled this receiver.</summary>
    public int Accepts => Volatile.Read(ref _accepts);

    public string Endpoint => $"127.0.0.1:{Port}";

    public IReadOnlyList<WireFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToList();
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="predicate"/> holds over the frames received so far. Returns
    /// false on timeout, so a test asserts on a definite outcome rather than a sleep.
    /// </summary>
    public async Task<bool> WaitForAsync(Func<IReadOnlyList<WireFrame>, bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(Frames))
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return predicate(Frames);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            Interlocked.Increment(ref _accepts);

            if (_refuseImmediately)
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => ReadLoopAsync(client));
        }
    }

    private async Task ReadLoopAsync(TcpClient client)
    {
        using (client)
        using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8))
        {
            try
            {
                while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
                {
                    if (WireProtocol.TryParse(line, out var frame) && frame is not null)
                    {
                        lock (_gate)
                        {
                            _frames.Add(frame);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Peer went away or the receiver is shutting down; either ends this connection.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
