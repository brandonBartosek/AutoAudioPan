using System.Net;
using System.Text;
using System.Text.Json;

namespace AutoAudioPan.Core;

public sealed record ExtensionConfig(string Mode, double ManualPan, double Volume, bool Muted);
public sealed record HeadTrackingBridgeState(
    bool Enabled,
    bool Available,
    double YawDegrees,
    double MaxCorrectionDegrees,
    double MaxPanMagnitude,
    bool FocusAttenuationEnabled,
    double FocusFloor,
    double FocusWidth);

/// <summary>
/// Loopback HTTP endpoint the browser extension polls. While it is polling, the app leaves Chrome's audio alone
/// (the extension pans per window) and feeds it the Chrome row's mode/pan/volume/mute settings.
/// </summary>
public sealed class ExtensionBridge : IDisposable
{
    public const int DefaultPort = 47831;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private readonly HttpListener _listener = new();
    private long _lastSeen;
    private double _lastPan;
    private volatile ExtensionConfig _config = new("Auto", 0, 100, false);
    private volatile HeadTrackingBridgeState _headTracking = new(false, false, 0, 45, 0.75, false, 0.55, 0.55);

    public ExtensionBridge(int port = DefaultPort)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            _listener.Start();
            _ = Task.Run(Loop);
        }
        catch (Exception ex) { Log.Error("ExtensionBridge start", ex); }
    }

    public bool Connected
    {
        get
        {
            var t = Interlocked.Read(ref _lastSeen);
            return t != 0 && Environment.TickCount64 - t < Timeout.TotalMilliseconds;
        }
    }

    /// <summary>Pan of the window that most recently reported in, for display only.</summary>
    public double LastPan => Volatile.Read(ref _lastPan);

    public ExtensionConfig Config { set => _config = value; }
    public HeadTrackingBridgeState HeadTracking { set => _headTracking = value; }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            try { Handle(ctx); }
            catch (Exception ex) { Log.Error("ExtensionBridge request", ex); }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var origin = req.Headers["Origin"] ?? "";
        if (req.HttpMethod == "OPTIONS" && IsExtensionOrigin(origin))
        {
            AllowOrigin(res, origin);
            res.StatusCode = 204;
            res.Close();
            return;
        }
        // Only the extension (never a web page) may talk to this endpoint.
        if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/chrome"
            || !IsExtensionOrigin(origin))
        {
            res.StatusCode = 403;
            res.Close();
            return;
        }
        AllowOrigin(res, origin);

        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            if (doc.RootElement.TryGetProperty("pan", out var p) && p.TryGetDouble(out var pan))
                Volatile.Write(ref _lastPan, Math.Clamp(pan, -1, 1));
        }
        catch { }

        Interlocked.Exchange(ref _lastSeen, Environment.TickCount64);
        var c = _config;
        var h = _headTracking;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode = c.Mode,
            manualPan = c.ManualPan,
            volume = c.Volume,
            muted = c.Muted,
            headTracking = new
            {
                enabled = h.Enabled,
                available = h.Available,
                yawDegrees = h.YawDegrees,
                maxCorrectionDegrees = h.MaxCorrectionDegrees,
                maxPanMagnitude = h.MaxPanMagnitude,
                focusAttenuationEnabled = h.FocusAttenuationEnabled,
                focusFloor = h.FocusFloor,
                focusWidth = h.FocusWidth
            }
        });
        res.ContentType = "application/json";
        res.ContentLength64 = json.Length;
        res.OutputStream.Write(json);
        res.Close();
    }

    private static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Host.Length > 0;

    private static void AllowOrigin(HttpListenerResponse response, string origin)
    {
        response.Headers["Access-Control-Allow-Origin"] = origin;
        response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Vary"] = "Origin";
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch { }
    }
}
