using System;
using System.Collections;
#if UNITY_IOS || UNITY_EDITOR
using HoloKit;
#endif
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.XR.ARFoundation;

/// <summary>GPU camera conversion, inference and postprocessing. No per-frame readback.</summary>
public class Yolo : MonoBehaviour
{
    public GameObject CenterEyePose;
    [SerializeField] ARCameraManager arCameraManager;
#if UNITY_IOS || UNITY_EDITOR
    [SerializeField] HoloKitCameraManager m_HoloKitCameraManager;
#endif
    [SerializeField] ModelAsset modelAsset;
    [SerializeField] RawImage rawImage1;
    [SerializeField] RawImage rawImage2;
    [SerializeField] Material yCbCrMaterial;
    [SerializeField] Material cropMaterial;
    [SerializeField] float confidenceThreshold = 0.6f;
    [SerializeField] float conf_lower = 0.35f;
    // Anything above this is drawn as a character. Measured on the 94-image grouped test split, a
    // true pose scores 0.96 median and 0.69 at the 5th percentile, so 0.5 keeps real poses while
    // rejecting the weak activations that hands and clutter produce.
    [SerializeField] float conf_human = 0.5f;
    [SerializeField] float projectionPlaneHeight = 5;
    [SerializeField] Vector2 projectionPlaneOffset = Vector2.zero;
    [Tooltip("Side of the recognised square relative to the camera frame's short side, about its centre.")]
    [SerializeField, Range(0.3f, 1)] float cropScale = 1;
    [SerializeField, Range(0, 1)] float iouThreshold = 0.6f;
    [SerializeField, Min(1)] int maxDetections = 128;
    // Hits and misses count inferences: at 5 per second a track shows after 0.4 s and survives a 0.6 s dropout.
    [Header("Temporal stabilisation")]
    [SerializeField] bool stabilizeDetections = true;
    [SerializeField, Min(1)] int trackMinHits = 2;
    [SerializeField, Min(0)] int trackMaxMisses = 3;
    [SerializeField, Range(0, 1)] float trackMatchIou = 0.3f;
    [SerializeField, Range(0, 1)] float trackSmoothing = 0.6f;
    [SerializeField, Range(0, 1)] float trackClassScore = 0.6f;
    [Tooltip("Seconds for a drawn character to ease toward its latest detection. 0 jumps at each inference.")]
    [SerializeField, Min(0)] float displaySmoothingTime = 0.2f;
    [Tooltip("Show one character at a time: the most confident person. Pronouncing reads that one.")]
    [SerializeField] bool showSingleCharacter = true;
    [Header("Performance")]
    [Tooltip("Live camera inferences per second; 0 runs on every camera frame. Video replay is never limited.")]
    [SerializeField, Min(0)] float maxInferenceRate = 5;
    [SerializeField] bool showDebugInfo;
    [SerializeField] string preferredWebcam;
    [SerializeField] VideoPlayer videoSource;

    Worker worker;
    Tensor<float> inputTensor;
    TextureTransform textureSettings;
    CommandBuffer commandBuffer;
    WebCamTexture webCamTexture;
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    MacCamera macCamera;
    string MacCameraName => macCamera?.DeviceName;
#else
    string MacCameraName => null;
#endif
    RenderTexture rgbIntermediate, cropIntermediate;
    Material runtimeYcbcr, runtimeCrop;
    GpuYoloPostprocessor postprocessor;
    GraphicsFence inferenceFence;
    bool hasFence, useARCamera, readbackPending, cleanupRequested;
    public bool HasFrame { get; private set; }
    public GpuYoloPostprocessor GpuResults => postprocessor;
    /// <summary>Plane height matching the camera frame's short side.</summary>
    public float ProjectionPlaneHeight => projectionPlaneHeight;
    /// <summary>Plane height of the recognised square, the centre <see cref="CropScale"/> of the frame;
    /// characters are placed on this square.</summary>
    public float RecognisedPlaneHeight => projectionPlaneHeight * cropScale;
    public Vector2 ProjectionPlaneOffset => projectionPlaneOffset;
    public float CropScale => cropScale;
    public Texture CameraPreview => cropIntermediate;
    /// <summary>Whole desktop camera frame; its centre square is exactly the model input.</summary>
    public Texture CameraFrame
    {
        get
        {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            if (macCamera != null) return macCamera.Texture;
#endif
            return webCamTexture != null && webCamTexture.width > 16 ? webCamTexture : null;
        }
    }
    /// <summary>True when <see cref="CameraFrame"/> rows run top to bottom and must be flipped to display.</summary>
    public bool CameraFrameFlipped =>
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        macCamera != null ||
#endif
        (webCamTexture != null && webCamTexture.videoVerticallyMirrored);
    public string CameraStatus { get; private set; } = "Starting camera…";
    public string ActiveWebcam => MacCameraName ?? (webCamTexture != null ? webCamTexture.deviceName : string.Empty);
    public bool IsVideoReplay => videoSource != null;
    public long VideoFrame => videoSource != null ? videoSource.frame : -1;
    public ulong ProcessedFrames { get; private set; }
    StabilizerSettings Stabilizer => new StabilizerSettings
    {
        enabled = stabilizeDetections,
        minHits = trackMinHits,
        maxMisses = trackMaxMisses,
        matchIou = trackMatchIou,
        smoothing = trackSmoothing,
        classScore = trackClassScore,
        displayTime = displaySmoothingTime,
        singleBest = showSingleCharacter,
    };
    public bool IsWaitingForGpu => hasFence && !inferenceFence.passed;
    long lastVideoFrame = -1;
    float nextInferenceTime;

