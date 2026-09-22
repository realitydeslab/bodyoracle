#if UNITY_EDITOR || BODYORACLE_SIMULATOR_TEST
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>Simulator-only video camera harness. Never compiled into normal players.</summary>
public sealed class BodyOracleSimulatorReplay : MonoBehaviour
{
    public Yolo detector;
    public SpriteVisualizer presentation;
    public VideoPlayer video;
    public Camera displayCamera;
    public Renderer preview;
    public Text status;
    public Button soundButton;
    public Button nextButton;
    [Serializable] public class ClipResult
    {
        public string source;
        public int processedFrames, samples, samplesWithDetections, samplesWithCenterDetection;
        public bool audioStarted, inferencePausedDuringAudio, inferenceResumedAfterAudio;
        public int selectedClass = -1;
        public long videoFrame;
        public double videoTime;
        public bool videoPlaying, recognitionEnabled, waitingForGpu, audioPlaying;
    }
    [Serializable] public class Report
    {
        public string unity, platform, gpu, graphicsApi, runId;
        public bool compute, indirectDrawing, instancing, asyncReadback, autoTest, complete, passed;
        public string spriteDrawMode;
        public string scope = "VideoPlayer RGB frames replace camera input; real iOS UI and GPU pipeline. ARKit/YCbCr capture, tracking and optical HoloKit alignment are not simulated.";
        public List<ClipResult> clips = new List<ClipResult>();
        public List<string> errors = new List<string>();
    }
    readonly string[] clips = { "pose_1.mp4", "pose_squats.mp4" };
    Report report;
    Material previewMaterial;
    int clipIndex;
    ulong frameStart, lastSample, frozenFrame;
    bool autoTest, samplePending, soundRequested, wasPlaying, captured;
    float audioStart, nextReport, startTime;
    string externalUrl;
    ClipResult Current => report.clips[clipIndex];
    string ReportPath => Path.Combine(Application.persistentDataPath, "simulator-replay-report.json");

    void Awake()
    {
        Application.targetFrameRate = 30;
        Screen.orientation = ScreenOrientation.Portrait;
        DetectionData.isDetecting = true;
        var args = Environment.GetCommandLineArgs();
        autoTest = Array.IndexOf(args, "-bodyOracleAutoTest") >= 0 || Environment.GetEnvironmentVariable("BODYORACLE_AUTOTEST") == "1";
        int urlIndex = Array.IndexOf(args, "-poseVideoUrl");
        if (urlIndex >= 0 && urlIndex + 1 < args.Length) externalUrl = args[urlIndex + 1];
        externalUrl = Environment.GetEnvironmentVariable("BODYORACLE_VIDEO_URL") ?? externalUrl;
        report = new Report { unity = Application.unityVersion, platform = Application.platform.ToString(),
            gpu = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            compute = SystemInfo.supportsComputeShaders, indirectDrawing = SystemInfo.supportsIndirectArgumentsBuffer,
            instancing = SystemInfo.supportsInstancing,
            spriteDrawMode = SystemInfo.supportsIndirectArgumentsBuffer ? "GPU indirect" : "GPU procedural, fixed capacity",
            asyncReadback = SystemInfo.supportsAsyncGPUReadback, autoTest = autoTest };
        int runIndex = Array.IndexOf(args, "-bodyOracleRunId");
        report.runId = Environment.GetEnvironmentVariable("BODYORACLE_RUN_ID") ??
            (runIndex >= 0 && runIndex + 1 < args.Length ? args[runIndex + 1] : Guid.NewGuid().ToString());
        report.clips.Add(new ClipResult { source = Source(0) });
        video.url = Current.source;
        startTime = Time.realtimeSinceStartup;
        Application.logMessageReceived += OnLog;
        Debug.Log("BODYORACLE_SIMULATOR_START " + JsonUtility.ToJson(report));
    }

    void Start()
    {
        previewMaterial = preview.material;
        presentation.SetMovementMonitoringEnabled(false);
        nextButton.onClick.AddListener(NextClip);
        Save();
    }

    string Source(int index) => externalUrl ?? Path.Combine(Application.streamingAssetsPath, "BodyOracleReplay", clips[index]);

