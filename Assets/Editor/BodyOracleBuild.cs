using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BodyOracleBuild
{
    public static void Mac() => Build(BuildTarget.StandaloneOSX, BodyOracleProjectSetup.MacScene, "Builds/Mac/BodyOracle.app");
    public static void IOS() => Build(BuildTarget.iOS, BodyOracleProjectSetup.IOSScene, "Builds/iOS");
    public static void IOSDeviceValidation() => Build(BuildTarget.iOS, BodyOracleProjectSetup.IOSScene, "Builds/iOSDeviceValidation", true);

    static void Build(BuildTarget target, string scene, string defaultOutput, bool deviceValidation = false)
    {
        BodyOracleProjectSetup.ConfigureRuntimePackages();
        string output = defaultOutput;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-buildOutput") output = args[i + 1];
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { scene }, locationPathName = output, target = target,
            extraScriptingDefines = deviceValidation ? new[] { "BODYORACLE_DEVICE_TEST" } : Array.Empty<string>(),
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.Development });
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/build-" + target + "-result.txt",
            report.summary.result + "\n" + report.summary.totalErrors + " errors\n" + report.summary.totalWarnings + " warnings\n" + output);
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception(target + " build failed: " + report.summary.result);
        Debug.Log("BODYORACLE_BUILD_PASS " + target + " " + output);
    }
}
