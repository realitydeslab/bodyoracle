using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Run with unity run . -- -executeMethod BodyOracleGpuValidation.Run.
/// Readbacks here are intentional test assertions, never part of the runtime pipeline.</summary>
public static class BodyOracleGpuValidation
{
    static readonly List<string> passed = new List<string>();
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static float[] Fixture(int n) => new float[24 * n];
    static void Box(float[] data, int index, int cls, float score, float x, float y, float w, float h)
    {
        int n = data.Length / 24;
        data[index] = x; data[n + index] = 640 - y;
        data[2 * n + index] = w; data[3 * n + index] = h;
        data[(4 + cls) * n + index] = score;
    }

    internal static List<GpuPoseDetection> Reference(float[] data, int capacity) => Reference(data, capacity, new Vector2(1, -2));

    internal static List<GpuPoseDetection> Reference(float[] data, int capacity, Vector2 offset)
    {
        int n = data.Length / 24;
        var decoded = new List<GpuPoseDetection>();
        var lowClasses = new HashSet<int> { 0, 1, 4, 7, 14, 16, 17 };
        for (int i = 0; i < n; i++)
        {
            int cls = 0;
            float score = -1;
            for (int c = 0; c < 20; c++)
            {
                float p = data[(4 + c) * n + i];
                if (!float.IsNaN(p) && !float.IsInfinity(p) && p > score) { score = p; cls = c; }
            }
            float x = data[i], y = 640 - data[n + i], w = data[2 * n + i], h = data[3 * n + i];
            if (score < 0.1f || w <= 0 || h <= 0 || !float.IsFinite(x + y + w + h)) continue;
            if (score < (lowClasses.Contains(cls) ? 0.35f : 0.6f)) cls = 18;
            decoded.Add(new GpuPoseDetection {
                box = new Vector4(x - w / 2, y - h / 2, w, h),
                info = new Vector4(score, cls, (x / 640 * 2 - 1) * 5 + offset.x, (y / 640 * 2 - 1) * 5 + offset.y),
                extra = new Vector4(h / 640 * 10, i, 0, 0) });
        }
        decoded = decoded.OrderByDescending(d => d.info.x).ThenBy(d => d.extra.y).ToList();
        var kept = new List<GpuPoseDetection>();
        foreach (var d in decoded)
        {
            bool suppress = kept.Any(k => {
                float intersection = Mathf.Max(0, Mathf.Min(k.box.x + k.box.z, d.box.x + d.box.z) - Mathf.Max(k.box.x, d.box.x)) *
                    Mathf.Max(0, Mathf.Min(k.box.y + k.box.w, d.box.y + d.box.w) - Mathf.Max(k.box.y, d.box.y));
                return intersection / (k.box.z * k.box.w + d.box.z * d.box.w - intersection) > 0.6f;
            });
            if (!suppress) kept.Add(d);
            if (kept.Count == capacity) break;
        }
        return kept;
    }

    static void Compare(string name, float[] data, int capacity = 128)
    {
        using var input = new ComputeBuffer(data.Length, sizeof(float));
        input.SetData(data);
        using var post = new GpuYoloPostprocessor(capacity);
        post.Process(input, data.Length / 24, 0.1f, 0.35f, 0.6f, 0.6f, 10, new Vector2(1, -2));
        var metadata = new uint[3]; post.Metadata.GetData(metadata);
        var results = new GpuPoseDetection[capacity]; post.Detections.GetData(results);
        var center = new GpuPoseDetection[1]; post.Center.GetData(center);
        var expected = Reference(data, capacity);
        Check(metadata[0] == expected.Count, name + ": result count mismatch " + metadata[0] + " vs " + expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Check(Vector4.Distance(results[i].box, expected[i].box) < 0.001f, name + ": box mismatch");
            Check(Vector4.Distance(results[i].info, expected[i].info) < 0.001f, name + ": score/class/projection mismatch");
            Check(Vector4.Distance(results[i].extra, expected[i].extra) < 0.001f, name + ": order/height mismatch");
        }
        int centerIndex = -1;
        float distance = float.MaxValue;
        for (int i = 0; i < expected.Count; i++)
        {
            var d = expected[i].ToDetection();
            if (d.boundingBox.Contains(new Vector2(320, 320)))
            {
                float next = (d.boundingBox.center - new Vector2(320, 320)).sqrMagnitude;
                if (next < distance) { centerIndex = i; distance = next; }
            }
        }
        Check(metadata[1] == centerIndex + 1, name + ": center selection mismatch");
        Check(centerIndex < 0 ? center[0].info.y == -1 : center[0].extra.y == expected[centerIndex].extra.y,
            name + ": center record mismatch");
        // Reuse every buffer and verify that an empty frame clears all previous state.
        input.SetData(new float[data.Length]);
        post.Process(input, data.Length / 24, 0.1f, 0.35f, 0.6f, 0.6f, 10, Vector2.zero);
        post.Metadata.GetData(metadata); post.Center.GetData(center);
        Check(metadata[0] == 0 && metadata[1] == 0 && metadata[2] == 0 && center[0].info.y == -1, name + ": stale state after empty frame");
        passed.Add(name);
    }

