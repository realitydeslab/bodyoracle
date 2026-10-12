#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>Adds View > Enter Full Screen (Control-Command-F) to the Mac player's menu bar, using
/// the window's native full screen. Native side: Tools/macos-native/BodyOracleMenu.mm.</summary>
public static class MacMenu
{
    [DllImport("BodyOracleMac")] static extern void BodyOracleMenu_Install();
    [DllImport("BodyOracleMac")] static extern void BodyOracleMenu_ExitFullScreen();

    /// <summary>Leaves native full screen; does nothing in a window.</summary>
    public static void ExitFullScreen()
    {
        try { BodyOracleMenu_ExitFullScreen(); }
        catch (System.Exception exception) when (exception is System.DllNotFoundException || exception is System.EntryPointNotFoundException) { }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        try { BodyOracleMenu_Install(); }
        catch (System.Exception exception) when (exception is System.DllNotFoundException || exception is System.EntryPointNotFoundException)
        {
            Debug.LogWarning("Full-screen menu unavailable: " + exception.Message);
        }
    }
}
#endif
