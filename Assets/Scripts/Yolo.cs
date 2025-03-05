using UnityEngine;
using Unity.Sentis;
using UnityEngine.XR.ARFoundation;
using System.Collections.Generic;
using UnityEngine.UI;
using System.Collections;
using System;
using UnityEngine.XR.ARSubsystems;

public class Yolo : MonoBehaviour
{
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private ModelAsset modelAsset;
    private Model runtimeModel;
    private Worker worker;
    
    private int cam_width;
    private int cam_height;
    private int crop_size;
    
    private WebCamTexture webCamTexture;
    private bool useARCamera = false;

    private static Texture2D _croppedTexture;
    private static TextureTransform _textureSettings;

    [SerializeField] private float confidenceThreshold = 0.5f;
    
    // 虚拟投影平面参数
    [SerializeField] private float projectionPlaneDistance = 9.0f; // 投影平面距离相机的距离（米）
    [SerializeField] private float projectionPlaneWidth =  2.8f;    // 投影平面的宽度（米）
    [SerializeField] private Vector2 projectionPlaneOffset = Vector2.zero; // 投影平面偏移（米）
    
    // 中心检测结果
    private DetectionData.PoseDetection centerDetection;
    private bool hasCenterDetection = false;
    
    // event system for detection results
    public event Action<List<DetectionData.PoseDetection>> OnDetectionsUpdated;
    private List<DetectionData.PoseDetection> latestDetections = new List<DetectionData.PoseDetection>();
    
