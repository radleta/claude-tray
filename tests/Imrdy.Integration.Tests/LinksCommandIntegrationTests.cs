using System.Text.Json;
using FluentAssertions;
using Imrdy.Integration.Tests.Helpers;
using Xunit;

namespace Imrdy.Integration.Tests;

/// <summary>
/// <c>imrdy links</c> as the operator runs it: the published binary, a temp
/// <c>IMRDY_HOME</c>, and whatever <c>publishers.json</c> holds. This is the only place the
/// command's own wiring is proven — that <c>Program.cs</c> routes <c>links</c> to the Spectre
/// CLI at all rather than falling through and starting a tray, which is a defect no unit test
/// can see and a passing build does not catch.
/// </summary>
[Trait("Category", "Integration")]
public class LinksCommandIntegrationTests : IDisposable
{
    private readonly CliTestFixture _cli = new();
    private readonly string _home;

    public LinksCommandIntegrationTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imrdy-links-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string args) =>
        _cli.RunAsync(args, workingDirectory: _home,
            environmentVariables: new Dictionary<string, string> { ["IMRDY_HOME"] = _home });

    private void WritePublishers() => File.WriteAllText(
        Path.Combine(_home, "publishers.json"),
        """
        {"publishers":[
          {"name":"desk2","endpoint":"100.90.1.5:47600","desktop_index":3,"enabled":true},
          {"name":"wsl-box","endpoint":"C:\\imrdy-links-target\\sessions","muted":true,"enabled":true},
          {"name":"laptop","endpoint":"100.90.1.9:47600","enabled":false}
        ]}
        """);

    [Fact]
    public async Task Links_WithNoPublishersFile_ReportsNoLinksAndExitsZero()
    {
        var (exitCode, stdout, _) = await RunAsync("links");

        exitCode.Should().Be(0);
        stdout.Should().Contain("No links registered.");
    }

    [Fact]
    public async Task Links_RunsAsACliCommand_RatherThanFallingThroughToTheTray()
    {
        // The command produced no output at all until "links" was added to Program.cs's verb
        // list — it started a tray instead and exited 0. Any output at all is the assertion.
        var (exitCode, stdout, _) = await RunAsync("links");

        exitCode.Should().Be(0);
        stdout.Should().NotBeEmpty("a CLI command that prints nothing has fallen through to the tray branch");
    }

    [Fact]
    public async Task Links_ReportsEachLinksState_AndAFileSinkIsNeverConnected()
    {
        WritePublishers();

        var (exitCode, stdout, _) = await RunAsync("links");

        exitCode.Should().Be(0, "a CLI process holds no health tables, so no row can be Failed");
        stdout.Should().Contain("desk2").And.Contain("wsl-box").And.Contain("laptop");
        stdout.Should().Contain("FileSink", "a rooted-path endpoint is a file sink (D27)");
        stdout.Should().NotContain("Connected", "a CLI process holds no sinks, so nothing is connected");
        stdout.Should().Contain("disabled", "the laptop record is disabled");
    }

    [Fact]
    public async Task LinksJson_EmitsTheSameRecords()
    {
        WritePublishers();

        var (exitCode, stdout, _) = await RunAsync("links --json");

        exitCode.Should().Be(0);
        var root = JsonDocument.Parse(stdout).RootElement;
        root.GetProperty("listenEnabled").GetBoolean().Should().BeFalse();
        root.GetProperty("listenPort").GetInt32().Should().Be(47600);

        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(3);
        rows.Select(r => r.GetProperty("name").GetString()).Should()
            .Equal("desk2", "laptop", "wsl-box");
        rows.Should().AllSatisfy(r =>
        {
            r.GetProperty("outbound").ValueKind.Should().Be(JsonValueKind.Null);
            r.GetProperty("inbound").ValueKind.Should().Be(JsonValueKind.Null);
        });
    }

    [Fact]
    public async Task Links_SaysWhichHealthSourceProducedItsRows()
    {
        // r-2 makes the command a shell guard only when a tray answered, and a records-only run
        // looks exactly like a live run in which nothing is wrong. Every run has to state which
        // it was, or the exit code is untrustworthy for a new reason.
        WritePublishers();

        var (_, stdout, _) = await RunAsync("links");

        stdout.Should().Contain("health:");
        stdout.Should().Contain("records only",
            "IMRDY_HOME is set, so the live query is skipped — a tray serves the home it was started with");
    }

    [Fact]
    public async Task LinksJson_PutsTheHealthSourceOnStderr_SoAPipeGetsOnlyThePayload()
    {
        WritePublishers();

        var (_, stdout, stderr) = await RunAsync("links --json");

        stderr.Should().Contain("health:");
        JsonDocument.Parse(stdout).RootElement.TryGetProperty("rows", out _).Should().BeTrue(
            "stdout must stay parseable by jq");
    }

    [Fact]
    public async Task Links_ReportsARecordWithNoEndpointAsReceiveOnly_NotAsAMissingSink()
    {
        // r-1: a receiver registers a publisher to carry its desktop mapping and mute. There is
        // nothing to dial, so "no sink" would report a fault the link cannot have.
        File.WriteAllText(
            Path.Combine(_home, "publishers.json"),
            """{"publishers":[{"name":"wsl-box","desktop_index":2,"enabled":true}]}""");

        var (exitCode, stdout, _) = await RunAsync("links");

        exitCode.Should().Be(0);
        stdout.Should().Contain("receive-only").And.NotContain("no sink");
    }

    [Fact]
    public async Task Links_IsDiscoverableFromHelp()
    {
        // Two of the five touches only show up here: a command missing from ShowHelp or
        // ShowCommandHelp works perfectly and cannot be found.
        var (_, topLevel, _) = await RunAsync("--help");
        var (_, detail, _) = await RunAsync("links --help");

        topLevel.Should().Contain("links");
        detail.Should().Contain("--json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[LinksCommandIntegrationTests] cleanup of '{_home}' failed: {ex.Message}");
        }
    }
}
