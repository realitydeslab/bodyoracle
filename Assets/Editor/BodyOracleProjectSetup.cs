using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

public static class BodyOracleProjectSetup
{
    public const string MacScene = "Assets/Scenes/DesktopWebcam.unity";
    public const string IOSScene = "Assets/Scenes/StereoscopicRendering.unity";
    static Font font;

    public static void ConfigureRuntimePackages()
    {
        // Inference Engine's editor UI brings App UI transitively. The player uses
        // UGUI; App UI 1.3.6's mobile EnhancedTouch initialization conflicts with
        // Input System 1.20. Use the package's supported editor-only define.
        foreach (var target in new[] { NamedBuildTarget.Standalone, NamedBuildTarget.iOS })
        {
            var defines = PlayerSettings.GetScriptingDefineSymbols(target).Split(';').Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (defines.Contains("APP_UI_EDITOR_ONLY")) continue;
            defines.Add("APP_UI_EDITOR_ONLY");
            PlayerSettings.SetScriptingDefineSymbols(target, defines.ToArray());
        }
        AssetDatabase.SaveAssets();
    }

    public static void PrepareModel()
    {
        var importer = AssetImporter.GetAtPath("Assets/Models/3-1-epoch120-last.onnx");
        var serialized = new SerializedObject(importer);
        var dimensions = serialized.FindProperty("dynamicDimConfigs");
        for (int i = 0; i < dimensions.arraySize; i++)
        {
            var dimension = dimensions.GetArrayElementAtIndex(i);
            string name = dimension.FindPropertyRelative("name").stringValue;
            if (name == "batch" || name == "height" || name == "width")
                dimension.FindPropertyRelative("size").intValue = name == "batch" ? 1 : 640;
        }
        if (serialized.ApplyModifiedPropertiesWithoutUndo()) importer.SaveAndReimport();
    }

