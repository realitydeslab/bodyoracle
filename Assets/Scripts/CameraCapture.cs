using UnityEngine;
using UnityEngine.UI;

public static class CameraCapture
{
    private static WebCamTexture webCamTexture;

    public static void Init()
    {
        webCamTexture = new WebCamTexture();
        webCamTexture.Play();
    }

    public static WebCamTexture GetCameraTexture()
    {
        return webCamTexture;
    }
}