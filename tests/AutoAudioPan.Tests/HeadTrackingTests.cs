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

    [Theory]
    [InlineData(1, 0.75, 0.75)]
    [InlineData(-1, 0.75, -0.75)]
    [InlineData(0.4, 0.75, 0.4)]
    public void PanCap_PreventsFullOneEarRouting(double pan, double cap, double expected) =>
        Assert.Equal(expected, HeadYawMath.ApplyPanCap(pan, cap), 6);

    [Fact]
    public void FocusGain_IsNormalNearHeadDirectionAndFlooredFarAway()
    {
        var near = HeadYawMath.FocusGain(0.2, 9, 45, true, 0.55, 0.55);
        var far = HeadYawMath.FocusGain(-1, 45, 45, true, 0.55, 0.55);

        Assert.Equal(1, near, 2);
        Assert.InRange(far, 0.55, 0.7);
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

        var fallback = stabilizer.Update(true, true, false, 30, 0, first, first.AddSeconds(2), 0, 0.2);
        var recovered = stabilizer.Update(true, true, true, 0, 1, first.AddSeconds(1.1), first.AddSeconds(1.1), 0, 0.2);

        Assert.Equal(HeadTrackingMode.Fallback, fallback.Mode);
        Assert.True(fallback.CorrectionEnabled);
        Assert.InRange(Math.Abs(fallback.YawDegrees), 0, 30);
        Assert.Equal(HeadTrackingMode.Tracking, recovered.Mode);
        Assert.InRange(Math.Abs(recovered.YawDegrees), 0, Math.Abs(fallback.YawDegrees));
    }

    [Fact]
    public void StabilizerRetainsExtremePoseThroughLossThenDecays()
    {
        var stabilizer = new HeadTrackingStabilizer();
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stabilizer.Update(true, true, true, 55, 1, first, first, 0, 1);

        var held = stabilizer.Update(true, true, false, 55, 0, first, first.AddMilliseconds(800), 0, 0.2);
        var decaying = stabilizer.Update(true, true, false, 55, 0, first, first.AddSeconds(1.3), 0, 0.2);

        Assert.Equal(HeadTrackingMode.TemporarilyLost, held.Mode);
        Assert.True(held.CorrectionEnabled);
        Assert.Equal(55, held.YawDegrees, 6);
        Assert.Equal(HeadTrackingMode.Fallback, decaying.Mode);
        Assert.True(Math.Abs(decaying.YawDegrees) < 55);
    }

    [Fact]
    public void StabilizerReacquiresWithoutResettingToZero()
    {
        var stabilizer = new HeadTrackingStabilizer();
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stabilizer.Update(true, true, true, -45, 1, first, first, 0, 0.2);
        stabilizer.Update(true, true, false, -45, 0, first, first.AddSeconds(0.8), 0, 0.2);

        var reacquired = stabilizer.Update(true, true, true, -40, 1, first.AddSeconds(0.9), first.AddSeconds(0.9), 0, 0.2);

        Assert.Equal(HeadTrackingMode.Tracking, reacquired.Mode);
        Assert.True(reacquired.CorrectionEnabled);
        Assert.InRange(reacquired.YawDegrees, -20, -10);
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

    [Fact]
    public void GazeAssistRequiresDwellBeforeChangingFocus()
    {
        var stabilizer = new GazeAssistStabilizer();
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var candidate = stabilizer.Update(true, true, true, 0.8, 1, false, first, first, 0.55,
            TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(900), 0.5);
        var confirmed = stabilizer.Update(true, true, true, 0.8, 1, false, first.AddMilliseconds(450),
            first.AddMilliseconds(450), 0.55, TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(900), 0.5);

        Assert.Equal(GazeAssistMode.Candidate, candidate.Mode);
        Assert.False(candidate.FocusConfirmed);
        Assert.Equal(GazeAssistMode.Tracking, confirmed.Mode);
        Assert.True(confirmed.FocusConfirmed);
        Assert.True(confirmed.Direction > 0);
    }

    [Fact]
    public void GazeAssistRejectsBlinksAndSaccades()
    {
        var stabilizer = new GazeAssistStabilizer();
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stabilizer.Update(true, true, true, -0.7, 1, false, first, first, 0.55,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(900), 1);
        stabilizer.Update(true, true, true, -0.7, 1, false, first.AddMilliseconds(2),
            first.AddMilliseconds(2), 0.55, TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(900), 1);

        var blink = stabilizer.Update(true, true, true, -0.7, 1, true, first.AddMilliseconds(50),
            first.AddMilliseconds(50), 0.55, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(900), 1);
        var saccade = stabilizer.Update(true, true, true, 0.9, 1, false, first.AddMilliseconds(100),
            first.AddMilliseconds(100), 0.55, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(900), 1);

        Assert.Equal(GazeAssistMode.TemporarilyLost, blink.Mode);
        Assert.Equal(GazeAssistMode.TemporarilyLost, saccade.Mode);
        Assert.True(blink.Direction < 0);
        Assert.True(saccade.Direction < 0);
    }

    [Fact]
    public void GazeAssistRetainsFocusBrieflyThenFallsBack()
    {
        var stabilizer = new GazeAssistStabilizer();
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stabilizer.Update(true, true, true, 0.7, 1, false, first, first, 0.55,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(900), 1);
        stabilizer.Update(true, true, true, 0.7, 1, false, first.AddMilliseconds(2),
            first.AddMilliseconds(2), 0.55, TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(900), 1);

        var held = stabilizer.Update(true, true, false, 0.7, 0, false, first,
            first.AddMilliseconds(500), 0.55, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(900), 1);
        var fallback = stabilizer.Update(true, true, false, 0.7, 0, false, first,
            first.AddSeconds(2), 0.55, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(900), 0.2);

        Assert.Equal(GazeAssistMode.TemporarilyLost, held.Mode);
        Assert.True(held.FocusConfirmed);
        Assert.Equal(GazeAssistMode.Fallback, fallback.Mode);
        Assert.True(Math.Abs(fallback.Direction) < Math.Abs(held.Direction));
    }

    [Fact]
    public void FocusGainForDirectionUsesNormalizedGazeDirection()
    {
        var near = HeadYawMath.FocusGainForDirection(0.8, 0.8, true, 0.55, 0.55);
        var far = HeadYawMath.FocusGainForDirection(-0.8, 0.8, true, 0.55, 0.55);

        Assert.True(near > far);
        Assert.InRange(far, 0.55, 1);
    }
}
