using System.Drawing;
using FluentAssertions;
using Imrdy.Core.Graphics;
using Imrdy.Windows.Icons;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Imrdy.Integration.Tests.Rendering;

/// <summary>
/// Smoke tests for PackIconRenderer using the dev-test graphics pack fixture.
/// Requires the dev-test pack to be present (delivered by Step 9).
/// </summary>
[Trait("Category", "Integration")]
public class PackRenderSmokeTest : IDisposable
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string DevTestPackDir => Path.Combine(
        RepoRoot, "src", "Imrdy.Windows", "Resources", "graphics-packs", "dev-test");

    private readonly string _tempPackDir;
    private GraphicsPackLoader.LoadedGraphicsPack? _pack;
    private PackIconRenderer? _renderer;

    public PackRenderSmokeTest()
    {
        _tempPackDir = Path.Combine(Path.GetTempPath(), "imrdy-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempPackDir);

        // Copy dev-test pack to temp dir to avoid any path side-effects
        if (Directory.Exists(DevTestPackDir))
        {
            foreach (var file in Directory.GetFiles(DevTestPackDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(DevTestPackDir, file);
                var destPath = Path.Combine(_tempPackDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath, overwrite: true);
            }
        }
    }

    private GraphicsPackLoader.LoadedGraphicsPack LoadDevTestPack()
    {
        var loader = new GraphicsPackLoader();
        var packJsonPath = Path.Combine(_tempPackDir, "pack.json");
        var pack = loader.LoadPack(_tempPackDir, packJsonPath);
        pack.Should().NotBeNull($"dev-test pack should load from {_tempPackDir}. Ensure Step 9 has placed the fixture at src/Imrdy.Windows/Resources/graphics-packs/dev-test/");
        return pack!;
    }

    [Fact]
    public void PackIconRenderer_IsHealthy_WhenDevTestPackLoads()
    {
        _pack = LoadDevTestPack();
        _renderer = new PackIconRenderer(_pack, NullLogger<PackIconRenderer>.Instance);

        _renderer.IsHealthy.Should().BeTrue("dev-test pack should render without errors");
    }

    [Fact]
    public void GetIcon_IdleTier0_ReturnsNonNullIconMatchingSmallIconSize()
    {
        _pack = LoadDevTestPack();
        _renderer = new PackIconRenderer(_pack, NullLogger<PackIconRenderer>.Instance);
        _renderer.IsHealthy.Should().BeTrue("renderer must be healthy for this assertion to be meaningful");

        var icon = _renderer.GetIcon("idle", 0, disconnected: false);

        icon.Should().NotBeNull();
        icon.Size.Should().Be(SystemInformation.SmallIconSize,
            "icon dimensions must match the system small icon size");
    }

    [Fact]
    public void GetIcon_IdleTier4_ReturnsDifferentIconHandleThanTier0()
    {
        _pack = LoadDevTestPack();
        _renderer = new PackIconRenderer(_pack, NullLogger<PackIconRenderer>.Instance);
        _renderer.IsHealthy.Should().BeTrue("renderer must be healthy for this assertion to be meaningful");

        var tier0 = _renderer.GetIcon("idle", 0, disconnected: false);
        var tier4 = _renderer.GetIcon("idle", 4, disconnected: false);

        tier0.Handle.Should().NotBe(tier4.Handle, "aging tier 4 should produce a visually distinct icon from tier 0");
    }

    [Fact]
    public void GetIcon_NonexistentStatus_ReturnsFallbackIconNotNull()
    {
        _pack = LoadDevTestPack();
        _renderer = new PackIconRenderer(_pack, NullLogger<PackIconRenderer>.Instance);
        _renderer.IsHealthy.Should().BeTrue("renderer must be healthy for this assertion to be meaningful");

        var fallback = _renderer.GetIcon("nonexistent_status_xyz", 0, disconnected: false);

        fallback.Should().NotBeNull("unknown statuses must return a fallback icon, not null");
    }

    public void Dispose()
    {
        _renderer?.Dispose();

        try
        {
            if (Directory.Exists(_tempPackDir))
                Directory.Delete(_tempPackDir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PackRenderSmokeTest] Failed to clean up '{_tempPackDir}': {ex.Message}");
        }
    }
}
