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
