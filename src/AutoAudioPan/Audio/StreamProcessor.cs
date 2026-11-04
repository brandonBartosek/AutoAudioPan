using AutoAudioPan.Core;

namespace AutoAudioPan.Audio;

/// <summary>
/// Per-sample pan/gain stage with smoothing, fade-in and a soft limiter so that
/// parameter changes and stream starts never produce clicks or hard clipping.
/// </summary>
public sealed class StreamProcessor
{
    public const float SampleRate = 48000f;
    private const float LimiterKnee = 0.8f;
    // One-pole smoothing coefficients (~5 ms gain, ~30 ms pan at 48 kHz).
    private static readonly float GainCoef = 1f - MathF.Exp(-1f / (0.005f * SampleRate));
    private static readonly float PanCoef = 1f - MathF.Exp(-1f / (0.030f * SampleRate));
    private const int FadeInFrames = 2400; // 50 ms

    private float _gain;
    private float _pan;
    private bool _first = true;
    private int _fadeFrame;

    public void Process(float[] samples, int count, float targetGain, float targetPan)
    {
        if (_first)
        {
            _pan = targetPan;
            _first = false;
        }
        for (int i = 0; i + 1 < count; i += 2)
        {
            _gain += (targetGain - _gain) * GainCoef;
            _pan += (targetPan - _pan) * PanCoef;
            float fade = _fadeFrame < FadeInFrames ? (float)(targetGain > 0f ? _fadeFrame++ : _fadeFrame) / FadeInFrames : 1f;
            float g = _gain * fade;
            PanMath.Apply(samples[i], samples[i + 1], _pan, out var l, out var r);
            samples[i] = SoftLimit(l * g);
            samples[i + 1] = SoftLimit(r * g);
        }
    }

    /// <summary>Transparent below the knee, smoothly saturating to +-1 above it.</summary>
    public static float SoftLimit(float x)
    {
        float a = Math.Abs(x);
        if (a <= LimiterKnee) return x;
        float over = (a - LimiterKnee) / (1f - LimiterKnee);
        float y = LimiterKnee + (1f - LimiterKnee) * MathF.Tanh(over);
        return x < 0 ? -y : y;
    }
}
