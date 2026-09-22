using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>Full-frame video replay through the same GPU pipeline and sprite meshes.
/// Frame decoding and readbacks are test instrumentation, not application code.</summary>
public static class BodyOracleVideoValidation
{
    [Serializable] class ClipReport
    {
        public string clip;
        public int frames, framesWithDetections, detections, truncatedFrames, referenceComparisons, modelComparisons;
        public int[] classCounts = new int[20];
        public double meanSynchronizedGpuMilliseconds;
        public float maximumModelDifference;
    }
    [Serializable] class Report
    {
        public string unityVersion, gpu;
        public string protocol = "All decoded video frames; GPU crop, inference, exact NMS, projection and indirect character rendering. Readbacks only for assertions and artifacts. No ground-truth character labels.";
        public List<ClipReport> clips = new List<ClipReport>();
    }
    static string Argument(string name, string fallback)
    {
        var args=Environment.GetCommandLineArgs();
        for(int i=0;i+1<args.Length;i++) if(args[i]==name) return args[i+1];
        return fallback;
    }
    static void Check(bool value,string message) { if(!value) throw new Exception(message); }

    public static void Run()
    {
        BodyOracleGpuValidation.PrepareInferenceLifecycle();
        string root=Argument("-poseFrames","/tmp/bodyoracle-video-test");
        string destination=Argument("-poseResults","Logs/pose-video");
        Directory.CreateDirectory(destination);
        EditorSceneManager.OpenScene(BodyOracleProjectSetup.MacScene,OpenSceneMode.Single);
        var desktop=new SerializedObject(UnityEngine.Object.FindAnyObjectByType<DesktopOracle>());
        var spriteProperty=desktop.FindProperty("classSprites");
        var sprites=Enumerable.Range(0,spriteProperty.arraySize).Select(i=>(Sprite)spriteProperty.GetArrayElementAtIndex(i).objectReferenceValue).ToArray();
        var asset=AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Models/3-1-epoch120-last.onnx");
        var model=BodyOracleModel.Load(asset);
        using var worker=new Worker(model,BackendType.GPUCompute);
        using var baseline=new Worker(ModelLoader.Load(asset),BackendType.GPUCompute);
        var fallback=(HashSet<int>)typeof(Worker).GetField("m_LayerCPUFallback",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(worker);
        Check(!model.layers.Any(l=>fallback.Contains(l.outputs[0])),"Video model contains CPU fallback layers");
        using var input=new Tensor<float>(new TensorShape(1,3,640,640));
        using var post=new GpuYoloPostprocessor(128);
        using var renderer=new GpuSpriteRenderer(sprites);
        using var commands=new CommandBuffer();
        var cropMaterial=new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Shader/Custom_CropShader.mat"));
        var source=new Texture2D(2,2,TextureFormat.RGBA32,false);
        var pixels=new Texture2D(640,640,TextureFormat.RGBA32,false);
        var crop=new RenderTexture(640,640,0,RenderTextureFormat.ARGB32); crop.Create();
        var composite=new RenderTexture(640,640,0,RenderTextureFormat.ARGB32); composite.Create();
        var metadata=new uint[3]; var detections=new GpuPoseDetection[128];
        var report=new Report { unityVersion=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName };
        try
        {
            foreach(string clip in Directory.GetDirectories(root).OrderBy(p=>p))
            {
                string[] frames=Directory.GetFiles(clip,"frame-*.png").OrderBy(p=>p).ToArray();
                if(frames.Length==0) continue;
                var stats=new ClipReport { clip=Path.GetFileName(clip),frames=frames.Length };
                var csv=new StringBuilder("frame,detection,class,confidence,x,y,width,height,center,capacityReached\n");
                string folder=Path.Combine(destination,stats.clip);Directory.CreateDirectory(folder);
                var timer=new Stopwatch(); double elapsed=0;
                for(int f=0;f<frames.Length;f++)
                {
                    Check(source.LoadImage(File.ReadAllBytes(frames[f])),"Video frame could not be decoded");
                    cropMaterial.SetFloat("_AspectRatio",(float)source.width/source.height);
                    cropMaterial.SetFloat("_Rotate",0);cropMaterial.SetFloat("_VideoRotation",0);cropMaterial.SetFloat("_VerticalMirror",0);
                    timer.Restart();
                    commands.Clear(); commands.Blit(source,crop,cropMaterial);
                    TextureConverter.ToTensor(commands,crop,input,new TextureTransform().SetTensorLayout(TensorLayout.NCHW));
                    Graphics.ExecuteCommandBuffer(commands);
                    worker.Schedule(input);
                    var output=worker.PeekOutput(0) as Tensor<float>;
                    post.Process(output,.1f,.35f,.6f,.6f,10,Vector2.zero);
                    post.Metadata.GetData(metadata); // test synchronization, excluded from runtime
                    elapsed+=timer.Elapsed.TotalMilliseconds;
                    post.Detections.GetData(detections);
                    Check(metadata[0]<=128,"Video output capacity exceeded");
                    if(metadata[0]>0) stats.framesWithDetections++;
                    if(metadata[2]!=0) stats.truncatedFrames++;
                    for(int i=0;i<metadata[0];i++)
                    {
                        var d=detections[i];int cls=(int)d.info.y;
                        Check(cls>=0&&cls<20&&float.IsFinite(d.box.x+d.box.y+d.box.z+d.box.w),"Invalid video detection");
                        stats.classCounts[cls]++;stats.detections++;
                        csv.AppendLine(string.Join(",",new[] { (f+1).ToString(),i.ToString(),cls.ToString(),
                            d.info.x.ToString("R",CultureInfo.InvariantCulture),d.box.x.ToString("R",CultureInfo.InvariantCulture),
                            d.box.y.ToString("R",CultureInfo.InvariantCulture),d.box.z.ToString("R",CultureInfo.InvariantCulture),
                            d.box.w.ToString("R",CultureInfo.InvariantCulture),(metadata[1]==i+1?1:0).ToString(),metadata[2].ToString() }));
                    }
                    if(f%10==0||f==frames.Length-1)
                    {
                        var values=output.DownloadToArray();
                        int n=output.shape[2];var boxesAndClasses=new float[24*n];Array.Copy(values,boxesAndClasses,boxesAndClasses.Length);
                        var expected=BodyOracleGpuValidation.Reference(boxesAndClasses,128,Vector2.zero);
                        Check(expected.Count==metadata[0],"Video CPU/GPU NMS count mismatch at "+f);
                        for(int i=0;i<expected.Count;i++)
                        {
                            Check(Vector4.Distance(expected[i].box,detections[i].box)<.002f,"Video bounding box mismatch");
                            Check(Vector4.Distance(expected[i].info,detections[i].info)<.002f,"Video class/projection mismatch");
                        }
                        stats.referenceComparisons++;
                    }
                    if(f==0||f==frames.Length/2||f==frames.Length-1)
                    {
                        baseline.Schedule(input);
                        var original=(baseline.PeekOutput(0) as Tensor<float>).DownloadToArray();
                        var optimized=output.DownloadToArray();
                        Check(original.Length==optimized.Length,"Specialized model output shape changed");
                        float max=0;
                        for(int i=0;i<original.Length;i++) max=Mathf.Max(max,Mathf.Abs(original[i]-optimized[i]));
                        Check(max<.05f,"Specialized model differs from source model by "+max);
                        stats.maximumModelDifference=Mathf.Max(stats.maximumModelDifference,max);stats.modelComparisons++;
                    }
                    commands.Clear();commands.Blit(crop,composite);commands.SetRenderTarget(composite);
                    // Pass Unity's projection directly: CommandBuffer performs the GPU conversion.
                    // Pre-converting here inverted only the sprites relative to the blitted video.
                    commands.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.Ortho(-5,5,-5,5,.1f,20));
                    renderer.Record(commands,post,Matrix4x4.identity,-9);
                    Graphics.ExecuteCommandBuffer(commands);
                    var previous=RenderTexture.active;RenderTexture.active=composite;
                    pixels.ReadPixels(new Rect(0,0,640,640),0,0);pixels.Apply();RenderTexture.active=previous;
                    File.WriteAllBytes(Path.Combine(folder,$"frame-{f+1:D4}.png"),pixels.EncodeToPNG());
                }
                stats.meanSynchronizedGpuMilliseconds=elapsed/frames.Length;
                File.WriteAllText(Path.Combine(folder,"detections.csv"),csv.ToString());
                Check(stats.framesWithDetections>0,"No human detected anywhere in clip "+stats.clip);
                report.clips.Add(stats);
                Debug.Log("BODYORACLE_VIDEO_CLIP_PASS "+JsonUtility.ToJson(stats));
            }
            Check(report.clips.Count>0,"No decoded video frames found");
            File.WriteAllText(Path.Combine(destination,"report.json"),JsonUtility.ToJson(report,true));
            Debug.Log("BODYORACLE_VIDEO_VALIDATION_PASS");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(source);UnityEngine.Object.DestroyImmediate(pixels);UnityEngine.Object.DestroyImmediate(cropMaterial);
            crop.Release();composite.Release();UnityEngine.Object.DestroyImmediate(crop);UnityEngine.Object.DestroyImmediate(composite);
        }
    }
}
