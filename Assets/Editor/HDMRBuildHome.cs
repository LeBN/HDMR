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

/// <summary>
///   Unity.exe -batchmode -quit -projectPath HDMR -buildTarget Android \
///             -executeMethod HDMRBuildHome.Build -logFile build.log
///
/// First scaffold of the "HDMR Home" build (com.natis.hdmr.home) — the
/// eventual replacement launcher UI (3DS-style, see research/3ds-ui/) that
/// the nuvc/nuvc.xr builds feed into. This pass only gets a real
/// OVRCameraRig-based scene compiling and packaging correctly with the Meta
/// XR Core/Interaction SDKs now installed; it does NOT yet port the
/// prototype's UI, ray/poke interaction, or the live OVROverlay capture feed
/// — those are separate, larger passes once this baseline is confirmed to
/// build and boot.
///
/// Unlike nuvc/nuvc.xr, this build WANTS OVRGradleGeneration's real VR
/// manifest stamping — see MetaManifestCleanup.cs, which explicitly skips
/// this package.
/// </summary>
public static class HDMRBuildHome
{
    const string ScenePath = "Assets/Scenes/HDMRHome.unity";
    const string Pkg = "com.natis.hdmr.home";

    public static bool Active;

    [MenuItem("HDMR/Build Home APK (scaffold)")]
    public static void Build()
    {
        Active = true;
        try
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            ConfigurePlayerSettings();
            ConfigureXR();
            ConfigureOvrProjectConfig();
            EnsureScene();
            GenerateManifest();

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HDMR-home.apk");
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
            Debug.Log($"[HDMRBuildHome] result={s.result} errors={s.totalErrors} time={s.totalTime} out={apk}");
            if (s.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            { Debug.LogError("[HDMRBuildHome] BUILD FAILED"); EditorApplication.Exit(1); }
            Debug.Log("[HDMRBuildHome] BUILD OK -> " + apk);
        }
        catch (Exception e) { Debug.LogError("[HDMRBuildHome] exception: " + e); EditorApplication.Exit(2); }
        finally { Active = false; }
    }

    static void ConfigurePlayerSettings()
    {
        var android = NamedBuildTarget.Android;
        PlayerSettings.SetApplicationIdentifier(android, Pkg);
        PlayerSettings.productName = "HDMR Home";
        PlayerSettings.companyName = "natis";

        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);
        PlayerSettings.allowUnsafeCode = true;

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetMobileMTRendering(android, true);
        PlayerSettings.MTRendering = true;
        PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;

        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.forceInternetPermission = false;
        PlayerSettings.Android.renderOutsideSafeArea = true;
        PlayerSettings.runInBackground = true;
        try { PlayerSettings.Android.resizableWindow = false; } catch { }
        PlayerSettings.colorSpace = ColorSpace.Gamma;

