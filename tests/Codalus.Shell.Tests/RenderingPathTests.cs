using Avalonia;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>Without a GPU Windows draws in software, not through ANGLE on WARP; CODALUS_RENDERING overrides the choice.</summary>
public sealed class RenderingPathTests
{
    [Theory]
    [InlineData(null, true, true)] // WARP: software
    [InlineData(null, false, false)] // a GPU: ANGLE
    [InlineData(null, null, false)] // DXGI couldn't say: Avalonia's defaults
    [InlineData("", true, true)]
    [InlineData("auto", true, true)]
    [InlineData("software", false, true)] // asked for, on a GPU
    [InlineData(" Software ", null, true)]
    [InlineData("gpu", true, false)] // ANGLE, even on WARP
    [InlineData("GPU", true, false)]
    public void Windows_draws_in_software_on_warp_unless_told_otherwise(string? setting, bool? warp, bool software)
    {
        var probed = 0;
        Assert.Equal(software, Rendering.UsesSoftware(setting, () =>
        {
            probed++;
            return warp;
        }));

        // An explicit choice doesn't probe the adapter.
        Assert.Equal(Rendering.Choice(setting) is null ? 1 : 0, probed);

        var options = Rendering.Windows(setting, () => warp);
        if (software)
        {
            Assert.Equal([Win32RenderingMode.Software], options.RenderingMode);
            Assert.Equal([Win32CompositionMode.RedirectionSurface], options.CompositionMode);
        }
        else
        {
            Assert.Equal(new Win32PlatformOptions().RenderingMode, options.RenderingMode);
            Assert.Equal(new Win32PlatformOptions().CompositionMode, options.CompositionMode);
        }
    }

    [Theory]
    [InlineData(0x1414u, 0x8Cu, 0u, true)] // Microsoft Basic Render Driver
    [InlineData(0x1414u, 0x8Cu, 2u, true)]
    [InlineData(0x10DEu, 0x2484u, 2u, true)] // flagged software, whoever made it
    [InlineData(0x10DEu, 0x2484u, 0u, false)] // a GPU
    [InlineData(0x1414u, 0x008Du, 0u, false)] // Microsoft's, but another device (Hyper-V's GPU partition, say)
    [InlineData(0x1414u, 0x8Cu, 1u, true)] // DXGI_ADAPTER_FLAG_REMOTE alone doesn't change it
    public void An_adapter_is_warp_when_flagged_software_or_the_basic_render_driver(uint vendor, uint device, uint flags, bool software) =>
        Assert.Equal(software, Rendering.IsSoftware(vendor, device, flags));

    [Fact]
    public void The_adapter_probe_answers_on_windows_and_nowhere_else()
    {
        var warp = Rendering.FirstAdapterIsSoftware();
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(warp); // DXGI is on every supported Windows, and always has a first adapter (WARP when there's no GPU)
        }
        else
        {
            Assert.Null(warp);
        }
    }

    [Fact]
    public void X11_keeps_its_framebuffer_and_draws_in_software_only_when_asked()
    {
        Assert.True(Rendering.X11(null).UseRetainedFramebuffer);
        Assert.Equal(new X11PlatformOptions().RenderingMode, Rendering.X11(null).RenderingMode);
        Assert.Equal(new X11PlatformOptions().RenderingMode, Rendering.X11("gpu").RenderingMode);
        Assert.Equal([X11RenderingMode.Software], Rendering.X11("software").RenderingMode);
        Assert.True(Rendering.X11("software").UseRetainedFramebuffer);
    }
}
