using AutoAudioPan.Audio;
using Xunit;

namespace AutoAudioPan.Tests;

public class StreamProcessorTests
{
    [Fact]
    public void SoftLimit_NeverExceedsOne_AndIsTransparentBelowKnee()
    {
        Assert.Equal(0.5f, StreamProcessor.SoftLimit(0.5f));
        foreach (var x in new[] { 0.9f, 1f, 5f, 500f, -500f })
            Assert.InRange(StreamProcessor.SoftLimit(x), -1f, 1f);
    }

    [Fact]
    public void FadeIn_StartsAtSilence()
    {
        var p = new StreamProcessor();
        var buf = Enumerable.Repeat(1f, 2000).ToArray();
        p.Process(buf, buf.Length, 1f, 0f);
        Assert.Equal(0f, buf[0]);
        Assert.True(buf[^1] > buf[100]);
    }

    [Fact]
    public void GainStep_IsSmoothed_NoLargeSampleJumps()
    {
        var p = new StreamProcessor();
        var warm = Enumerable.Repeat(0.01f, 9600).ToArray();
        p.Process(warm, warm.Length, 1f, 0f);
        var buf = Enumerable.Repeat(0.002f, 4800).ToArray();
        p.Process(buf, buf.Length, 500f, 0f);
        float maxJump = 0;
        for (int i = 2; i < buf.Length; i += 2) maxJump = Math.Max(maxJump, Math.Abs(buf[i] - buf[i - 2]));
        Assert.True(maxJump < 0.05f, $"jump {maxJump}");
        Assert.All(buf, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void LoudInput_IsLimited()
    {
        var p = new StreamProcessor();
        var buf = Enumerable.Repeat(1f, 20000).ToArray();
        p.Process(buf, buf.Length, 500f, 0.5f);
        Assert.All(buf, v => Assert.InRange(v, -1f, 1f));
    }
}