    IEnumerator Start()
    {
#if !UNITY_IOS || UNITY_EDITOR
        if (!IsVideoReplay && !Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            CameraStatus = "Allow camera access to recognize your poses.";
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        }
        if (!IsVideoReplay && !Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            CameraStatus = "Camera access denied. Enable it in System Settings → Privacy & Security → Camera, then restart.";
            yield break;
        }
#endif
        Initialize();
        yield break;
    }

    void Initialize()
    {
        try
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing || !SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("Body Oracle requires GPU compute, instancing and asynchronous GPU readback.");
            if (modelAsset == null || cropMaterial == null)
                throw new InvalidOperationException("Assign the YOLO model and crop material.");
            worker = new Worker(BodyOracleModel.Load(modelAsset), BackendType.GPUCompute);
            inputTensor = new Tensor<float>(new TensorShape(1, 3, 640, 640));
            textureSettings = new TextureTransform().SetTensorLayout(TensorLayout.NCHW);
            commandBuffer = new CommandBuffer { name = "Body Oracle GPU preprocessing" };
            runtimeCrop = new Material(cropMaterial);
            postprocessor = new GpuYoloPostprocessor(maxDetections);
            if (IsVideoReplay)
            {
                videoSource.playOnAwake = false;
                videoSource.renderMode = VideoRenderMode.APIOnly;
                videoSource.audioOutputMode = VideoAudioOutputMode.None;
                videoSource.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
                videoSource.isLooping = true;
                videoSource.prepareCompleted += OnVideoPrepared;
                videoSource.errorReceived += OnVideoError;
                SelectVideo(videoSource.url);
                return;
            }
#if UNITY_IOS && !UNITY_EDITOR
            useARCamera = true;
            if (arCameraManager == null) arCameraManager = FindAnyObjectByType<ARCameraManager>();
            if (arCameraManager == null || yCbCrMaterial == null)
                throw new InvalidOperationException("AR camera and YCbCr material are required on iOS.");
            runtimeYcbcr = new Material(yCbCrMaterial);
            arCameraManager.frameReceived += OnCameraFrameReceived;
#else
            if (WebCamTexture.devices.Length == 0)
            {
                CameraStatus = "No webcam found. Connect a camera and restart.";
                Debug.LogWarning("No webcam found. Connect a camera to run Body Oracle.", this);
                return;
            }
            var devices = WebCamTexture.devices;
            int index = Array.FindIndex(devices, device => device.name == preferredWebcam);
            SelectWebcam(index < 0 ? 0 : index);
#endif
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
            CameraStatus = "Recognition could not start. See the application log.";
            enabled = false;
            CleanupResources();
        }
    }

    public void SelectWebcam(int index)
    {
        if (useARCamera || IsVideoReplay || worker == null) return;
        var devices = WebCamTexture.devices;
        if (index < 0 || index >= devices.Length) return;
        StopWebcam();
        HasFrame = false;
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        // -bodyOracleWebCamTexture keeps Unity's CPU-copying path for comparison.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-bodyOracleWebCamTexture") < 0)
        {
            macCamera = MacCamera.TryStart(devices[index].name, 1280, 720, 30, out string error);
            if (macCamera != null)
            {
                CameraStatus = "Opening " + devices[index].name + "…";
                Debug.Log("Body Oracle camera: zero-copy Metal capture from " + devices[index].name, this);
                return;
            }
            Debug.LogWarning("Zero-copy camera unavailable, using WebCamTexture: " + error, this);
        }
#endif
        webCamTexture = new WebCamTexture(devices[index].name, 1280, 720, 30);
        CameraStatus = "Opening " + devices[index].name + "…";
        webCamTexture.Play();
    }

    bool CanProcessFrame => isActiveAndEnabled && worker != null && DetectionData.isDetecting &&
        !readbackPending && (!hasFence || inferenceFence.passed);
    // The GPU would otherwise run the model on every camera frame and heat the phone.
    bool InferenceDue => IsVideoReplay || maxInferenceRate <= 0 || Time.unscaledTime >= nextInferenceTime;

    void Update()
    {
        if (IsVideoReplay)
        {
            // A real camera keeps producing frames while audio selection freezes
            // inference. Keep replay streaming too, avoiding AVPlayer pause/resume
            // interaction with the iOS audio session.
            if (CanProcessFrame && videoSource.isPrepared && videoSource.texture != null &&
                videoSource.frame >= 0 && videoSource.frame != lastVideoFrame)
            {
                lastVideoFrame = videoSource.frame;
                ProcessRgbFrame(videoSource.texture, 0, false);
                CameraStatus = "Video replay: " + System.IO.Path.GetFileName(videoSource.url);
            }
            return;
        }
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        if (!useARCamera && macCamera != null && worker != null && macCamera.TryUpdate())
        {
            CameraStatus = "Camera: " + macCamera.DeviceName;
            // CoreVideo rows run top to bottom; Unity samples bottom to top.
            ProcessRgbFrame(macCamera.Texture, 0, true, CanProcessFrame && InferenceDue);
            return;
        }
#endif
        if (!useARCamera && webCamTexture != null && webCamTexture.isPlaying &&
            webCamTexture.didUpdateThisFrame && worker != null)
            ProcessWebCamFrame(CanProcessFrame && InferenceDue);
    }

    void EnsureTextures(int width, int height, bool needsRgb)
    {
        if (cropIntermediate == null)
        {
            // Crop and resize in a single GPU blit, at the model's actual input size.
            cropIntermediate = new RenderTexture(640, 640, 0, RenderTextureFormat.ARGB32)
                { name = "Body Oracle model input", filterMode = FilterMode.Bilinear };
            cropIntermediate.Create();
        }
        if (needsRgb && (rgbIntermediate == null || rgbIntermediate.width != width || rgbIntermediate.height != height))
        {
            ReleaseTexture(ref rgbIntermediate);
            rgbIntermediate = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                { name = "Body Oracle camera RGB", filterMode = FilterMode.Bilinear };
            rgbIntermediate.Create();
        }
    }

    void ProcessWebCamFrame(bool infer)
    {
        if (webCamTexture.width <= 16 || webCamTexture.height <= 16) return;
        CameraStatus = "Camera: " + webCamTexture.deviceName;
        ProcessRgbFrame(webCamTexture, webCamTexture.videoRotationAngle, webCamTexture.videoVerticallyMirrored, infer);
    }

    // The desktop preview shows the model input, so it is refreshed on every camera frame, also
    // while recognition is paused for a character sound; only inference is skipped.
    void ProcessRgbFrame(Texture texture, float rotation, bool verticalMirror, bool infer = true)
    {
        EnsureTextures(texture.width, texture.height, false);
        runtimeCrop.SetFloat("_AspectRatio", (float)texture.width / texture.height);
        runtimeCrop.SetFloat("_Rotate", 0);
        runtimeCrop.SetFloat("_VideoRotation", rotation);
        runtimeCrop.SetFloat("_VerticalMirror", verticalMirror ? 1 : 0);
        runtimeCrop.SetFloat("_CropScale", cropScale);
        commandBuffer.Clear();
        commandBuffer.Blit(texture, cropIntermediate, runtimeCrop);
        if (infer) ScheduleInference();
        else Graphics.ExecuteCommandBuffer(commandBuffer);
    }

    public void SelectVideo(string url)
    {
        if (!IsVideoReplay) throw new InvalidOperationException("Assign a VideoPlayer before starting video replay.");
        videoSource.Stop();
        HasFrame = false;
        lastVideoFrame = -1;
        CameraStatus = "Preparing video replay…";
        videoSource.url = url;
        videoSource.Prepare();
    }

    void OnVideoPrepared(VideoPlayer source) => source.Play();
    void OnVideoError(VideoPlayer source, string message)
    {
        HasFrame = false;
        CameraStatus = "Video error: " + message;
        Debug.LogError(CameraStatus, this);
    }

    void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        if (!CanProcessFrame || !InferenceDue || args.textures.Count < 2) return;
        var y = args.textures[0];
        EnsureTextures(y.width, y.height, true);
        runtimeYcbcr.SetTexture("_textureY", y);
        runtimeYcbcr.SetTexture("_textureCbCr", args.textures[1]);
        runtimeCrop.SetFloat("_AspectRatio", (float)y.width / y.height);
        runtimeCrop.SetFloat("_CropScale", cropScale);
        // ARKit delivers the image in the sensor's landscape orientation. Turn it to the screen's
        // orientation so people stand upright for the model: Stereo is always LandscapeLeft, while
        // Mono also rotates to portrait and the other landscape.
        float rotation = 0;