    // Temporal stabilisation: a track has to be seen repeatedly before it is drawn, survives a short
    // dropout, and keeps its class when a detection's score falls under the class-change threshold.
    static void ValidateStabilizer()
    {
        var settings = new StabilizerSettings
        {
            enabled = true, minHits = 3, maxMisses = 2,
            matchIou = 0.3f, smoothing = 0f, classScore = 0.6f,
        };
        using var post = new GpuYoloPostprocessor(8);
        var metadata = new uint[3];
        var results = new GpuPoseDetection[8];

        uint Feed(float[] data)
        {
            using var input = new ComputeBuffer(data.Length, sizeof(float));
            input.SetData(data);
            post.Process(input, data.Length / 24, 0.1f, 0.35f, 0.6f, 0.6f, 10, new Vector2(1, -2), settings);
            post.Metadata.GetData(metadata);
            post.Detections.GetData(results);
            return metadata[0];
        }

        var person = Fixture(1); Box(person, 0, 2, 0.9f, 320, 320, 100, 100);
        Check(Feed(person) == 0, "stabilizer: drew a track on its first frame");
        Check(Feed(person) == 0, "stabilizer: drew a track before minHits");
        Check(Feed(person) == 1, "stabilizer: track never became visible at minHits");
        Check(results[0].info.y == 2, "stabilizer: wrong class on a confirmed track");

        // A low-scoring frame of a different class must not swap the drawn character.
        var flicker = Fixture(1); Box(flicker, 0, 18, 0.4f, 320, 320, 100, 100);
        Check(Feed(flicker) == 1, "stabilizer: lost the track on a low-scoring frame");
        Check(results[0].info.y == 2, "stabilizer: class flipped below the class-change threshold");

        // A confident detection of another class is allowed to change it.
        var changed = Fixture(1); Box(changed, 0, 5, 0.95f, 320, 320, 100, 100);
        Check(Feed(changed) == 1 && results[0].info.y == 5, "stabilizer: confident class change rejected");

        // Losing the subject coasts for maxMisses frames, then the track retires.
        var empty = Fixture(1);
        Check(Feed(empty) == 1, "stabilizer: dropped the track on the first miss");
        Check(Feed(empty) == 1, "stabilizer: dropped the track inside the miss budget");
        Check(Feed(empty) == 0, "stabilizer: kept the track past maxMisses");
        Check(metadata[1] == 0, "stabilizer: stale center after the track retired");

        // A transient blob that never reaches minHits is never drawn.
        var blip = Fixture(1); Box(blip, 0, 7, 0.9f, 100, 100, 60, 60);
        Check(Feed(blip) == 0 && Feed(empty) == 0 && Feed(empty) == 0 && Feed(empty) == 0,
            "stabilizer: a transient detection reached the screen");

        // Clearing tracks restarts the confirmation delay.
        post.ResetTracks();
        Check(Feed(person) == 0, "stabilizer: ResetTracks left a confirmed track behind");
        passed.Add("temporal stabilisation hysteresis, class stickiness and reset");
    }

