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
        public List<string> errors = new List<string>();
    }
    Report report;
    Yolo detector;
    float startTime, nextSave;
    ulong lastSample;
    bool pending, captured;
    string DirectoryPath => Application.persistentDataPath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void StartIfRequested()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-bodyOracleCameraTest") >= 0 ||
            Environment.GetEnvironmentVariable("BODYORACLE_CAMERA_TEST") == "1")
            new GameObject("Device camera validation").AddComponent<BodyOracleDeviceValidation>();
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
        Application.logMessageReceived += OnLog;
        Save();
    }

    void Update()
    {
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
    void OnDestroy() => Application.logMessageReceived -= OnLog;
}
#endif
