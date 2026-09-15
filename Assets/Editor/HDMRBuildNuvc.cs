using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using HdmrNuvc;

/// <summary>
///   Unity.exe -batchmode -quit -projectPath HDMR -buildTarget Android \
///             -executeMethod HDMRBuildNuvc.BuildNuvc -logFile build.log
///
/// Parallel build to the aandusb spike: same head-locked 2D panel, but the
/// capture path is the from-scratch nuvc plugin (Node D). Separate package
/// (com.natis.hdmr.nuvc) so b21 stays the fallback.
/// </summary>
public static class HDMRBuildNuvc
{
    const string ScenePath = "Assets/Scenes/HDMRNuvc.unity";
    const string Pkg = "com.natis.hdmr.nuvc";

    /// <summary>Set for the duration of this build so UsbManifestPostProcessor
    /// injects the USB_DEVICE_ATTACHED intent-filter (aandusb normally supplies it).</summary>
    public static bool Active;

    [MenuItem("HDMR/Build nuvc APK")]
    public static void BuildNuvc()
    {
        Active = true;
        try
        {
            ConfigurePlayerSettings();
            DisableXR();          // this is the FLAT 2D panel build — never boot into VR
            EnsureScene();

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HDMR-nuvc.apk");
            EditorUserBuildSettings.buildAppBundle = false;

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"[HDMRBuildNuvc] result={s.result} errors={s.totalErrors} time={s.totalTime} out={apk}");
            if (s.result != BuildResult.Succeeded) { Debug.LogError("[HDMRBuildNuvc] BUILD FAILED"); EditorApplication.Exit(1); }
            Debug.Log("[HDMRBuildNuvc] BUILD OK -> " + apk);
        }
        catch (Exception e) { Debug.LogError("[HDMRBuildNuvc] exception: " + e); EditorApplication.Exit(2); }
        finally { Active = false; }
    }

    static void ConfigurePlayerSettings()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var android = NamedBuildTarget.Android;
        PlayerSettings.SetApplicationIdentifier(android, Pkg);
        PlayerSettings.productName = "HDMR nuvc";
        PlayerSettings.companyName = "natis";

        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);
        PlayerSettings.allowUnsafeCode = true;

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetMobileMTRendering(android, false);

        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.forceInternetPermission = false;
        PlayerSettings.Android.renderOutsideSafeArea = true;
        PlayerSettings.runInBackground = true;

        try
        {
            PlayerSettings.Android.resizeableActivity = true;
            PlayerSettings.Android.defaultWindowWidth = 1600;
            PlayerSettings.Android.defaultWindowHeight = 900;
            PlayerSettings.Android.minimumWindowWidth = 480;
            PlayerSettings.Android.minimumWindowHeight = 270;
        }
        catch (Exception e) { Debug.LogWarning("[HDMRBuildNuvc] window size: " + e.Message); }

        // keep our blit shaders — nothing in a scene references them by asset
        var g = GraphicsSettings.GetGraphicsSettings();
        var so = new SerializedObject(g);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        bool anyAdded = false;
        foreach (var name in new[] { "Hdmr/NV12", "Hdmr/NuvcStereo", "Hdmr/NuvcTestSBS" })
        {
            bool has = false;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var sh = arr.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (sh != null && sh.name == name) { has = true; break; }
            }
            if (has) continue;
            var shader = Shader.Find(name);
            if (shader != null)
            {
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = shader;
                anyAdded = true;
                Debug.Log("[HDMRBuildNuvc] added " + name + " to always-included shaders");
            }
            else Debug.LogWarning("[HDMRBuildNuvc] Shader.Find(" + name + ") == null");
        }
        if (anyAdded) so.ApplyModifiedProperties();

        Debug.Log("[HDMRBuildNuvc] player settings configured");
    }

    // The XR nuvc build adds OpenXR project-wide (loader + init-on-start). Undo
    // it for THIS build so the flat 2D APK never initialises XR / goes immersive.
    static void DisableXR()
    {
        try
        {
            var perBTType = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
            var genType   = FindType("UnityEngine.XR.Management.XRGeneralSettings");
            var mdStore   = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
            if (perBTType == null || genType == null || mdStore == null) { Debug.Log("[HDMRBuildNuvc] XR mgmt not present — nothing to disable"); return; }

            string key = (string)genType.GetField("k_SettingsKey", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var tryGet = typeof(EditorBuildSettings).GetMethods()
                .First(m => m.Name == "TryGetConfigObject" && m.IsGenericMethod).MakeGenericMethod(perBTType);
            var args = new object[] { key, null };
            if (!(bool)tryGet.Invoke(null, args) || args[1] == null) { Debug.Log("[HDMRBuildNuvc] no XRGeneralSettings — ok"); return; }
            var perBT = args[1];

            var getMgr = perBTType.GetMethod("ManagerSettingsForBuildTarget");
            var mgr = perBTType.GetMethod("HasManagerSettingsForBuildTarget")?.Invoke(perBT, new object[] { BuildTargetGroup.Android }) is bool has && has
                ? getMgr.Invoke(perBT, new object[] { BuildTargetGroup.Android }) : null;
            if (mgr != null)
            {
                var rm = mdStore.GetMethod("RemoveLoader", BindingFlags.Public | BindingFlags.Static);
                bool ok = (bool)rm.Invoke(null, new object[] { mgr, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android });
                Debug.Log("[HDMRBuildNuvc] RemoveLoader(OpenXR, Android) -> " + ok);
            }
            var genForBT = perBTType.GetMethod("SettingsForBuildTarget").Invoke(perBT, new object[] { BuildTargetGroup.Android });
            var initProp = genType.GetProperty("InitManagerOnStart");
            if (genForBT != null && initProp != null && initProp.CanWrite) initProp.SetValue(genForBT, false);
            EditorUtility.SetDirty((UnityEngine.Object)perBT);
            AssetDatabase.SaveAssets();
        }
        catch (Exception e) { Debug.LogWarning("[HDMRBuildNuvc] DisableXR: " + e.Message); }
    }

    static Type FindType(string full)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        { var t = a.GetType(full); if (t != null) return t; }
        return null;
    }

    static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        camGo.tag = "MainCamera";
        var cam = camGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        camGo.transform.position = new Vector3(0f, 0f, -2f);

        var rig = new GameObject("Nuvc");
        var capture = rig.AddComponent<NuvcCapture>();
        var ui = rig.AddComponent<NuvcUi>();
        ui.cap = capture;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[HDMRBuildNuvc] scene written -> " + ScenePath);
    }
}