    public static void Run()
    {
        PrepareInferenceLifecycle();
        passed.Clear();
        try
        {
            Check(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal, "This validation expects a real Metal GPU; do not pass -nographics.");
            Compare("empty", Fixture(1));
            var single = Fixture(1); Box(single, 0, 2, 0.9f, 320, 320, 100, 100);
            Compare("single and center", single);
            var chain = Fixture(5);
            Box(chain, 0, 2, 0.9f, 300, 320, 100, 100);
            Box(chain, 1, 3, 0.8f, 320, 320, 100, 100);
            Box(chain, 2, 4, 0.7f, 340, 320, 100, 100);
            Box(chain, 3, 0, 0.35f, 100, 100, 40, 40);
            Box(chain, 4, 2, 0.1f, 500, 500, 40, 40);
            Compare("greedy NMS chain and threshold boundaries", chain);
            var touching = Fixture(3);
            Box(touching, 0, 1, 0.9f, 100, 100, 100, 100);
            Box(touching, 1, 2, 0.9f, 200, 100, 100, 100);
            Box(touching, 2, 3, 0.9f, 100, 100, 100, 100);
            Compare("touching boxes and deterministic ties", touching);
            var invalid = Fixture(4);
            Box(invalid, 0, 2, float.NaN, 320, 320, 100, 100);
            Box(invalid, 1, 2, 0.9f, float.NaN, 320, 100, 100);
            Box(invalid, 2, 2, 0.9f, 320, 320, 0, 100);
            Box(invalid, 3, 2, 0.9f, 320, 320, 100, -1);
            Compare("invalid predictions", invalid);
            var random = new System.Random(42);
            var stress = Fixture(8400);
            for (int i = 0; i < 8400; i++)
                Box(stress, i, random.Next(20), (float)random.NextDouble(), random.Next(640), random.Next(640), random.Next(20, 300), random.Next(20, 300));
            Compare("8400 candidates across thread groups", stress);
            Compare("bounded output", touching, 1);
            ValidateStabilizer();
            ValidateRendering(single, false);
            ValidateRendering(single, true);
            ValidateModel();
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/gpu-validation.txt", "Unity " + Application.unityVersion + "\nGPU " + SystemInfo.graphicsDeviceName + "\nPASS\n" + string.Join("\n", passed));
            Debug.Log("BODYORACLE_GPU_VALIDATION_PASS: " + string.Join("; ", passed));
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            throw;
        }
    }

