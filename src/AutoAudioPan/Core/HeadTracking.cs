namespace AutoAudioPan.Core;

public sealed class HeadTrackingSettings
{
    public bool Enabled { get; set; }
    public double DeadZoneDegrees { get; set; } = 2.5;
    public double Smoothing { get; set; } = 0.2;
    public double MaxCorrectionDegrees { get; set; } = 45;
}

public interface IHeadYawProvider : IDisposable
{
    bool IsAvailable { get; }
    bool HasSample { get; }
    double Confidence { get; }
    DateTime LastSampleUtc { get; }
    string Status { get; }
    double CurrentYawDegrees { get; }
    void Start();
    void Stop();
    void CalibrateNeutral();
}

/// <summary>Manual fallback provider used for testing when the webcam is unavailable.</summary>
public sealed class ManualHeadYawProvider : IHeadYawProvider
{
    private double _yaw;
    private double _neutral;

    public bool IsAvailable => true;
    public bool HasSample => true;
    public double Confidence => 1;
    public DateTime LastSampleUtc => DateTime.UtcNow;
    public string Status => "Manual yaw provider";
    public double CurrentYawDegrees => Volatile.Read(ref _yaw) - Volatile.Read(ref _neutral);

    public void Start() { }
    public void Stop() { }
    public void SetYaw(double degrees) => Volatile.Write(ref _yaw, Math.Clamp(degrees, -90, 90));

    public void CalibrateNeutral() => Volatile.Write(ref _neutral, Volatile.Read(ref _yaw));

    public void Dispose() { }
}

public sealed class HeadYawFilter
{
    public double CurrentDegrees { get; private set; }

    public double Update(double rawDegrees, double deadZoneDegrees, double smoothing)
    {
        var target = HeadYawMath.ApplyDeadZone(rawDegrees, deadZoneDegrees);
        CurrentDegrees = HeadYawMath.Smooth(CurrentDegrees, target, smoothing);
        return CurrentDegrees;
    }

    public void Reset() => CurrentDegrees = 0;
}

public enum HeadTrackingMode
{
    Disabled,
    Calibrating,
    Tracking,
    TemporarilyLost,
    Fallback
}

public sealed record HeadTrackingSnapshot(
    HeadTrackingMode Mode,
    double YawDegrees,
    double Confidence,
    TimeSpan SampleAge,
    bool CorrectionEnabled);

/// <summary>Stabilizes timestamped yaw samples and degrades smoothly when the face is lost.</summary>
public sealed class HeadTrackingStabilizer
{
    private readonly HeadYawFilter _filter = new();
    private DateTime _lastSampleUtc;
    private double _lastRawYaw;
    private double _velocity;
    private bool _hasSample;

    public double CurrentDegrees => _filter.CurrentDegrees;

    public HeadTrackingSnapshot Update(
        bool enabled,
        bool providerAvailable,
        bool hasSample,
        double rawYaw,
        double confidence,
        DateTime lastSampleUtc,
        DateTime now,
        double deadZoneDegrees,
        double smoothing)
    {
        if (!enabled)
        {
            Reset();
            return new(HeadTrackingMode.Disabled, 0, 0, TimeSpan.Zero, false);
        }

        var sampleAge = lastSampleUtc != default && lastSampleUtc <= now
            ? now - lastSampleUtc
            : TimeSpan.MaxValue;
        var valid = providerAvailable && hasSample && confidence >= 0.25 && sampleAge <= TimeSpan.FromSeconds(1);
        if (valid)
        {
            if (_hasSample)
            {
                var seconds = Math.Max(0.001, (lastSampleUtc - _lastSampleUtc).TotalSeconds);
                _velocity = Math.Clamp((rawYaw - _lastRawYaw) / seconds, -90, 90);
            }
            _lastRawYaw = rawYaw;
            _lastSampleUtc = lastSampleUtc;
            _hasSample = true;
            var filtered = _filter.Update(rawYaw, deadZoneDegrees, smoothing);
            return new(HeadTrackingMode.Tracking, filtered, confidence, sampleAge, true);
        }

        if (_hasSample && sampleAge <= TimeSpan.FromMilliseconds(350))
        {
            var predicted = Math.Clamp(
                _lastRawYaw + _velocity * sampleAge.TotalSeconds,
                _lastRawYaw - 12,
                _lastRawYaw + 12);
            var filtered = _filter.Update(predicted, deadZoneDegrees, smoothing);
            return new(HeadTrackingMode.TemporarilyLost, filtered, 0, sampleAge, true);
        }

        var recovering = _filter.Update(0, deadZoneDegrees, Math.Max(smoothing, 0.12));
        return new(HeadTrackingMode.Fallback, recovering, 0, sampleAge, false);
    }

    public void Reset()
    {
        _filter.Reset();
        _lastSampleUtc = default;
        _lastRawYaw = 0;
        _velocity = 0;
        _hasSample = false;
    }
}

public static class HeadYawMath
{
    public static double ApplyDeadZone(double degrees, double deadZoneDegrees)
    {
        var deadZone = Math.Max(0, deadZoneDegrees);
        var magnitude = Math.Abs(degrees);
        if (magnitude <= deadZone) return 0;
        return Math.CopySign(magnitude - deadZone, degrees);
    }

    public static double Smooth(double current, double target, double amount)
    {
        var alpha = Math.Clamp(amount, 0, 1);
        return current + (target - current) * alpha;
    }

    /// <summary>Subtracts head yaw from the screen-source pan and bounds the correction.</summary>
    public static double CorrectPan(
        double sourcePan,
        double filteredYawDegrees,
        double maxCorrectionDegrees)
    {
        var limit = Math.Max(0.001, Math.Abs(maxCorrectionDegrees));
        var correction = Math.Clamp(filteredYawDegrees / limit, -1, 1);
        return Math.Clamp(sourcePan - correction, -1, 1);
    }
}
