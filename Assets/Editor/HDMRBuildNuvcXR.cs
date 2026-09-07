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

    // Assign the OpenXR loader for Android + enable the Meta Quest feature.
    // Reflection throughout (no asmdef needed on this Editor script), using only
    // public APIs: EditorBuildSettings config object + public instance methods.
    static void ConfigureXR()
    {
        var perBTType = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
        var genType   = FindType("UnityEngine.XR.Management.XRGeneralSettings");
        var mdStore   = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
        if (perBTType == null || genType == null || mdStore == null)
        { Debug.LogError("[HDMRBuildNuvcXR] XR Management types missing — packages not resolved?"); return; }

        string key = (string)genType.GetField("k_SettingsKey", BindingFlags.Public | BindingFlags.Static).GetValue(null);

        // EditorBuildSettings.TryGetConfigObject<T>(string, out T)
        var tryGet = typeof(EditorBuildSettings).GetMethods()
            .First(m => m.Name == "TryGetConfigObject" && m.IsGenericMethod)
            .MakeGenericMethod(perBTType);
        var args = new object[] { key, null };
        bool have = (bool)tryGet.Invoke(null, args);
        var perBT = args[1];

        if (perBT == null || perBT.Equals(null))
        {
            perBT = ScriptableObject.CreateInstance(perBTType);
            Directory.CreateDirectory("Assets/XR");
            AssetDatabase.CreateAsset((UnityEngine.Object)perBT, "Assets/XR/XRGeneralSettings.asset");
            typeof(EditorBuildSettings).GetMethod("AddConfigObject")
                .Invoke(null, new object[] { key, (UnityEngine.Object)perBT, true });
        }

        var hasMgr = perBTType.GetMethod("HasManagerSettingsForBuildTarget");
        var createMgr = perBTType.GetMethod("CreateDefaultManagerSettingsForBuildTarget");
        var getMgr = perBTType.GetMethod("ManagerSettingsForBuildTarget");
        if (!(bool)hasMgr.Invoke(perBT, new object[] { BuildTargetGroup.Android }))
            createMgr.Invoke(perBT, new object[] { BuildTargetGroup.Android });
        var manager = getMgr.Invoke(perBT, new object[] { BuildTargetGroup.Android });
        if (manager == null) { Debug.LogError("[HDMRBuildNuvcXR] no XRManagerSettings for Android"); return; }

        var assign = mdStore.GetMethod("AssignLoader", BindingFlags.Public | BindingFlags.Static);
        bool ok = (bool)assign.Invoke(null, new object[] { manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android });
        Debug.Log("[HDMRBuildNuvcXR] AssignLoader(OpenXR, Android) -> " + ok);

        // keep the manager init-on-start so the runtime brings XR up by itself
        var initProp = genType.GetProperty("InitManagerOnStart");
        var genForBT = perBTType.GetMethod("SettingsForBuildTarget").Invoke(perBT, new object[] { BuildTargetGroup.Android });
        if (genForBT != null && initProp != null && initProp.CanWrite) initProp.SetValue(genForBT, true);

        EditorUtility.SetDirty((UnityEngine.Object)perBT);
        AssetDatabase.SaveAssets();

        // OpenXR features
        var fh = FindType("UnityEditor.XR.OpenXR.Features.FeatureHelpers");
        if (fh == null) { Debug.LogError("[HDMRBuildNuvcXR] FeatureHelpers missing"); return; }
        fh.GetMethod("RefreshFeatures", BindingFlags.Public | BindingFlags.Static)
          .Invoke(null, new object[] { BuildTargetGroup.Android });
        var getFeat = fh.GetMethod("GetFeatureWithIdForBuildTarget", BindingFlags.Public | BindingFlags.Static);
        // only Meta Quest Support — NO controller interaction profile (passive
        // viewer, no input; a profile makes Horizon OS demand a live controller).
        foreach (var fid in new[] { "com.unity.openxr.feature.metaquest" })
        {
            var feat = getFeat.Invoke(null, new object[] { BuildTargetGroup.Android, fid });
            if (feat == null) { Debug.LogWarning("[HDMRBuildNuvcXR] feature not found: " + fid); continue; }
            feat.GetType().GetProperty("enabled").SetValue(feat, true);
            EditorUtility.SetDirty((UnityEngine.Object)feat);
        }
        // make sure a controller profile enabled by an earlier run is off
        foreach (var fid in new[] { "com.unity.openxr.feature.input.metaquestplus",
                                    "com.unity.openxr.feature.input.metaquestpro" })
        {
            var feat = getFeat.Invoke(null, new object[] { BuildTargetGroup.Android, fid });
            if (feat == null) continue;
            feat.GetType().GetProperty("enabled").SetValue(feat, false);
            EditorUtility.SetDirty((UnityEngine.Object)feat);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[HDMRBuildNuvcXR] OpenXR + Meta Quest features enabled for Android");
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

        // XR rig root at floor/eye origin
        var rigRoot = new GameObject("XR Rig");

        var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(rigRoot.transform, false);
        var cam = camGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 100f;

        // head tracking — legacy SpatialTracking.TrackedPoseDriver (from
        // com.unity.xr.legacyinputhelpers), pose source = centre eye.
        var tpdType = FindType("UnityEngine.SpatialTracking.TrackedPoseDriver");
        if (tpdType != null)
        {
            var tpd = camGo.AddComponent(tpdType);
            try
            {
                // SetPoseSource(DeviceType.GenericXRDevice=0, TrackedPose.Center=2)
                var devEnum = FindType("UnityEngine.SpatialTracking.TrackedPoseDriver+DeviceType");
                var poseEnum = FindType("UnityEngine.SpatialTracking.TrackedPoseDriver+TrackedPose");
                var setPose = tpdType.GetMethod("SetPoseSource");
                if (setPose != null && devEnum != null && poseEnum != null)
                    setPose.Invoke(tpd, new object[] {
                        System.Enum.ToObject(devEnum, 0),
                        System.Enum.ToObject(poseEnum, 2) });
            }
            catch (Exception e) { Debug.LogWarning("[HDMRBuildNuvcXR] TPD config: " + e.Message); }
        }
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