    static void ValidateRendering(float[] fixture, bool procedural)
    {
        using var input = new ComputeBuffer(fixture.Length, 4); input.SetData(fixture);
        using var post = new GpuYoloPostprocessor(128);
        post.Process(input, 1, .1f, .35f, .6f, .6f, 10, Vector2.zero);
        // Simulate stale, valid-looking data beyond the current GPU detection count.
        // Fixed-capacity drawing must reject this slot without a CPU count readback.
        var detections = new GpuPoseDetection[post.Capacity];
        post.Detections.GetData(detections);
        detections[post.Capacity - 1] = detections[0];
        detections[post.Capacity - 1].info.z = -1.8f;
        detections[post.Capacity - 1].info.w = -1.8f;
        detections[post.Capacity - 1].extra.x = 1;
        post.Detections.SetData(detections);
        var shader = Resources.Load<Shader>("BodyOracleSprites");
        Check(shader != null && shader.isSupported, "GPU sprite shader unsupported");
        // Four different corners catch vertical/horizontal flips that a white quad cannot.
        var orientation = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        orientation.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.yellow });
        orientation.Apply();
        var material = new Material(shader) { enableInstancing = true, mainTexture = orientation };
        var mesh = new Mesh {
            vertices = new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) },
            uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
            triangles = new[] { 0,1,2,0,2,3 } };
        using var args = new ComputeBuffer(100, 4, ComputeBufferType.IndirectArguments);
        var argData = new uint[100]; argData[0] = 6; args.SetData(argData);
        var compute = UnityEngine.Object.Instantiate(Resources.Load<ComputeShader>("YoloPostprocess"));
        int kernel = compute.FindKernel("BuildDrawArguments");
        compute.SetBuffer(kernel, "_Metadata", post.Metadata);
        compute.SetBuffer(kernel, "_DrawArgs", args);
        compute.Dispatch(kernel, 1, 1, 1);
        args.GetData(argData); Check(argData[1] == 1, "Indirect instance count mismatch");
        var rt = new RenderTexture(64, 64, 0); rt.Create();
        var pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        var props = new MaterialPropertyBlock();
        props.SetBuffer("_Detections", post.Detections); props.SetBuffer("_Metadata", post.Metadata);
        props.SetMatrix("_PoseToWorld", Matrix4x4.identity); props.SetFloat("_PlaneDistance", -1);
        props.SetInt("_ClassId", 2); props.SetInt("_OnlyCenter", 0);
        using var commands = new CommandBuffer();
        commands.SetRenderTarget(rt); commands.ClearRenderTarget(false, true, Color.black);
        // CommandBuffer expects Unity's projection matrix and applies the GPU conversion.
        // Pre-converting with GL.GetGPUProjectionMatrix flips the overlay on Metal.
        commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(-2,2,-2,2,.1f,10));
        if (procedural) commands.DrawMeshInstancedProcedural(mesh, 0, material, 0, post.Capacity, props);
        else commands.DrawMeshInstancedIndirect(mesh, 0, material, 0, args, 0, props);
        Graphics.ExecuteCommandBuffer(commands);
        var previous = RenderTexture.active; RenderTexture.active = rt;
        pixels.ReadPixels(new Rect(0,0,64,64), 0,0); pixels.Apply(); RenderTexture.active = previous;
        Directory.CreateDirectory("Logs"); File.WriteAllBytes(procedural ? "Logs/gpu-sprite-procedural-test.png" : "Logs/gpu-sprite-test.png", pixels.EncodeToPNG());
        Check(Vector4.Distance(pixels.GetPixel(24,24), Color.red) < .05f &&
              Vector4.Distance(pixels.GetPixel(40,24), Color.green) < .05f &&
              Vector4.Distance(pixels.GetPixel(24,40), Color.blue) < .05f &&
              Vector4.Distance(pixels.GetPixel(40,40), Color.yellow) < .05f,
            "GPU sprite orientation is incorrect: expected red/green below blue/yellow");
        Check(!ShaderUtil.GetShaderMessages(shader).Any(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error),
            "GPU sprite shader has compilation errors");
        Check(pixels.GetPixel(0,0).r < .1f, "Sprite projection size incorrect");
        UnityEngine.Object.DestroyImmediate(pixels); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(compute);
        UnityEngine.Object.DestroyImmediate(orientation);
        passed.Add((procedural ? "procedural" : "indirect") + " GPU sprite pixel assertions including asymmetric orientation and stale-slot rejection");
    }

    static void ValidateModel()
    {
        BodyOracleProjectSetup.PrepareModel();
        var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Models/3-1-epoch120-last.onnx");
        Check(asset != null, "Scene model missing");
        var model = BodyOracleModel.Load(asset);
        using var worker = new Worker(model, BackendType.GPUCompute);
        var fallbackField = typeof(Worker).GetField("m_LayerCPUFallback", BindingFlags.NonPublic | BindingFlags.Instance);
        var fallback = (HashSet<int>)fallbackField.GetValue(worker);
        var cpuLayers = model.layers.Where(layer => fallback.Contains(layer.outputs[0])).ToArray();
        Debug.Log("BODYORACLE_MODEL_CPU_FALLBACK_LAYERS: " + cpuLayers.Length + " " + string.Join("; ", cpuLayers.Select(l => l.ToString())));
        Check(cpuLayers.Length == 0, "The model has CPU fallback layers; inspect before claiming GPU-only inference.");
        using var input = new Tensor<float>(new TensorShape(1,3,640,640));
        TextureConverter.ToTensor(Texture2D.blackTexture, input, new TextureTransform().SetTensorLayout(TensorLayout.NCHW));
        worker.Schedule(input);
        var output = worker.PeekOutput(0) as Tensor<float>;
        Check(output != null, "Model output is not float");
        Debug.Log("BODYORACLE_MODEL_OUTPUT: " + output.shape);
        using var post = new GpuYoloPostprocessor(128);
        post.Process(output, .1f, .35f, .6f, .6f, 10, Vector2.zero);
        var metadata = new uint[3]; post.Metadata.GetData(metadata);
        Check(metadata[0] <= 128, "Invalid model postprocess count");
        passed.Add("actual scene model GPU inference and postprocessing; zero CPU fallback layers");
    }

    internal static void PrepareInferenceLifecycle()
    {
        Unity.Collections.NativeLeakDetection.Mode = Unity.Collections.NativeLeakDetectionMode.EnabledWithStackTrace;
        // CLI executeMethod runs outside Play mode, so RuntimeInitializeOnLoadMethod
        // is not called. Inference 2.6.1 otherwise leaves its static 64-byte dummy
        // buffer allocated at Editor exit. Register the same lifecycle as a player.
        var type = typeof(Worker).Assembly.GetType("Unity.InferenceEngine.ComputeTensorDataReaper");
        var initialize = type?.GetMethod("RegisterCleanup", BindingFlags.NonPublic | BindingFlags.Static);
        Check(initialize != null, "Inference lifecycle changed; update the editor validation adapter.");
        initialize.Invoke(null, null);
    }

}
