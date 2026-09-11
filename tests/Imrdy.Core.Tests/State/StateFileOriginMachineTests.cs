using FluentAssertions;
using Imrdy.Core.Hooks;
using Imrdy.Core.State;

namespace Imrdy.Core.Tests.State;

public class StateFileOriginMachineTests
{
    private static StateFileModel Sample(string? originMachine = null) => new()
    {
        SessionId = "abc123",
        Status = "idle",
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
        OriginMachine = originMachine,
    };

    [Fact]
    public void OriginMachine_DefaultsToNull_MeaningLocal()
    {
        Sample().OriginMachine.Should().BeNull();
    }

    [Fact]
    public void OriginMachine_SerializesAsOriginMachine()
    {
        var reader = new StateFileReader();
        var path = Path.Combine(Path.GetTempPath(), $"imrdy-origin-{Guid.NewGuid()}.json");

        try
        {
            reader.WriteStateFile(path, Sample("workstation-Ubuntu"));

            File.ReadAllText(path).Should().Contain("\"origin_machine\":\"workstation-Ubuntu\"");
            reader.ReadStateFile(path)!.OriginMachine.Should().Be("workstation-Ubuntu");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OriginMachine_UnknownToHookSeam_IsNotPreserved()
    {
        // Deliberate: no hook writes origin_machine on either side (D5), so the hook seam
        // has nothing to preserve. Its protection lives at the ingest merge instead.
        var merged = FieldPreservation.PreserveFields(Sample(), Sample("workstation-Ubuntu"));

        merged.OriginMachine.Should().BeNull();
    }

    [Fact]
    public void OriginMachine_AbsentFromJson_ReadsAsNull()
    {
        var reader = new StateFileReader();
        var path = Path.Combine(Path.GetTempPath(), $"imrdy-origin-{Guid.NewGuid()}.json");
        File.WriteAllText(path,
            """{"session_id":"abc","status":"idle","project":"p","cwd":"/c","hook_event":"Stop"}""");

        try
        {
            reader.ReadStateFile(path)!.OriginMachine.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
