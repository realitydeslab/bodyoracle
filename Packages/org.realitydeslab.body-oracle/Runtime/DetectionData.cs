using UnityEngine;

public static class DetectionData
{
    public static bool isDetecting = true;

    // 定义检测结果的数据结构
    [System.Serializable]
    public struct PoseDetection
    {
        public int predictedClassId;           // 预测的类别ID
        public float maxClassProbability;      // 最大类别概率（置信度）
        public Rect boundingBox;               // 原始图像中的边界框（像素坐标）
        public Vector2 projectionPosition;     // 虚拟平面上的位置（相对于相机的偏移）
        public float projectionHeight;          // 虚拟平面上的高度（世界单位）

        public PoseDetection(int classId, float probability, Rect bbox)
        {
            predictedClassId = classId;
            maxClassProbability = probability;
            boundingBox = bbox;
            projectionPosition = Vector2.zero;
            projectionHeight = 0f;
        }
    }
} 