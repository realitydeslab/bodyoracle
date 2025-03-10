using UnityEngine;
using Unity.Sentis;
using UnityEngine.XR.ARFoundation;
using System.Collections.Generic;
using UnityEngine.UI;
using System.Collections;
using System;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.Rendering;
using System.IO;

public class Yolo : MonoBehaviour
{
    public GameObject CenterEyePose;
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private ModelAsset modelAsset;
    private Model runtimeModel;
    private Worker worker;
    
    private WebCamTexture webCamTexture;
    private bool useARCamera = false;
    // [SerializeField] private RawImage rawImage1;

    private static TextureTransform _textureSettings;

    [SerializeField] private float confidenceThreshold = 0.6f;
    
    // 虚拟投影平面参数
    [SerializeField] private float projectionPlaneWidth =  2.8f;    // 投影平面的宽度（米）
    [SerializeField] private Vector2 projectionPlaneOffset = Vector2.zero; // 投影平面偏移（米）
    
    // 中心检测结果
    private DetectionData.PoseDetection centerDetection;
    private bool hasCenterDetection = false;
    
    // event system for detection results
    public event Action<List<DetectionData.PoseDetection>> OnDetectionsUpdated;
    private List<DetectionData.PoseDetection> latestDetections = new List<DetectionData.PoseDetection>();
    
    // YCbCr转换相关
    private Texture2D _yTexture;
    private Texture2D _cbCrTexture;
    private Texture _mainTexture;
    [SerializeField] private Material yCbCrMaterial;
    private CommandBuffer commandBuffer;
   [SerializeField] private RenderTexture rgbIntermediate;
    private Tensor<float> inputTensor;
    
    // 用于调试
    [SerializeField] private bool showDebugInfo = true;
    
    void Start()
    {
        runtimeModel = ModelLoader.Load(modelAsset);
        worker = new Worker(runtimeModel, BackendType.GPUCompute);

        _textureSettings = new TextureTransform();
        _textureSettings.SetDimensions(640, 640);
        _textureSettings.SetTensorLayout(TensorLayout.NCHW);

        commandBuffer = new CommandBuffer();
        commandBuffer.name = "YCbCrToTensorConversion";
        
        // 创建中间RGB纹理
        if (rgbIntermediate == null) {
            rgbIntermediate = new RenderTexture(640, 640, 0, RenderTextureFormat.ARGB32);
            rgbIntermediate.enableRandomWrite = true;
            rgbIntermediate.Create();
        }
        
        // 创建输入Tensor
        inputTensor = new Tensor<float>(new TensorShape(1, 3, 640, 640));

#if UNITY_IOS && !UNITY_EDITOR
        // use ARCameraManager on iOS
        useARCamera = true;
        arCameraManager = FindAnyObjectByType<ARCameraManager>();
        if (arCameraManager != null)
        {
            arCameraManager.frameReceived += OnCameraFrameReceived;
            _textureSettings.SetChannelSwizzle(1, 2, 3, 0);
        }
        else
        {
            Debug.LogError("ARCameraManager not found, falling back to WebCamTexture");
            InitializeWebCam();
        }
#else
        // use WebCamTexture on other platforms
        useARCamera = false;
        InitializeWebCam();
#endif
        
        Application.quitting += OnApplicationQuit;
    }

