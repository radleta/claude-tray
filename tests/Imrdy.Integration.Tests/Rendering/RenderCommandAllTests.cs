using FluentAssertions;
using Imrdy.Windows.Commands;
using Xunit;

namespace Imrdy.Integration.Tests.Rendering;

/// <summary>
/// Integration tests for the batch-render path of <see cref="RenderCommand"/>:
/// <c>imrdy render &lt;component&gt; --all [--output-dir &lt;dir&gt;]</c> and
/// <c>imrdy render --all [--output-dir &lt;dir&gt;]</c>.
///
/// STA threading: all render paths use an STA thread to satisfy WinForms DrawToBitmap.
/// Shares the RenderCommandConsole collection with sibling test classes so that
/// Console.Out/Error redirects don't race across parallel test class execution.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RenderCommandConsole")]
public class RenderCommandAllTests
{
    private const int ExpectedDashboardFixtureCount = 14;
    private const int ExpectedGlobalFixtureCount    = 23; // 14 dashboard + 2 workspace-dashboard + 5 overlay + 2 connections

    /// <summary>
    /// Returns the repo root by walking up from the test binary output directory.
    /// AppContext.BaseDirectory is e.g. .../tests/Imrdy.Integration.Tests/bin/Debug/net10.0-windows.../
    /// so 5 levels up reaches the repo root.
    /// </summary>
    private static string RepoRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static int RunOnSta(string[] args, StringWriter stdout, StringWriter stderr)
    {
        int exitCode = -1;
        Exception? threadEx = null;

        var origOut = Console.Out;
        var origErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    exitCode = RenderCommand.Run(args);
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }

        if (threadEx is not null)
            throw new InvalidOperationException($"STA thread threw: {threadEx.Message}", threadEx);

        return exitCode;
    }

    [Fact]
    public void Run_ComponentAll_WritesAllPngsAndExitsZero()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "imrdy-render-test-" + Guid.NewGuid());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var origCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = RepoRoot();
        try
        {
            var exitCode = RunOnSta(
                ["render", "dashboard", "--all", "--output-dir", tmpDir],
                stdout, stderr);

            exitCode.Should().Be(0, because: $"component --all happy path should succeed; stderr={stderr}");
            stderr.ToString().Should().BeEmpty(because: "no errors expected on success");

            var pngs = Directory.GetFiles(tmpDir, "*.png");
            pngs.Should().HaveCount(ExpectedDashboardFixtureCount,
                because: "one PNG per fixture file must be written");

            foreach (var png in pngs)
            {
                new FileInfo(png).Length.Should().BeGreaterThan(500,
                    because: $"PNG {Path.GetFileName(png)} must be a non-trivial image (> 500 bytes)");
            }

            var summaryLines = stdout.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.TrimStart().StartsWith("dashboard/", StringComparison.Ordinal))
                .ToList();
            summaryLines.Should().HaveCount(ExpectedDashboardFixtureCount,
                because: "one summary line per fixture must be printed to stdout");

            foreach (var line in summaryLines)
            {
                line.Should().MatchRegex(@"\d+x\d+",
                    because: "each summary line must include WxH dimensions");
            }
        }
        finally
        {
            Environment.CurrentDirectory = origCwd;
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Run_GlobalAll_WritesAllPngsAndExitsZero()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "imrdy-render-test-" + Guid.NewGuid());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var origCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = RepoRoot();
        try
        {
            var exitCode = RunOnSta(
                ["render", "--all", "--output-dir", tmpDir],
                stdout, stderr);

            exitCode.Should().Be(0, because: $"global --all happy path should succeed; stderr={stderr}");
            stderr.ToString().Should().BeEmpty(because: "no errors expected on success");

            var pngs = Directory.GetFiles(tmpDir, "*.png");
            pngs.Should().HaveCount(ExpectedGlobalFixtureCount,
                because: "one PNG per fixture across all registered components must be written");

            // Filter to render summary lines only — FluentAssertions may print a license
            // warning to Console.Out when first used, which bleeds into this test's
            // redirected stdout during a full-suite run.
            var lines = stdout.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("dashboard/", StringComparison.Ordinal)
                         || l.StartsWith("workspace-dashboard/", StringComparison.Ordinal)
                         || l.StartsWith("overlay/", StringComparison.Ordinal)
                         || l.StartsWith("connections/", StringComparison.Ordinal))
                .ToList();
            lines.Should().HaveCount(ExpectedGlobalFixtureCount,
                because: "one summary line per fixture across all components must be printed to stdout");
        }
        finally
        {
            Environment.CurrentDirectory = origCwd;
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Run_OutputAndAllTogether_WritesCannotCombineAndReturnsOne()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = RunOnSta(
            ["render", "dashboard", "--all", "--output", "foo.png"],
            stdout, stderr);

        exitCode.Should().Be(1, because: "--output + --all is a user error");
        stderr.ToString().Should().Contain("cannot combine",
            because: "message must describe the mutual exclusion");
    }
}
