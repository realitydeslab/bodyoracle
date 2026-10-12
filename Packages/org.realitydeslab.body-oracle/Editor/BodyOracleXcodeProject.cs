#if UNITY_IOS
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>Post-processes the exported Xcode project: linker flags and export compliance.</summary>
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
        DeclareExemptEncryption(Path.Combine(path, "Info.plist"));
    }

    // Body Oracle uses no encryption beyond the OS's own (it makes no network requests), so the
    // exported Info.plist says so and App Store Connect does not ask for export compliance on
    // every uploaded build.
    static void DeclareExemptEncryption(string plistPath)
    {
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        plist.root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
        File.WriteAllText(plistPath, plist.WriteToString());
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
