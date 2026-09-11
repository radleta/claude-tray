using System.Text;
using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;

namespace Imrdy.Core.Tests.Publishing;

public class WireProtocolTests
{
    private static string Line(WireFrame frame) => Encoding.UTF8.GetString(WireProtocol.Serialize(frame));

    [Fact]
    public void Serialize_EndsWithExactlyOneNewline()
    {
        var line = Line(WireFrame.Remove("s1"));

        line.Should().EndWith("\n");
        line.TrimEnd('\n').Should().NotContain("\n");
    }

    [Fact]
    public void Hello_CarriesOnlyTheConnectFields()
    {
        var line = Line(WireFrame.Hello("workstation-Ubuntu", "secret"));

        line.Should().Contain("\"type\":\"hello\"")
            .And.Contain("\"schemaVersion\":\"1\"")
            .And.Contain("\"machine\":\"workstation-Ubuntu\"")
            .And.Contain("\"key\":\"secret\"")
            .And.NotContain("state")
            .And.NotContain("session_id");
    }

    [Fact]
    public void Hello_WithNoConfiguredKey_OmitsTheKeyField()
    {
        Line(WireFrame.Hello("workstation", null)).Should().NotContain("\"key\"");
    }

    [Fact]
    public void Remove_UsesTheSnakeCaseSessionIdSpelling()
    {
        Line(WireFrame.Remove("abc-123")).Should().Contain("\"session_id\":\"abc-123\"");
    }

    [Fact]
    public void Session_RoundTripsTheStatePayload()
    {
        var state = new StateFileModel
        {
            SessionId = "abc-123",
            Status = "idle",
            Project = "imrdy",
            Cwd = "/home/user/imrdy",
            HookEvent = "Stop",
            OriginMachine = "workstation-Ubuntu",
        };

        WireProtocol.TryParse(WireProtocol.Serialize(WireFrame.Session(state)), out var parsed).Should().BeTrue();

        parsed!.Type.Should().Be(WireFrameTypes.Session);
        parsed.State!.SessionId.Should().Be("abc-123");
        parsed.State.OriginMachine.Should().Be("workstation-Ubuntu");
    }

    [Fact]
    public void TryParse_IgnoresUnknownFields()
    {
        // Additive changes stay within a schema major (D28), so a peer one revision ahead must
        // still parse here rather than dropping the frame.
        WireProtocol.TryParse("""{"type":"remove","session_id":"s1","invented_later":true}""", out var frame)
            .Should().BeTrue();

        frame!.SessionId.Should().Be("s1");
    }

    [Fact]
    public void TryParse_AcceptsAnUnknownTypeSoTheCallerCanSkipIt()
    {
        WireProtocol.TryParse("""{"type":"something-new"}""", out var frame).Should().BeTrue();

        frame!.Type.Should().Be("something-new");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"type\":}")]
    [InlineData("{}")]
    public void TryParse_RejectsMalformedOrTypelessLines(string line)
    {
        WireProtocol.TryParse(line, out var frame).Should().BeFalse();
        frame?.Type.Should().BeNull();
    }

    [Fact]
    public void MaxLineBytes_Is64KiB()
    {
        WireProtocol.MaxLineBytes.Should().Be(65536);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("1.4", 1)]
    [InlineData("2", 2)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("v1", null)]
    public void SchemaMajor_ReadsTheMajorOrNothing(string? version, int? expected)
    {
        WireProtocol.SchemaMajor(version).Should().Be(expected);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.7", true)]
    [InlineData("2", false)]
    [InlineData(null, false)]
    public void IsCompatible_AcceptsOnlyTheSameMajor(string? version, bool expected)
    {
        WireProtocol.IsCompatible(version).Should().Be(expected);
    }
}
