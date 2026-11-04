using OpenCvSharp;
using OpenCvSharp.Face;

namespace AutoAudioPan.Core;

/// <summary>Local webcam provider using OpenCV's frontal detector and LBF landmarks.</summary>
public sealed class OpenCvHeadYawProvider : IHeadYawProvider
{
    private const int CameraWidth = 640;
    private const int CameraHeight = 480;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private double _cameraYaw;
    private double _neutral;
    private bool _cameraSample;
    private string _status = "Camera off";

    public bool IsAvailable
    {
        get { lock (_gate) return _cameraSample; }
    }

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public double CurrentYawDegrees
    {
        get
        {
            lock (_gate)
            {
                return _cameraSample ? _cameraYaw - _neutral : 0;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_worker != null) return;
            _stop = new CancellationTokenSource();
            _status = "Starting camera...";
            _worker = Task.Run(() => CaptureLoop(_stop.Token));
        }
    }

    public void Stop()
    {
        Task? worker;
        lock (_gate)
        {
            _stop?.Cancel();
            worker = _worker;
            _worker = null;
            _cameraSample = false;
            _status = "Camera off";
        }
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lock (_gate) { _stop?.Dispose(); _stop = null; }
    }

    public void CalibrateNeutral()
    {
        lock (_gate) _neutral = _cameraSample ? _cameraYaw : 0;
    }

    private void CaptureLoop(CancellationToken token)
    {
        var modelPaths = new List<string>();
        try
        {
            var cascadePath = ExtractModel("haarcascade_frontalface_default.xml");
            var landmarkPath = ExtractModel("lbfmodel.yaml");
            modelPaths.Add(cascadePath);
            modelPaths.Add(landmarkPath);

            using var camera = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
            camera.Set(VideoCaptureProperties.FrameWidth, CameraWidth);
            camera.Set(VideoCaptureProperties.FrameHeight, CameraHeight);
            if (!camera.IsOpened()) throw new InvalidOperationException("No webcam is available.");

            using var detector = new CascadeClassifier(cascadePath);
            using var facemark = FacemarkLBF.Create(new FacemarkLBF.Params());
            facemark.LoadModel(landmarkPath);
            using var frame = new Mat();
            using var gray = new Mat();
            lock (_gate) _status = "Webcam head tracking";

            while (!token.IsCancellationRequested)
            {
                if (!camera.Read(frame) || frame.Empty())
                {
                    SetCameraUnavailable("Webcam frame unavailable.");
                    Thread.Sleep(100);
                    continue;
                }

                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                var faces = detector.DetectMultiScale(
                    gray, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(80, 80));
                if (faces.Length > 0 && facemark.Fit(gray, InputArray.Create(faces), out var landmarks)
                    && TryReadYaw(landmarks, out var yaw))
                {
                    lock (_gate)
                    {
                        _cameraYaw = yaw;
                        _cameraSample = true;
                        _status = "Webcam head tracking";
                    }
                }
                else
                {
                    SetCameraUnavailable("No face detected.");
                }
                Thread.Sleep(33);
            }
        }
        catch (Exception ex)
        {
            SetCameraUnavailable($"Webcam unavailable: {ex.Message}");
            Log.Error("Head tracking camera", ex);
        }
        finally
        {
            lock (_gate) _cameraSample = false;
            foreach (var modelPath in modelPaths)
            {
                try { File.Delete(modelPath); } catch { }
            }
        }
    }

    private void SetCameraUnavailable(string status)
    {
        lock (_gate)
        {
            _cameraSample = false;
            _status = status;
        }
    }

    private static bool TryReadYaw(Point2f[][] landmarks, out double yaw)
    {
        yaw = 0;
        if (landmarks.Length == 0 || landmarks[0].Length < 68) return false;

        var face = landmarks[0];
        var leftEyeX = face.Skip(36).Take(6).Average(p => p.X);
        var rightEyeX = face.Skip(42).Take(6).Average(p => p.X);
        var noseX = face[30].X;
        var eyeDistance = Math.Abs(rightEyeX - leftEyeX);
        if (eyeDistance < 1) return false;

        var eyeMid = (leftEyeX + rightEyeX) / 2;
        yaw = Math.Clamp(-(noseX - eyeMid) / eyeDistance * 90, -60, 60);
        return true;
    }

    private static string ExtractModel(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AutoAudioPan");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path)) return path;

        var resourceName = $"AutoAudioPan.Assets.{fileName}";
        using var source = typeof(OpenCvHeadYawProvider).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Embedded head-tracking model is missing.", resourceName);
        using var destination = File.Create(path);
        source.CopyTo(destination);
        return path;
    }

    public void Dispose() => Stop();
}
