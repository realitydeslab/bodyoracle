using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Assigns a model asset to every Yolo component in the application and test scenes.
// The Yolo.modelAsset reference stores an importer-assigned fileID, so the scenes have to be
// rewritten through Unity rather than by editing their YAML.
public static class BodyOracleModelSwap
{
    static readonly string[] Scenes =
    {
        BodyOracleProjectSetup.IOSScene,
        BodyOracleProjectSetup.MacScene,
        "Assets/Tests/Scenes/iOSVideoReplay.unity",
    };

    public static void Run()
    {
        string modelPath = Argument("-modelPath") ?? throw new ArgumentException("-modelPath is required");
        if (AssetDatabase.LoadAssetAtPath<ModelAsset>(modelPath) == null)
            throw new FileNotFoundException("Model asset not found or not imported: " + modelPath);

        var report = new List<string>();
        foreach (string scenePath in Scenes)
        {
            if (!File.Exists(scenePath)) { report.Add(scenePath + ": missing, skipped"); continue; }
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            // Opening a scene in single mode unloads assets, so re-resolve the model per scene.
            var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(modelPath);
            var detectors = UnityEngine.Object.FindObjectsByType<Yolo>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var detector in detectors)
            {
                var serialized = new SerializedObject(detector);
                var property = serialized.FindProperty("modelAsset");
                report.Add($"{scenePath}: {detector.name}.modelAsset {Name(property.objectReferenceValue)} -> {model.name}");
                property.objectReferenceValue = model;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(detector);
            }
            if (detectors.Length == 0) report.Add(scenePath + ": no Yolo component found");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);
        }

        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/model-swap.txt", string.Join("\n", report));
        foreach (string line in report) Debug.Log("BODYORACLE_MODEL_SWAP " + line);
        Debug.Log("BODYORACLE_MODEL_SWAP_PASS " + modelPath);
    }

    static string Name(UnityEngine.Object value) => value == null ? "(none)" : value.name;

    static string Argument(string flag)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == flag) return args[i + 1];
        return null;
    }
}
