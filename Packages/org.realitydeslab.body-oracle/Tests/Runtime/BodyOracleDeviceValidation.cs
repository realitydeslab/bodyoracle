#if UNITY_EDITOR || BODYORACLE_DEVICE_TEST
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

/// <summary>Opt-in diagnostics for the original iOS camera scene in a development build.</summary>
public sealed class BodyOracleDeviceValidation : MonoBehaviour
{
    [Serializable] public class Report
    {
        public string unity, device, gpu, sessionState, notTrackingReason, cameraStatus, runId;
        public string scope = "Real ARKit camera capture, GPU preprocessing and inference smoke test. Detection counts are not labelled pose accuracy or optical alignment validation.";
        public bool cameraPermissionGranted, complete, passed;
        public ulong processedFrames;
        public int samples, samplesWithDetections;
        public float elapsedSeconds;
        // Largest angle between the AR camera pose that LateUpdate code (the sprite constraint and draw)
        // sees and the pose the camera renders with. Near 0 when overlays and the camera agree.
        public float poseMismatchDegrees;
        // Angle between the tracked pose (Update, before the SDK's iOS 27 correction runs at execution
        // order 10000) and the corrected pose seen in LateUpdate: how far the correction turns the rig.
        public float poseCorrectionDegrees;
        public string renderMode;
        public List<string> errors = new List<string>();
    }
    Report report;
    Yolo detector;
    float startTime, nextSave;
    ulong lastSample;
    bool pending, captured;
    Transform arCamera;
    Quaternion lateUpdatePose;
    bool hasLateUpdatePose;
    string DirectoryPath => Application.persistentDataPath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void StartIfRequested()
    {
        OverridePlaneDistance();
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-bodyOracleCameraTest") >= 0 ||
            Environment.GetEnvironmentVariable("BODYORACLE_CAMERA_TEST") == "1")
            new GameObject("Device camera validation").AddComponent<BodyOracleDeviceValidation>();
    }

    // BODYORACLE_PLANE_DISTANCE moves the characters' plane (and the guide on it) to that many metres,
    // scaling its height so the angular size stays the same: a stereo disparity test without rebuilds.
    // Runs after Awake and before any Start, so the guide is created at the new distance.
    static void OverridePlaneDistance()
    {
        if (!float.TryParse(Environment.GetEnvironmentVariable("BODYORACLE_PLANE_DISTANCE"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float distance) || distance <= 0)
            return;
        var visualizer = FindAnyObjectByType<SpriteVisualizer>();
        var yolo = FindAnyObjectByType<Yolo>();
        const System.Reflection.BindingFlags field = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var distanceField = typeof(SpriteVisualizer).GetField("projectionPlaneDistance", field);
        var heightField = typeof(Yolo).GetField("projectionPlaneHeight", field);
        if (visualizer == null || yolo == null || distanceField == null || heightField == null) return;
        float previous = (float)distanceField.GetValue(visualizer);
        distanceField.SetValue(visualizer, distance);
        heightField.SetValue(yolo, (float)heightField.GetValue(yolo) * distance / previous);
        Debug.Log($"BODYORACLE_PLANE_DISTANCE {previous} -> {distance} m");
    }

    void Start()
    {
        report = new Report { unity = Application.unityVersion, device = SystemInfo.deviceModel, gpu = SystemInfo.graphicsDeviceName };
        var args = Environment.GetCommandLineArgs();
        int runIndex = Array.IndexOf(args, "-bodyOracleRunId");
        report.runId = Environment.GetEnvironmentVariable("BODYORACLE_RUN_ID") ??
            (runIndex >= 0 && runIndex + 1 < args.Length ? args[runIndex + 1] : Guid.NewGuid().ToString());
        startTime = Time.realtimeSinceStartup;
        detector = FindAnyObjectByType<Yolo>();
        // Keep a stationary test phone from entering the application's sleep mode.
        FindAnyObjectByType<SpriteVisualizer>()?.SetMovementMonitoringEnabled(false);
#if UNITY_IOS
        // Stereo cannot be tapped from devicectl; -bodyOracleStereo or BODYORACLE_STEREO=1 starts in HoloKit mode.
        if ((Array.IndexOf(args, "-bodyOracleStereo") >= 0 || Environment.GetEnvironmentVariable("BODYORACLE_STEREO") == "1") &&
            FindAnyObjectByType<HoloKit.HoloKitCameraManager>() is { } holoKit)
            holoKit.ScreenRenderMode = HoloKit.ScreenRenderMode.Stereo;
#endif
        arCamera = FindAnyObjectByType<ARCameraManager>()?.transform;
        Application.onBeforeRender += ComparePose;
        Application.logMessageReceived += OnLog;
        Save();
    }

    Quaternion rawPose;
    bool hasRawPose;

    void Update()
    {
        if (arCamera != null) { rawPose = arCamera.rotation; hasRawPose = true; }
        if (report.complete) return;
        report.elapsedSeconds = Time.realtimeSinceStartup - startTime;
        report.sessionState = ARSession.state.ToString();
        report.notTrackingReason = ARSession.notTrackingReason.ToString();
        report.cameraPermissionGranted = Application.HasUserAuthorization(UserAuthorization.WebCam);
        report.cameraStatus = detector != null ? detector.CameraStatus : "Missing detector";
        report.processedFrames = detector != null ? detector.ProcessedFrames : 0;
        if (detector != null && detector.HasFrame && report.processedFrames >= lastSample + 10 && !pending)
        {
            lastSample = report.processedFrames;
            pending = true;
            AsyncGPUReadback.Request(detector.GpuResults.Metadata, request => {
                pending = false;
                if (request.hasError) { report.errors.Add("GPU metadata readback failed"); return; }
                report.samples++;
                if (request.GetData<uint>()[0] > 0) report.samplesWithDetections++;
            });
        }
        if (!captured && report.processedFrames >= 60 && report.cameraPermissionGranted && ARSession.state == ARSessionState.SessionTracking)
        {
            captured = true;
            ScreenCapture.CaptureScreenshot("camera-screen.png");
            // Test-only capture of the actual GPU preprocessing output.
            var previous = RenderTexture.active;
            RenderTexture.active = (RenderTexture)detector.CameraPreview;
            var image = new Texture2D(640, 640, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 640, 640), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(DirectoryPath, "camera-model-input.png"), image.EncodeToPNG());
            Destroy(image);
            RenderTexture.active = previous;
        }
        if (!pending && ((report.elapsedSeconds >= 30 && report.processedFrames >= 60 && ARSession.state == ARSessionState.SessionTracking) || report.elapsedSeconds >= 90))
        {
            report.complete = true;
            report.passed = report.errors.Count == 0 && report.processedFrames >= 60 && report.samples > 0 && ARSession.state == ARSessionState.SessionTracking;
            Debug.Log("BODYORACLE_DEVICE_CAMERA_" + (report.passed ? "PASS" : "FAIL"));
            Save();
        }
        if (Time.realtimeSinceStartup >= nextSave) { nextSave = Time.realtimeSinceStartup + 1; Save(); }
    }

    void Save() => File.WriteAllText(Path.Combine(DirectoryPath, "device-camera-report.json"), JsonUtility.ToJson(report, true));
    void OnLog(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) report.errors.Add(message);
    }
    void LateUpdate()
    {
        if (arCamera == null) return;
        lateUpdatePose = arCamera.rotation;
        hasLateUpdatePose = true;
        if (hasRawPose && report != null && !report.complete && report.processedFrames >= 10)
            report.poseCorrectionDegrees = Quaternion.Angle(rawPose, lateUpdatePose);
#if UNITY_IOS
        if (report != null && FindAnyObjectByType<HoloKit.HoloKitCameraManager>() is { } rig) report.renderMode = rig.ScreenRenderMode.ToString();
#endif
    }

    [BeforeRenderOrder(1000)] // after every pose writer
    void ComparePose()
    {
        if (!hasLateUpdatePose || report == null || report.complete || report.processedFrames < 10) return;
        report.poseMismatchDegrees = Mathf.Max(report.poseMismatchDegrees, Quaternion.Angle(lateUpdatePose, arCamera.rotation));
    }

    void OnDestroy()
    {
        Application.onBeforeRender -= ComparePose;
        Application.logMessageReceived -= OnLog;
    }
}
#endif
