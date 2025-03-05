using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.XR.ARFoundation;
using UnityEngine.UI;
using UnityEngine.XR.ARSubsystems;
using System.Linq;

public class SpriteVisualizer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Yolo yoloDetector;
    [SerializeField] private ARCameraManager arCameraManager; // 添加ARCameraManager引用
    [SerializeField] private ARSession arSession; // AR会话引用
    private Camera arCamera; // 存储AR相机引用
    
    [Header("Class Sprites")]
    [SerializeField] private Sprite[] classSprites; // 每个类别对应的Sprite
    [SerializeField] private string[] classNames; // 类别名称，用于调试
    
    [Header("Sound Sprites")]
    [SerializeField] private Sprite[] soundSprites; // 每个类别对应的声音Sprite
    [SerializeField] private AudioClip[] soundClips; // 每个类别对应的音频
    
    [Header("Sound Sprite Settings")]
    [SerializeField] private GameObject soundSpriteObject; // 声音Sprite对象
    [SerializeField] private Vector3 soundSpritePosition = new Vector3(0, 0, 2.0f); // 相对于相机的位置
    [SerializeField] private Vector3 soundSpriteScale = new Vector3(0.5f, 0.5f, 0.5f); // 缩放
    [SerializeField] private Button triggerButton; // 触发按钮
    
    [Header("Visualization Settings")]
    [SerializeField] private bool showDebugInfo = false; // 是否显示调试信息
    [SerializeField] private Material spriteMaterial; // Sprite使用的材质
    
    [Header("Fixed Objects")]
    [SerializeField] private GameObject fixedSprite; // 固定位置的Sprite对象
    [SerializeField] private GameObject fixedText; // 固定位置的TextMeshPro对象
    [SerializeField] private Vector3 fixedSpritePosition = new Vector3(0, 0, 2.5f); // 相对于相机的位置
    [SerializeField] private Vector3 fixedSpriteScale = new Vector3(0.32f, 0.32f, 0.32f); // 缩放
    [SerializeField] private Vector3 fixedTextPosition = new Vector3(0, -1.2f, 2.5f); // 相对于相机的位置
    [SerializeField] private Vector3 fixedTextScale = new Vector3(0.3f, 0.3f, 0.3f); // 缩放
    
    [Header("Rotation Settings")]
    [SerializeField] private float rotationSmoothTime = 0.1f; // 旋转平滑时间
    [SerializeField] private float rotationThreshold = 0.5f; // 旋转更新阈值（度）
    [SerializeField] private int rotationUpdateInterval = 2; // 每隔多少帧更新一次旋转
    
    // 存储上一帧的相机旋转
    private Quaternion lastCameraRotation;
    
    // 用于平滑旋转
    private Vector3 rotationVelocity;
    
    // 帧计数器，用于控制旋转更新频率
    private int frameCounter = 0;
    
    // 音频播放器
    private AudioSource audioSource;
    // 是否正在播放音频
    private bool isPlayingAudio = false;
    // 当前显示的声音Sprite的类别ID
    private int currentSoundSpriteClassId = -1;
    
    // 对象池，用于重用Sprite对象
    private List<GameObject> spritePool = new List<GameObject>();
    private List<GameObject> activeSprites = new List<GameObject>();
    
    // 当前活跃的检测结果
    private Dictionary<int, DetectionData.PoseDetection> activeDetections = new Dictionary<int, DetectionData.PoseDetection>();
    
    [Header("AR Session Settings")]
    [SerializeField] private bool handleARSessionReset = true; // 是否处理AR会话重置
    [SerializeField] private float sessionResetDelay = 0.5f; // AR会话重置后的延迟时间（秒）
    
    // AR会话状态
    private bool isARSessionTracking = true;
    private Coroutine sessionResetCoroutine;
    
    void Start()
    {
        if (yoloDetector == null)
        {
            yoloDetector = FindAnyObjectByType<Yolo>();
            if (yoloDetector == null)
            {
                Debug.LogError("No Yolo detector found in the scene!");
                enabled = false;
                return;
            }
        }
        
        // 查找ARCameraManager
        if (arCameraManager == null)
        {
            arCameraManager = FindAnyObjectByType<ARCameraManager>();
            if (arCameraManager == null)
            {
                Debug.LogWarning("ARCameraManager not found, falling back to Camera.main");
                arCamera = Camera.main;
            }
            else
            {
                arCamera = arCameraManager.GetComponent<Camera>();
                if (arCamera == null)
                {
                    Debug.LogWarning("Camera component not found on ARCameraManager, falling back to Camera.main");
                    arCamera = Camera.main;
                }
            }
        }
        else
        {
            arCamera = arCameraManager.GetComponent<Camera>();
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }
        }
        
        // 查找ARSession
        if (arSession == null && handleARSessionReset)
        {
            arSession = FindAnyObjectByType<ARSession>();
            if (arSession == null)
            {
                Debug.LogWarning("ARSession not found, AR session reset handling will be disabled");
                handleARSessionReset = false;
            }
        }
        
        // 订阅AR会话状态变化事件
        if (handleARSessionReset && arSession != null)
        {
            ARSession.stateChanged += OnARSessionStateChanged;
        }
        
        // 验证相机引用
        if (arCamera == null)
        {
            Debug.LogError("Failed to find any camera reference. Fixed objects will not work correctly.");
        }
        else
        {
            // 初始化上一帧的相机旋转
            lastCameraRotation = arCamera.transform.rotation;
        }
        
        // 验证类别Sprite数组
        if (classSprites == null || classSprites.Length < 7)
        {
            Debug.LogWarning("Class sprites array is not properly set up. Expected at least 7 sprites.");
        }
        
        // 验证声音Sprite数组
        if (soundSprites == null || soundSprites.Length < 7)
        {
            Debug.LogWarning("Sound sprites array is not properly set up. Expected at least 7 sprites.");
        }
        
        // 验证音频数组
        if (soundClips == null || soundClips.Length < 7)
        {
            Debug.LogWarning("Sound clips array is not properly set up. Expected at least 7 audio clips.");
        }
        
        // 如果没有指定材质，使用默认的Sprite材质
        if (spriteMaterial == null)
        {
            spriteMaterial = new Material(Shader.Find("Sprites/Default"));
        }
        
        // 验证固定位置的对象
        if (fixedSprite == null)
        {
            Debug.LogWarning("Fixed sprite object is not assigned.");
        }
        
        if (fixedText == null)
        {
            Debug.LogWarning("Fixed text object is not assigned.");
        }
        
        // 验证声音Sprite对象
        if (soundSpriteObject == null)
        {
            Debug.LogWarning("Sound sprite object is not assigned. Creating a new one.");
            soundSpriteObject = new GameObject("SoundSprite");
            SpriteRenderer renderer = soundSpriteObject.AddComponent<SpriteRenderer>();
            renderer.material = spriteMaterial;
            renderer.sortingOrder = 200; // 确保在最前面显示
            soundSpriteObject.transform.SetParent(transform);
            soundSpriteObject.SetActive(false);
        }
        
        // 添加音频源
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        
        // 设置按钮点击事件
        if (triggerButton != null)
        {
            triggerButton.onClick.AddListener(OnTriggerButtonClicked);
        }
        else
        {
            Debug.LogWarning("Trigger button is not assigned.");
        }
        
        // 订阅检测事件
        yoloDetector.OnDetectionsUpdated += UpdateVisualizations;
    }
    
    void Update()
    {
        // 增加帧计数器
        frameCounter++;
        
        // 只有在AR会话跟踪状态下才更新对象
        if (!handleARSessionReset || isARSessionTracking)
        {
            // 更新固定位置的对象
            UpdateFixedObjects();
            
            // 更新声音Sprite对象
            UpdateSoundSpriteObject();
        }
        
        // 检查音频播放状态（无论AR会话状态如何）
        CheckAudioPlaybackStatus();
    }
    
    // 更新固定位置的对象
    private void UpdateFixedObjects()
    {
        // 确保我们有有效的相机引用
        if (arCamera == null)
        {
            // 尝试重新获取相机引用
            if (arCameraManager != null)
            {
                arCamera = arCameraManager.GetComponent<Camera>();
            }
            
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }
            
            if (arCamera == null)
            {
                Debug.LogError("无法获取有效的相机引用，无法更新固定对象");
                return; // 仍然没有有效的相机引用，退出
            }
        }
        
        // 获取相机的变换信息
        Transform cameraTransform = arCamera.transform;
        
        // 检查相机旋转是否发生了足够大的变化，或者是否到了旋转更新间隔
        float rotationDifference = Quaternion.Angle(lastCameraRotation, cameraTransform.rotation);
        bool shouldUpdateRotation = rotationDifference > rotationThreshold || (frameCounter % rotationUpdateInterval == 0);
        
        // 更新固定Sprite的位置和旋转
        if (fixedSprite != null)
        {
            try
            {
                // 使用相对位置计算世界空间中的位置
                // 注意：这里不使用transform.forward等，而是直接使用矩阵变换
                Vector3 localOffset = new Vector3(
                    fixedSpritePosition.x,
                    fixedSpritePosition.y,
                    fixedSpritePosition.z
                );
                
                // 将本地偏移转换为世界空间偏移
                Vector3 worldPosition = cameraTransform.position + 
                                       cameraTransform.TransformDirection(localOffset);
                
                // 设置位置
                fixedSprite.transform.position = worldPosition;
                
                // 只有当旋转变化足够大时才更新旋转
                if (shouldUpdateRotation)
                {
                    // 使用平滑旋转
                    Quaternion targetRotation = cameraTransform.rotation;
                    fixedSprite.transform.rotation = Quaternion.Slerp(
                        fixedSprite.transform.rotation,
                        targetRotation,
                        Time.deltaTime / rotationSmoothTime
                    );
                }
                
                // 设置缩放
                fixedSprite.transform.localScale = fixedSpriteScale;
                
                // if (showDebugInfo)
                // {
                    // Debug.Log($"相机位置: {cameraTransform.position}, 固定Sprite位置: {worldPosition}, 旋转差异: {rotationDifference}度");
                // }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"更新固定Sprite时出错: {e.Message}");
            }
        }
        
        // 更新固定文本的位置和旋转
        if (fixedText != null)
        {
            try
            {
                // 使用相对位置计算世界空间中的位置
                Vector3 localOffset = new Vector3(
                    fixedTextPosition.x,
                    fixedTextPosition.y,
                    fixedTextPosition.z
                );
                
                // 将本地偏移转换为世界空间偏移
                Vector3 worldPosition = cameraTransform.position + 
                                       cameraTransform.TransformDirection(localOffset);
                
                // 设置位置
                fixedText.transform.position = worldPosition;
                
                // 只有当旋转变化足够大时才更新旋转
                if (shouldUpdateRotation)
                {
                    // 使用平滑旋转
                    Quaternion targetRotation = cameraTransform.rotation;
                    fixedText.transform.rotation = Quaternion.Slerp(
                        fixedText.transform.rotation,
                        targetRotation,
                        Time.deltaTime / rotationSmoothTime
                    );
                }
                
                // 设置缩放
                fixedText.transform.localScale = fixedTextScale;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"更新固定文本时出错: {e.Message}");
            }
        }
        
        // 如果旋转发生了足够大的变化，更新上一帧的相机旋转
        if (shouldUpdateRotation)
        {
            lastCameraRotation = cameraTransform.rotation;
        }
    }
    
    // 更新声音Sprite对象
    private void UpdateSoundSpriteObject()
    {
        if (soundSpriteObject == null || arCamera == null)
            return;
        
        // 获取相机的变换信息
        Transform cameraTransform = arCamera.transform;
        
        // 检查相机旋转是否发生了足够大的变化，或者是否到了旋转更新间隔
        float rotationDifference = Quaternion.Angle(lastCameraRotation, cameraTransform.rotation);
        bool shouldUpdateRotation = rotationDifference > rotationThreshold || (frameCounter % rotationUpdateInterval == 0);

        // 只有在显示声音Sprite时才更新位置和旋转
        if (soundSpriteObject.activeSelf)
        {
            try
            {
                // 使用相对位置计算世界空间中的位置
                Vector3 localOffset = new Vector3(
                    soundSpritePosition.x,
                    soundSpritePosition.y,
                    soundSpritePosition.z
                );
                
                // 将本地偏移转换为世界空间偏移
                Vector3 worldPosition = cameraTransform.position + 
                                       cameraTransform.TransformDirection(localOffset);
                
                // 设置位置
                soundSpriteObject.transform.position = worldPosition;
                
                // 只有当旋转变化足够大时才更新旋转
                if (shouldUpdateRotation)
                {
                    // 使用平滑旋转
                    Quaternion targetRotation = cameraTransform.rotation;
                    soundSpriteObject.transform.rotation = Quaternion.Slerp(
                        soundSpriteObject.transform.rotation,
                        targetRotation,
                        Time.deltaTime / rotationSmoothTime
                    );
                }
                
                // 设置缩放
                soundSpriteObject.transform.localScale = soundSpriteScale;
                
                // if (showDebugInfo)
                // {
                //     Debug.Log($"声音Sprite位置: {worldPosition}, 旋转差异: {rotationDifference}度");
                // }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"更新声音Sprite时出错: {e.Message}");
            }
        }
        
        if (shouldUpdateRotation)
        {
            lastCameraRotation = cameraTransform.rotation;
        }
    }
    
    // 检查音频播放状态
    private void CheckAudioPlaybackStatus()
    {
        if (isPlayingAudio && !audioSource.isPlaying)
        {
            isPlayingAudio = false;
            soundSpriteObject.SetActive(false);
            currentSoundSpriteClassId = -1;
        }
    }
    
    // 按钮点击事件
    public void OnTriggerButtonClicked()
    {
        // 如果正在播放音频，停止播放并隐藏声音Sprite
        if (isPlayingAudio)
        {
            StopSoundAndHideSprite();
            return;
        }
        
        // 尝试获取中心检测结果
        DetectionData.PoseDetection centerDetection;
        if (yoloDetector.TryGetCenterDetection(out centerDetection))
        {
            // 显示声音Sprite并播放音频
            ShowSoundSpriteAndPlayAudio(centerDetection.predictedClassId);
        }
        else
        {
            // 没有中心检测结果，不做任何反应
            if (showDebugInfo)
            {
                Debug.Log("没有找到包含中心点的检测结果");
            }
        }
    }
    
    // 显示声音Sprite并播放音频
    private void ShowSoundSpriteAndPlayAudio(int classId)
    {
        // 验证类别ID是否有效
        if (classId < 0 || classId >= soundSprites.Length || soundSprites[classId] == null)
        {
            Debug.LogWarning($"Invalid class ID for sound sprite: {classId}");
            return;
        }
        
        // 验证音频是否有效
        if (classId >= soundClips.Length || soundClips[classId] == null)
        {
            Debug.LogWarning($"Invalid class ID for sound clip: {classId}");
            return;
        }
        
        // 设置声音Sprite
        SpriteRenderer renderer = soundSpriteObject.GetComponent<SpriteRenderer>();
        if (renderer != null)
        {
            renderer.sprite = soundSprites[classId];
            renderer.color = Color.white; // 重置颜色
        }
        
        // 在激活前设置正确的位置和旋转
        if (arCamera != null)
        {
            // 获取相机的变换信息
            Transform cameraTransform = arCamera.transform;
            
            // 使用相对位置计算世界空间中的位置
            Vector3 localOffset = new Vector3(
                soundSpritePosition.x,
                soundSpritePosition.y,
                soundSpritePosition.z
            );
            
            // 将本地偏移转换为世界空间偏移
            Vector3 worldPosition = cameraTransform.position + 
                                   cameraTransform.TransformDirection(localOffset);
            
            // 设置位置
            soundSpriteObject.transform.position = worldPosition;
            
            // 直接设置为相机的旋转，不使用平滑过渡
            soundSpriteObject.transform.rotation = cameraTransform.rotation;
            
            // 设置缩放
            soundSpriteObject.transform.localScale = soundSpriteScale;
        }
        
        // 显示声音Sprite
        soundSpriteObject.SetActive(true);
        
        // 播放音频
        audioSource.clip = soundClips[classId];
        audioSource.Play();
        isPlayingAudio = true;
        
        // 记录当前显示的声音Sprite的类别ID
        currentSoundSpriteClassId = classId;
        
        // 更新上一帧的相机旋转，确保下一次更新时有正确的参考
        if (arCamera != null)
        {
            lastCameraRotation = arCamera.transform.rotation;
        }
        
        if (showDebugInfo)
        {
            string className = classId < classNames.Length ? classNames[classId] : $"Class {classId}";
            Debug.Log($"显示声音Sprite并播放音频: {className}");
        }
    }
    
    // 停止声音并隐藏Sprite
    private void StopSoundAndHideSprite()
    {
        // 停止音频播放
        audioSource.Stop();
        isPlayingAudio = false;
        
        // 隐藏声音Sprite
        soundSpriteObject.SetActive(false);
        
        // 重置当前显示的声音Sprite的类别ID
        currentSoundSpriteClassId = -1;
    }
    
    void OnDestroy()
    {
        // 取消订阅事件
        if (yoloDetector != null)
            yoloDetector.OnDetectionsUpdated -= UpdateVisualizations;
        
        // 取消订阅AR会话状态变化事件
        if (handleARSessionReset && arSession != null)
        {
            ARSession.stateChanged -= OnARSessionStateChanged;
        }
        
        // 清理对象池
        foreach (var sprite in spritePool)
        {
            if (sprite != null)
                Destroy(sprite);
        }
        spritePool.Clear();
        activeSprites.Clear();
    }
    
    // 更新可视化
    private void UpdateVisualizations(List<DetectionData.PoseDetection> detections)
    {
        try
        {
            // 清除当前活跃的检测
            activeDetections.Clear();
            
            // 处理新的检测结果
            foreach (var detection in detections)
            {
                // 使用检测ID作为唯一标识符
                int detectionId = GetDetectionId(detection);
                activeDetections[detectionId] = detection;
            }
            
            // 更新可视化元素
            UpdateSpriteVisualizations();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in UpdateVisualizations: {e.Message}\n{e.StackTrace}");
        }
    }
    
    // 生成检测的唯一ID
    private int GetDetectionId(DetectionData.PoseDetection detection)
    {
        // 使用类别ID和位置信息生成唯一ID
        return detection.predictedClassId * 1000 + 
               Mathf.RoundToInt(detection.projectionPosition.x * 100) + 
               Mathf.RoundToInt(detection.projectionPosition.y * 100);
    }
    
    // 更新Sprite可视化
    private void UpdateSpriteVisualizations()
    {
        try
        {
            // 首先，将所有活跃的Sprite标记为未使用
            foreach (var sprite in activeSprites)
            {
                if (sprite != null)
                {
                    sprite.SetActive(false);
                }
            }
            activeSprites.Clear();
            
            // 为每个检测创建或重用Sprite对象
            foreach (var kvp in activeDetections)
            {
                int detectionId = kvp.Key;
                DetectionData.PoseDetection detection = kvp.Value;
                
                // 检查类别ID是否有效
                if (detection.predictedClassId < 0 || detection.predictedClassId >= classSprites.Length || 
                    classSprites[detection.predictedClassId] == null)
                {
                    Debug.LogWarning($"Invalid class ID: {detection.predictedClassId} or missing sprite");
                    continue;
                }
                
                // 获取或创建Sprite对象
                GameObject spriteObj = GetSpriteFromPool();
                if (spriteObj == null)
                {
                    Debug.LogError("Failed to get sprite from pool");
                    continue;
                }
                
                activeSprites.Add(spriteObj);
                
                // 设置Sprite
                SpriteRenderer renderer = spriteObj.GetComponent<SpriteRenderer>();
                if (renderer == null)
                {
                    Debug.LogError("SpriteRenderer component not found on sprite object");
                    continue;
                }
                
                renderer.sprite = classSprites[detection.predictedClassId];
                
                // 设置位置
                spriteObj.transform.position = detection.projectionPosition;
                
                // 计算缩放比例，保持原始宽高比，但宽度与检测宽度匹配
                float originalWidth = renderer.sprite.bounds.size.x;
                float targetWidth = detection.projectionWidth;
                float scale = targetWidth / originalWidth;
                
                spriteObj.transform.localScale = new Vector3(scale, scale, scale);
                
                // 确保Sprite面向相机
                if (arCamera != null)
                {
                    // 使用平滑旋转
                    Quaternion targetRotation = arCamera.transform.rotation;
                    spriteObj.transform.rotation = Quaternion.Slerp(
                        spriteObj.transform.rotation,
                        targetRotation,
                        Time.deltaTime / rotationSmoothTime
                    );
                }
                else
                {
                    spriteObj.transform.rotation = Camera.main.transform.rotation;
                }
                
                // 添加调试信息
                if (showDebugInfo)
                {
                    string className = detection.predictedClassId < classNames.Length ? 
                                      classNames[detection.predictedClassId] : 
                                      $"Class {detection.predictedClassId}";
                    
                    Debug.Log($"检测到: {className}, 置信度: {detection.maxClassProbability:F2}, BoundingBox: {detection.boundingBox}");
                    Debug.Log($"该物体的显示位置: {detection.projectionPosition}, 宽度: {detection.projectionWidth}");
                }
                
                // 激活对象
                spriteObj.SetActive(true);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in UpdateSpriteVisualizations: {e.Message}\n{e.StackTrace}");
        }
    }
    
    // 从对象池获取Sprite对象
    private GameObject GetSpriteFromPool()
    {
        try
        {
            // 尝试从池中获取非活动的Sprite
            GameObject sprite = null;
            
            // 查找未使用的对象
            foreach (var obj in spritePool)
            {
                if (obj != null && !obj.activeInHierarchy)
                {
                    sprite = obj;
                    break;
                }
            }
            
            // 如果没有可用的Sprite，创建一批新的
            if (sprite == null)
            {
                // 预分配一批Sprite对象，而不是每次只创建一个
                int batchSize = 5; // 每次创建5个对象
                for (int i = 0; i < batchSize; i++)
                {
                    GameObject newObj = new GameObject($"DetectionSprite_{spritePool.Count}");
                    newObj.transform.SetParent(transform);
                    
                    // 添加SpriteRenderer组件
                    SpriteRenderer renderer = newObj.AddComponent<SpriteRenderer>();
                    renderer.material = spriteMaterial;
                    renderer.sortingOrder = 100; // 确保在前面显示
                    
                    // 确保新创建的对象默认不可见
                    newObj.SetActive(false);
                    
                    spritePool.Add(newObj);
                }
                
                // 使用第一个新创建的Sprite
                sprite = spritePool[spritePool.Count - batchSize];
                
                if (showDebugInfo)
                {
                    Debug.Log($"对象池扩展：当前大小 {spritePool.Count}");
                }
            }
            
            return sprite;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in GetSpriteFromPool: {e.Message}");
            return null;
        }
    }
    
    // 处理AR会话状态变化
    private void OnARSessionStateChanged(ARSessionStateChangedEventArgs args)
    {
        if (showDebugInfo)
        {
            Debug.Log($"AR会话状态变化: {args.state}");
        }
        
        switch (args.state)
        {
            case ARSessionState.None:
            case ARSessionState.CheckingAvailability:
            case ARSessionState.NeedsInstall:
            case ARSessionState.Installing:
            case ARSessionState.Ready:
                // 这些状态表示AR会话尚未开始或正在准备中
                isARSessionTracking = false;
                
                // 如果会话已准备好但尚未启动，尝试启动会话
                if (arSession != null && !arSession.enabled)
                {
                    arSession.enabled = true;
                    if (showDebugInfo)
                    {
                        Debug.Log("AR会话已准备好，正在启动...");
                    }
                }
                break;
                
            case ARSessionState.SessionInitializing:
                // AR会话正在初始化
                isARSessionTracking = false;
                // 开始会话重置处理
                if (sessionResetCoroutine != null)
                {
                    StopCoroutine(sessionResetCoroutine);
                }
                sessionResetCoroutine = StartCoroutine(HandleSessionReset());
                break;
                
            case ARSessionState.SessionTracking:
                // AR会话正在跟踪
                isARSessionTracking = true;
                break;
        }
    }
    
    // 处理会话重置的协程
    private IEnumerator HandleSessionReset()
    {
        // 隐藏所有活跃的Sprite
        foreach (var sprite in activeSprites)
        {
            if (sprite != null)
            {
                sprite.SetActive(false);
            }
        }
        
        // 隐藏声音Sprite
        if (soundSpriteObject != null && soundSpriteObject.activeSelf)
        {
            soundSpriteObject.SetActive(false);
        }
        
        // 停止音频播放
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
            isPlayingAudio = false;
        }
        
        // 等待一段时间，让AR会话稳定
        yield return new WaitForSeconds(sessionResetDelay);
        
        // 重置相机旋转记录
        if (arCamera != null)
        {
            lastCameraRotation = arCamera.transform.rotation;
        }
        
        // 清空检测结果
        activeDetections.Clear();
        
        if (showDebugInfo)
        {
            Debug.Log("AR会话重置处理完成");
        }
    }
}
    