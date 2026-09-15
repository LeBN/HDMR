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
using Oculus.Interaction.Input;

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
            AddInteractionAndUI();
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
        try { PlayerSettings.Android.resizeableActivity = false; } catch { }
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

    // Step 1 of porting the HTML prototype's UI: the interaction foundation
    // (ray + poke, hands + controllers) and a bare Canvas placeholder to
    // point it at — verified as "compiles, builds, boots clean on-device",
    // NOT as "looks/feels right", which needs the user in-headset. The
    // actual tile grid / wizard / mode-screen content from the prototype is
    // a separate, later pass on top of this.
    //
    // Prefabs verified against Meta's current docs (2026-09-15, via
    // developers.meta.com — metavr MCP was unavailable this session) rather
    // than assumed from memory:
    //   - OVRComprehensiveInteractionRig, child of OVRCameraRig — the
    //     current (non-Legacy) all-in-one ray/poke/grab rig.
    //   - EmptyUIBackplateWithCanvas (Meta XR Interaction SDK's own sample
    //     content, ships un-gated under Runtime/Sample/) — a Canvas already
    //     wired for ray+poke pointer events, standing in for the prototype's
    //     "deck" panel until real tile content replaces it.
    static void AddInteractionAndUI()
    {
        var rigGo = GameObject.Find("OVRCameraRig");
        if (rigGo == null) { Debug.LogError("[HDMRBuildHome] OVRCameraRig not found in scene — EnsureScene ran?"); return; }

        var interactionPrefab = LoadUniquePrefab("OVRComprehensiveInteractionRig");
        if (interactionPrefab != null)
        {
            var interactionGo = (GameObject)PrefabUtility.InstantiatePrefab(interactionPrefab, rigGo.transform);
            interactionGo.name = "OVRComprehensiveInteractionRig";
            interactionGo.transform.localPosition = Vector3.zero;
            interactionGo.transform.localRotation = Quaternion.identity;

            // The real "Add OVR Comprehensive Interaction Rig" wizard
            // (OVRComprehensiveInteractionRigWizard.cs, internal — not
            // callable from here) unpacks this prefab instance completely
            // before wiring it (its MakeInteractionRigEditable step).
            PrefabUtility.UnpackPrefabInstance(interactionGo, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            // The prefab ships with every component's _cameraRigRef field
            // empty (no OVRCameraRigRef of its own) — the wizard fills it
            // via an internal OnObjectCreated hook we can't call from here.
            // Reproduced via public API only: add one OVRCameraRigRef,
            // inject it with this OVRCameraRig, fill every component's
            // empty _cameraRigRef with it.
            var ovrCameraRig = rigGo.GetComponent<OVRCameraRig>();
            var rigRef = interactionGo.AddComponent<OVRCameraRigRef>();
            rigRef.InjectInteractionOVRCameraRig(ovrCameraRig);
            int wired = 0;
            foreach (var comp in interactionGo.GetComponentsInChildren<Component>(true))
            {
                if (comp == null || comp == rigRef) continue;
                var so = new SerializedObject(comp);
                var prop = so.FindProperty("_cameraRigRef");
                if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference && prop.objectReferenceValue == null)
                {
                    prop.objectReferenceValue = rigRef;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    wired++;
                }
            }

            // KNOWN ISSUE (2026-09-15, not yet fixed): even with the above
            // — confirmed via SerializedObject that every _cameraRigRef is
            // correctly wired — the hand-tracking path still throws a
            // continuous (non-fatal) NullReferenceException on-device:
            // TrackingToWorldTransformerOVR.get_Transform() ->
            // Oculus.Interaction.Input.Hand.get_Scale() ->
            // ShadowHandExtensions.FromHandRoot/FromHand ->
            // TouchHandGrabInteractor.DoPreprocess(). Tried and ruled out:
            // (a) the unpack above, (b) confirming the reference wiring
            // itself is correct at the SerializedObject level. Ray/poke via
            // controllers is unaffected (the exception is specific to the
            // hand-tracking interactor stack) and the app stays stable —
            // becomes the foreground VR app, 72fps, no crash. Root-causing
            // further needs interactive Play Mode debugging (step through
            // Awake/Start ordering, or diff against a rig actually built by
            // the real Editor wizard) — not something to keep guessing at
            // headlessly. Left in place since it doesn't block anything;
            // revisit before shipping hand-tracking interaction.
            Debug.Log($"[HDMRBuildHome] OVRComprehensiveInteractionRig added under OVRCameraRig, {wired} _cameraRigRef wired (hand-tracking NRE known issue, see comment)");
        }

        var canvasPrefab = LoadUniquePrefab("EmptyUIBackplateWithCanvas");
        if (canvasPrefab != null)
        {
            var canvasGo = (GameObject)PrefabUtility.InstantiatePrefab(canvasPrefab);
            canvasGo.name = "HomeCanvas (placeholder)";
            // Rough placeholder placement, facing back toward the rig origin
            // — not tuned, just enough to exist for a ray to reach once
            // someone's actually wearing the headset. Real placement is a
            // feel/UX call, not something to guess headless.
            canvasGo.transform.position = new Vector3(0f, 1.4f, 1.6f);
            canvasGo.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            Debug.Log("[HDMRBuildHome] EmptyUIBackplateWithCanvas placeholder added");

            AddHomeTiles(canvasGo);
            AddTabbar(canvasGo);
        }

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
    }

    // Port step 2: the prototype's two primary tiles ("Lancer le mode
    // Stéréo VR" / "Calibrage stéréo" — see research/3ds-ui/nav-schema.md)
    // as real, ray/poke-interactable buttons. Copy only for now — the
    // prototype's feel (pop/lift/idle animation, cursor highlight, sound)
    // is a later pass once this basic layout is confirmed working
    // in-headset; onClick is a placeholder log, not real navigation yet.
    // From the prototype's TILES array (hdmr-home.html) / nav-schema.md.
    // Same button prefab for every tile in this pass — the prototype's
    // visual distinction (primary vs. secondary size, "wide" MR/Cinéma,
    // the quieter/inverted profile style, the empty-profile dashed state)
    // is layout/skin work for a later pass once this structural port is
    // confirmed working in-headset. Getting every tile PRESENT and
    // CLICKABLE first, per the user's "continue le portage, on testera à
    // la fin" — not stopping to polish each row before the next exists.
    static readonly (string label, string key)[] PrimaryTiles =
    {
        ("Lancer le mode\nStéréo VR", "vr"),
        ("Calibrage\nstéréo",         "cal"),
    };
    static readonly (string label, string key)[] SecondaryTiles =
    {
        ("Mode MR",        "mr"),
        ("Cinéma",         "cine"),
        ("Profil 1",       "profile0"),
        ("Nouveau profil", "profile1-new"),
        ("Étalonnage",     "etal"),
    };

    static void AddHomeTiles(GameObject canvasGo)
    {
        var canvasRoot = canvasGo.transform.Find("CanvasRoot");
        if (canvasRoot == null)
        {
            // Fallback in case the sample's internal naming changes —
            // still find something Canvas-shaped rather than silently
            // no-op'ing.
            var canvas = canvasGo.GetComponentInChildren<Canvas>(true);
            canvasRoot = canvas != null ? canvas.transform : null;
        }
        if (canvasRoot == null) { Debug.LogError("[HDMRBuildHome] CanvasRoot not found in canvas prefab"); return; }

        var tileButtonPrefab = LoadUniquePrefab("TextTileButton_IconAndLabel_Regular_UnityUIButton");
        if (tileButtonPrefab == null) return;

        int n1 = AddTileRow(canvasRoot, tileButtonPrefab, PrimaryTiles, y: 80f, tileW: 180f, gap: 20f);
        int n2 = AddTileRow(canvasRoot, tileButtonPrefab, SecondaryTiles, y: -100f, tileW: 100f, gap: 14f);
        Debug.Log($"[HDMRBuildHome] tiles added under {canvasRoot.name}: {n1} primary, {n2} secondary");
    }

    // Prototype's bottom bar: Profils | Accueil | Paramètres. Same
    // placeholder-button approach as the tiles — real 3DS-style dividers,
    // centring and the "flush with the bounding box" look (per the user's
    // explicit description this session) are layout/skin, not structure.
    static readonly (string label, string key)[] TabbarItems =
    {
        ("Profils",    "profils"),
        ("Accueil",    "accueil"),
        ("Paramètres", "parametres"),
    };

    static void AddTabbar(GameObject canvasGo)
    {
        var canvasRoot = canvasGo.transform.Find("CanvasRoot");
        if (canvasRoot == null) { Debug.LogError("[HDMRBuildHome] CanvasRoot not found for tabbar"); return; }
        var tileButtonPrefab = LoadUniquePrefab("TextTileButton_IconAndLabel_Regular_UnityUIButton");
        if (tileButtonPrefab == null) return;

        int n = AddTileRow(canvasRoot, tileButtonPrefab, TabbarItems, y: -240f, tileW: 130f, gap: 10f);
        Debug.Log($"[HDMRBuildHome] tabbar added under {canvasRoot.name}: {n} items");
    }

    static int AddTileRow(Transform parent, GameObject tileButtonPrefab, (string label, string key)[] tiles, float y, float tileW, float gap)
    {
        float startX = -(tiles.Length * tileW + (tiles.Length - 1) * gap) / 2f + tileW / 2f;
        for (int i = 0; i < tiles.Length; i++)
        {
            var (label, key) = tiles[i];
            var tileGo = (GameObject)PrefabUtility.InstantiatePrefab(tileButtonPrefab, parent);
            tileGo.name = "Tile_" + key;
            var rt = tileGo.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchoredPosition = new Vector2(startX + i * (tileW + gap), y);
                rt.sizeDelta = new Vector2(tileW, tileW);
            }

            var label3d = tileGo.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (label3d != null) label3d.text = label;
            else Debug.LogWarning($"[HDMRBuildHome] no TMP_Text found on tile '{key}' — prefab structure may have changed");

            var btn = tileGo.GetComponentInChildren<UnityEngine.UI.Button>(true);
            if (btn != null)
            {
                string capturedKey = key; // local copy for the closure — foreach/for pitfall
                UnityEditor.Events.UnityEventTools.AddStringPersistentListener(
                    btn.onClick, LogTileClickTarget, capturedKey);
            }
            else Debug.LogWarning($"[HDMRBuildHome] no Button found on tile '{key}' — prefab structure may have changed");
        }
        return tiles.Length;
    }

    // Placeholder click target, wired as a real persistent UnityEvent
    // listener (survives into the build, not just an Editor-only closure)
    // so the button genuinely does something observable on-device before
    // real navigation exists.
    public static void LogTileClickTarget(string key) => Debug.Log("[HDMRBuildHome] tile clicked: " + key);

    static GameObject LoadUniquePrefab(string exactName)
    {
        var guids = AssetDatabase.FindAssets(exactName + " t:Prefab");
        if (guids.Length == 0) { Debug.LogError($"[HDMRBuildHome] prefab '{exactName}' not found — package missing/not resolved?"); return null; }
        string path = null;
        foreach (var g in guids)
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            if (Path.GetFileNameWithoutExtension(p) == exactName) { path = p; break; }
        }
        if (path == null) path = AssetDatabase.GUIDToAssetPath(guids[0]); // fall back to best guess, still logged below
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) Debug.LogError($"[HDMRBuildHome] failed to load prefab at {path}");
        else Debug.Log($"[HDMRBuildHome] resolved '{exactName}' -> {path}");
        return prefab;
    }
}
