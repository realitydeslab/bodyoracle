using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Static sprite meshes; all detection-dependent placement happens in the vertex shader.</summary>
public sealed class GpuSpriteRenderer : IDisposable
{
    readonly Mesh[] meshes = new Mesh[20];
    readonly Material[] materials = new Material[20];
    readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
    readonly ComputeBuffer arguments;
    readonly ComputeShader argumentShader;
    readonly int argumentKernel;
    readonly bool useIndirect;

    public GpuSpriteRenderer(Sprite[] sprites, bool forceProcedural = false)
    {
        if (!SystemInfo.supportsInstancing)
            throw new NotSupportedException("Body Oracle requires GPU instancing.");
        useIndirect = SystemInfo.supportsIndirectArgumentsBuffer && !forceProcedural;
        var shader = Resources.Load<Shader>("BodyOracleSprites");
        if (shader == null || !shader.isSupported)
            throw new InvalidOperationException("Body Oracle GPU sprite shader is missing or unsupported.");
        if (useIndirect)
        {
            argumentShader = UnityEngine.Object.Instantiate(Resources.Load<ComputeShader>("YoloPostprocess"));
            argumentKernel = argumentShader.FindKernel("BuildDrawArguments");
        }
        var data = new uint[20 * 5];
        for (int c = 0; c < 20; c++)
        {
            if (sprites == null || c >= sprites.Length || sprites[c] == null) continue;
            Sprite sprite = sprites[c];
            // Preserve tight meshes, atlas UVs and pivots from the original SpriteRenderer.
            var vertices = sprite.vertices;
            var positions = new Vector3[vertices.Length];
            float height = Mathf.Max(sprite.bounds.size.y, 0.0001f);
            for (int v = 0; v < vertices.Length; v++) positions[v] = vertices[v] / height;
            var triangles = Array.ConvertAll(sprite.triangles, index => (int)index);
            meshes[c] = new Mesh { name = "Body Oracle sprite " + c };
            meshes[c].vertices = positions;
            meshes[c].uv = sprite.uv;
            meshes[c].triangles = triangles;
            meshes[c].RecalculateBounds();
            meshes[c].UploadMeshData(true);
            materials[c] = new Material(shader) { mainTexture = sprite.texture, enableInstancing = true };
            data[c * 5] = (uint)triangles.Length;
        }
        if (useIndirect)
        {
            arguments = new ComputeBuffer(data.Length, sizeof(uint), ComputeBufferType.IndirectArguments);
            arguments.SetData(data);
            argumentShader.SetBuffer(argumentKernel, "_DrawArgs", arguments);
        }
    }

    public void Draw(GpuYoloPostprocessor results, Transform parent, float planeDistance, bool onlyCenter, int layer)
    {
        if (results == null) return;
        // Inference is slower than the display; ease the characters between results every frame.
        results.Follow(Time.unscaledDeltaTime);
        Prepare(results.DisplayDetections, results.DisplayMetadata, parent.localToWorldMatrix, planeDistance, onlyCenter);
        // Model predictions are not clamped to the input image. A conservative bound
        // prevents CPU frustum culling from dropping valid off-centre detections.
        var bounds = new Bounds(parent.position, Vector3.one * 10000);
        for (int c = 0; c < 20; c++)
        {
            if (meshes[c] == null) continue;
            properties.SetInt("_ClassId", c);
            // Kept for ComputeBuffer interoperability with the inference engine.
            // The instance count is written by BuildDrawArguments, never read by C#.
#pragma warning disable CS0618
            if (useIndirect)
                Graphics.DrawMeshInstancedIndirect(meshes[c], 0, materials[c], bounds, arguments,
                    c * 5 * sizeof(uint), properties, ShadowCastingMode.Off, false, layer,
                    null, LightProbeUsage.Off);
            else
                // Static capacity; the vertex shader rejects unused slots using GPU metadata.
                // No detection count or position is copied to the CPU.
                Graphics.DrawMeshInstancedProcedural(meshes[c], 0, materials[c], bounds, results.Capacity,
                    properties, ShadowCastingMode.Off, false, layer, null, LightProbeUsage.Off);
#pragma warning restore CS0618
        }
    }

    // Offscreen rendering uses the identical meshes, material, buffers and arguments.
    // Useful for deterministic video validation without running a webcam or changing a scene.
    public void Record(CommandBuffer commands, GpuYoloPostprocessor results, Matrix4x4 poseToWorld, float planeDistance)
    {
        Prepare(results.Detections, results.Metadata, poseToWorld, planeDistance, false);
        for (int c = 0; c < 20; c++)
        {
            if (meshes[c] == null) continue;
            properties.SetInt("_ClassId", c);
            if (useIndirect)
                commands.DrawMeshInstancedIndirect(meshes[c], 0, materials[c], 0, arguments, c * 5 * sizeof(uint), properties);
            else
                commands.DrawMeshInstancedProcedural(meshes[c], 0, materials[c], 0, results.Capacity, properties);
        }
    }

    void Prepare(ComputeBuffer detections, ComputeBuffer metadata, Matrix4x4 poseToWorld, float planeDistance, bool onlyCenter)
    {
        if (useIndirect)
        {
            argumentShader.SetBuffer(argumentKernel, "_Metadata", metadata);
            argumentShader.Dispatch(argumentKernel, 1, 1, 1);
        }
        properties.SetBuffer("_Detections", detections);
        properties.SetBuffer("_Metadata", metadata);
        properties.SetMatrix("_PoseToWorld", poseToWorld);
        properties.SetFloat("_PlaneDistance", planeDistance);
        properties.SetInt("_OnlyCenter", onlyCenter ? 1 : 0);
    }

    public void Dispose()
    {
        arguments?.Dispose();
        Release(argumentShader);
        for (int c = 0; c < 20; c++)
        {
            Release(meshes[c]);
            Release(materials[c]);
        }
    }

    static void Release(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(value);
        else UnityEngine.Object.DestroyImmediate(value);
    }
}
