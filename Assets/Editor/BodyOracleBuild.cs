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

    /// <summary>Release export for TestFlight. App Store Connect rejects development builds, so this
    /// drops BuildOptions.Development and stamps the build number. `unity build` takes no trailing
    /// arguments, so the number arrives through BODYORACLE_BUILD_NUMBER.</summary>
    public static void IOSRelease()
    {
        string number = Environment.GetEnvironmentVariable("BODYORACLE_BUILD_NUMBER");
        if (!string.IsNullOrEmpty(number)) PlayerSettings.iOS.buildNumber = number;
        Build(BuildTarget.iOS, BodyOracleProjectSetup.IOSScene, "Builds/iOSRelease", false, true);
    }

    /// <summary>Bundle id of the App Store Connect app record. The Mac App Store build joins the iOS
    /// record (universal purchase), so it uses the iOS id instead of the local-dev Standalone id.</summary>
    const string StoreBundleId = "org.realitydeslab.body-oracle";

    /// <summary>Mac App Store export for TestFlight. Emits an Xcode project so Tools/testflight_mac.py can
    /// archive and sign it with Xcode's automatic signing. The store bundle id and the build number
    /// (BODYORACLE_BUILD_NUMBER) apply only to this build; ProjectSettings keep their local-dev values.</summary>
    public static void MacRelease()
    {
        string previousId = PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone);
        string previousNumber = PlayerSettings.macOS.buildNumber;
        bool previousXcode = UnityEditor.OSXStandalone.UserBuildSettings.createXcodeProject;
        try
        {
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, StoreBundleId);
            string number = Environment.GetEnvironmentVariable("BODYORACLE_BUILD_NUMBER");
            if (!string.IsNullOrEmpty(number)) PlayerSettings.macOS.buildNumber = number;
            UnityEditor.OSXStandalone.UserBuildSettings.createXcodeProject = true;
            Build(BuildTarget.StandaloneOSX, BodyOracleProjectSetup.MacScene, "Builds/MacRelease", false, true);
            Debug.Log("BODYORACLE_MAC_RELEASE " + StoreBundleId + " " + PlayerSettings.bundleVersion + " (" + PlayerSettings.macOS.buildNumber + ")");
        }
        finally
        {
            UnityEditor.OSXStandalone.UserBuildSettings.createXcodeProject = previousXcode;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, previousId);
            PlayerSettings.macOS.buildNumber = previousNumber;
        }
    }

    static void Build(BuildTarget target, string scene, string defaultOutput, bool deviceValidation = false,
        bool release = false)
    {
        BodyOracleProjectSetup.ConfigureRuntimePackages();
        string output = defaultOutput;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-buildOutput") output = args[i + 1];
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { scene }, locationPathName = output, target = target,
            extraScriptingDefines = deviceValidation ? new[] { "BODYORACLE_DEVICE_TEST" } : Array.Empty<string>(),
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = release ? BuildOptions.None : BuildOptions.Development });
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/build-" + target + "-result.txt",
            report.summary.result + "\n" + report.summary.totalErrors + " errors\n" + report.summary.totalWarnings + " warnings\n" + output);
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception(target + " build failed: " + report.summary.result);
        Debug.Log("BODYORACLE_BUILD_PASS " + target + " " + output +
            (release ? " release version " + PlayerSettings.bundleVersion + " (" +
                (target == BuildTarget.iOS ? PlayerSettings.iOS.buildNumber : PlayerSettings.macOS.buildNumber) + ")" : ""));
    }
}
