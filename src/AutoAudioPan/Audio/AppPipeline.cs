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
    private float _pan;

    public volatile float TargetPan;
    public volatile float Volume = 1f;
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
        float vol = Muted ? 0f : Volume;
        float target = TargetPan;
        // Smooth pan changes so window moves don't click.
        _pan += (target - _pan) * 0.15f;
        float pan = _pan;
        for (int i = 0; i + 1 < count; i += 2)
        {
            Core.PanMath.Apply(samples[i], samples[i + 1], pan, out var l, out var r);
            samples[i] = l * vol;
            samples[i + 1] = r * vol;
        }
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
