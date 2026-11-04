using System.Runtime.InteropServices;

namespace AutoAudioPan.Native;

/// <summary>Per-process-tree WASAPI loopback capture (Windows 10 2004+ / Windows 11). 48 kHz float32 stereo.</summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public event Action<float[], int>? DataAvailable; // interleaved samples, sample count
    public event Action<Exception>? Failed;

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private IntPtr _event;
    private Thread? _thread;
    private volatile bool _stop;

    public static ProcessLoopbackCapture Start(uint processId)
    {
        var c = new ProcessLoopbackCapture();
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        c._thread = new Thread(() =>
        {
            try { c.Init(processId); }
            catch (Exception ex) { error = ex; c.Cleanup(); ready.Set(); return; }
            ready.Set();
            c.Loop();
            c.Cleanup();
        }) { IsBackground = true, Name = "ProcessLoopback", Priority = ThreadPriority.AboveNormal };
        c._thread.SetApartmentState(ApartmentState.MTA);
        c._thread.Start();
        ready.Wait();
        if (error != null) throw error;
        return c;
    }

    private void Init(uint pid)
    {
        var blob = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0 };
        var blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var pvPtr = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(blob, blobPtr, false);
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pvPtr, i, 0);
            Marshal.WriteInt16(pvPtr, 0, 0x41); // VT_BLOB
            Marshal.WriteInt32(pvPtr, 8, Marshal.SizeOf<AudioClientActivationParams>());
            Marshal.WriteIntPtr(pvPtr, 16, blobPtr);

            var handler = new ActivateHandler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pvPtr, handler, out var op);
            if (!handler.Done.Wait(5000)) throw new TimeoutException("Audio activation timed out");
            op.GetActivateResult(out int hr, out object unk);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            _client = (IAudioClient)unk;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(pvPtr);
        }

        var fmt = new WaveFormatEx
        {
            FormatTag = 3, Channels = Channels, SamplesPerSec = SampleRate, BitsPerSample = 32,
            BlockAlign = Channels * 4, AvgBytesPerSec = SampleRate * Channels * 4, Size = 0
        };
        const uint flags = 0x00020000 | 0x00040000 | 0x80000000 | 0x08000000; // LOOPBACK|EVENTCALLBACK|AUTOCONVERTPCM|SRC
        var guid = Guid.Empty;
        Marshal.ThrowExceptionForHR(_client.Initialize(0, flags, 200 * 10000, 0, ref fmt, ref guid));
        _event = CreateEvent(IntPtr.Zero, false, false, null);
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_event));
        var capIid = typeof(IAudioCaptureClient).GUID;
        Marshal.ThrowExceptionForHR(_client.GetService(ref capIid, out var svc));
        _capture = (IAudioCaptureClient)svc;
        Marshal.ThrowExceptionForHR(_client.Start());
    }

    private void Loop()
    {
        var buf = new float[SampleRate * Channels];
        try
        {
            while (!_stop)
            {
                WaitForSingleObject(_event, 100);
                while (!_stop)
                {
                    _capture!.GetNextPacketSize(out uint packet);
                    if (packet == 0) break;
                    int hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
                    int n = (int)frames * Channels;
                    if (n > buf.Length) buf = new float[n];
                    if ((flags & 2) != 0) Array.Clear(buf, 0, n); // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(data, buf, 0, n);
                    _capture.ReleaseBuffer(frames);
                    DataAvailable?.Invoke(buf, n);
                }
            }
        }
        catch (Exception ex) when (!_stop) { Failed?.Invoke(ex); }
    }

    public void Dispose()
    {
        _stop = true;
        if (_event != IntPtr.Zero) SetEvent(_event);
        _thread?.Join(1000);
    }

    private void Cleanup()
    {
        try { _client?.Stop(); } catch { }
        if (_capture != null) Marshal.ReleaseComObject(_capture);
        if (_client != null) Marshal.ReleaseComObject(_client);
        _capture = null; _client = null;
        if (_event != IntPtr.Zero) { CloseHandle(_event); _event = IntPtr.Zero; }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [DllImport("kernel32.dll")] private static extern IntPtr CreateEvent(IntPtr a, bool manual, bool initial, string? name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag, Channels; public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivateHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Done.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, ref Guid sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormatEx format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