        Debug.Log("[HDMRBuildHome] player settings configured");
    }

    // Same OpenXR-loader plumbing as HDMRBuildNuvcXR — reflection so this file
    // has no hard compile-time dependency on the XR Management assemblies
    // beyond what's already proven working there.
    static void ConfigureXR()
    {
        var perBTType = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
        var genType   = FindType("UnityEngine.XR.Management.XRGeneralSettings");
        var mdStore   = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
        if (perBTType == null || genType == null || mdStore == null)
        { Debug.LogError("[HDMRBuildHome] XR Management types missing — packages not resolved?"); return; }

        string key = (string)genType.GetField("k_SettingsKey", BindingFlags.Public | BindingFlags.Static).GetValue(null);

        var tryGet = typeof(EditorBuildSettings).GetMethods()
            .First(m => m.Name == "TryGetConfigObject" && m.IsGenericMethod)
            .MakeGenericMethod(perBTType);
        var args = new object[] { key, null };
        tryGet.Invoke(null, args);
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
        if (manager == null) { Debug.LogError("[HDMRBuildHome] no XRManagerSettings for Android"); return; }

        var assign = mdStore.GetMethod("AssignLoader", BindingFlags.Public | BindingFlags.Static);
        bool ok = (bool)assign.Invoke(null, new object[] { manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android });
        Debug.Log("[HDMRBuildHome] AssignLoader(OpenXR, Android) -> " + ok);

        var initProp = genType.GetProperty("InitManagerOnStart");
        var genForBT = perBTType.GetMethod("SettingsForBuildTarget").Invoke(perBT, new object[] { BuildTargetGroup.Android });
        if (genForBT != null && initProp != null && initProp.CanWrite) initProp.SetValue(genForBT, true);

        EditorUtility.SetDirty((UnityEngine.Object)perBT);
        AssetDatabase.SaveAssets();

        var fh = FindType("UnityEditor.XR.OpenXR.Features.FeatureHelpers");
        if (fh == null) { Debug.LogError("[HDMRBuildHome] FeatureHelpers missing"); return; }
        fh.GetMethod("RefreshFeatures", BindingFlags.Public | BindingFlags.Static)
          .Invoke(null, new object[] { BuildTargetGroup.Android });
        var getFeat = fh.GetMethod("GetFeatureWithIdForBuildTarget", BindingFlags.Public | BindingFlags.Static);
        var mq = getFeat.Invoke(null, new object[] { BuildTargetGroup.Android, "com.unity.openxr.feature.metaquest" });
        if (mq == null) Debug.LogWarning("[HDMRBuildHome] Meta Quest Support feature not found");
        else { mq.GetType().GetProperty("enabled").SetValue(mq, true); EditorUtility.SetDirty((UnityEngine.Object)mq); }
        AssetDatabase.SaveAssets();
    }

    // Direct reference — unlike ConfigureXR, this project now has a real
    // compile-time dependency on com.meta.xr.sdk.core, so no reflection
    // needed here. Verified against the installed package source
    // (Editor/OVRProjectConfig.cs): CachedProjectConfig / CommitProjectConfig
    // is the real, current API — not OVRProjectConfig.GetProjectConfig(),
    // which doesn't exist in this version.
    static void ConfigureOvrProjectConfig()
    {
        var cfg = OVRProjectConfig.CachedProjectConfig;
        foreach (var dt in new[] {
            OVRProjectConfig.DeviceType.Quest2,
            OVRProjectConfig.DeviceType.Quest3,
            OVRProjectConfig.DeviceType.Quest3S,
        })
        {
            if (!cfg.targetDeviceTypes.Contains(dt)) cfg.targetDeviceTypes.Add(dt);
        }
        // Interaction SDK's ray/poke needs hand tracking available (not
        // required) alongside controllers — this build takes no input yet,
        // but the manifest bit costs nothing to have right from the start.
        cfg.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
        OVRProjectConfig.CommitProjectConfig(cfg);
        Debug.Log("[HDMRBuildHome] OVRProjectConfig committed: devices=" +
            string.Join(",", cfg.targetDeviceTypes) + " handTracking=" + cfg.handTrackingSupport);
    }

    static void GenerateManifest()
    {
        OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(true);
        Debug.Log("[HDMRBuildHome] AndroidManifest generated via OVRManifestPreprocessor");
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

    // Minimal placeholder scene: OVRCameraRig only, no UI/interaction/overlay
    // yet — this pass is strictly "does the Meta XR stack compile, resolve,
    // and package". Found by asset search rather than a hardcoded
    // "Packages/com.meta.xr.sdk.core/..." path, so it keeps working if the
    // package's internal layout changes between versions.
    static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var guids = AssetDatabase.FindAssets("OVRCameraRig t:Prefab");
        if (guids.Length == 0)
        { Debug.LogError("[HDMRBuildHome] OVRCameraRig prefab not found — is com.meta.xr.sdk.core resolved?"); return; }
        string prefabPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        { Debug.LogError("[HDMRBuildHome] failed to load OVRCameraRig prefab at " + prefabPath); return; }

        var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        rig.name = "OVRCameraRig";
        rig.transform.position = Vector3.zero;

        var light = new GameObject("Directional Light", typeof(Light));
        light.GetComponent<Light>().type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50, -30, 0);

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        Debug.Log("[HDMRBuildHome] scene written -> " + ScenePath + " (prefab: " + prefabPath + ")");
    }
}
