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
    private double _confidence;
    private double _neutral;
    private double _gazeDirection;
    private double _gazeConfidence;
    private double _gazeNeutral;
    private bool _cameraSample;
    private bool _gazeSample;
    private bool _blink;
    private bool _cameraReady;
    private DateTime _lastSampleUtc;
    private DateTime _lastGazeSampleUtc;
    private string _status = "Camera off";

    public bool IsAvailable
    {
        get { lock (_gate) return _cameraReady; }
    }

    public bool HasSample { get { lock (_gate) return _cameraSample; } }
    public double Confidence { get { lock (_gate) return _confidence; } }
    public DateTime LastSampleUtc { get { lock (_gate) return _lastSampleUtc; } }
    public bool HasGazeSample { get { lock (_gate) return _gazeSample; } }
    public double GazeConfidence { get { lock (_gate) return _gazeConfidence; } }
    public bool Blink { get { lock (_gate) return _blink; } }
    public DateTime LastGazeSampleUtc { get { lock (_gate) return _lastGazeSampleUtc; } }

    public double CurrentGazeDirection
    {
        get
        {
            lock (_gate)
            {
                return _gazeDirection - _gazeNeutral;
            }
        }
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
                return _cameraYaw - _neutral;
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
            _gazeSample = false;
            _status = "Camera off";
        }
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lock (_gate) { _stop?.Dispose(); _stop = null; }
    }

    public void CalibrateNeutral()
    {
        lock (_gate)
        {
            _neutral = _cameraSample ? _cameraYaw : 0;
            _gazeNeutral = _gazeSample ? _gazeDirection : 0;
        }
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
            lock (_gate)
            {
                _cameraReady = true;
                _status = "Webcam head tracking";
            }

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
                        _confidence = FaceConfidence(faces[0]);
                        _lastSampleUtc = DateTime.UtcNow;
                        _cameraSample = true;
                        if (TryReadGaze(gray, landmarks, out var gaze, out var gazeConfidence, out var blink))
                        {
                            _gazeDirection = gaze;
                            _gazeConfidence = gazeConfidence;
                            _lastGazeSampleUtc = _lastSampleUtc;
                            _gazeSample = true;
                            _blink = blink;
                        }
                        else
                        {
                            _gazeConfidence = 0;
                            _gazeSample = false;
                            _blink = false;
                        }
                        _status = "Webcam head and eye tracking";
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
            lock (_gate)
            {
                _cameraSample = false;
                _gazeSample = false;
                _cameraReady = false;
            }
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
            _gazeSample = false;
            _confidence = 0;
            _gazeConfidence = 0;
            _blink = false;
            _status = status;
        }
    }

    private static double FaceConfidence(Rect face)
    {
        var area = Math.Max(1, face.Width * face.Height);
        return Math.Clamp(Math.Sqrt(area) / 260, 0, 1);
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

    private static bool TryReadGaze(
        Mat gray,
        Point2f[][] landmarks,
        out double direction,
        out double confidence,
        out bool blink)
    {
        direction = 0;
        confidence = 0;
        blink = false;
        if (landmarks.Length == 0 || landmarks[0].Length < 68) return false;

        var face = landmarks[0];
        var left = ReadEye(gray, face, 36, out var leftBlink, out var leftConfidence);
        var right = ReadEye(gray, face, 42, out var rightBlink, out var rightConfidence);
        blink = leftBlink || rightBlink;
        if (blink || left == null || right == null) return false;

        direction = Math.Clamp(((left.Value + right.Value) * 0.5 - 0.5) * 2, -1, 1);
        confidence = Math.Clamp((leftConfidence + rightConfidence) * 0.5, 0, 1);
        return confidence > 0;
    }

    private static double? ReadEye(
        Mat gray,
        Point2f[] face,
        int start,
        out bool blink,
        out double confidence)
    {
        blink = false;
        confidence = 0;
        var eye = face.Skip(start).Take(6).ToArray();
        var width = Distance(eye[0], eye[3]);
        var height = (Distance(eye[1], eye[5]) + Distance(eye[2], eye[4])) * 0.5;
        if (width < 4 || height / width < 0.16)
        {
            blink = true;
            return null;
        }

        var minX = Math.Max(0, (int)Math.Floor(eye.Min(p => p.X)));
        var maxX = Math.Min(gray.Cols - 1, (int)Math.Ceiling(eye.Max(p => p.X)));
        var minY = Math.Max(0, (int)Math.Floor(eye.Min(p => p.Y)));
        var maxY = Math.Min(gray.Rows - 1, (int)Math.Ceiling(eye.Max(p => p.Y)));
        if (maxX <= minX || maxY <= minY) return null;

        double sum = 0;
        int pixels = 0;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if (InsidePolygon(x, y, eye))
            {
                sum += gray.At<byte>(y, x);
                pixels++;
            }
        if (pixels < 8) return null;

        var mean = sum / pixels;
        var threshold = Math.Min(90, mean * 0.65);
        double weightedX = 0;
        double weightTotal = 0;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if (InsidePolygon(x, y, eye))
            {
                var value = gray.At<byte>(y, x);
                var weight = threshold - value;
                if (weight > 0)
                {
                    weightedX += x * weight;
                    weightTotal += weight;
                }
            }
        if (weightTotal < 4) return null;

        var center = weightedX / weightTotal;
        confidence = Math.Clamp(Math.Min(1, weightTotal / (width * height * 0.12)), 0, 1);
        return Math.Clamp((center - minX) / Math.Max(1, maxX - minX), 0, 1);
    }

    private static bool InsidePolygon(int x, int y, Point2f[] polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var j = (i + polygon.Length - 1) % polygon.Length;
            var yi = polygon[i].Y;
            var yj = polygon[j].Y;
            var crosses = (yi > y) != (yj > y);
            var denominator = yj - yi;
            if (crosses && Math.Abs(denominator) > 0.0001f
                && x < (polygon[j].X - polygon[i].X) * (y - yi) / denominator + polygon[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static double Distance(Point2f a, Point2f b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

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
