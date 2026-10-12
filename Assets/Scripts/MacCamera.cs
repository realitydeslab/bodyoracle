#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>Zero-copy macOS camera: AVFoundation frames arrive as Metal textures on Unity's device,
/// never through the CPU. Native side: Tools/macos-native/BodyOracleCamera.mm.</summary>
public sealed class MacCamera : IDisposable
{
    const string Plugin = "BodyOracleMac";
    [DllImport(Plugin)] static extern IntPtr BodyOracleCamera_Start(string name, int width, int height, int fps);
    [DllImport(Plugin)] static extern ulong BodyOracleCamera_Acquire(IntPtr handle, out IntPtr texture, out int width, out int height);
    [DllImport(Plugin)] static extern int BodyOracleCamera_Error(IntPtr handle, StringBuilder buffer, int size);
    [DllImport(Plugin)] static extern void BodyOracleCamera_Stop(IntPtr handle);

    IntPtr handle;
    Texture2D texture;
    public string DeviceName { get; }
    public Texture Texture => texture;

    MacCamera(IntPtr handle, string deviceName)
    {
        this.handle = handle;
        DeviceName = deviceName;
    }

    /// <summary>Returns null, with a reason, when the plug-in is missing or the camera cannot start.</summary>
    public static MacCamera TryStart(string deviceName, int width, int height, int fps, out string error)
    {
        error = null;
        try
        {
            IntPtr handle = BodyOracleCamera_Start(deviceName, width, height, fps);
            var message = new StringBuilder(256);
            if (BodyOracleCamera_Error(handle, message, message.Capacity) != 0)
            {
                BodyOracleCamera_Stop(handle);
                error = message.ToString();
                return null;
            }
            return new MacCamera(handle, deviceName);
        }
        catch (Exception exception) when (exception is DllNotFoundException || exception is EntryPointNotFoundException)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>Points <see cref="Texture"/> at the newest camera frame; false when none has arrived.</summary>
    public bool TryUpdate()
    {
        if (handle == IntPtr.Zero || BodyOracleCamera_Acquire(handle, out IntPtr native, out int width, out int height) == 0)
            return false;
        if (texture == null || texture.width != width || texture.height != height)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            // sRGB, matching the plug-in's BGRA8Unorm_sRGB view.
            texture = Texture2D.CreateExternalTexture(width, height, TextureFormat.BGRA32, false, false, native);
            texture.name = "Body Oracle camera";
        }
        else texture.UpdateExternalTexture(native);
        return true;
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) BodyOracleCamera_Stop(handle);
        handle = IntPtr.Zero;
        if (texture != null) UnityEngine.Object.Destroy(texture);
        texture = null;
    }
}
#endif
