using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Batch entry point for the Phase-1 probe build.
///   Unity.exe -batchmode -quit -projectPath HDMR -buildTarget Android \
///             -executeMethod HDMRBuild.BuildProbe -logFile build.log
/// </summary>
public static class HDMRBuild
{
    const string ScenePath = "Assets/Scenes/HDMRProbe.unity";
    const string Pkg = "com.natis.hdmr";

    [MenuItem("HDMR/Configure Player Settings")]
    public static void Configure() => ConfigurePlayerSettings();

    [MenuItem("HDMR/Build Probe APK")]
    public static void BuildProbe()
    {
        try
        {
            ConfigurePlayerSettings();
            EnsureScene();

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HDMR-probe.apk");

            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Disabled;

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            Debug.Log($"[HDMRBuild] result={s.result} errors={s.totalErrors} time={s.totalTime} size={s.totalSize} out={apk}");
            if (s.result != BuildResult.Succeeded)
            {
                Debug.LogError("[HDMRBuild] BUILD FAILED");
                EditorApplication.Exit(1);
            }
            Debug.Log("[HDMRBuild] BUILD OK -> " + apk);
        }
        catch (Exception e)
        {
            Debug.LogError("[HDMRBuild] exception: " + e);
            EditorApplication.Exit(2);
        }
    }

    static void ConfigurePlayerSettings()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var android = NamedBuildTarget.Android;
        PlayerSettings.SetApplicationIdentifier(android, Pkg);
        PlayerSettings.productName = "HDMR Probe";
        PlayerSettings.companyName = "natis";

        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetMobileMTRendering(android, false);

        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.forceInternetPermission = false;
        PlayerSettings.Android.startInFullscreen = true;
        PlayerSettings.Android.renderOutsideSafeArea = true;

        // no XR provider on purpose — this is a flat 2D panel app for the spike
        Debug.Log("[HDMRBuild] player settings configured");
    }

    static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        camGo.tag = "MainCamera";
        var cam = camGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.07f, 0.12f);
        camGo.transform.position = new Vector3(0f, 1f, -10f);

        new GameObject("UsbUvcProbe", typeof(UsbUvcProbe));

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[HDMRBuild] scene written -> " + ScenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }
}