    private void InitializeWebCam()
    {
        // initialize WebCamTexture
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length > 0)
        {
            webCamTexture = new WebCamTexture(devices[0].name, 1280, 720, 24);
            webCamTexture.Play();
        }
        else
        {
            Debug.LogError("No webcam found");
        }
    }

    void Update()
    {
        // if using WebCamTexture, process image Here
        if (!useARCamera && webCamTexture != null && webCamTexture.isPlaying && webCamTexture.didUpdateThisFrame)
        {
            ProcessWebCamFrame();
        }
    }

    private void ProcessWebCamFrame()
    {
        if (webCamTexture == null || !webCamTexture.isPlaying)
            return;

        // rawImage_people.texture = webCamTexture;
            
        if (showDebugInfo)
        {
            Debug.Log($"WebCamTexture Size: {webCamTexture.width}x{webCamTexture.height}");
        }
        
        _mainTexture = webCamTexture;

        yCbCrMaterial.SetTexture("_MainTex", _mainTexture);
        yCbCrMaterial.SetFloat("_UseYCbCr", 0.0f);
        
        // Calculate aspect ratio for cropping logic
        float sourceWidth = _mainTexture.width;
        float sourceHeight = _mainTexture.height;
        float aspectRatio = sourceWidth / sourceHeight;
        
        yCbCrMaterial.SetFloat("_AspectRatio", aspectRatio);

        commandBuffer.Clear();
        commandBuffer.Blit(null, rgbIntermediate, yCbCrMaterial);
        commandBuffer.ToTensor(rgbIntermediate, inputTensor, _textureSettings);
        Graphics.ExecuteCommandBuffer(commandBuffer);

        // rawImage1.texture = TextureConverter.ToTexture(inputTensor);

        worker.Schedule(inputTensor);
        Tensor outputTensor = worker.PeekOutput(0);
        
        ProcessOutput(outputTensor as Tensor<float>);
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        if (args.textures.Count < 2)
            return;

        bool useYCbCr = false;
        
        if (args.textures.Count > 0)
        {
            foreach (var tex in args.textures)
            {
                switch (tex.format)
                {
                    case TextureFormat.R8:
                        _yTexture = tex as Texture2D;
                        useYCbCr = true;
                        break;
                    case TextureFormat.RG16:
                        _cbCrTexture = tex as Texture2D;
                        useYCbCr = true;
                        break;
                }
            }
        }

        if (useYCbCr && _yTexture != null && _cbCrTexture != null)
        {
            // Set the YCbCr textures
            yCbCrMaterial.SetTexture("_YTex", _yTexture);
            yCbCrMaterial.SetTexture("_CbCrTex", _cbCrTexture);
            yCbCrMaterial.SetFloat("_UseYCbCr", 1.0f);
            
            // Calculate aspect ratio for cropping logic
            float sourceWidth = _yTexture.width;
            float sourceHeight = _yTexture.height;
            float aspectRatio = sourceWidth / sourceHeight;
            
            yCbCrMaterial.SetFloat("_AspectRatio", aspectRatio);
        }
        else return;

        commandBuffer.Clear();
        commandBuffer.Blit(null, rgbIntermediate, yCbCrMaterial);
        commandBuffer.ToTensor(rgbIntermediate, inputTensor, _textureSettings);
        Graphics.ExecuteCommandBuffer(commandBuffer);

        worker.Schedule(inputTensor);
        Tensor outputTensor = worker.PeekOutput(0);
        // worker.CopyOutput(0, ref outputTensor);

        ProcessOutput(outputTensor as Tensor<float>);
    }

    private void ProcessOutput(Tensor<float> output)
    {
        if (output == null) return;
        
        List<DetectionData.PoseDetection> detections = new List<DetectionData.PoseDetection>();
        
        TensorShape shape = output.shape;
        
        float[] data = output.DownloadToArray();
        
        // transpose array
        int numDetections = shape[2]; // 8400
        int numFeatures = shape[1];   // 62

        float[] transposedData = new float[numDetections * numFeatures];
        
        for (int i = 0; i < numDetections; i++)
        {
            for (int j = 0; j < numFeatures; j++)
            {
                // [0, j, i] --> [i, j]
                int srcIndex = j * numDetections + i;
                int dstIndex = i * numFeatures + j;
                transposedData[dstIndex] = data[srcIndex];
            }
        }
        
        for (int i = 0; i < numDetections; i++)
        {
            int baseIndex = i * numFeatures + 4;
            
            // Class ID
            int maxClassId = 0;
            float maxProb = transposedData[baseIndex];
            for (int j = 1; j < 7; j++)
            {
                float prob = transposedData[baseIndex + j];
                if (prob > maxProb)
                {
                    maxProb = prob;
                    maxClassId = j;
                }
            }
            
            if (maxProb < confidenceThreshold)
                continue;
            if (maxProb > 1)
                Debug.LogError("maxProb > 1");

            // Bounding Box in cropped image (640x640)
            float croppedCenterX = transposedData[baseIndex - 4];
            float croppedCenterY = (640 - transposedData[baseIndex - 3]);
            float width = transposedData[baseIndex - 2];
            float height = transposedData[baseIndex - 1];
            // Debug.Log($"BBox in YOLO: X: {croppedCenterX}, Y: {croppedCenterY}, width: {width}, height: {height}");

            float bboxX = croppedCenterX - width / 2;
            float bboxY = croppedCenterY - height / 2;
            Rect bbox = new Rect(bboxX, bboxY, width, height);
            
            DetectionData.PoseDetection detection = new DetectionData.PoseDetection(maxClassId, maxProb, bbox);
            
            // 计算投影平面上的位置和尺寸
            detection.projectionPosition = MapToProjectionPlane(croppedCenterX, croppedCenterY);
            detection.projectionWidth = CalculateProjectionWidth(width);
            
            detections.Add(detection);
        }
        
        List<DetectionData.PoseDetection> result = ApplyNMS(detections, 0.6f);
        // List<DetectionData.PoseDetection> result = detections;

        // save latest detections and notify listeners
        latestDetections = result;
        OnDetectionsUpdated?.Invoke(latestDetections);
    }

    private bool CalculateIoU(Rect boxA, Rect boxB, float iouThreshold)
    {
        // calculate intersection area
        float xMin = Mathf.Max(boxA.x, boxB.x);
        float yMin = Mathf.Max(boxA.y, boxB.y);
        float xMax = Mathf.Min(boxA.x + boxA.width, boxB.x + boxB.width);
        float yMax = Mathf.Min(boxA.y + boxA.height, boxB.y + boxB.height);
        
        // no intersection
        if (xMax < xMin || yMax < yMin)
            return false;
        
        float intersectionArea = (xMax - xMin) * (yMax - yMin);
        
        float boxAArea = boxA.width * boxA.height;
        float boxBArea = boxB.width * boxB.height;

        if (boxBArea / intersectionArea > 0.8f) {
            return true;
        }
        
        float unionArea = boxAArea + boxBArea - intersectionArea;
        return (intersectionArea / unionArea) > iouThreshold;
    }

    private List<DetectionData.PoseDetection> ApplyNMS(List<DetectionData.PoseDetection> detections, float iouThreshold)
    {
        // if detections less than 2, no NMS
        if (detections.Count < 2)
            return detections;
            
        // Sort by confidence (descending)
        detections.Sort((a, b) => b.maxClassProbability.CompareTo(a.maxClassProbability));
        
        List<DetectionData.PoseDetection> result = new List<DetectionData.PoseDetection>();
        List<bool> isSupressed = new List<bool>(new bool[detections.Count]); // False
        
        for (int i = 0; i < detections.Count; i++)
        {
            if (isSupressed[i])
                continue;
                
            result.Add(detections[i]);
            
            for (int j = i + 1; j < detections.Count; j++)
            {
                if (isSupressed[j])
                    continue;
                    
                // If IoU > threshold, suppress the lower confidence one
                if (CalculateIoU(detections[i].boundingBox, detections[j].boundingBox, iouThreshold))
                    isSupressed[j] = true;
            }
        }
        
        // 查找包含画面中心点的检测结果
        UpdateCenterDetection(result);
        
        return result;
    }
    
    // 更新中心检测结果
    private void UpdateCenterDetection(List<DetectionData.PoseDetection> detections)
    {
        // 画面中心点坐标
        Vector2 centerPoint = new Vector2(320, 320);
        
        // 存储包含中心点的检测结果
        List<DetectionData.PoseDetection> centerDetections = new List<DetectionData.PoseDetection>();
        
        // 遍历所有检测结果
        foreach (var detection in detections)
        {
            // 检查边界框是否包含中心点
            if (detection.boundingBox.Contains(centerPoint))
            {
                centerDetections.Add(detection);
            }
        }
        
        // 如果没有找到包含中心点的检测结果
        if (centerDetections.Count == 0)
        {
            hasCenterDetection = false;
            return;
        }
        
        // 如果只有一个包含中心点的检测结果，直接使用
        if (centerDetections.Count == 1)
        {
            centerDetection = centerDetections[0];
            hasCenterDetection = true;
            return;
        }
        
        // 如果有多个包含中心点的检测结果，找到中心点最接近画面中心的一个
        DetectionData.PoseDetection closestDetection = centerDetections[0];
        float minDistance = float.MaxValue;
        
        foreach (var detection in centerDetections)
        {
            // 计算边界框中心点
            Vector2 bboxCenter = new Vector2(
                detection.boundingBox.x + detection.boundingBox.width / 2,
                detection.boundingBox.y + detection.boundingBox.height / 2
            );
            
            // 计算到画面中心的距离
            float distance = Vector2.Distance(bboxCenter, centerPoint);
            
            // 如果距离更小，更新最近的检测结果
            if (distance < minDistance)
            {
                minDistance = distance;
                closestDetection = detection;
            }
        }
        
        centerDetection = closestDetection;
        hasCenterDetection = true;
    }
    
    // 获取中心检测结果
    public bool TryGetCenterDetection(out DetectionData.PoseDetection detection)
    {
        if (hasCenterDetection)
        {
            detection = centerDetection;
            return true;
        }
        
        detection = new DetectionData.PoseDetection(-1, 0, new Rect());
        return false;
    }

    private void OnApplicationQuit()
    {
        // 确保在应用退出时释放所有资源
        CleanupResources();
    }
    
    private void CleanupResources()
    {
        if (worker != null)
        {
            worker.Dispose();
            worker = null;
        }
        
        if (webCamTexture != null && webCamTexture.isPlaying)
        {
            webCamTexture.Stop();
        }
        
        if (useARCamera && arCameraManager != null)
        {
            arCameraManager.frameReceived -= OnCameraFrameReceived;
        }
        
        // 释放CommandBuffer
        if (commandBuffer != null)
        {
            commandBuffer.Dispose();
            commandBuffer = null;
        }
        
        // 释放RenderTexture
        if (rgbIntermediate != null)
        {
            rgbIntermediate.Release();
            rgbIntermediate = null;
        }
        
        // 释放TensorFloat
        if (inputTensor != null)
        {
            inputTensor.Dispose();
            inputTensor = null;
        }
    }

    private void OnDestroy()
    {
        // 使用通用的清理方法
        CleanupResources();
        
        // 移除事件监听
        Application.quitting -= OnApplicationQuit;
        commandBuffer.Release();
    }

    public List<DetectionData.PoseDetection> GetLatestDetections()
    {
        return latestDetections;
    }

    // 将图像坐标映射到虚拟投影平面上的相对位置
    private Vector2 MapToProjectionPlane(float imageX, float imageY)
    {
        // 将图像坐标归一化到[-1,1]范围
        // 注意：输入的imageX和imageY是像素坐标，范围为[0,640]
        float normalizedX = (imageX / 640.0f) * 2 - 1;
        float normalizedY = (imageY / 640.0f) * 2 - 1;
        
        // 计算投影平面上的位置，等比例放大
        float planeX = normalizedX * (projectionPlaneWidth / 2.0f) + projectionPlaneOffset.x;
        float planeY = normalizedY * (projectionPlaneWidth / 2.0f) + projectionPlaneOffset.y;
        
        // 返回相对于相机的偏移位置（不再转换为世界坐标）
        return new Vector2(planeX, planeY);
    }
    
    // 计算投影平面上的宽度
    private float CalculateProjectionWidth(float imageWidth)
    {
        // 将图像宽度转换为投影平面上的宽度
        return imageWidth / 640.0f * projectionPlaneWidth;
    }
}