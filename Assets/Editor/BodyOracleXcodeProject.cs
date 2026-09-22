#if UNITY_IOS
using System;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>Keep generated projects compatible with the current Apple linker.</summary>
public static class BodyOracleXcodeProject
{
    [PostProcessBuild(1000)]
    public static void OnPostProcessBuild(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS) return;
        var project = new PBXProject();
        string file = PBXProject.GetPBXProjectPath(path);
        project.ReadFromFile(file);
        // HoloKit 0.5.6 selects a removed linker using an Xcode 15 beta flag.
        // Use Xcode's default linker instead; preserve all other linker flags.
        project.UpdateBuildProperty(project.GetUnityFrameworkTargetGuid(), "OTHER_LDFLAGS",
            Array.Empty<string>(), new[] { "-ld64" });
        project.WriteToFile(file);
    }

    public static void RepairExport()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-xcodeProject");
        if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing -xcodeProject path");
        OnPostProcessBuild(BuildTarget.iOS, args[i + 1]);
    }
}
#endif
