namespace AutoAudioPan.Core;

public sealed class HeadTrackingSettings
{
    public bool Enabled { get; set; }
    public bool GazeAssistEnabled { get; set; }
    public int GazeDwellMilliseconds { get; set; } = 400;
    public double GazeConfidenceThreshold { get; set; } = 0.55;
    public double GazeSmoothing { get; set; } = 0.2;
    public int GazeRetentionMilliseconds { get; set; } = 900;
    public double DeadZoneDegrees { get; set; } = 2.5;
    public double Smoothing { get; set; } = 0.2;
    public double MaxCorrectionDegrees { get; set; } = 45;
    public double MaxPanMagnitude { get; set; } = 0.75;
    public bool FocusAttenuationEnabled { get; set; }
    public double FocusFloor { get; set; } = 0.55;
    public double FocusWidth { get; set; } = 0.55;
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

public enum GazeAssistMode
{
    Disabled,
    Tracking,
    Candidate,
    TemporarilyLost,
    Fallback
}

public sealed record GazeAssistSnapshot(
    GazeAssistMode Mode,
    double Direction,
    double Confidence,
    double DwellProgress,
    TimeSpan SampleAge,
    bool FocusConfirmed);

/// <summary>Filters local eye signals into a dwell-confirmed focus direction.</summary>
public sealed class GazeAssistStabilizer
{
    private DateTime _lastSampleUtc;
    private DateTime _candidateSinceUtc;
    private double _lastRawDirection;
    private double _direction;
    private double _candidateDirection;
    private bool _hasSample;
    private bool _hasCandidate;
    private bool _focusConfirmed;

    public GazeAssistSnapshot Update(
        bool enabled,
        bool providerAvailable,
        bool hasSample,
        double rawDirection,
        double confidence,
        bool blink,
        DateTime lastSampleUtc,
        DateTime now,
        double confidenceThreshold,
        TimeSpan dwell,
        TimeSpan retention,
        double smoothing)
    {
        if (!enabled)
        {
            Reset();
            return new(GazeAssistMode.Disabled, 0, 0, 0, TimeSpan.Zero, false);
        }

        var sampleAge = lastSampleUtc != default && lastSampleUtc <= now
            ? now - lastSampleUtc
            : TimeSpan.MaxValue;
        var threshold = double.IsFinite(confidenceThreshold)
            ? Math.Clamp(confidenceThreshold, 0, 1)
            : 0.55;
        var jump = _hasSample && Math.Abs(rawDirection - _lastRawDirection) > 0.65;
        var valid = providerAvailable && hasSample && !blink
                    && confidence >= threshold
                    && sampleAge <= TimeSpan.FromSeconds(1)
                    && !jump;

        if (jump)
        {
            _lastRawDirection = Math.Clamp(rawDirection, -1, 1);
            _candidateDirection = _lastRawDirection;
            _candidateSinceUtc = now;
            _hasCandidate = true;
        }

        if (valid)
        {
            _lastRawDirection = Math.Clamp(rawDirection, -1, 1);
            _lastSampleUtc = lastSampleUtc;
            _hasSample = true;
            var zone = Zone(_lastRawDirection);
            if (!_hasCandidate || Zone(_candidateDirection) != zone)
            {
                _candidateDirection = _lastRawDirection;
                _candidateSinceUtc = now;
                _hasCandidate = true;
            }
            else
            {
                _candidateDirection = HeadYawMath.Smooth(_candidateDirection, _lastRawDirection, 0.35);
            }

            var dwellProgress = DwellProgress(now - _candidateSinceUtc, dwell);
            if (dwellProgress >= 1)
            {
                _direction = HeadYawMath.Smooth(_direction, _candidateDirection, smoothing);
                _focusConfirmed = true;
            }

            var confirmed = dwellProgress >= 1;
            return new(
                confirmed ? GazeAssistMode.Tracking : GazeAssistMode.Candidate,
                _direction,
                confidence,
                confirmed ? 1 : dwellProgress,
                sampleAge,
                confirmed);
        }

        if (_hasSample && sampleAge <= retention)
        {
            return new(
                GazeAssistMode.TemporarilyLost,
                _direction,
                0,
                _hasCandidate ? DwellProgress(now - _candidateSinceUtc, dwell) : 0,
                sampleAge,
                _focusConfirmed);
        }

        var recovering = HeadYawMath.Smooth(_direction, 0, Math.Max(smoothing * 0.35, 0.035));
        _direction = recovering;
        return new(
            GazeAssistMode.Fallback,
            recovering,
            0,
            0,
            sampleAge,
            false);
    }

