using UnityEngine;
using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine.UI;

public class Yolo : MonoBehaviour
{
    public ModelAsset modelAsset;
    private Model runtimeModel;
    private Worker worker;

    void Start()
    {
        runtimeModel = ModelLoader.Load(modelAsset);
        worker = new Worker(runtimeModel, BackendType.GPUCompute);

        CameraCapture.Init();
        screenImage = GameObject.Find("ScreenImage").GetComponent<Image>();
    }

    private Image screenImage;

    void Update()
    {
        WebCamTexture texture_cam = CameraCapture.GetCameraTexture();
        screenImage.material.mainTexture = texture_cam;
        if (texture_cam.width >= 100)
        {
            Tensor<float> inputTensor = PreprocessImage(texture_cam);
            worker.Schedule(inputTensor);
            Tensor<float> outputTensor = worker.PeekOutput() as Tensor<float>;
            Debug.Log("outputTensor:");
            Debug.Log(outputTensor);
        }
    }

    Tensor<float> PreprocessImage(WebCamTexture camera_texture, int target = 640)
    {
        int source_w = camera_texture.width;
        int source_h = camera_texture.height;
        int crop_size = Mathf.Min(source_w, source_h); // TEST!!!
        int start_x = (source_w - crop_size) / 2;
        int start_y = (source_h - crop_size) / 2;

        Texture2D texture_cropped = new Texture2D(crop_size, crop_size, TextureFormat.RGB24, false);

        Color[] pixels = camera_texture.GetPixels(start_x, start_y, crop_size, crop_size);
        texture_cropped.SetPixels(pixels);
        texture_cropped.Apply();

        Texture2D texture_resized = new Texture2D(target, target, TextureFormat.RGB24, false);
        Graphics.ConvertTexture(texture_cropped, texture_resized);
        
        // Convert to a tensor
        Tensor<float> tensor = TextureConverter.ToTensor(texture_resized, target, target, 3);
        return tensor;
    }
}