    void Update()
    {
        displayCamera.orthographicSize = detector.ProjectionPlaneHeight * .5f / Mathf.Min(1, displayCamera.aspect);
        if (detector.CameraPreview != null) previewMaterial.SetTexture("_BaseMap", detector.CameraPreview);
        Current.processedFrames = (int)(detector.ProcessedFrames - frameStart);
        Current.videoFrame = video.frame;
        Current.videoTime = video.time;
        Current.videoPlaying = video.isPlaying;
        Current.recognitionEnabled = DetectionData.isDetecting;
        Current.waitingForGpu = detector.IsWaitingForGpu;
        Current.audioPlaying = presentation.IsPlayingAudio;
        status.text = (report.complete ? (report.passed ? "REPLAY TEST PASSED\n" : "REPLAY TEST FAILED\n") : "SIMULATED CAMERA · VIDEO REPLAY\n") +
            Path.GetFileName(Current.source) + " · " + Current.processedFrames + " processed frames\n" + detector.CameraStatus;
        nextButton.interactable = !presentation.IsPlayingAudio && !samplePending;
        if (!captured && Current.processedFrames >= 15)
        {
            captured = true;
            ScreenCapture.CaptureScreenshot("replay-" + clipIndex + ".png"); // iOS prepends persistentDataPath.
        }
        if (detector.HasFrame && detector.ProcessedFrames >= lastSample + 10 && !samplePending)
        {
            lastSample = detector.ProcessedFrames;
            samplePending = true;
            var result = Current;
            // Test instrumentation only: normal players retain their no-per-frame-readback path.
            AsyncGPUReadback.Request(detector.GpuResults.Metadata, request => {
                samplePending = false;
                if (request.hasError) { report.errors.Add("GPU metadata readback failed"); return; }
                var data = request.GetData<uint>();
                result.samples++;
                if (data[0] > 0) result.samplesWithDetections++;
                if (data[1] > 0) result.samplesWithCenterDetection++;
                if (autoTest && clipIndex == 0 && !soundRequested && data[1] > 0 && Current.processedFrames >= 30)
                {
                    soundRequested = true;
                    soundButton.onClick.Invoke(); // Exercise the actual iOS UI listener.
                }
            });
        }
        if (presentation.IsPlayingAudio && !wasPlaying)
        {
            Current.audioStarted = true;
            Current.selectedClass = presentation.SelectedClass;
            frozenFrame = detector.ProcessedFrames;
            audioStart = Time.realtimeSinceStartup;
            ScreenCapture.CaptureScreenshot("audio-selection.png");
        }
        if (presentation.IsPlayingAudio && Time.realtimeSinceStartup - audioStart > .5f)
            Current.inferencePausedDuringAudio = detector.ProcessedFrames == frozenFrame;
        if (Current.audioStarted && !presentation.IsPlayingAudio && detector.ProcessedFrames > frozenFrame)
            Current.inferenceResumedAfterAudio = true;
        wasPlaying = presentation.IsPlayingAudio;
        if (autoTest && !report.complete && Current.processedFrames >= 60 && !samplePending && !presentation.IsPlayingAudio &&
            (clipIndex > 0 || Current.inferenceResumedAfterAudio))
        {
            if (clipIndex == 0 && externalUrl == null) NextClip();
            else Complete();
        }
        if (autoTest && !report.complete && Time.realtimeSinceStartup - startTime > 180)
        {
            report.errors.Add("Replay validation timed out after 180 seconds");
            Complete();
        }
        if (Time.realtimeSinceStartup >= nextReport) { nextReport = Time.realtimeSinceStartup + 1; Save(); }
    }

    void NextClip()
    {
        if (presentation.IsPlayingAudio || samplePending || externalUrl != null) return;
        if (clipIndex == clips.Length - 1) return;
        clipIndex++;
        report.clips.Add(new ClipResult { source = Source(clipIndex) });
        frameStart = detector.ProcessedFrames;
        lastSample = frameStart;
        captured = false;
        detector.SelectVideo(Current.source);
    }

    void Complete()
    {
        report.complete = true;
        report.passed = report.errors.Count == 0 && report.compute && report.instancing && report.asyncReadback &&
            report.clips.TrueForAll(c => c.processedFrames >= 60 && c.samplesWithDetections > 0) &&
            report.clips[0].audioStarted && report.clips[0].inferencePausedDuringAudio && report.clips[0].inferenceResumedAfterAudio;
        Save();
        Debug.Log("BODYORACLE_SIMULATOR_" + (report.passed ? "PASS " : "FAIL ") + JsonUtility.ToJson(report));
    }
    void Save() => File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
    void OnLog(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) report.errors.Add(message);
    }
    void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        if (nextButton != null) nextButton.onClick.RemoveListener(NextClip);
        if (previewMaterial != null) Destroy(previewMaterial);
    }
}
#endif
