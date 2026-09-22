using System;
using System.Runtime.InteropServices;
using Unity.InferenceEngine;
using UnityEngine;

[StructLayout(LayoutKind.Sequential)]
public struct GpuPoseDetection
{
    public Vector4 box;
    public Vector4 info;
    public Vector4 extra;
    public const int Stride = 48;

    public DetectionData.PoseDetection ToDetection()
    {
        return new DetectionData.PoseDetection((int)info.y, info.x, new Rect(box.x, box.y, box.z, box.w))
        {
            projectionPosition = new Vector2(info.z, info.w),
            projectionHeight = extra.x
        };
    }
}

/// <summary>Temporal stabilisation applied to the suppressed detections.</summary>
public struct StabilizerSettings
{
    public bool enabled;
    public int minHits;        // frames a track must be seen before it is drawn
    public int maxMisses;      // frames a lost track keeps being drawn
    public float matchIou;     // IoU needed to associate a detection with a track
    public float smoothing;    // 0 snaps to the detection, approaching 1 freezes the box
    public float classScore;   // minimum score allowed to change a track's class

    public static StabilizerSettings Off => new StabilizerSettings { enabled = false };
}

/// <summary>Owns postprocessing buffers, never the worker-owned output tensor.</summary>
public sealed class GpuYoloPostprocessor : IDisposable
{
    readonly ComputeShader shader;
    readonly int decode, suppress, stabilize;
    ComputeBuffer candidates;
    int candidateCount;
    int frameIndex;
    public int Capacity { get; }
    public ComputeBuffer Detections { get; }
    public ComputeBuffer Center { get; }
    public ComputeBuffer Metadata { get; }
    ComputeBuffer Tracks { get; }

    public GpuYoloPostprocessor(int capacity)
    {
        if (!SystemInfo.supportsComputeShaders) throw new NotSupportedException("Body Oracle requires compute shaders.");
        var asset = Resources.Load<ComputeShader>("YoloPostprocess");
        if (asset == null) throw new InvalidOperationException("Missing YoloPostprocess.compute resource.");
        shader = UnityEngine.Object.Instantiate(asset);
        decode = shader.FindKernel("Decode");
        suppress = shader.FindKernel("Suppress");
        stabilize = shader.FindKernel("Stabilize");
        Capacity = Mathf.Max(1, capacity);
        Detections = new ComputeBuffer(Capacity, GpuPoseDetection.Stride);
        Center = new ComputeBuffer(1, GpuPoseDetection.Stride);
        Metadata = new ComputeBuffer(3, sizeof(uint));
        Metadata.SetData(new uint[3]);
        Center.SetData(new[] { new GpuPoseDetection { info = new Vector4(0, -1, 0, 0) } });
        Tracks = new ComputeBuffer(Capacity, GpuPoseDetection.Stride);
        shader.SetBuffer(suppress, "_Detections", Detections);
        shader.SetBuffer(suppress, "_Center", Center);
        shader.SetBuffer(suppress, "_Metadata", Metadata);
        shader.SetBuffer(stabilize, "_Detections", Detections);
        shader.SetBuffer(stabilize, "_Center", Center);
        shader.SetBuffer(stabilize, "_Metadata", Metadata);
        shader.SetBuffer(stabilize, "_Tracks", Tracks);
        shader.SetInt("_MaxDetections", Capacity);
        ResetTracks();
    }

    public void Process(Tensor<float> output, float humanThreshold, float lowThreshold,
        float confidenceThreshold, float iouThreshold, float planeHeight, Vector2 planeOffset)
        => Process(output, humanThreshold, lowThreshold, confidenceThreshold, iouThreshold,
            planeHeight, planeOffset, StabilizerSettings.Off);

    public void Process(Tensor<float> output, float humanThreshold, float lowThreshold,
        float confidenceThreshold, float iouThreshold, float planeHeight, Vector2 planeOffset,
        StabilizerSettings stabilizer)
    {
        if (output == null || output.shape.rank != 3 || output.shape[0] != 1 || output.shape[1] < 24)
            throw new ArgumentException("Expected YOLO output [1, 4 + 20 classes (+ optional keypoints), candidates].");
        // GPUCompute already owns this buffer. Pin does not download or transpose it.
        Process(ComputeTensorData.Pin(output).buffer, output.shape[2], humanThreshold, lowThreshold,
            confidenceThreshold, iouThreshold, planeHeight, planeOffset, stabilizer);
    }

    public void Process(ComputeBuffer output, int count, float humanThreshold, float lowThreshold,
        float confidenceThreshold, float iouThreshold, float planeHeight, Vector2 planeOffset)
        => Process(output, count, humanThreshold, lowThreshold, confidenceThreshold, iouThreshold,
            planeHeight, planeOffset, StabilizerSettings.Off);

    public void Process(ComputeBuffer output, int count, float humanThreshold, float lowThreshold,
        float confidenceThreshold, float iouThreshold, float planeHeight, Vector2 planeOffset,
        StabilizerSettings stabilizer)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (candidateCount != count)
        {
            candidates?.Dispose();
            candidates = new ComputeBuffer(count, GpuPoseDetection.Stride);
            candidateCount = count;
            shader.SetBuffer(decode, "_Candidates", candidates);
            shader.SetBuffer(suppress, "_Candidates", candidates);
        }
        shader.SetInt("_CandidateCount", count);
        shader.SetFloat("_HumanThreshold", humanThreshold);
        shader.SetFloat("_LowThreshold", lowThreshold);
        shader.SetFloat("_ConfidenceThreshold", confidenceThreshold);
        shader.SetFloat("_IouThreshold", iouThreshold);
        shader.SetFloat("_PlaneHeight", planeHeight);
        shader.SetVector("_PlaneOffset", planeOffset);
        shader.SetBuffer(decode, "_Output", output);
        shader.Dispatch(decode, (count + 255) / 256, 1, 1);
        shader.Dispatch(suppress, 1, 1, 1);
        if (!stabilizer.enabled) return;
        // The frame stamp separates tracks matched this frame from the ones that must age.
        frameIndex++;
        shader.SetInt("_FrameIndex", frameIndex);
        shader.SetInt("_TrackMinHits", Mathf.Max(1, stabilizer.minHits));
        shader.SetInt("_TrackMaxMisses", Mathf.Max(0, stabilizer.maxMisses));
        shader.SetFloat("_TrackMatchIou", stabilizer.matchIou);
        shader.SetFloat("_TrackSmoothing", Mathf.Clamp01(stabilizer.smoothing));
        shader.SetFloat("_TrackClassScore", stabilizer.classScore);
        shader.Dispatch(stabilize, 1, 1, 1);
    }

    /// <summary>Drops every track, so recognition restarts clean after a pause or a camera change.</summary>
    public void ResetTracks()
    {
        var cleared = new GpuPoseDetection[Capacity];
        for (int i = 0; i < cleared.Length; i++) cleared[i].info = new Vector4(-1, -1, 0, 0);
        Tracks.SetData(cleared);
        frameIndex = 0;
    }

    public void Dispose()
    {
        candidates?.Dispose();
        Detections.Dispose();
        Center.Dispose();
        Metadata.Dispose();
        Tracks.Dispose();
        if (Application.isPlaying) UnityEngine.Object.Destroy(shader);
        else UnityEngine.Object.DestroyImmediate(shader);
    }
}
