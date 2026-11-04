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

    [Fact]
    public void Stabilizer_HoldsAndPredictsBriefFaceLoss()
    {
        var stabilizer = new HeadTrackingStabilizer();
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracked = stabilizer.Update(true, true, true, 20, 1, first, first, 0, 1);
        var lost = stabilizer.Update(true, true, false, 20, 0, first, first.AddMilliseconds(100), 0, 1);

        Assert.Equal(HeadTrackingMode.Tracking, tracked.Mode);
        Assert.Equal(HeadTrackingMode.TemporarilyLost, lost.Mode);
        Assert.True(lost.CorrectionEnabled);
        Assert.Equal(20, lost.YawDegrees, 6);
    }

    [Fact]
    public void StabilizerFallsBackAfterTimeoutAndRecoversSmoothly()
    {
        var stabilizer = new HeadTrackingStabilizer();
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stabilizer.Update(true, true, true, 30, 1, first, first, 0, 1);

        var fallback = stabilizer.Update(true, true, false, 30, 0, first, first.AddSeconds(1), 0, 0.2);
        var recovered = stabilizer.Update(true, true, true, 0, 1, first.AddSeconds(1.1), first.AddSeconds(1.1), 0, 0.2);

        Assert.Equal(HeadTrackingMode.Fallback, fallback.Mode);
        Assert.False(fallback.CorrectionEnabled);
        Assert.InRange(Math.Abs(fallback.YawDegrees), 0, 30);
        Assert.Equal(HeadTrackingMode.Tracking, recovered.Mode);
        Assert.InRange(Math.Abs(recovered.YawDegrees), 0, Math.Abs(fallback.YawDegrees));
    }

    [Fact]
    public void StabilizerRejectsLowConfidenceSamples()
    {
        var stabilizer = new HeadTrackingStabilizer();
        var now = DateTime.UtcNow;
        var result = stabilizer.Update(true, true, true, 30, 0.1, now, now, 0, 1);

        Assert.Equal(HeadTrackingMode.Fallback, result.Mode);
        Assert.False(result.CorrectionEnabled);
    }
}
