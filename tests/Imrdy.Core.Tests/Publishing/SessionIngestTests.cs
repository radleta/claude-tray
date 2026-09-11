using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;

namespace Imrdy.Core.Tests.Publishing;

/// <summary>
/// The ingest seam is the trust boundary: the session id here arrived off the wire or off
/// another machine's mount, and it becomes a filename. These tests pin that both verbs refuse
/// an id the hook path would have refused, and that nothing is written or deleted when they do.
/// </summary>
public class SessionIngestTests : IDisposable
{
    private readonly string _root;
    private readonly string _sessionsDir;
    private readonly StateFileReader _reader = new();
    private readonly SessionIngest _ingest;

    public SessionIngestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imrdy-ingest-tests", Guid.NewGuid().ToString());
        _sessionsDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(_sessionsDir);
        _ingest = new SessionIngest(_sessionsDir, _reader);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static StateFileModel Model(string sessionId) => new()
    {
        SessionId = sessionId,
        Status = "busy",
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
    };

    [Fact]
    public void Apply_WritesAValidSessionWithOriginStamped()
    {
        _ingest.Apply(Model("s1"), "desk2", out var refusal).Should().BeTrue();

        refusal.Should().BeNull();
        _reader.ReadStateFile(Path.Combine(_sessionsDir, "s1.json"))!.OriginMachine.Should().Be("desk2");
    }

    [Fact]
    public void Apply_RepeatedWithAnIdenticalPayload_DoesNotRewriteTheFile()
    {
        // A connect snapshot repeats on every dial over a sessions directory nothing sweeps,
        // and the write here is a direct write by design (D15) — so every one of them raises a
        // Changed on the receiver's watcher and travels the whole drain pipeline. Unconditional,
        // a reconnect costs one write and one watcher event per session for a receiver whose
        // state did not move. The file's write time is the assertion because it is exactly what
        // the watcher keys on.
        var path = Path.Combine(_sessionsDir, "s1.json");

        _ingest.Apply(Model("s1"), "desk2", out _).Should().BeTrue();
        var firstWrite = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);

        Thread.Sleep(20);
        _ingest.Apply(Model("s1"), "desk2", out var refusal).Should().BeTrue();

        refusal.Should().BeNull();
        File.GetLastWriteTimeUtc(path).Should().Be(firstWrite);
        File.ReadAllBytes(path).Should().Equal(bytes);
    }

    [Fact]
    public void Apply_WithAChangedPayload_StillWritesTheFile()
    {
        // The other half: idempotence must remove writes, never change what a write does.
        var path = Path.Combine(_sessionsDir, "s1.json");

        _ingest.Apply(Model("s1"), "desk2", out _).Should().BeTrue();

        _ingest.Apply(Model("s1") with { Status = "idle" }, "desk2", out _).Should().BeTrue();

        _reader.ReadStateFile(path)!.Status.Should().Be("idle");
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData(@"..\..\evil")]
    [InlineData("sub/evil")]
    [InlineData("")]
    [InlineData("evil:stream")]
    public void Apply_RefusesAnIdThatIsNotASafeFilename(string sessionId)
    {
        _ingest.Apply(Model(sessionId), "desk2", out var refusal).Should().BeFalse();

        refusal.Should().NotBeNullOrEmpty("the reason becomes that link's LastError, not silence");
        Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Apply_RefusesARootedIdThatWouldDiscardTheSessionsDirectory()
    {
        // Path.Combine drops its first argument when the second is rooted, so a rooted id is
        // an arbitrary-path write rather than a traversal.
        var rooted = Path.Combine(_root, "outside", "settings");

        _ingest.Apply(Model(rooted), "desk2", out var refusal).Should().BeFalse();

        refusal.Should().NotBeNullOrEmpty();
        File.Exists(rooted + ".json").Should().BeFalse();
    }

    [Fact]
    public void Remove_RefusesTheSameIdsAndDeletesNothing()
    {
        var victim = Path.Combine(_root, "keep.json");
        File.WriteAllText(victim, "{}");

        _ingest.Remove("../keep", out var refusal).Should().BeFalse();

        refusal.Should().NotBeNullOrEmpty();
        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public void Remove_DeletesAValidSession()
    {
        _ingest.Apply(Model("s1"), "desk2", out _).Should().BeTrue();

        _ingest.Remove("s1", out var refusal).Should().BeTrue();

        refusal.Should().BeNull();
        File.Exists(Path.Combine(_sessionsDir, "s1.json")).Should().BeFalse();
    }

    [Fact]
    public void Apply_EscapesAnOriginMachineCarryingControlCharacters()
    {
        // origin_machine is written to disk and rendered in the tooltip, the dashboard and the
        // connections window. Escaping it once, here, is what keeps every reader of that file
        // from having to sanitize it again (CWE-117).
        _ingest.Apply(Model("s1"), "desk2\r\nforged", out _).Should().BeTrue();

        _reader.ReadStateFile(Path.Combine(_sessionsDir, "s1.json"))!
            .OriginMachine.Should().Be(@"desk2\r\nforged");
    }
}