#if UNITY_IOS || UNITY_EDITOR
        if (m_HoloKitCameraManager != null && m_HoloKitCameraManager.ScreenRenderMode == ScreenRenderMode.Mono)
            rotation = Screen.orientation switch
            {
                ScreenOrientation.Portrait => 90,
                ScreenOrientation.PortraitUpsideDown => 270,
                ScreenOrientation.LandscapeRight => 180,
                _ => 0,
            };
#endif
        runtimeCrop.SetFloat("_Rotate", 0);
        runtimeCrop.SetFloat("_VideoRotation", rotation);
        commandBuffer.Clear();
        commandBuffer.Blit(null, rgbIntermediate, runtimeYcbcr);
        commandBuffer.Blit(rgbIntermediate, cropIntermediate, runtimeCrop);
        ScheduleInference();
        CameraStatus = "ARKit camera: " + y.width + " × " + y.height;
    }

    void ScheduleInference()
    {
        TextureConverter.ToTensor(commandBuffer, cropIntermediate, inputTensor, textureSettings);
        Graphics.ExecuteCommandBuffer(commandBuffer);
        worker.Schedule(inputTensor);
        if (maxInferenceRate > 0)
        {
            // Keep the average rate after a pause or a late camera frame, without a catch-up burst.
            float interval = 1 / maxInferenceRate;
            nextInferenceTime = Mathf.Max(nextInferenceTime + interval, Time.unscaledTime + interval * 0.5f);
        }
        postprocessor.Process(worker.PeekOutput(0) as Tensor<float>, conf_human, conf_lower,
            confidenceThreshold, iouThreshold, RecognisedPlaneHeight, projectionPlaneOffset, Stabilizer);
        HasFrame = true;
        ProcessedFrames++;
        if (SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute)
        {
            inferenceFence = Graphics.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation,
                SynchronisationStageFlags.ComputeProcessing);
            hasFence = true;
        }
    }

    /// <summary>Read exactly one 48-byte record on user interaction; never blocks the main thread.
    /// Inference is held until the callback so the selected sprite and audio refer to the same frame.</summary>
    public bool RequestCenterDetection(Action<bool, DetectionData.PoseDetection> callback)
    {
        if (!isActiveAndEnabled || !HasFrame || postprocessor == null || readbackPending) return false;
        readbackPending = true;
        AsyncGPUReadback.Request(postprocessor.Center, request =>
        {
            readbackPending = false;
            try
            {
                if (cleanupRequested || this == null || !isActiveAndEnabled) return;
                if (request.hasError)
                {
                    Debug.LogWarning("Could not read the selected detection from the GPU.", this);
                    callback?.Invoke(false, default);
                    return;
                }
                var result = request.GetData<GpuPoseDetection>()[0];
                callback?.Invoke(result.info.y >= 0, result.ToDetection());
            }
            finally
            {
                if (cleanupRequested) DisposeGpuResources();
            }
        });
        return true;
    }

    static void ReleaseTexture(ref RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Destroy(texture);
        texture = null;
    }

    void CleanupResources()
    {
        cleanupRequested = true;
        HasFrame = false;
        if (videoSource != null)
        {
            videoSource.prepareCompleted -= OnVideoPrepared;
            videoSource.errorReceived -= OnVideoError;
            videoSource.Stop();
        }
        if (arCameraManager != null) arCameraManager.frameReceived -= OnCameraFrameReceived;
        StopWebcam();
        // A readback owns an outstanding use of the buffers. Release them in its callback.
        if (!readbackPending) DisposeGpuResources();
    }

    void StopWebcam()
    {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        macCamera?.Dispose();
        macCamera = null;
#endif
        if (webCamTexture == null) return;
        webCamTexture.Stop();
        Destroy(webCamTexture);
        webCamTexture = null;
    }

    void DisposeGpuResources()
    {
        worker?.Dispose(); worker = null;
        inputTensor?.Dispose(); inputTensor = null;
        commandBuffer?.Dispose(); commandBuffer = null;
        postprocessor?.Dispose(); postprocessor = null;
        ReleaseTexture(ref rgbIntermediate);
        ReleaseTexture(ref cropIntermediate);
        if (runtimeCrop != null) Destroy(runtimeCrop);
        if (runtimeYcbcr != null) Destroy(runtimeYcbcr);
        runtimeCrop = runtimeYcbcr = null;
    }

    // Recognition pauses while a character sound plays and while the device sits still, so stale
    // tracks must not survive into the next session.
    void OnEnable() => postprocessor?.ResetTracks();

    void OnDestroy() => CleanupResources();
}