    void Start()
    {
        runtimeModel = ModelLoader.Load(modelAsset);
        worker = new Worker(runtimeModel, BackendType.GPUCompute);

#if UNITY_IOS && !UNITY_EDITOR
        // use ARCameraManager on iOS
        useARCamera = true;
        arCameraManager = FindAnyObjectByType<ARCameraManager>();
        if (arCameraManager != null)
        {
            arCameraManager.frameReceived += OnCameraFrameReceived;
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

        _textureSettings = new TextureTransform();
        _textureSettings.SetDimensions(640, 640);
        _textureSettings.SetTensorLayout(TensorLayout.NCHW);
        
        // 确保在应用退出时释放资源
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
        // 使用RenderTexture和Graphics.Blit来处理原生纹理
        RenderTexture tempRT = RenderTexture.GetTemporary(webCamTexture.width, webCamTexture.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(webCamTexture, tempRT);
        
        // 创建一个可读写的Texture2D
        Texture2D cameraTexture = new Texture2D(webCamTexture.width, webCamTexture.height, TextureFormat.RGB24, false);
        
        // 激活临时RenderTexture
        RenderTexture prevRT = RenderTexture.active;
        RenderTexture.active = tempRT;
        
        // 读取像素
        cameraTexture.ReadPixels(new Rect(0, 0, tempRT.width, tempRT.height), 0, 0);
        cameraTexture.Apply();
        
        // 恢复之前的RenderTexture
        RenderTexture.active = prevRT;
        RenderTexture.ReleaseTemporary(tempRT);
        
        cam_width = cameraTexture.width;
        cam_height = cameraTexture.height;
        crop_size = Mathf.Min(cam_width, cam_height);
        Debug.Log($"cam_width: {cam_width}, cam_height: {cam_height}, crop_size: {crop_size}");
        
        Tensor<float> inputTensor = PreprocessTexture(cameraTexture);

        try
        {
            worker.Schedule(inputTensor);
            
            Tensor outputTensor = null;
            worker.CopyOutput(0, ref outputTensor);

            ProcessOutput(outputTensor as Tensor<float>);
            
            outputTensor.Dispose();
        }
        finally
        {
            // 确保inputTensor被释放
            inputTensor.Dispose();
            Destroy(cameraTexture);
        }
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        if (args.textures.Count == 0) return;
        
        // 获取AR相机纹理
        Texture2D arCameraTexture = args.textures[0];
        
        // 使用RenderTexture和Graphics.Blit来处理原生纹理
        RenderTexture tempRT = RenderTexture.GetTemporary(arCameraTexture.width, arCameraTexture.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(arCameraTexture, tempRT);
        
        // 创建一个可读写的Texture2D
        Texture2D cameraTexture = new Texture2D(arCameraTexture.width, arCameraTexture.height, TextureFormat.RGB24, false);
        
        // 激活临时RenderTexture
        RenderTexture prevRT = RenderTexture.active;
        RenderTexture.active = tempRT;
        
        // 读取像素
        cameraTexture.ReadPixels(new Rect(0, 0, tempRT.width, tempRT.height), 0, 0);
        cameraTexture.Apply();
        
        // 恢复之前的RenderTexture
        RenderTexture.active = prevRT;
        RenderTexture.ReleaseTemporary(tempRT);
        
        cam_width = cameraTexture.width;
        cam_height = cameraTexture.height;
        crop_size = Mathf.Min(cam_width, cam_height);

        Tensor<float> inputTensor = PreprocessTexture(cameraTexture);

        try
        {
            worker.Schedule(inputTensor);
            
            Tensor outputTensor = null;
            worker.CopyOutput(0, ref outputTensor);

            ProcessOutput(outputTensor as Tensor<float>);
            
            outputTensor.Dispose();
        }
        finally
        {
            // 确保inputTensor被释放
            inputTensor.Dispose();
            Destroy(cameraTexture);
        }
    }

    private Tensor<float> PreprocessTexture(Texture2D texture)
    {
        // Check if the textures need to be recreated
        if (_croppedTexture == null || _croppedTexture.width != crop_size)
        {
            if (_croppedTexture != null) Destroy(_croppedTexture);
            _croppedTexture = new Texture2D(crop_size, crop_size, texture.format, false);
        }
        
        // Crop
        int start_x = (cam_width - crop_size) / 2;
        int start_y = (cam_height - crop_size) / 2;
        
        Color[] pixels = texture.GetPixels(start_x, start_y, crop_size, crop_size);
        _croppedTexture.SetPixels(pixels);
        _croppedTexture.Apply();

        Tensor<float> convertedTensor = new Tensor<float>(new TensorShape(1, 3, 640, 640));
        TextureConverter.ToTensor(_croppedTexture, convertedTensor, _textureSettings);
        return convertedTensor;
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
            Debug.Log($"BBox in YOLO: X: {croppedCenterX}, Y: {croppedCenterY}, width: {width}, height: {height}");

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
        
        if (_croppedTexture != null)
        {
            Destroy(_croppedTexture);
            _croppedTexture = null;
        }
        
        if (webCamTexture != null && webCamTexture.isPlaying)
        {
            webCamTexture.Stop();
        }
        
        if (useARCamera && arCameraManager != null)
        {
            arCameraManager.frameReceived -= OnCameraFrameReceived;
        }
    }

    private void OnDestroy()
    {
        // 使用通用的清理方法
        CleanupResources();
        
        // 移除事件监听
        Application.quitting -= OnApplicationQuit;
    }

    public List<DetectionData.PoseDetection> GetLatestDetections()
    {
        return latestDetections;
    }

    // 将图像坐标映射到虚拟投影平面上的世界坐标
    private Vector3 MapToProjectionPlane(float imageX, float imageY)
    {
        Camera mainCamera = null;
        
        // 优先使用AR相机
        if (useARCamera && arCameraManager != null)
        {
            mainCamera = arCameraManager.GetComponent<Camera>();
        }
        
        // 如果AR相机不可用，则使用主相机
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
        }
        
        if (mainCamera == null)
        {
            Debug.LogError("无法获取相机引用，无法计算投影平面位置");
            return Vector3.zero;
        }
            
        // 将图像坐标归一化到[-1,1]范围
        // 注意：输入的imageX和imageY是像素坐标，范围为[0,640]
        float normalizedX = (imageX / 640.0f) * 2 - 1;
        float normalizedY = (imageY / 640.0f) * 2 - 1;
        
        // 计算投影平面上的位置，等比例放大
        float planeX = normalizedX * (projectionPlaneWidth / 2.0f) + projectionPlaneOffset.x;
        float planeY = normalizedY * (projectionPlaneWidth / 2.0f) + projectionPlaneOffset.y;
        
        // 获取相机的变换信息
        Transform cameraTransform = mainCamera.transform;
        
        // 创建相对于相机的本地偏移
        Vector3 localOffset = new Vector3(
            planeX,
            planeY,
            projectionPlaneDistance
        );
        
        // 将本地偏移转换为世界空间位置
        Vector3 worldPosition = cameraTransform.position + 
                               cameraTransform.TransformDirection(localOffset);
        
        return worldPosition;
    }
    
    // 计算投影平面上的宽度
    private float CalculateProjectionWidth(float imageWidth)
    {
        // 将图像宽度转换为投影平面上的宽度
        return imageWidth / 640.0f * projectionPlaneWidth;
    }
}