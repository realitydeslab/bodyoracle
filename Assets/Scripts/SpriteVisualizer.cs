using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.XR.ARFoundation;
using UnityEngine.UI;
using UnityEngine.XR.ARSubsystems;
using System.Linq;
using UnityEngine.Animations;
using HoloKit;

public class SpriteVisualizer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Yolo yoloDetector;
    [SerializeField] private HoloKitCameraManager m_HoloKitCameraManager;
    // Parent Constraint相关引用
    [SerializeField] private Transform mainCameraTransform;
    [SerializeField] private Transform centerEyePoseTransform;
    [SerializeField] private GameObject constraintObject;
    private bool lastRenderModeMono = true; // 用于跟踪上一次的渲染模式
    
    [Header("Class Sprites")]
    [SerializeField] private Sprite[] classSprites; // 每个类别对应的Sprite
    [SerializeField] private string[] classNames; // 类别名称，用于调试
    
    [Header("Sound Sprites")]
    [SerializeField] private Sprite[] soundSprites; // 每个类别对应的声音Sprite
    [SerializeField] private AudioClip[] soundClips; // 每个类别对应的音频
    
    [Header("Sound Sprite Settings")]
    [SerializeField] private GameObject soundSpriteObject; // 声音Sprite对象
    [SerializeField] private Button triggerButton; // 触发按钮
    
    [Header("Visualization Settings")]
    [SerializeField] private bool showDebugInfo = false; // 是否显示调试信息
    [SerializeField] private Material spriteMaterial; // Sprite使用的材质
    [SerializeField] private float projectionPlaneDistance = 9.0f; // 投影平面距离相机的距离（米）
    
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

        if (mainCameraTransform == null || centerEyePoseTransform == null)
        {
            Debug.LogWarning("无法找到Main Camera或Center Eye Pose对象");
            GameObject mainCamera = GameObject.Find("Main Camera");
            GameObject centerEyePose = GameObject.Find("Center Eye Pose");
            mainCameraTransform = mainCamera.transform;
            centerEyePoseTransform = centerEyePose.transform;
        }
    }
    
    void Update()
    {
        // 检查音频播放状态
        CheckAudioPlaybackStatus();
        
        // 根据渲染模式切换Parent Constraint的source
        UpdateParentConstraints();
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
    
    private void UpdateParentConstraints()
    {
        // 检查必要的引用是否存在
        if (mainCameraTransform == null || centerEyePoseTransform == null)
        {
            return;
        }
        
        bool isMonoMode = m_HoloKitCameraManager.ScreenRenderMode == ScreenRenderMode.Mono;

        // 只有在渲染模式发生变化时才更新Parent Constraint
        if (isMonoMode != lastRenderModeMono)
        {
            if (showDebugInfo)
            {
                Debug.Log($"渲染模式已变更为: {(isMonoMode ? "Mono" : "Stereo")}");
            }
            
            Transform sourceTransform = isMonoMode ? mainCameraTransform : centerEyePoseTransform;
            ParentConstraint constraint = constraintObject.GetComponent<ParentConstraint>();
            if (constraint.sourceCount > 0){
                constraint.RemoveSource(0);
            }
            constraint.AddSource(new ConstraintSource{
                sourceTransform = sourceTransform,
                weight = 1.0f
            });
            constraint.constraintActive = true;
            if (showDebugInfo)
            {
                Debug.Log($"已更新 {constraintObject.name} 的Parent Constraint source为 {sourceTransform.name}");
            }

            lastRenderModeMono = isMonoMode;
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
        
        soundSpriteObject.SetActive(true);
        
        // 播放音频
        audioSource.clip = soundClips[classId];
        audioSource.Play();
        isPlayingAudio = true;
        
        // 记录当前显示的声音Sprite的类别ID
        currentSoundSpriteClassId = classId;
        
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
                
                // 设置sprite的位置
                spriteObj.transform.localPosition = new Vector3(
                    detection.projectionPosition.x,
                    detection.projectionPosition.y, 
                    projectionPlaneDistance
                );
                // Debug.Log($"Sprite position: {spriteObj.transform.localPosition}");

                // 设置sprite的旋转
                spriteObj.transform.localRotation = Quaternion.identity;

                // 计算缩放比例，保持原始宽高比，但宽度与检测宽度匹配
                float originalWidth = renderer.sprite.bounds.size.x;
                float targetWidth = detection.projectionWidth;
                float scale = targetWidth / originalWidth;
                
                spriteObj.transform.localScale = new Vector3(scale, scale, scale);
                // Debug.Log($"Sprite scale: {spriteObj.transform.localScale}");
                
                // 激活Sprite对象
                spriteObj.SetActive(true);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in UpdateSpriteVisualizations: {e.Message}");
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
}
    