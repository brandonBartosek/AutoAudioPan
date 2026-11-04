using AutoAudioPan.Native;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AutoAudioPan.Audio;

/// <summary>Captures one app's process tree, applies pan/volume, and renders to the default output.</summary>
public sealed class AppPipeline : IDisposable
{
    private readonly ProcessLoopbackCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _out;
    private readonly StreamProcessor _proc = new();
    private readonly System.Diagnostics.Stopwatch _age = System.Diagnostics.Stopwatch.StartNew();
    private float _appliedFocusGain = 1f;

    public volatile float TargetPan;
    public volatile float Volume = 1f;
    public volatile float FocusGain = 1f;
    public volatile float InputGain = 1f;
    public volatile bool Muted;

    public AppPipeline(uint rootPid)
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(ProcessLoopbackCapture.SampleRate, ProcessLoopbackCapture.Channels);
        _buffer = new BufferedWaveProvider(fmt)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true
        };
        _out = new WasapiOut(AudioClientShareMode.Shared, 40);
        _out.Init(_buffer);
        _buffer.AddSamples(new byte[fmt.AverageBytesPerSecond / 25], 0, fmt.AverageBytesPerSecond / 25); // 40 ms priming
        try { _capture = ProcessLoopbackCapture.Start(rootPid); }
        catch { _out.Dispose(); throw; }
        _capture.DataAvailable += OnData;
        _out.Play();
    }

    public event Action<Exception>? Failed
    {
        add => _capture.Failed += value;
        remove => _capture.Failed -= value;
    }

    private byte[] _bytes = new byte[8192];

    private void OnData(float[] samples, int count)
    {
        float peak = 0;
        for (int i = 0; i < count; i++) { var a = Math.Abs(samples[i]); if (a > peak) peak = a; }
        // Stay silent briefly while the original session is attenuated so its direct audio and our copy never overlap.
        float vol = Muted || _age.ElapsedMilliseconds < 250 ? 0f : Volume;
        // Originals are attenuated, so captured peaks are tiny; a loud buffer means an unattenuated source, so skip the gain.
        float gain = InputGain;
        if (gain > 1f && peak > 0.02f) gain = 1f;
        var targetFocus = Math.Clamp(FocusGain, 0, 1);
        var frames = Math.Max(1, count / ProcessLoopbackCapture.Channels);
        var ramp = Math.Clamp(frames / (float)ProcessLoopbackCapture.SampleRate / 0.08f, 0.05f, 1);
        _appliedFocusGain += (targetFocus - _appliedFocusGain) * ramp;
        vol *= gain * _appliedFocusGain;
        _proc.Process(samples, count, vol, TargetPan);
        int bytes = count * 4;
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        Buffer.BlockCopy(samples, 0, _bytes, 0, bytes);
        _buffer.AddSamples(_bytes, 0, bytes);
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
