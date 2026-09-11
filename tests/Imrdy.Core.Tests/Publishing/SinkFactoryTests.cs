using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class SinkFactoryTests
{
    private static readonly SinkContext Context =
        new(() => "workstation-Ubuntu", () => null, new StateFileReader(), () => []);

    private static ISessionSink? Create(string? endpoint, bool enabled = true, ILogger? logger = null) =>
        SinkFactory.TryCreate(
            new PublisherEntry { Name = "link", Endpoint = endpoint, Enabled = enabled },
            Context,
            logger ?? NullLogger.Instance);

    private static void CreateAndAssert<T>(string endpoint)
    {
        var sink = Create(endpoint);
        try
        {
            sink.Should().BeOfType<T>();
        }
        finally
        {
            (sink as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData("127.0.0.1:47600")]
    [InlineData("desktop2:47600")]
    [InlineData("[::1]:47600")]
    public void HostPortEndpoint_BuildsATcpSink(string endpoint)
    {
        CreateAndAssert<TcpSink>(endpoint);
    }

    [Fact]
    public void RootedPathEndpoint_BuildsAFileSink()
    {
        CreateAndAssert<FileSink>(Path.Combine(Path.GetTempPath(), "imrdy-sessions"));
    }

    [Fact]
    public void WindowsDriveLetterPath_IsAPathNotAHostPort()
    {
        // The host:port branch is tested first, and a drive-letter path also carries a colon.
        // Only the port test separates them, which is why this case has its own assertion.
        CreateAndAssert<FileSink>(@"C:\Users\radle\.imrdy\sessions");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("desktop2")]
    [InlineData("desktop2:")]
    [InlineData("desktop2:0")]
    [InlineData("desktop2:99999")]
    [InlineData("desktop2:notaport")]
    [InlineData(":47600")]
    public void UnusableEndpoint_BuildsNoSink(string endpoint)
    {
        Create(endpoint).Should().BeNull();
    }

    [Fact]
    public void DisabledEntry_BuildsNoSink()
    {
        Create("127.0.0.1:47600", enabled: false).Should().BeNull();
    }

    [Fact]
    public void NullEndpoint_BuildsNoSink_BecauseAReceiveOnlyRecordHasNothingToDial()
    {
        // r-1: a receiver registers a publisher to carry its desktop mapping and mute. The
        // endpoint is the direction, so omitting one must not make this machine dial it.
        Create(endpoint: null).Should().BeNull();
    }

    [Fact]
    public void NullEndpoint_LogsNothing_BecauseReceiveOnlyIsNotAMisconfiguration()
    {
        // Reconcile runs on every drain tick, so a warning here would be a false alarm several
        // times a second for a link the operator configured exactly as intended.
        var log = new CapturingLogger();

        Create(endpoint: null, logger: log);

        log.Lines.Should().BeEmpty();
    }

    [Fact]
    public void EmptyEndpoint_StillWarns_BecauseAnEmptyStringIsAMalformedRecord()
    {
        // The distinction r-1 turns on: absent is deliberate, blank is a mistake.
        var log = new CapturingLogger();

        Create(endpoint: "   ", logger: log).Should().BeNull();

        log.Lines.Should().ContainSingle().Which.Should().Contain("empty endpoint");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
