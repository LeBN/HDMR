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
///             -executeMethod HDMRBuildNuvcXR.BuildXR -logFile build.log
///
/// Immersive (OpenXR) build of nuvc: a head-locked video quad rendered per-eye
/// through Hdmr/NuvcStereoXR, so each eye actually gets its own un-distorted
/// half of the Switch-2 SBS feed. Separate package (com.natis.hdmr.nuvc.xr) so
/// the flat 2D calibration build stays intact.
/// </summary>
public static class HDMRBuildNuvcXR
{
    const string ScenePath = "Assets/Scenes/HDMRNuvcXR.unity";
    const string Pkg = "com.natis.hdmr.nuvc.xr";

    public static bool Active;

    [MenuItem("HDMR/Build nuvc XR APK")]
    public static void BuildXR()
    {
        Active = true;
        try
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            ConfigurePlayerSettings();
            ConfigureXR();
            EnsureScene();

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HDMR-nuvc-xr.apk");
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
            Debug.Log($"[HDMRBuildNuvcXR] result={s.result} errors={s.totalErrors} time={s.totalTime} out={apk}");
            if (s.result != BuildResult.Succeeded) { Debug.LogError("[HDMRBuildNuvcXR] BUILD FAILED"); EditorApplication.Exit(1); }
            Debug.Log("[HDMRBuildNuvcXR] BUILD OK -> " + apk);
        }
        catch (Exception e) { Debug.LogError("[HDMRBuildNuvcXR] exception: " + e); EditorApplication.Exit(2); }
        finally { Active = false; }
    }

    static void ConfigurePlayerSettings()
    {
        var android = NamedBuildTarget.Android;
        PlayerSettings.SetApplicationIdentifier(android, Pkg);
        PlayerSettings.productName = "HDMR nuvc XR";
        PlayerSettings.companyName = "natis";

        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);
        PlayerSettings.allowUnsafeCode = true;

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        // Vulkan first (Quest OpenXR prefers it), GLES3 fallback.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetMobileMTRendering(android, true);
        PlayerSettings.MTRendering = true;

        // stereo: single-pass instanced (the NuvcStereoXR shader is built for it)
        PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;

        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.forceInternetPermission = false;
        PlayerSettings.Android.renderOutsideSafeArea = true;
        PlayerSettings.runInBackground = true;
        try { PlayerSettings.Android.resizableWindow = false; } catch { }

        // colour space – linear is what Quest wants
        PlayerSettings.colorSpace = ColorSpace.Linear;

        // keep the blit shaders (nothing references them by asset)
        var g = GraphicsSettings.GetGraphicsSettings();
        var so = new SerializedObject(g);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        foreach (var name in new[] { "Hdmr/NV12", "Hdmr/NuvcStereoXR", "Hdmr/NuvcStereo", "Hdmr/NuvcTestSBS" })
        {
            bool has = false;
            for (int i = 0; i < arr.arraySize; i++)
                if ((arr.GetArrayElementAtIndex(i).objectReferenceValue as Shader)?.name == name) { has = true; break; }
            if (has) continue;
            var sh = Shader.Find(name);
            if (sh != null) { arr.InsertArrayElementAtIndex(arr.arraySize); arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh; }
        }
        so.ApplyModifiedProperties();
        Debug.Log("[HDMRBuildNuvcXR] player settings configured");
    }

    // Assign the OpenXR loader for Android and enable the Meta Quest feature,
    // all via reflection so this file compiles even before the packages resolve.
    static void ConfigureXR()
    {
        // 1. XRGeneralSettingsPerBuildTarget.GetOrCreate() -> AssignedSettings (XRManagerSettings for Android)
        var perBTType = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
        var mdStoreType = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
        if (perBTType == null || mdStoreType == null)
        {
            Debug.LogWarning("[HDMRBuildNuvcXR] XR Management editor types not found — is com.unity.xr.management resolved?");
            return;
        }
        var getOrCreate = perBTType.GetMethod("GetOrCreate", BindingFlags.Public | BindingFlags.Static);
        var perBT = getOrCreate.Invoke(null, null);
        var getSettingsForBT = perBTType.GetMethod("SettingsForBuildTarget", BindingFlags.Public | BindingFlags.Instance);
        var general = getSettingsForBT?.Invoke(perBT, new object[] { BuildTargetGroup.Android });
        if (general == null)
        {
            var createDefault = perBTType.GetMethod("GetOrCreate", BindingFlags.Public | BindingFlags.Static);
            // fall through — AssignLoader below can still work on the manager
        }
        var generalType = general?.GetType() ?? FindType("UnityEngine.XR.Management.XRGeneralSettings");
        object manager = null;
        if (general != null)
        {
            var mgrProp = generalType.GetProperty("Manager") ?? generalType.GetProperty("AssignedSettings");
            manager = mgrProp?.GetValue(general);
        }
        if (manager == null)
        {
            // create a fresh XRManagerSettings and stash it
            var xrMgrType = FindType("UnityEngine.XR.Management.XRManagerSettings");
            manager = ScriptableObject.CreateInstance(xrMgrType);
        }

        // 2. AssignLoader(manager, "UnityEngine.XR.OpenXR.OpenXRLoader", Android)
        var assign = mdStoreType.GetMethod("AssignLoader", BindingFlags.Public | BindingFlags.Static);
        bool ok = (bool)assign.Invoke(null, new object[] { manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android });
        Debug.Log("[HDMRBuildNuvcXR] AssignLoader(OpenXR, Android) -> " + ok);

        // persist the general settings (SetSettingsForBuildTarget)
        var setSettingsForBT = perBTType.GetMethod("SetSettingsForBuildTarget", BindingFlags.Public | BindingFlags.Instance);
        if (setSettingsForBT != null && general != null)
            setSettingsForBT.Invoke(perBT, new object[] { BuildTargetGroup.Android, general });
        EditorUtility.SetDirty((UnityEngine.Object)perBT);
        AssetDatabase.SaveAssets();

        // 3. Refresh + enable OpenXR features
        var fhType = FindType("UnityEditor.XR.OpenXR.Features.FeatureHelpers");
        if (fhType != null)
        {
            fhType.GetMethod("RefreshFeatures", BindingFlags.Public | BindingFlags.Static)
                  ?.Invoke(null, new object[] { BuildTargetGroup.Android });

            EnableFeature(fhType, "com.unity.openxr.feature.metaquest");           // Meta Quest Support
            EnableFeature(fhType, "com.unity.openxr.feature.input.metaquestplus"); // Touch Plus controller
            AssetDatabase.SaveAssets();
            Debug.Log("[HDMRBuildNuvcXR] OpenXR Meta Quest features enabled");
        }
        else Debug.LogWarning("[HDMRBuildNuvcXR] FeatureHelpers not found — is com.unity.xr.openxr resolved?");
    }

    static void EnableFeature(Type featureHelpers, string featureId)
    {
        try
        {
            var get = featureHelpers.GetMethod("GetFeatureWithIdForBuildTarget", BindingFlags.Public | BindingFlags.Static)
                   ?? featureHelpers.GetMethod("GetFeatureWithIdForActiveBuildTarget", BindingFlags.Public | BindingFlags.Static);
            object feature = get.GetParameters().Length == 2
                ? get.Invoke(null, new object[] { BuildTargetGroup.Android, featureId })
                : get.Invoke(null, new object[] { featureId });
            if (feature == null) { Debug.LogWarning("[HDMRBuildNuvcXR] feature not found: " + featureId); return; }
            var enabledProp = feature.GetType().GetProperty("enabled");
            enabledProp.SetValue(feature, true);
            EditorUtility.SetDirty((UnityEngine.Object)feature);
        }
        catch (Exception e) { Debug.LogWarning($"[HDMRBuildNuvcXR] EnableFeature({featureId}): {e.Message}"); }
    }

    static Type FindType(string full)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(full);
            if (t != null) return t;
        }
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
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 100f;
        // head tracking
        var tpdType = FindType("UnityEngine.SpatialTracking.TrackedPoseDriver");
        if (tpdType != null) camGo.AddComponent(tpdType);
        else Debug.LogWarning("[HDMRBuildNuvcXR] TrackedPoseDriver not found — head tracking may be missing");

        var rig = new GameObject("Nuvc");
        var capture = rig.AddComponent<NuvcCapture>();
        var xr = rig.AddComponent<NuvcXrRig>();
        xr.head = camGo.transform;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[HDMRBuildNuvcXR] scene written -> " + ScenePath);
    }
}
