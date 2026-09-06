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
using Serenegiant.UVC;
using Hdmr;

/// <summary>
///  Node C build: HDMR as an immersive OpenXR app with a head-locked MONO
///  OVROverlay compositor quad (com.natis.hdmr.xr — side by side with b18).
///
///  Two steps (separate -executeMethod invocations, so a domain reload happens
///  between them):
///    1. HDMRBuildXR.SetupXR   — enable OpenXR loader + Meta feature, OVR fix-all
///    2. HDMRBuildXR.BuildXR   — build the APK
/// </summary>
public static class HDMRBuildXR
{
    const string ScenePath = "Assets/Scenes/HDMRXr.unity";
    const string Pkg = "com.natis.hdmr.xr";
    const string Product = "HDMR XR";

    // ---------------------------------------------------------------- step 1
    [MenuItem("HDMR/XR/1 · Setup XR")]
    public static void SetupXR()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        Step("OVRProjectSetup.FixAll", RunOvrProjectSetup);
        Step("EnableOpenXRLoader", () => EnableOpenXRLoader(BuildTargetGroup.Android));
        Step("EnableMetaOpenXRFeatures", () => EnableMetaOpenXRFeatures(BuildTargetGroup.Android));
        Step("EnablePassthrough", EnablePassthrough);
        Step("GenerateOvrManifest", GenerateOvrManifest);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[HDMRXR] SetupXR done — run HDMRBuildXR.BuildXR next.");
    }

    static void Step(string name, Action a)
    {
        try { a(); Debug.Log("[HDMRXR] step OK: " + name); }
        catch (Exception e) { Debug.LogWarning($"[HDMRXR] step FAILED: {name} -> {(e is TargetInvocationException tie ? tie.InnerException : e)}"); }
    }

    // ---------------------------------------------------------------- step 2
    [MenuItem("HDMR/XR/2 · Build XR APK")]
    public static void BuildXR()
    {
        try
        {
            ConfigurePlayerSettings();
            EnsureXrScene();

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HDMR-xr.apk");
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
            Debug.Log($"[HDMRXR] result={s.result} errors={s.totalErrors} size={s.totalSize} -> {apk}");
            if (s.result != BuildResult.Succeeded) { Debug.LogError("[HDMRXR] BUILD FAILED"); EditorApplication.Exit(1); }
            Debug.Log("[HDMRXR] BUILD OK -> " + apk);
        }
        catch (Exception e)
        {
            Debug.LogError("[HDMRXR] BuildXR exception: " + e);
            EditorApplication.Exit(2);
        }
    }

    // ---------------------------------------------------------------- XR wiring
    static void EnableOpenXRLoader(BuildTargetGroup group)
    {
        var perBt = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
        var mdStore = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
        var xrGeneral = FindType("UnityEngine.XR.Management.XRGeneralSettings");
        var xrManager = FindType("UnityEngine.XR.Management.XRManagerSettings");
        if (perBt == null || mdStore == null || xrGeneral == null || xrManager == null)
        { Debug.LogWarning("[HDMRXR] XR Management types missing"); return; }

        string key = (string)xrGeneral.GetField("k_SettingsKey", BindingFlags.Public | BindingFlags.Static).GetValue(null);

        UnityEngine.Object perBtObj = null;
        {
            var args = new object[] { key, null };
            typeof(EditorBuildSettings).GetMethod("TryGetConfigObject").MakeGenericMethod(perBt).Invoke(null, args);
            perBtObj = (UnityEngine.Object)args[1];
        }
        if (perBtObj == null)
        {
            perBtObj = ScriptableObject.CreateInstance(perBt);
            Directory.CreateDirectory("Assets/XR");
            AssetDatabase.CreateAsset(perBtObj, "Assets/XR/XRGeneralSettings.asset");
            EditorBuildSettings.AddConfigObject(key, perBtObj, true);
        }

        var settingsForBt = perBt.GetMethod("SettingsForBuildTarget");
        var setForBt = perBt.GetMethod("SetSettingsForBuildTarget");
        object gs = settingsForBt.Invoke(perBtObj, new object[] { group });
        if (gs == null)
        {
            gs = ScriptableObject.CreateInstance(xrGeneral);
            ((UnityEngine.Object)gs).name = group + " Settings";
            setForBt.Invoke(perBtObj, new object[] { group, gs });
            AssetDatabase.AddObjectToAsset((UnityEngine.Object)gs, perBtObj);
        }

        var mgrProp = xrGeneral.GetProperty("Manager", BindingFlags.Public | BindingFlags.Instance);
        object mgr = mgrProp.GetValue(gs);
        if (mgr == null)
        {
            mgr = ScriptableObject.CreateInstance(xrManager);
            ((UnityEngine.Object)mgr).name = "XR Manager";
            mgrProp.SetValue(gs, mgr);
            AssetDatabase.AddObjectToAsset((UnityEngine.Object)mgr, perBtObj);
        }

        var assign = mdStore.GetMethod("AssignLoader", BindingFlags.Public | BindingFlags.Static);
        bool ok = (bool)assign.Invoke(null, new object[] { mgr, "UnityEngine.XR.OpenXR.OpenXRLoader", group });
        Debug.Log("[HDMRXR] AssignLoader OpenXR = " + ok);
        EditorUtility.SetDirty(perBtObj);
        AssetDatabase.SaveAssets();
    }

    static void EnableMetaOpenXRFeatures(BuildTargetGroup group)
    {
        // clean stray duplicate folders from earlier runs
        foreach (var d in Directory.GetDirectories("Assets/XR").Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^Settings \d+$")))
        { AssetDatabase.DeleteAsset(d.Replace('\\', '/')); Debug.Log("[HDMRXR] removed stray " + d); }

        const string path = "Assets/XR/Settings/OpenXR Package Settings.asset";
        var all = AssetDatabase.LoadAllAssetsAtPath(path);
        if (all == null || all.Length == 0) { Debug.LogWarning("[HDMRXR] OpenXR Package Settings asset not found"); return; }

        // GLES3 build: only the feature that's actually required. Foveation /
        // subsampled layout / buffer discards are Vulkan-only and fail validation.
        string[] want =
        {
            "MetaQuestFeature",
            "MetaQuestTouchProControllerProfile", "OculusTouchControllerProfile",
        };
        // Vulkan-only features that must be OFF for a GLES3 build
        string[] vulkanOnly = { "MetaXRSubsampledLayout", "FoveatedRenderingFeature", "MetaXRSpaceWarp" };

        int on = 0, off = 0;
        foreach (var o in all)
        {
            if (o == null) continue;
            string n = o.name;                        // e.g. "MetaQuestFeature Android"
            if (!n.EndsWith(" " + group)) continue;
            var so = new SerializedObject(o);
            var p = so.FindProperty("m_enabled");
            if (p == null) continue;

            if (want.Any(w => n.StartsWith(w)) && !p.boolValue)
            { p.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); on++; Debug.Log("[HDMRXR] OpenXR feature ON: " + n); }
            else if (vulkanOnly.Any(w => n.StartsWith(w)) && p.boolValue)
            { p.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); off++; Debug.Log("[HDMRXR] OpenXR feature OFF (Vulkan-only): " + n); }
        }
        Debug.Log($"[HDMRXR] {on} on / {off} off for {group}");

        // Optimize Buffer Discards is Vulkan-only — turn it off for GLES
        Step("optimizeBufferDiscards off", () => {
            var t = FindType("UnityEngine.XR.OpenXR.OpenXRSettings");
            var m = t.GetMethod("GetSettingsForBuildTargetGroup", BindingFlags.Public | BindingFlags.Static);
            var s = m.Invoke(null, new object[] { group });
            var pr = t.GetProperty("optimizeBufferDiscards", BindingFlags.Public | BindingFlags.Instance);
            if (pr != null && pr.CanWrite) { pr.SetValue(s, false); EditorUtility.SetDirty((UnityEngine.Object)s); }
        });
        AssetDatabase.SaveAssets();
    }

    static void RunOvrProjectSetup()
    {
        var t = FindType("OVRProjectSetup");
        if (t == null) { Debug.LogWarning("[HDMRXR] OVRProjectSetup not found"); return; }
        var m = t.GetMethod("FixAll", new[] { typeof(BuildTargetGroup) })
             ?? t.GetMethod("FixAllAsync", new[] { typeof(BuildTargetGroup) });
        if (m == null) { Debug.LogWarning("[HDMRXR] OVRProjectSetup.FixAll* not found"); return; }
        var task = m.Invoke(null, new object[] { BuildTargetGroup.Android });
        if (task is System.Threading.Tasks.Task tk)
            for (int i = 0; i < 300 && !tk.IsCompleted; i++) System.Threading.Thread.Sleep(50);
        Debug.Log("[HDMRXR] OVRProjectSetup fix-all invoked");
    }

    static void EnablePassthrough()
    {
        var cfgType = FindType("OVRProjectConfig");
        if (cfgType == null) { Debug.LogWarning("[HDMRXR] OVRProjectConfig not found"); return; }
        object cfg = cfgType.GetProperty("CachedProjectConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (cfg == null) { Debug.LogWarning("[HDMRXR] CachedProjectConfig null"); return; }

        var prop = cfgType.GetProperty("insightPassthroughSupport", BindingFlags.Public | BindingFlags.Instance);
        object supported = Enum.Parse(prop.PropertyType, "Supported");   // FeatureSupport.Supported
        prop.SetValue(cfg, supported);

        cfgType.GetMethod("CommitProjectConfig", BindingFlags.Public | BindingFlags.Static)
               .Invoke(null, new[] { cfg });
        EditorUtility.SetDirty((UnityEngine.Object)cfg);
        AssetDatabase.SaveAssets();
        Debug.Log("[HDMRXR] passthrough support -> Supported");
    }

    static void GenerateOvrManifest()
    {
        var t = FindType("OVRManifestPreprocessor");
        if (t == null) { Debug.LogWarning("[HDMRXR] OVRManifestPreprocessor not found"); return; }
        var m = t.GetMethod("GenerateOrUpdateAndroidManifest", BindingFlags.Public | BindingFlags.Static)
             ?? t.GetMethod("GenerateManifestForSubmission", BindingFlags.Public | BindingFlags.Static);
        if (m == null) { Debug.LogWarning("[HDMRXR] manifest gen method not found"); return; }
        if (m.GetParameters().Length == 1) m.Invoke(null, new object[] { true });
        else m.Invoke(null, null);
        Debug.Log("[HDMRXR] OVR AndroidManifest generated");
    }

    // ---------------------------------------------------------------- player settings
    static void ConfigurePlayerSettings()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var android = NamedBuildTarget.Android;
        PlayerSettings.SetApplicationIdentifier(android, Pkg);
        PlayerSettings.productName = Product;
        PlayerSettings.companyName = "natis";

        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;   // OpenXR/Quest requires >=32
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

        // keep GLES3 — the UVC plugin's instrumented holder is the GLES one
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetMobileMTRendering(android, false);

        // VR app — XR Plugin Management drives the loader; no legacy VR flag in Unity 6
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.forceInternetPermission = false;
        PlayerSettings.Android.renderOutsideSafeArea = true;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.runInBackground = true;   // don't freeze scripts on focus loss
        QualitySettings.vSyncCount = 0;

        IncludeShader("Unlit/Texture");
        IncludeShader("Unlit/Color");

        Debug.Log("[HDMRXR] player settings configured -> " + Pkg);
    }

    // ---------------------------------------------------------------- scene
    static void EnsureXrScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // OVRCameraRig prefab
        GameObject rigGo = null;
        var rigGuid = AssetDatabase.FindAssets("OVRCameraRig t:Prefab").FirstOrDefault();
        if (rigGuid != null)
        {
            var path = AssetDatabase.GUIDToAssetPath(rigGuid);
            rigGo = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            Debug.Log("[HDMRXR] OVRCameraRig from " + path);
        }
        if (rigGo == null)
        {
            Debug.LogWarning("[HDMRXR] OVRCameraRig prefab not found — plain camera");
            rigGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            rigGo.tag = "MainCamera";
        }

        // passthrough: cameras clear to transparent, no skybox; add OVRPassthroughLayer
        foreach (var cam in rigGo.GetComponentsInChildren<Camera>(true))
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }
        var ptType = FindType("OVRPassthroughLayer");   // defaults: Underlay, Reconstructed, visible
        if (ptType != null) { rigGo.AddComponent(ptType); Debug.Log("[HDMRXR] OVRPassthroughLayer added"); }
        else Debug.LogWarning("[HDMRXR] OVRPassthroughLayer type not found");

        // OVRManager.isInsightPassthroughEnabled defaults false -> flip it
        var mgrType = FindType("OVRManager");
        var ovrMgr = mgrType != null ? rigGo.GetComponentInChildren(mgrType, true) : null;
        if (ovrMgr != null)
        {
            var so = new SerializedObject(ovrMgr);
            var p = so.FindProperty("isInsightPassthroughEnabled");
            if (p != null) { p.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); Debug.Log("[HDMRXR] OVRManager passthrough enabled"); }
            else Debug.LogWarning("[HDMRXR] isInsightPassthroughEnabled prop not found");
        }

        // UVC rig
        var uvc = new GameObject("UVC");
        var audio = uvc.AddComponent<AudioSource>();
        audio.playOnAwake = false; audio.spatialBlend = 0f;
        var mgr = uvc.AddComponent<UVCManager>();
        var drawer = uvc.AddComponent<HdmrQuadDrawer>();
        var ctrl = uvc.AddComponent<HdmrController>();
        var remote = uvc.AddComponent<HdmrRemote>();
        var test = uvc.AddComponent<HdmrSelfTest>();

        mgr.PreferH264 = false;
        mgr.DefaultWidth = 1280;
        mgr.DefaultHeight = 720;
        mgr.UVCDrawers = new Component[] { drawer };

        drawer.audioSource = audio;
        ctrl.manager = mgr;
        ctrl.drawer = drawer;
        test.ctrl = ctrl; test.drawer = drawer;
        remote.ctrl = ctrl; remote.selfTest = test;

        // the screen (passthrough-aware, damped follow, b18-style video quad)
        var screen = new GameObject("HdmrXrRig").AddComponent<HdmrXrRig>();
        screen.drawer = drawer;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[HDMRXR] scene written -> " + ScenePath);
    }

    static void IncludeShader(string name)
    {
        var sh = Shader.Find(name);
        if (sh == null) { Debug.LogWarning("[HDMRXR] shader not found: " + name); return; }
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0];
        var so = new SerializedObject(gs);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        for (int i = 0; i < list.arraySize; i++)
            if (list.GetArrayElementAtIndex(i).objectReferenceValue == sh) return;
        list.arraySize++;
        list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = sh;
        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log("[HDMRXR] always-include shader: " + name);
    }

    static void SetIf(SerializedObject so, string prop, object val)
    {
        var p = so.FindProperty(prop);
        if (p == null) return;
        if (val is bool b) p.boolValue = b;
        else if (val is int i) p.intValue = i;
        else if (val is float f) p.floatValue = f;
    }

    static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => { try { return a.GetType(name); } catch { return null; } })
        .FirstOrDefault(t => t != null);
}
