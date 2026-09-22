using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.Video;

public static class BodyOracleSimulatorBuild
{
    const string ScenePath = "Assets/Tests/Scenes/iOSVideoReplay.unity";
    const string MediaPath = "Assets/StreamingAssets/BodyOracleReplay";
    static string Argument(string key, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    public static void Build()
        => BuildReplay(true);

    public static void BuildDeviceReplay()
        => BuildReplay(false);

    static void BuildReplay(bool simulator)
    {
        BodyOracleProjectSetup.ConfigureRuntimePackages();
        CreateScene();
        string input = Argument("-poseVideoDirectory", "Logs/simulator-inputs");
        if (Directory.Exists(MediaPath)) throw new InvalidOperationException("Preserve or move the existing replay media folder before building: " + MediaPath);
        var sdk = PlayerSettings.iOS.sdkVersion;
        var architecture = PlayerSettings.iOS.simulatorSdkArchitecture;
        var identifier = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS);
        var defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.iOS);
        var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.iOS);
        bool xrWasEnabled = xr != null && xr.InitManagerOnStart;
        var loaders = xr != null ? xr.Manager.activeLoaders.ToList() : null;
        string xrPath = xr != null ? AssetDatabase.GetAssetPath(xr) : null;
        byte[] xrBackup = xrPath != null ? File.ReadAllBytes(xrPath) : null;
        var native = PluginImporter.GetAllImporters().Where(p => p.assetPath.EndsWith("libHoloKitLowLatencyTracking.a")).ToArray();
        try
        {
            Directory.CreateDirectory(MediaPath);
            foreach (string clip in new[] { "pose_1.mp4", "pose_squats.mp4" }) File.Copy(Path.Combine(input, clip), Path.Combine(MediaPath, clip));
            AssetDatabase.Refresh();
            PlayerSettings.iOS.sdkVersion = simulator ? iOSSdkVersion.SimulatorSDK : iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.simulatorSdkArchitecture = AppleMobileArchitectureSimulator.ARM64;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, simulator ? "org.realitydeslab.body-oracle.simulator" : "org.realitydeslab.body-oracle.replay");
            if (xr != null)
            {
                xr.InitManagerOnStart = false;
                foreach (var loader in loaders) xr.Manager.TryRemoveLoader(loader);
                EditorUtility.SetDirty(xr.Manager);
                EditorUtility.SetDirty(xr);
            }
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.iOS,
                string.Join(";", defines.Split(';').Where(d => !d.StartsWith("UNITY_XR_ARKIT_"))));
            // The hardware head-tracker archive has device-only object files.
            if (simulator) foreach (var plugin in native) plugin.SetIncludeInBuildDelegate(_ => false);
            AssetDatabase.SaveAssets();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { ScenePath }, target = BuildTarget.iOS,
                locationPathName = Argument("-buildOutput", simulator ? "Builds/iOSSimulator" : "Builds/iOSDeviceReplay"),
                options = BuildOptions.Development,
                extraScriptingDefines = simulator ? new[] { "BODYORACLE_SIMULATOR_TEST", "APP_UI_EDITOR_ONLY" } : new[] { "BODYORACLE_SIMULATOR_TEST" }
            });
            if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("Simulator export failed: " + report.summary.result);
            if (simulator) AddSimulatorBridge(Argument("-buildOutput", "Builds/iOSSimulator"));
            Debug.Log("BODYORACLE_SIMULATOR_BUILD_PASS");
        }
        finally
        {
            PlayerSettings.iOS.sdkVersion = sdk;
            PlayerSettings.iOS.simulatorSdkArchitecture = architecture;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, identifier);
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.iOS, defines);
            if (xr != null)
            {
                xr.InitManagerOnStart = xrWasEnabled;
                xr.Manager.TrySetLoaders(loaders);
                EditorUtility.SetDirty(xr.Manager);
                EditorUtility.SetDirty(xr);
            }
            foreach (var plugin in native) plugin.SetIncludeInBuildDelegate(null);
            AssetDatabase.DeleteAsset(MediaPath);
            AssetDatabase.SaveAssets();
            // BuildPlayer may unload the loader assets, invalidating saved object references.
            // Restore the exact serialized pre-build settings after all other saves.
            if (xrBackup != null)
            {
                File.WriteAllBytes(xrPath, xrBackup);
                AssetDatabase.ImportAsset(xrPath, ImportAssetOptions.ForceUpdate);
            }
        }
    }

    static void AddSimulatorBridge(string path)
    {
#if UNITY_IOS
        const string relative = "Libraries/BodyOracleUnsupportedHeadTracking.cpp";
        File.Copy("Tools/Simulator/UnsupportedHeadTracking.cpp", Path.Combine(path, relative), true);
        string projectPath = UnityEditor.iOS.Xcode.PBXProject.GetPBXProjectPath(path);
        var project = new UnityEditor.iOS.Xcode.PBXProject();
        project.ReadFromFile(projectPath);
        string guid = project.AddFile(relative, relative, UnityEditor.iOS.Xcode.PBXSourceTree.Source);
        project.AddFileToBuild(project.GetUnityFrameworkTargetGuid(), guid);
        project.WriteToFile(projectPath);
#endif
    }

    public static void CreateScene()
    {
        var scene = EditorSceneManager.OpenScene(BodyOracleProjectSetup.IOSScene, OpenSceneMode.Single);
        var detector = UnityEngine.Object.FindAnyObjectByType<Yolo>();
        var visualizer = UnityEngine.Object.FindAnyObjectByType<SpriteVisualizer>();
        var original = new SerializedObject(visualizer);
        var mono = (GameObject)original.FindProperty("panel_mono").objectReferenceValue;
        var stereo = (GameObject)original.FindProperty("panel_stereo").objectReferenceValue;
        var soundButton = (Button)original.FindProperty("triggerButton").objectReferenceValue;
        var recordButton = (Button)original.FindProperty("recordButton").objectReferenceValue;
        foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            string ns = component.GetType().Namespace ?? "";
            if (ns.StartsWith("HoloKit")) UnityEngine.Object.DestroyImmediate(component);
            else if (ns.StartsWith("UnityEngine.XR") || ns.StartsWith("Unity.XR") || ns.StartsWith("UnityEngine.InputSystem.XR")) component.enabled = false;
        }
        foreach (var constraint in UnityEngine.Object.FindObjectsByType<ParentConstraint>(FindObjectsInactive.Include, FindObjectsSortMode.None)) constraint.constraintActive = false;
        foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None)) camera.enabled = false;
        foreach (var listener in UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None)) listener.enabled = false;
        mono.SetActive(true); stereo.SetActive(false);
        if (recordButton != null) recordButton.gameObject.SetActive(false);
        // Hide unsupported HoloKit mode/record controls while preserving the original Read button and sound UI.
        foreach (var button in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (button != soundButton) button.gameObject.SetActive(false);
        visualizer.transform.SetParent(null, false);
        visualizer.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        visualizer.transform.localScale = Vector3.one;
        original.Update();
        original.FindProperty("enableMovementMonitoring").boolValue = false;
        original.FindProperty("projectionPlaneDistance").floatValue = 9;
        original.ApplyModifiedPropertiesWithoutUndo();
        var player = detector.gameObject.AddComponent<VideoPlayer>();
        player.playOnAwake = false; player.source = VideoSource.Url;
        var d = new SerializedObject(detector);
        d.FindProperty("videoSource").objectReferenceValue = player;
        d.FindProperty("projectionPlaneHeight").floatValue = 10;
        d.FindProperty("projectionPlaneOffset").vector2Value = Vector2.zero;
        d.ApplyModifiedPropertiesWithoutUndo();
        var display = new GameObject("Simulator video camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
        display.tag = "MainCamera"; display.orthographic = true; display.orthographicSize = 5;
        display.clearFlags = CameraClearFlags.SolidColor; display.backgroundColor = Color.black;
        display.nearClipPlane = .1f; display.farClipPlane = 30;
        display.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        var preview = GameObject.CreatePrimitive(PrimitiveType.Quad);
        preview.name = "Simulated camera image"; preview.transform.position = new Vector3(0,0,10); preview.transform.localScale = new Vector3(10,10,1);
        UnityEngine.Object.DestroyImmediate(preview.GetComponent<Collider>());
        preview.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/DesktopWebcam.mat");
        var overlay = new GameObject("Replay test controls", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        overlay.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        overlay.GetComponent<Canvas>().sortingOrder = 100;
        var scale = overlay.GetComponent<CanvasScaler>(); scale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scale.referenceResolution = new Vector2(800,1400); scale.matchWidthOrHeight = 0;
        var text = new GameObject("Replay status", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
        text.transform.SetParent(overlay.transform,false); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 25; text.color = Color.white; text.alignment = TextAnchor.UpperCenter; text.raycastTarget = false;
        text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(.5f,1);
        text.rectTransform.pivot = new Vector2(.5f,1); text.rectTransform.anchoredPosition = new Vector2(0,-110);
        text.rectTransform.sizeDelta = new Vector2(760,140);
        var next = new GameObject("Next replay video", typeof(RectTransform), typeof(Image), typeof(Button)).GetComponent<Button>();
        next.transform.SetParent(overlay.transform,false);
        var rect = (RectTransform)next.transform; rect.anchorMin = rect.anchorMax = new Vector2(.5f,0);
        rect.anchoredPosition = new Vector2(0,130); rect.sizeDelta = new Vector2(300,60);
        next.GetComponent<Image>().color = new Color(.2f,.2f,.3f); next.targetGraphic = next.GetComponent<Image>();
        var label = UnityEngine.Object.Instantiate(text,next.transform);label.name = "Label";label.text = "Next replay video";
        label.alignment = TextAnchor.MiddleCenter;label.rectTransform.anchorMin=Vector2.zero;label.rectTransform.anchorMax=Vector2.one;
        label.rectTransform.offsetMin=label.rectTransform.offsetMax=Vector2.zero;
        var replay = new GameObject("Simulator replay harness").AddComponent<BodyOracleSimulatorReplay>();
        replay.detector=detector;replay.presentation=visualizer;replay.video=player;replay.displayCamera=display;
        replay.preview=preview.GetComponent<Renderer>();replay.status=text;replay.soundButton=soundButton;replay.nextButton=next;
        Directory.CreateDirectory("Assets/Tests/Scenes");
        EditorSceneManager.SaveScene(scene,ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("BODYORACLE_SIMULATOR_SCENE_READY");
    }
}
