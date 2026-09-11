using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

public class MachineNameResolverTests
{
    [Fact]
    public void Resolve_NoConfig_NotWsl_IsTheHostname()
    {
        MachineNameResolver.Resolve(null, "workstation", null).Should().Be("workstation");
    }

    [Fact]
    public void Resolve_NoConfig_InWsl_IsHostnameDashDistro()
    {
        MachineNameResolver.Resolve(null, "workstation", "Ubuntu-22.04")
            .Should().Be("workstation-Ubuntu-22.04");
    }

    [Fact]
    public void Resolve_ConfiguredName_WinsOverBothDefaults()
    {
        MachineNameResolver.Resolve("laptop", "workstation", "Ubuntu").Should().Be("laptop");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankConfiguredName_FallsBackToTheDefault(string configured)
    {
        MachineNameResolver.Resolve(configured, "workstation", null).Should().Be("workstation");
    }

    [Fact]
    public void Resolve_TrimsWhitespace()
    {
        MachineNameResolver.Resolve("  laptop  ", "workstation", null).Should().Be("laptop");
        MachineNameResolver.Resolve(null, "workstation", " Ubuntu ").Should().Be("workstation-Ubuntu");
    }

    [Fact]
    public void IsSameMachine_TheReceiverItself_IsLocal()
    {
        MachineNameResolver.IsSameMachine("workstation", "workstation").Should().BeTrue();
    }

    [Fact]
    public void IsSameMachine_ADistroOnTheReceiversOwnBox_IsLocal()
    {
        // D19: it earns real terminal focus and no desktop mapping.
        MachineNameResolver.IsSameMachine("workstation-Ubuntu", "workstation").Should().BeTrue();
    }

    [Fact]
    public void IsSameMachine_AnotherMachine_IsNotLocal()
    {
        MachineNameResolver.IsSameMachine("desk2", "workstation").Should().BeFalse();
    }

    [Fact]
    public void IsSameMachine_ADistroOnAnotherMachine_IsNotLocal()
    {
        MachineNameResolver.IsSameMachine("desk2-Ubuntu", "workstation").Should().BeFalse();
    }

    [Fact]
    public void IsSameMachine_RequiresTheSeparator_NotJustAPrefix()
    {
        // "desk2" must not read as local to a receiver named "desk".
        MachineNameResolver.IsSameMachine("desk2", "desk").Should().BeFalse();
    }

    [Fact]
    public void IsSameMachine_IsCaseInsensitive()
    {
        MachineNameResolver.IsSameMachine("WORKSTATION-Ubuntu", "workstation").Should().BeTrue();
    }
}
