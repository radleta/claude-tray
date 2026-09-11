using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

public class PublisherStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly PublisherStore _store;

    public PublisherStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "imrdy-pubstore-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "publishers.json");
        _store = new PublisherStore(_filePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        _store.Load().Publishers.Should().BeEmpty();
    }

    [Fact]
    public void Add_ThenLoad_RoundTripsEveryField()
    {
        _store.Add("workstation-Ubuntu", "/mnt/c/Users/rich/.imrdy/sessions");
        _store.SetDesktopIndex("workstation-Ubuntu", 4);
        _store.SetMuted("workstation-Ubuntu", true);
        _store.SetEnabled("workstation-Ubuntu", false);

        var entry = _store.Find("workstation-Ubuntu")!;

        entry.Name.Should().Be("workstation-Ubuntu");
        entry.Endpoint.Should().Be("/mnt/c/Users/rich/.imrdy/sessions");
        entry.DesktopIndex.Should().Be(4);
        entry.Muted.Should().BeTrue();
        entry.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Add_Defaults_AreEnabledAndUnmutedWithNoDesktopMapping()
    {
        _store.Add("desk2", "100.64.0.5:47600");

        var entry = _store.Find("desk2")!;

        entry.Enabled.Should().BeTrue();
        entry.Muted.Should().BeFalse();
        entry.DesktopIndex.Should().BeNull();
    }

    [Fact]
    public void Add_ExistingName_UpdatesEndpointAndKeepsPerLinkSettings()
    {
        _store.Add("desk2", "100.64.0.5:47600");
        _store.SetDesktopIndex("desk2", 2);
        _store.SetMuted("desk2", true);

        _store.Add("desk2", "100.64.0.9:47600");

        var entry = _store.Find("desk2")!;
        entry.Endpoint.Should().Be("100.64.0.9:47600");
        entry.DesktopIndex.Should().Be(2);
        entry.Muted.Should().BeTrue();
        _store.Load().Publishers.Should().ContainSingle();
    }

    [Fact]
    public void Upsert_NewName_WritesEveryFieldInOneGo()
    {
        _store.Upsert(new PublisherEntry
        {
            Name = "desk2",
            Endpoint = "100.64.0.5:47600",
            DesktopIndex = 3,
            Muted = true,
            Enabled = false,
        });

        var entry = _store.Find("desk2")!;
        entry.Endpoint.Should().Be("100.64.0.5:47600");
        entry.DesktopIndex.Should().Be(3);
        entry.Muted.Should().BeTrue();
        entry.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Upsert_ExistingName_ReplacesTheWholeRecord()
    {
        // This is the difference from Add, which keeps per-link settings: the connections
        // window's save path holds every field already, so the record it hands over is the
        // record — a field cleared in the dialog must not survive the save.
        _store.Add("desk2", "100.64.0.5:47600");
        _store.SetDesktopIndex("desk2", 2);
        _store.SetMuted("desk2", true);

        _store.Upsert(new PublisherEntry { Name = "desk2", Endpoint = "100.64.0.9:47600" });

        var entry = _store.Find("desk2")!;
        entry.Endpoint.Should().Be("100.64.0.9:47600");
        entry.DesktopIndex.Should().BeNull();
        entry.Muted.Should().BeFalse();
        _store.Load().Publishers.Should().ContainSingle();
    }

    [Fact]
    public void Upsert_NullEndpoint_RegistersReceiveOnly()
    {
        _store.Upsert(new PublisherEntry { Name = "mac-mini", Endpoint = null });

        _store.Find("mac-mini")!.Endpoint.Should().BeNull();
    }

    [Fact]
    public void Upsert_CaseOnlyNameChange_TakesTheSuppliedCasing()
    {
        // A rename to a genuinely different name is a separate Remove of the old record, but a
        // case-only correction addresses the same record — so the supplied entry wins outright
        // rather than leaving the operator's fix silently unapplied.
        _store.Add("Desk2", "100.64.0.5:47600");

        _store.Upsert(new PublisherEntry { Name = "desk2", Endpoint = "100.64.0.5:47600" });

        _store.Load().Publishers.Should().ContainSingle()
            .Which.Name.Should().Be("desk2");
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        _store.Add("Workstation-Ubuntu", "/mnt/c/sessions");

        _store.Find("workstation-UBUNTU").Should().NotBeNull();
    }

    [Fact]
    public void Remove_DropsTheEntry()
    {
        _store.Add("desk2", "100.64.0.5:47600");

        _store.Remove("desk2");

        _store.Load().Publishers.Should().BeEmpty();
    }

    [Fact]
    public void Remove_UnknownName_DoesNotWriteTheFile()
    {
        _store.Remove("never-added");

        File.Exists(_filePath).Should().BeFalse();
    }

    [Fact]
    public void SetDesktopIndex_Null_ClearsTheMapping()
    {
        _store.Add("desk2", "100.64.0.5:47600");
        _store.SetDesktopIndex("desk2", 3);

        _store.SetDesktopIndex("desk2", null);

        _store.Find("desk2")!.DesktopIndex.Should().BeNull();
    }

    [Fact]
    public void SetDesktopIndex_UnknownName_IsANoOp()
    {
        _store.SetDesktopIndex("never-added", 3);

        File.Exists(_filePath).Should().BeFalse();
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyAndLeavesTheFileAlone()
    {
        File.WriteAllText(_filePath, "{ not json at all");

        _store.Load().Publishers.Should().BeEmpty();

        File.ReadAllText(_filePath).Should().Be("{ not json at all");
    }

    [Fact]
    public void Save_WritesJsonWithSnakeCasePropertyNames()
    {
        _store.Add("desk2", "100.64.0.5:47600");
        _store.SetDesktopIndex("desk2", 3);

        var json = File.ReadAllText(_filePath);

        json.Should().Contain("\"desktop_index\":3");
        json.Should().Contain("\"endpoint\":\"100.64.0.5:47600\"");
    }

    [Fact]
    public void Add_WithNoEndpoint_RoundTripsAsNull_AndKeepsItsReceiverSideSettings()
    {
        // r-1: a receive-only registration. The record exists to carry this publisher's
        // desktop mapping and mute; a null that came back as "" would build an empty-endpoint
        // record, which is the malformed case.
        _store.Add("wsl-box", endpoint: null);
        _store.SetDesktopIndex("wsl-box", 2);
        _store.SetMuted("wsl-box", true);

        var entry = _store.Find("wsl-box")!;

        entry.Endpoint.Should().BeNull();
        entry.DesktopIndex.Should().Be(2);
        entry.Muted.Should().BeTrue();
    }
}
