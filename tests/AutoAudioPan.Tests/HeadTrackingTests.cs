using AutoAudioPan.Core;
using Xunit;

namespace AutoAudioPan.Tests;

public sealed class HeadTrackingTests
{
    [Theory]
    [InlineData(0.5, 0, 45, 0.5)]
    [InlineData(0.5, 22.5, 45, 0)]
    [InlineData(-0.5, -22.5, 45, 0)]
    public void CorrectPan_SubtractsYawFromSource(double source, double yaw, double limit, double expected) =>
        Assert.Equal(expected, HeadYawMath.CorrectPan(source, yaw, limit), 6);

    [Theory]
    [InlineData(1, 2, 0)]
    [InlineData(-4, 2, -2)]
    [InlineData(4, 2, 2)]
    public void DeadZone_RemovesSmallJitter(double yaw, double deadZone, double expected) =>
        Assert.Equal(expected, HeadYawMath.ApplyDeadZone(yaw, deadZone), 6);

    [Fact]
    public void Filter_SmoothsTowardTarget()
    {
        var filter = new HeadYawFilter();
        Assert.Equal(5, filter.Update(10, 0, 0.5), 6);
        Assert.Equal(7.5, filter.Update(10, 0, 0.5), 6);
    }

    [Fact]
    public void Correction_IsClampedToConfiguredMaximum()
    {
        Assert.Equal(-1, HeadYawMath.CorrectPan(0, 90, 45), 6);
        Assert.Equal(1, HeadYawMath.CorrectPan(0, -90, 45), 6);
    }
}