    [MenuItem("Body Oracle/Create or update desktop scene and build profiles")]
    public static void CreateDesktopScene()
    {
        ConfigureRuntimePackages();
        // Read the existing iOS scene's asset mappings rather than invent class/sound order.
        EditorSceneManager.OpenScene(IOSScene, OpenSceneMode.Single);
        var original = new SerializedObject(UnityEngine.Object.FindAnyObjectByType<SpriteVisualizer>());
        var sourceDetector = new SerializedObject(UnityEngine.Object.FindAnyObjectByType<Yolo>());
        var classSprites = ReadArray<Sprite>(original, "classSprites");
        var soundSprites = ReadArray<Sprite>(original, "soundSprites");
        var sounds = ReadArray<AudioClip>(original, "soundClips");
        var model = sourceDetector.FindProperty("modelAsset").objectReferenceValue;
        var crop = sourceDetector.FindProperty("cropMaterial").objectReferenceValue;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var camera = new GameObject("Desktop Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
        camera.tag = "MainCamera";
        camera.orthographic = true; camera.orthographicSize = 5;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f,.025f,.035f);
        camera.nearClipPlane = .1f; camera.farClipPlane = 30;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        var root = new GameObject("Desktop Body Oracle");
        var detector = root.AddComponent<Yolo>();
        var d = new SerializedObject(detector);
        d.FindProperty("modelAsset").objectReferenceValue = model;
        d.FindProperty("cropMaterial").objectReferenceValue = crop;
        d.FindProperty("projectionPlaneHeight").floatValue = 10;
        d.ApplyModifiedPropertiesWithoutUndo();
        var preview = GameObject.CreatePrimitive(PrimitiveType.Quad);
        preview.name = "Webcam Preview"; preview.transform.position = new Vector3(0,0,10);
        preview.transform.localScale = new Vector3(10,10,1);
        UnityEngine.Object.DestroyImmediate(preview.GetComponent<Collider>());
        Directory.CreateDirectory("Assets/Materials");
        const string materialPath = "Assets/Materials/DesktopWebcam.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(material, materialPath);
        }
        material.SetColor("_BaseColor", Color.white);
        preview.GetComponent<Renderer>().sharedMaterial = material;
        var canvas = new GameObject("Desktop Interface", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280,800); scaler.matchWidthOrHeight = 1;
        var top = Panel("Header", canvas.transform, new Vector2(0,1), new Vector2(1,1), new Vector2(0,-100), Vector2.zero);
        TextAt("Body Oracle", top, new Vector2(28,55), new Vector2(260,32), 26);
        var status = TextAt("Starting camera…", top, new Vector2(28,15), new Vector2(1000,28), 16);
        var bottom = Panel("Controls", canvas.transform, Vector2.zero, Vector2.right, Vector2.zero, new Vector2(0,120));
        var selection = TextAt("Stand back so the camera can see your whole pose.", bottom, new Vector2(28,76), new Vector2(800,28), 18);
        var play = ButtonAt("Play character · Space", bottom, new Vector2(28,20), new Vector2(260,42));
        var toggle = ToggleAt("Show camera", bottom, new Vector2(320,26));
        var dropdown = DropdownAt(bottom, new Vector2(580,20));
        var crosshair = TextAt("+", canvas.transform, Vector2.zero, new Vector2(30,30), 25);
        crosshair.rectTransform.anchorMin = crosshair.rectTransform.anchorMax = new Vector2(.5f,.5f);
        crosshair.rectTransform.pivot = new Vector2(.5f,.5f); crosshair.alignment = TextAnchor.MiddleCenter;
        var sound = new GameObject("Selected Sound", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        sound.transform.SetParent(canvas.transform, false); sound.preserveAspect = true;
        sound.rectTransform.anchorMin = sound.rectTransform.anchorMax = new Vector2(.85f,.5f);
        sound.rectTransform.sizeDelta = new Vector2(180,180);
        sound.gameObject.SetActive(false);
        var ui = root.AddComponent<DesktopOracle>();
        var u = new SerializedObject(ui);
        Set(u,"detector",detector); Set(u,"displayCamera",camera); Set(u,"previewRenderer",preview.GetComponent<Renderer>());
        Set(u,"statusText",status); Set(u,"selectionText",selection); Set(u,"cameraDropdown",dropdown);
        Set(u,"soundButton",play); Set(u,"previewToggle",toggle); Set(u,"soundImage",sound);
        SetArray(u,"classSprites",classSprites); SetArray(u,"soundSprites",soundSprites); SetArray(u,"soundClips",sounds);
        u.ApplyModifiedPropertiesWithoutUndo();
        var events = new GameObject("Event System", typeof(EventSystem), typeof(InputSystemUIInputModule));
        events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
        EditorSceneManager.SaveScene(scene, MacScene);
        // Shared settings retain both scenes; each build profile overrides the startup scene.
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MacScene,true), new EditorBuildSettingsScene(IOSScene,true) };
        PlayerSettings.defaultScreenWidth = 1280; PlayerSettings.defaultScreenHeight = 800;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone,"com.holokit.bodyoracle");
        // Unity shares this serialized usage string between its macOS and iOS players.
        PlayerSettings.iOS.cameraUsageDescription = "Body Oracle uses your camera to recognize body poses and display characters.";
        CreateProfile("Mac", BuildTarget.StandaloneOSX, MacScene);
        CreateProfile("iOS", BuildTarget.iOS, IOSScene);
        AssetDatabase.SaveAssets();
        Debug.Log("BODYORACLE_DESKTOP_SCENE_READY");
    }

    static void CreateProfile(string name, BuildTarget target, string scene)
    {
        Directory.CreateDirectory("Assets/Settings/Build Profiles");
        string path = "Assets/Settings/Build Profiles/" + name + ".asset";
        var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(path);
        if (profile == null)
        {
            var create = typeof(BuildProfile).GetMethod("CreateInstance", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(BuildTarget), typeof(StandaloneBuildSubtarget) }, null);
            profile = (BuildProfile)create.Invoke(null, new object[] { target,
                target == BuildTarget.StandaloneOSX ? StandaloneBuildSubtarget.Player : StandaloneBuildSubtarget.Default });
            profile.name = name; AssetDatabase.CreateAsset(profile,path);
        }
        var serializedProfile = new SerializedObject(profile);
        int subtarget = (int)(target == BuildTarget.StandaloneOSX ? StandaloneBuildSubtarget.Player : StandaloneBuildSubtarget.Default);
        if (serializedProfile.FindProperty("m_Subtarget").intValue != subtarget)
        {
            serializedProfile.FindProperty("m_Subtarget").intValue = subtarget;
            serializedProfile.FindProperty("m_PlatformId").stringValue = string.Empty;
            serializedProfile.FindProperty("m_PlatformBuildProfile").managedReferenceValue = null;
            serializedProfile.ApplyModifiedPropertiesWithoutUndo();
        }
        typeof(BuildProfile).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(profile, null);
        var platform = typeof(BuildProfile).GetProperty("platformBuildProfile", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(profile);
        Debug.Log("BODYORACLE_PROFILE " + name + " settings=" + (platform?.GetType().FullName ?? "null") +
            " supported=" + BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target),target));
        profile.overrideGlobalScenes = true;
        profile.scenes = new[] { new EditorBuildSettingsScene(scene,true) };
        EditorUtility.SetDirty(profile);
    }
    static T[] ReadArray<T>(SerializedObject obj,string name) where T : UnityEngine.Object
    {
        var p=obj.FindProperty(name); return Enumerable.Range(0,p.arraySize).Select(i=>(T)p.GetArrayElementAtIndex(i).objectReferenceValue).ToArray();
    }
    static void Set(SerializedObject obj,string name,UnityEngine.Object value) => obj.FindProperty(name).objectReferenceValue=value;
    static void SetArray<T>(SerializedObject obj,string name,T[] values) where T : UnityEngine.Object
    {
        var p=obj.FindProperty(name); p.arraySize=values.Length;
        for(int i=0;i<values.Length;i++) p.GetArrayElementAtIndex(i).objectReferenceValue=values[i];
    }
    static RectTransform Rect(string name,Transform parent,Vector2 position,Vector2 size)
    {
        var r=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>(); r.SetParent(parent,false);
        r.anchorMin=r.anchorMax=r.pivot=Vector2.zero; r.anchoredPosition=position; r.sizeDelta=size; return r;
    }
    static RectTransform Panel(string name,Transform parent,Vector2 min,Vector2 max,Vector2 offsetMin,Vector2 offsetMax)
    {
        var r=Rect(name,parent,Vector2.zero,Vector2.zero); r.anchorMin=min;r.anchorMax=max;r.offsetMin=offsetMin;r.offsetMax=offsetMax;
        r.gameObject.AddComponent<Image>().color=new Color(.04f,.04f,.06f,.94f); return r;
    }
    static Text TextAt(string content,Transform parent,Vector2 position,Vector2 size,int fontSize)
    {
        var t=Rect(content,parent,position,size).gameObject.AddComponent<Text>();t.font=font;t.fontSize=fontSize;t.text=content;
        t.color=Color.white;t.raycastTarget=false; return t;
    }
    static Button ButtonAt(string content,Transform parent,Vector2 position,Vector2 size)
    {
        var r=Rect(content,parent,position,size);r.gameObject.AddComponent<Image>().color=new Color(.24f,.20f,.38f);
        var b=r.gameObject.AddComponent<Button>();b.targetGraphic=r.GetComponent<Image>();
        var text=TextAt(content,r,Vector2.zero,size,18);text.alignment=TextAnchor.MiddleCenter;return b;
    }
    static Toggle ToggleAt(string content,Transform parent,Vector2 position)
    {
        var r=Rect(content,parent,position,new Vector2(220,30));var t=r.gameObject.AddComponent<Toggle>();
        var bg=Rect("Box",r,Vector2.zero,new Vector2(28,28)).gameObject.AddComponent<Image>();bg.color=Color.gray;
        var check=Rect("Check",bg.transform,new Vector2(5,5),new Vector2(18,18)).gameObject.AddComponent<Image>();
        t.targetGraphic=bg;t.graphic=check;t.isOn=true;TextAt(content,r,new Vector2(40,0),new Vector2(180,30),18);return t;
    }
    static Dropdown DropdownAt(Transform parent,Vector2 position)
    {
        var r=Rect("Camera selector",parent,position,new Vector2(380,42));r.gameObject.AddComponent<Image>().color=new Color(.16f,.16f,.2f);
        var d=r.gameObject.AddComponent<Dropdown>(); d.targetGraphic=r.GetComponent<Image>();
        d.captionText=TextAt("Camera",r,new Vector2(12,8),new Vector2(356,28),16);
        var template=Rect("Template",r,new Vector2(0,42),new Vector2(380,150));template.gameObject.AddComponent<Image>().color=new Color(.12f,.12f,.16f);
        var scroll=template.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;
        var viewport=Rect("Viewport",template,Vector2.zero,new Vector2(380,150));viewport.gameObject.AddComponent<RectMask2D>();
        var content=Rect("Content",viewport,Vector2.zero,new Vector2(380,38));content.anchorMin=content.anchorMax=new Vector2(0,1);content.pivot=new Vector2(0,1);
        var item=Rect("Item",content,Vector2.zero,new Vector2(380,38));item.anchorMin=item.anchorMax=item.pivot=new Vector2(0,1);
        var toggle=item.gameObject.AddComponent<Toggle>();var background=item.gameObject.AddComponent<Image>();background.color=new Color(.2f,.2f,.26f);toggle.targetGraphic=background;
        var check=Rect("Selected",item,new Vector2(8,8),new Vector2(18,18)).gameObject.AddComponent<Image>();
        check.color=Color.white;check.raycastTarget=false;toggle.graphic=check;
        var label=TextAt("Camera",item,new Vector2(38,4),new Vector2(330,30),16);
        scroll.viewport=viewport;scroll.content=content;d.template=template;d.itemText=label;template.gameObject.SetActive(false);return d;
    }
}
