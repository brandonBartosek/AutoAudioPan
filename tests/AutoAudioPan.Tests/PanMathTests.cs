using AutoAudioPan.Core;
using Xunit;

public class PanMathTests
{
    // Two 1920px monitors: virtual desktop [0, 3840)
    [Fact]
    public void WindowOnRightMonitorPansRight() =>
        Assert.Equal(0.5f, PanMath.FromWindow(1920, 3840, 0, 3840), 3);

    [Fact]
    public void WindowOnLeftMonitorPansLeft() =>
        Assert.Equal(-0.5f, PanMath.FromWindow(0, 1920, 0, 3840), 3);

    [Fact]
    public void CenteredWindowIsCentered() =>
        Assert.Equal(0f, PanMath.FromWindow(960, 2880, 0, 3840), 3);

    [Fact]
    public void NegativeDesktopOriginAndClamping()
    {
        Assert.Equal(-1f, PanMath.FromWindow(-4000, -3900, -1920, 3840));
        Assert.Equal(1f, PanMath.FromWindow(9000, 9100, -1920, 3840));
    }

    [Fact]
    public void FullLeftFoldsRightChannelIntoLeft()
    {
        PanMath.Apply(0.2f, 0.4f, -1f, out var l, out var r);
        Assert.Equal(0.6f, l, 3);
        Assert.Equal(0f, r, 3);
    }

    [Fact]
    public void CenterIsUnchanged()
    {
        PanMath.Apply(0.3f, 0.5f, 0f, out var l, out var r);
        Assert.Equal(0.3f, l, 3);
        Assert.Equal(0.5f, r, 3);
    }
}
