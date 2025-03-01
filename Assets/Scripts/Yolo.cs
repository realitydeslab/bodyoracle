using UnityEngine;
using Unity.Sentis;
using UnityEngine.XR.ARFoundation;
using System.Collections.Generic;
using UnityEngine.UI;

public class Yolo : MonoBehaviour
{
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private ModelAsset modelAsset;
    private Model runtimeModel;
    private Worker worker;
    private int cam_width;
    private int cam_height;
    private int crop_size;

    void Start()
    {
        runtimeModel = ModelLoader.Load(modelAsset);
        worker = new Worker(runtimeModel, BackendType.GPUCompute);

        arCameraManager = FindAnyObjectByType<ARCameraManager>();
        arCameraManager.frameReceived += OnCameraFrameReceived;
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        if (args.textures.Count == 0) return;
        Texture2D cameraTexture = args.textures[0];
        cam_width = cameraTexture.width;
        cam_height = cameraTexture.height;
        crop_size = Mathf.Min(cam_width, cam_height);
        Debug.Log("cameraTexture format: " + cameraTexture.format);

        Tensor<float> inputTensor = PreprocessTexture(cameraTexture);

        worker.Schedule(inputTensor);
        Tensor<float> outputTensor = worker.PeekOutput() as Tensor<float>;

        ProcessOutput(outputTensor);
    }

    private Tensor<float> PreprocessTexture(Texture2D texture)
    {
        // Crop
        int start_x = (cam_width - crop_size) / 2;
        int start_y = (cam_height - crop_size) / 2;

        Texture2D texture_cropped = new Texture2D(crop_size, crop_size, texture.format, false);

        Color[] pixels = texture.GetPixels(start_x, start_y, crop_size, crop_size);
        texture_cropped.SetPixels(pixels);
        texture_cropped.Apply();

        // Resize
        Texture2D texture_resized = new Texture2D(640, 640, texture.format, false);
        Graphics.ConvertTexture(texture_cropped, texture_resized);

        // Adjust color format: BGRA32 to RGB
        Color32[] pixels32 = texture_resized.GetPixels32();
        float[] rgbData = new float[640 * 640 * 3];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgbData[i * 3] = pixels[i].r / 255.0f;
            rgbData[i * 3 + 1] = pixels[i].g / 255.0f;
            rgbData[i * 3 + 2] = pixels[i].b / 255.0f;
        }

        return new Tensor<float>(new TensorShape(1, 3, 640, 640), rgbData);
    }

    private void ProcessOutput(Tensor<float> output)
    {
        float[] data = output.DownloadToArray();
        Debug.Log(data.Length);

        float conf_threshold = 0.5f;
        for (int i = 0; i < data.Length; i += 57)
        {
            int cls = (int)data[i];
            float conf = data[i + 5];
            if (conf < conf_threshold)
            {
                continue;
            }

            float x1 = data[i + 1] * crop_size + (cam_width - crop_size) / 2;
            float y1 = (1 - data[i + 2]) * crop_size + (cam_height - crop_size) / 2;
            float x2 = data[i + 3] * crop_size + (cam_width - crop_size) / 2;
            float y2 = (1 - data[i + 4]) * crop_size + (cam_height - crop_size) / 2;
            float w = x2 - x1;
            float h = y2 - y1;
            float cx = (x1 + x2) / 2.0f;
            float cy = (y1 + y2) / 2.0f;

            // TODO: display images
        }
    }
}