    public void Reset()
    {
        _lastSampleUtc = default;
        _candidateSinceUtc = default;
        _lastRawDirection = 0;
        _direction = 0;
        _candidateDirection = 0;
        _hasSample = false;
        _hasCandidate = false;
        _focusConfirmed = false;
    }

    private static double DwellProgress(TimeSpan elapsed, TimeSpan dwell)
    {
        var ms = Math.Max(1, dwell.TotalMilliseconds);
        return Math.Clamp(elapsed.TotalMilliseconds / ms, 0, 1);
    }

    private static int Zone(double direction) =>
        direction < -0.33 ? -1 : direction > 0.33 ? 1 : 0;
}

/// <summary>Stabilizes timestamped yaw samples and degrades smoothly when the face is lost.</summary>
public sealed class HeadTrackingStabilizer
{
    public static readonly TimeSpan PredictionHold = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan RetentionTimeout = TimeSpan.FromMilliseconds(1200);

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
        var valid = providerAvailable && hasSample && confidence >= (_hasSample ? 0.2 : 0.3)
                    && sampleAge <= TimeSpan.FromSeconds(1);
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

        if (_hasSample && sampleAge <= PredictionHold)
        {
            var predicted = Math.Clamp(
                _lastRawYaw + _velocity * sampleAge.TotalSeconds,
                _lastRawYaw - 12,
                _lastRawYaw + 12);
            var filtered = _filter.Update(predicted, deadZoneDegrees, smoothing);
            return new(HeadTrackingMode.TemporarilyLost, filtered, 0, sampleAge, true);
        }

        if (_hasSample && sampleAge <= RetentionTimeout)
            return new(HeadTrackingMode.TemporarilyLost, _filter.CurrentDegrees, 0, sampleAge, true);

        var recovering = _filter.Update(0, deadZoneDegrees, Math.Max(smoothing * 0.35, 0.035));
        var correctionEnabled = Math.Abs(recovering) > 0.25;
        return new(HeadTrackingMode.Fallback, recovering, 0, sampleAge, correctionEnabled);
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

    public static double ApplyPanCap(double pan, double maxPanMagnitude)
    {
        var cap = double.IsFinite(maxPanMagnitude)
            ? Math.Clamp(Math.Abs(maxPanMagnitude), 0.01, 1)
            : 0.75;
        return Math.Clamp(pan, -cap, cap);
    }

    public static double FocusGain(
        double sourcePan,
        double yawDegrees,
        double maxCorrectionDegrees,
        bool enabled,
        double floor,
        double width)
    {
        if (!enabled) return 1;
        var direction = Math.Clamp(yawDegrees / Math.Max(0.001, Math.Abs(maxCorrectionDegrees)), -1, 1);
        var distance = Math.Abs(Math.Clamp(sourcePan, -1, 1) - direction);
        var sigma = double.IsFinite(width) ? Math.Max(0.05, Math.Abs(width)) : 0.55;
        var focus = Math.Exp(-(distance * distance) / (2 * sigma * sigma));
        var minimum = double.IsFinite(floor) ? Math.Clamp(floor, 0, 1) : 0.55;
        return minimum + (1 - minimum) * focus;
    }

    public static double FocusGainForDirection(
        double sourcePan,
        double direction,
        bool enabled,
        double floor,
        double width) =>
        FocusGain(sourcePan, direction * 45, 45, enabled, floor, width);
}
