using Imrdy.Core.Validation;
using FluentAssertions;

namespace Imrdy.Core.Tests;

public class ConfigValidatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigValidator _validator = new();
    private static readonly string[] AvailablePacks = ["assistant", "retro"];

    public ConfigValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "imrdy-cfgval-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Validate_ValidConfig_IsValid()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{"sound": {"defaultPack": "assistant", "projects": {"my-proj": "retro"}}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
        result.Errors.Where(e => e.Severity == ValidationSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingFile_Warning()
    {
        var path = Path.Combine(_tempDir, "nope.json");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue(); // warning only
        result.Errors.Should().ContainSingle(e =>
            e.Severity == ValidationSeverity.Warning && e.Message.Contains("not found"));
    }

    [Fact]
    public void Validate_CorruptJson_Error()
    {
        var path = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(path, "not json");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("Invalid JSON"));
    }

    [Fact]
    public void Validate_UnknownTopLevelKey_Warning()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{"sound": {"defaultPack": "assistant"}, "unknownKey": "value"}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue(); // warnings don't invalidate
        result.Errors.Should().Contain(e =>
            e.Severity == ValidationSeverity.Warning && e.Message.Contains("Unknown key"));
    }

    [Fact]
    public void Validate_SoundEnabledKey_NotWarned()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{"sound": {"enabled": true}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().NotContain(e => e.Message.Contains("enabled"));
    }

    [Fact]
    public void Validate_DanglingDefaultPackReference_Error()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"sound": {"defaultPack": "deleted-pack"}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.Message.Contains("deleted-pack") && e.Message.Contains("not installed"));
    }

    [Fact]
    public void Validate_DanglingProjectPackReference_Error()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{"sound": {"projects": {"my-proj": "nonexistent-pack"}}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.Message.Contains("nonexistent-pack") && e.Message.Contains("my-proj"));
    }

    [Fact]
    public void Validate_CaseInsensitivePackLookup()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"sound": {"defaultPack": "ASSISTANT"}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_RandomDefaultPack_IsValid()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"sound": {"defaultPack": "random"}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DisabledPacksKey_NotWarned()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"sound": {"disabledPacks": ["retro"]}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().NotContain(e => e.Message.Contains("disabledPacks"));
    }

    [Fact]
    public void Validate_EmptyConfig_IsValid()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, "{}");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_RootNotObject_Error()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, "[]");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("JSON object"));
    }

    [Fact]
    public void Validate_UnknownSoundSubKey_Warning()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{"sound": {"enabled": true, "badKey": 42}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().Contain(e =>
            e.Severity == ValidationSeverity.Warning && e.Message.Contains("sound.badKey"));
    }

    [Fact]
    public void Validate_UnknownTraySubKey_Warning()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{"tray": {"enabled": true, "badKey": 42}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().Contain(e =>
            e.Severity == ValidationSeverity.Warning && e.Message.Contains("tray.badKey"));
    }

    [Fact]
    public void Validate_TrayNotObject_Error()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"tray": "invalid"}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("'tray' must be a JSON object"));
    }

    [Fact]
    public void Validate_SoundNotObject_Error()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"sound": "invalid"}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("'sound' must be a JSON object"));
    }

    [Fact]
    public void Validate_OldFlatKeys_AreUnknown()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{"default": "assistant", "projectMappings": {}, "soundEnabled": true}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.IsValid.Should().BeTrue(); // warnings only
        result.Errors.Where(e => e.Severity == ValidationSeverity.Warning).Should().HaveCount(3);
    }

    [Fact]
    public void Validate_AllRealSections_NoUnknownKeyWarnings()
    {
        // D33: overlay and diagnostics are real working sections that the known-key sets
        // used to omit, so `imrdy config validate` reported valid config as suspect.
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """
            {
              "tray": {"enabled": true, "iconStyle": "hexagons"},
              "sound": {"enabled": true, "defaultPack": "assistant"},
              "overlay": {"enabled": true, "position": "bottom-right", "size": 64, "spacing": 8,
                          "monitor": 0, "locked": false, "offsetX": 12, "offsetY": 34},
              "diagnostics": {"ipcEnabled": true},
              "network": {"machineName": "workstation", "authKey": "k", "listenPort": 47600, "listenEnabled": true}
            }
            """);

        var result = _validator.Validate(path, AvailablePacks);

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_UnknownNetworkKey_Warns()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"network": {"listenPrt": 47600}}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("network.listenPrt");
    }

    [Fact]
    public void Validate_NetworkNotAnObject_Errors()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"network": 47600}""");

        var result = _validator.Validate(path, AvailablePacks);

        result.Errors.Should().ContainSingle()
            .Which.Severity.Should().Be(ValidationSeverity.Error);
    }
}
