using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Serenegiant.UVC;
using Hdmr;

/// <summary>
///   Unity.exe -batchmode -quit -projectPath HDMR -buildTarget Android \
///             -executeMethod HDMRBuild.BuildSpike -logFile build.log
/// </summary>
public static class HDMRBuild
{
    const string ScenePath = "Assets/Scenes/HDMRSpike.unity";
    const string Cam2ScenePath = "Assets/Scenes/HDMRCam2Probe.unity";
    const string Pkg = "com.natis.hdmr";
    const string Cam2Pkg = "com.natis.hdmr.cam2";

    [MenuItem("HDMR/Configure Player Settings")]
    public static void Configure() => ConfigurePlayerSettings();

    [MenuItem("HDMR/Rebuild HDMRSpike scene")]
    public static void MakeScene() { EnsureScene(); EditorSceneManager.SaveOpenScenes(); }

    [MenuItem("HDMR/Build Spike APK")]
    public static void BuildSpike()
    {
        try
        {
            ConfigurePlayerSettings();
            EnsureScene();

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HDMR-spike.apk");

            EditorUserBuildSettings.buildAppBundle = false;

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
            if (s.result != BuildResult.Succeeded) { Debug.LogError("[HDMRBuild] BUILD FAILED"); EditorApplication.Exit(1); }
            Debug.Log("[HDMRBuild] BUILD OK -> " + apk);
        }
        catch (Exception e)
        {
            Debug.LogError("[HDMRBuild] exception: " + e);
            EditorApplication.Exit(2);
        }
    }

    [MenuItem("HDMR/Build Camera2 Probe APK")]
    public static void BuildCam2Probe()
    {
        try
        {
            ConfigurePlayerSettings(Cam2Pkg, "HDMR Cam2 Probe");
            EnsureCam2Scene();

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HDMR-cam2probe.apk");
            EditorUserBuildSettings.buildAppBundle = false;

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Cam2ScenePath },
                locationPathName = apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"[HDMRBuild] cam2 result={s.result} errors={s.totalErrors} out={apk}");
            if (s.result != BuildResult.Succeeded) { Debug.LogError("[HDMRBuild] CAM2 BUILD FAILED"); EditorApplication.Exit(1); }
            Debug.Log("[HDMRBuild] CAM2 BUILD OK -> " + apk);
        }
        catch (Exception e) { Debug.LogError("[HDMRBuild] cam2 exception: " + e); EditorApplication.Exit(2); }
    }

    static void EnsureCam2Scene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        camGo.tag = "MainCamera";
        var cam = camGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f);
        new GameObject("Cam2Probe", typeof(Camera2Probe));
        EditorSceneManager.SaveScene(scene, Cam2ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(Cam2ScenePath, true) };
        Debug.Log("[HDMRBuild] cam2 scene written -> " + Cam2ScenePath);
    }

    static void ConfigurePlayerSettings() => ConfigurePlayerSettings(Pkg, "HDMR Spike");

    static void ConfigurePlayerSettings(string pkg, string productName)
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var android = NamedBuildTarget.Android;
        PlayerSettings.SetApplicationIdentifier(android, pkg);
        PlayerSettings.productName = productName;
        PlayerSettings.companyName = "natis";

        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetMobileMTRendering(android, false);

        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.forceInternetPermission = false;
        PlayerSettings.Android.renderOutsideSafeArea = true;

        // 16:9 panel so the video fills it edge-to-edge with no crop and no bars
        try
        {
            PlayerSettings.Android.resizableWindow = true;
            PlayerSettings.Android.defaultWindowWidth = 1600;
            PlayerSettings.Android.defaultWindowHeight = 900;
            PlayerSettings.Android.minimumWindowWidth = 480;
            PlayerSettings.Android.minimumWindowHeight = 270;
        }
        catch (Exception e) { Debug.LogWarning("[HDMRBuild] window size: " + e.Message); }

        Debug.Log("[HDMRBuild] player settings configured");
    }

    static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // camera — just clears the panel to black; the video is drawn full-panel
        // by HdmrUi via GUI.DrawTextureWithTexCoords (always centred, fills, crops)
        var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        camGo.tag = "MainCamera";
        var cam = camGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        camGo.transform.position = new Vector3(0f, 0f, -2f);

        // UVC rig
        var rig = new GameObject("UVC");
        var audio = rig.AddComponent<AudioSource>();
        audio.playOnAwake = false;
        audio.spatialBlend = 0f;
        var mgr = rig.AddComponent<UVCManager>();        // auto-adds AndroidUtils via RequireComponent
        var drawer = rig.AddComponent<HdmrQuadDrawer>();
        var ctrl = rig.AddComponent<HdmrController>();
        var test = rig.AddComponent<HdmrSelfTest>();
        var ui = rig.AddComponent<HdmrUi>();
        var remote = rig.AddComponent<HdmrRemote>();

        mgr.PreferH264 = false;
        mgr.DefaultWidth = 1280;
        mgr.DefaultHeight = 720;
        mgr.UVCDrawers = new Component[] { drawer };

        drawer.quad = null;              // no world quad — HdmrUi draws the video full-panel
        drawer.audioSource = audio;

        ctrl.manager = mgr;
        ctrl.drawer = drawer;

        test.ctrl = ctrl;
        test.drawer = drawer;

        ui.ctrl = ctrl;
        ui.drawer = drawer;
        ui.selfTest = test;

        remote.ctrl = ctrl;
        remote.selfTest = test;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[HDMRBuild] scene written -> " + ScenePath);
    }
}
