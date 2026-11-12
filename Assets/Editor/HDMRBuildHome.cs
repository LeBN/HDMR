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

    // ADDED 2026-09-17, replacing the ScriptableObject-based config the
    // user correctly pushed back on ("le standard c'est d'avoir les
    // paramètres sur l'objet"). The constraint that shaped this: EnsureScene()
    // calls EditorSceneManager.NewScene(...) on every rebuild -- the WHOLE
    // scene is wiped and regenerated from code, which would silently
    // destroy any value living on a scene GameObject's component, same as
    // it silently destroyed a manual backplate resize earlier today (never
    // checked for that before wiping -- a real mistake, not a hypothetical
    // one). This captures the tile backplate's HDMRBackplateStyle values
    // from the CURRENTLY OPEN scene right before that wipe, so edits made
    // directly on the backplate object's own Inspector survive a rebuild,
    // instead of requiring a separate asset immune to the wipe.
    struct CapturedBackplateStyle { public bool found; public Color top, bottom; public float radius; public bool useManualSize; public Vector2 manualSize; }
    static CapturedBackplateStyle _capturedTileBackplateStyle;

    static void CaptureTileBackplateStyleBeforeWipe()
    {
        var existing = GameObject.Find("HomeCanvas (placeholder)/CanvasRoot/UIBackplate");
        var style = existing != null ? existing.GetComponent<Hdmr.Home.HDMRBackplateStyle>() : null;
        _capturedTileBackplateStyle = style != null
            ? new CapturedBackplateStyle { found = true, top = style.topColor, bottom = style.bottomColor, radius = style.cornerRadius, useManualSize = style.useManualSize, manualSize = style.manualSize }
            : new CapturedBackplateStyle { found = false };
    }

    [MenuItem("HDMR/Build Home APK (scaffold)")]
    public static void Build()
    {
        CaptureTileBackplateStyleBeforeWipe();
        // Guard added 2026-09-16 night cycle 2, after a real incident: this
        // was invoked (via execute_menu_item) while a separate Unity MCP
        // workflow agent had the Editor in Play Mode. EnsureScene()'s
        // EditorSceneManager.NewScene() throws InvalidOperationException in
        // that state, which used to fall straight into the catch below and
        // force-quit the whole interactive Editor via EditorApplication.
        // Exit(2) — not just abort the build. A no-op refusal here is much
        // cheaper than a lost Editor session.
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[HDMRBuildHome] Build() called while Editor is in Play Mode — refusing (stop Play Mode first).");
            return;
        }
        Active = true;
        try
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            EnsureTMPEssentials();
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
        catch (Exception e)
        {
            Debug.LogError("[HDMRBuildHome] exception: " + e);
            // Only force-quit in real batch-mode builds (CI/CLI), where
            // there's no interactive session to lose and a hung batch
            // process needs to exit non-zero. In an interactive Editor
            // (the case that actually bit us tonight), Exit() here kills
            // the whole session over a recoverable build-script exception —
            // log and return instead, leaving the Editor usable.
            if (Application.isBatchMode) EditorApplication.Exit(2);
        }
        finally { Active = false; }
    }

    // Fast iteration entry point — rebuilds just the scene (rig + UI),
    // no Gradle/APK step. Added 2026-09-16 so live fixes (duplicate
    // OVRCameraRigRef, CanvasRoot HorizontalLayoutGroup) can be re-verified
    // via Unity MCP against the actual scene state in seconds instead of a
    // full multi-minute build for every iteration.
    [MenuItem("HDMR/Rebuild Home Scene Only (no build)")]
    public static void RebuildSceneOnly()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[HDMRBuildHome] RebuildSceneOnly() called while Editor is in Play Mode — refusing (stop Play Mode first).");
            return;
        }
        CaptureTileBackplateStyleBeforeWipe();
        EnsureScene();
        AddInteractionAndUI();
        Debug.Log("[HDMRBuildHome] scene-only rebuild complete -> " + ScenePath);
    }

    const string TMPMarkerAsset = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

    // First attempt at this called ImportResources(...) inline inside
    // Build() and logged success right after — WRONG. AssetDatabase.
    // ImportPackage is asynchronous; -quit tore the process down before the
    // import actually finished, so the log lied by trusting its own call
    // instead of checking the real result (caught by the user testing it,
    // rightly furious that the popup kept coming back). Real fix: a
    // SEPARATE Unity.exe invocation
    // (-executeMethod HDMRBuildHome.ImportTMPEssentialsBlocking) that
    // triggers the import then polls via EditorApplication.update — the
    // same async-in-batch-mode pattern already proven this session for the
    // Package Manager registry check — until the file genuinely exists on
    // disk, before exiting. Run this once; Build() itself only checks.
    public static void ImportTMPEssentialsBlocking()
    {
        if (System.IO.File.Exists(TMPMarkerAsset))
        {
            Debug.Log("[HDMRBuildHome] TMP Essentials already present, nothing to do");
            EditorApplication.Exit(0);
            return;
        }
        TMPro.TMP_PackageResourceImporter.ImportResources(importEssentials: true, importExamples: false, interactive: false);
        double start = EditorApplication.timeSinceStartup;
        EditorApplication.CallbackFunction tick = null;
        tick = () =>
        {
            if (System.IO.File.Exists(TMPMarkerAsset))
            {
                EditorApplication.update -= tick;
                AssetDatabase.Refresh();
                Debug.Log("[HDMRBuildHome] TMP Essentials import CONFIRMED on disk");
                EditorApplication.Exit(0);
            }
            else if (EditorApplication.timeSinceStartup - start > 60)
            {
                EditorApplication.update -= tick;
                Debug.LogError("[HDMRBuildHome] TMP Essentials import did not appear on disk within 60s");
                EditorApplication.Exit(1);
            }
        };
        EditorApplication.update += tick;
    }

    // Build() only checks — it does not attempt the import itself, so it
    // can never again claim success without verifying the file is real.
    static void EnsureTMPEssentials()
    {
        if (!System.IO.File.Exists(TMPMarkerAsset))
            Debug.LogError("[HDMRBuildHome] TMP Essentials missing — run " +
                "-executeMethod HDMRBuildHome.ImportTMPEssentialsBlocking once first (separate invocation, the import is async)");
    }

    // Meta's own Project Setup Tool (UPST) — the actual, authoritative
    // config checker, verified against the installed package source
    // (OVRProjectSetup.cs: public static Task FixAllAsync(BuildTargetGroup)).
    // Run this ONCE (separate Unity.exe invocation, no -quit — same reason
    // as ImportTMPEssentialsBlocking: it returns a Task, and trusting a log
    // line without waiting for it is exactly the mistake that already
    // burned an entire round-trip tonight on the TMP import) before Build()
    // — lets Meta's own tooling catch and fix real config gaps (found live
    // in an interactive Editor session: min Android API 29 -> should be 32,
    // no Oculus Touch interaction profile registered under OpenXR) instead
    // of guessing at PlayerSettings/OVRManager fields by hand.
    public static void FixProjectSetupBlocking()
    {
        var task = OVRProjectSetup.FixAllAsync(BuildTargetGroup.Android);
        EditorApplication.CallbackFunction tick = null;
        double start = EditorApplication.timeSinceStartup;
        tick = () =>
        {
            if (task.IsCompleted)
            {
                EditorApplication.update -= tick;
                AssetDatabase.SaveAssets();
                Debug.Log($"[HDMRBuildHome] OVRProjectSetup.FixAllAsync completed (status={task.Status})");
                EditorApplication.Exit(task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? 0 : 1);
            }
            else if (EditorApplication.timeSinceStartup - start > 120)
            {
                EditorApplication.update -= tick;
                Debug.LogError("[HDMRBuildHome] OVRProjectSetup.FixAllAsync did not complete within 120s");
                EditorApplication.Exit(1);
            }
        };
        EditorApplication.update += tick;
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

        // FIXED 2026-09-16, found by a night-cycle audit agent re-checking
        // OVRProjectSetup live: this was hardcoded to API 29, and every
        // Build() run SILENTLY REVERTED the min API back to 29 even after
        // the earlier UPST fix (FixProjectSetupBlocking) had bumped it to
        // 32 — because that fix changed ProjectSettings.asset directly,
        // one time, external to this method, and this line just overwrites
        // it again on every single build since. Meta's own Project Setup
        // Tool flags API 32 as Required, not optional (confirmed live via
        // OVRProjectSetup.GetTasks(BuildTargetGroup.Android) tonight). Set
        // the real requirement directly here so it can't silently regress
        // on the next build the way it just did across most of tonight's
        // builds.
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
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
        // NOT Gamma — that's copied from nuvc/nuvc.xr, which don't use
        // OVRPlugin at all. Home does (Meta XR Core SDK), and
        // OVRGradleGeneration hard-fails the build otherwise: "Oculus
        // Utilities Plugin with OpenXR only supports linear lighting."
        // Copying a setting across builds without checking whether it
        // still applies is exactly how this broke.
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SplashScreen.show = false; // was never set on any of the 3 builds — real miss, caught by the user

        // FIXED 2026-09-16 — this is very likely the exact cause of the
        // "Unsupported Input Handling" dialog that blocked two separate
        // interactive builds tonight (see research/
        // unity-batchmode-telemetry-hang.md's second entry). Confirmed via
        // the actual project file: ProjectSettings/ProjectSettings.asset
        // had `activeInputHandler: 2` ("Both") — Unity's own console
        // warning is explicit: "Active Input Handling is set to Both, this
        // is unsupported on Android and might cause issues". There's no
        // public PlayerSettings API for this (verified — not in
        // UnityEditor.PlayerSettings in this version); it's set the same
        // way Unity's own Project Settings window does, via
        // SerializedObject on the ProjectSettings.asset object itself. 1 =
        // Input System Package (New) only — not 0 (Input Manager only),
        // since OpenXR's input path is built on the new Input System and
        // OVRInput doesn't care about this setting either way, so New-only
        // is the one choice that can't break XR input.
        {
            var settingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settingsAssets.Length > 0)
            {
                var so = new SerializedObject(settingsAssets[0]);
                var inputHandlerProp = so.FindProperty("activeInputHandler");
                if (inputHandlerProp != null && inputHandlerProp.intValue != 1)
                {
                    inputHandlerProp.intValue = 1;
                    so.ApplyModifiedProperties();
                    AssetDatabase.SaveAssets();
                    Debug.Log("[HDMRBuildHome] activeInputHandler set to 1 (Input System Package (New) only) — was 'Both', which Unity itself warns is unsupported on Android and was very likely the cause of the recurring 'Unsupported Input Handling' build-blocking dialog");
                }
            }
            else Debug.LogWarning("[HDMRBuildHome] could not load ProjectSettings.asset to fix activeInputHandler");
        }

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
        // "Mode MR" tile toggles real passthrough at runtime (see
        // PassthroughController.cs) — Supported, not Required, since the
        // app is still fully usable in plain VR (every Quest 3/3S supports
        // passthrough anyway, but Required would needlessly block install
        // on a hypothetical device that doesn't).
        cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
        OVRProjectConfig.CommitProjectConfig(cfg);
        Debug.Log("[HDMRBuildHome] OVRProjectConfig committed: devices=" +
            string.Join(",", cfg.targetDeviceTypes) + " handTracking=" + cfg.handTrackingSupport +
            " passthrough=" + cfg.insightPassthroughSupport);
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
        // Defense-in-depth: Build()/RebuildSceneOnly() already guard on
        // isPlaying before calling this, but EditorSceneManager.NewScene()
        // throws InvalidOperationException in Play Mode regardless of the
        // caller, so guard here too in case a future caller forgets.
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[HDMRBuildHome] EnsureScene() called while Editor is in Play Mode — refusing.");
            return;
        }
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

        // REVERSED 2026-09-16 — this used to force FloorLevel here, on the
        // theory that the default (EyeLevel) was why the floor plane didn't
        // line up with the real one. That fix was never actually verified
        // on-device and turned out to cause a much worse, confirmed bug:
        // read OVRManager.cs directly (Scripts/OVRManager.cs ~line 2754) —
        // its own Update() loop does
        //   if (OVRPlugin.UnityOpenXR.Enabled && _trackingOriginType == FloorLevel)
        //       _trackingOriginType = Stage;
        //   if (trackingOriginType /* getter, queries OVRPlugin.GetTrackingOriginType()
        //        on a real HMD — NOT the field */ != _trackingOriginType)
        //       trackingOriginType = _trackingOriginType; // setter — pushes to native, calls RefreshRecenterSpace()
        // On-device (confirmed via `adb logcat`, not guessed) this getter/
        // setter pair never converges once OpenXR is enabled with
        // FloorLevel — the setter re-fires on literally every single
        // Update(), spamming `[MetaXRFeature] OnAppSpaceChange` hundreds of
        // times per second, i.e. the native tracking reference space gets
        // torn down and recreated every frame. That is almost certainly
        // what produced the "un tile énorme et déformé" the user
        // screenshotted — anything positioned relative to world/tracking
        // space can glitch when its reference space resets mid-frame.
        // Meta's own docs are explicit here (developers.meta.com, "Configure
        // Meta XR camera settings"): "Using Stage tracking origin is not
        // recommended for any application... For fully-immersive VR
        // applications, use Eye Level or Floor Level." FloorLevel silently
        // becomes Stage under our OpenXR setup per the code above, so it's
        // not actually a safe choice here — EyeLevel is the one origin type
        // that skips this remap entirely (the condition explicitly checks
        // `== FloorLevel`). Trade-off, stated plainly: EyeLevel means Y=0 is
        // wherever the headset was at boot, not the true physical floor —
        // reintroduces the earlier, much less severe floor-alignment
        // question — but a per-frame tracking-space reset is a far worse,
        // confirmed, actively-reproducing bug, so this is the right
        // trade-off until Stage's real fix (or Spatial Anchors, per the
        // same doc's MR guidance) is investigated properly. Leaving
        // trackingOriginType at its default (EyeLevel) rather than forcing
        // anything — no reflection hack needed at all now.

        var light = new GameObject("Directional Light", typeof(Light));
        light.GetComponent<Light>().type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50, -30, 0);

        // A scene with literally nothing but a light and a UI panel is
        // disorienting in VR — no parallax reference at all means head
        // rotation gives you no visual feedback that anything is actually
        // world-locked, which reads exactly like "0DoF, stuck to my head"
        // even though tracking itself is fine. Found the hard way (user
        // tested a build without this and reported exactly that).
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor (reference)";
        // FIXED 2026-09-16 — this plane used to sit at Y=0 same as the rig,
        // which was correct back when trackingOriginType was forced to
        // FloorLevel (Y=0 == real floor). Now that EyeLevel is the default
        // (see the tracking-origin fix earlier in this method — FloorLevel
        // was silently remapping to Stage and causing a confirmed
        // continuous OnAppSpaceChange thrash bug), Y=0 is wherever the
        // headset's boot-time pose is — roughly eye height, not the floor.
        // Left at Vector3.zero, this plane would sit right at/through the
        // user's face instead of below their feet. Verified live (rig and
        // floor both read position (0,0,0) before this fix). -1.6m is an
        // assumed average adult standing eye height — doesn't need to be
        // exact, this plane only needs to read as "clearly below the
        // camera" to serve its actual purpose (a parallax/world-lock
        // anchor, not a precisely-calibrated physical floor).
        floor.transform.position = new Vector3(0f, -1.6f, 0f);
        floor.transform.localScale = new Vector3(3f, 1f, 3f); // Unity's default Plane is 10x10, so this is a 30x30m floor

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

        // FOUND 2026-09-16 via a real Play Mode run: no EventSystem existed
        // anywhere in the scene at all — confirmed by find_gameobjects
        // (zero results). PointableCanvasUnityEventWrapper (already on the
        // canvas, ships with EmptyUIBackplateWithCanvas) asserts a
        // PointableCanvasModule must exist in the scene
        // ("AssertionException: A PointableCanvasModule is required");
        // that module IS itself a PointerInputModule subclass, meant to
        // live on an EventSystem GameObject the same way
        // StandaloneInputModule normally would. Without it, ray/poke ->
        // Canvas click routing has no path to actually reach
        // Button.onClick. The real wizard presumably creates this when run
        // interactively; our manual instantiation skipped it entirely.
        if (UnityEngine.Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var esGo = new GameObject("EventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(Oculus.Interaction.PointableCanvasModule));
            Debug.Log("[HDMRBuildHome] created EventSystem + PointableCanvasModule (was missing entirely — canvas clicks had no input module to route through)");
        }

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

            // FIXED 2026-09-16 — the comment below this used to claim the
            // prefab ships with no OVRCameraRigRef of its own. That was
            // WRONG, confirmed by live inspection via Unity MCP: the prefab
            // DOES ship its own OVRCameraRigRef directly on this GameObject,
            // pre-wired with _leftHand/_rightHand but with CameraRig/
            // _ovrCameraRig left null. Blindly AddComponent<OVRCameraRigRef>()
            // here created a SECOND ref (camera+controllers wired, hands
            // null) — and since TrackingToWorldTransformerOVR._cameraRigRef
            // was already pointing at the FIRST (prefab-shipped) ref, the
            // "wire every empty field" loop below never touched it: it kept
            // pointing at a ref whose CameraRig was still null. That's the
            // actual, confirmed cause of the persistent hand-tracking NRE
            // (TrackingToWorldTransformerOVR.get_Transform ->
            // Hand.get_Scale -> NullReferenceException) that survived 3
            // earlier fix attempts — none of which questioned whether a
            // duplicate ref existed at all. Fix: reuse the existing ref
            // instead of adding a second one.
            var ovrCameraRig = rigGo.GetComponent<OVRCameraRig>();
            var rigRef = interactionGo.GetComponent<OVRCameraRigRef>();
            if (rigRef == null) rigRef = interactionGo.AddComponent<OVRCameraRigRef>();
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
            Debug.Log($"[HDMRBuildHome] OVRComprehensiveInteractionRig added under OVRCameraRig, {wired} _cameraRigRef wired");

            // WRONG, THEN FIXED FOR REAL 2026-09-16. First pass (see git
            // history / the previous version of this comment): a Play Mode
            // run surfaced AssertionExceptions from TunnelingEffect
            // (WallPenetrationTunneling, SmoothMovementTunneling) and
            // FirstPersonLocomotor wanting _centerEyeCamera/_playerOrigin/
            // _playerEyes — I wired those references to silence the
            // asserts WITHOUT asking what these components actually do.
            // They're comfort-vignette + movement components for FREE
            // LOCOMOTION — completely irrelevant to a static Home menu
            // with no player movement — and wiring them up made them
            // ACTUALLY FUNCTION instead of just asserting: the grey
            // "near a wall" tunneling vignette then rendered permanently
            // over the whole view on-device (confirmed by the user
            // directly: "le filtre gris openXR... est tout le temps
            // affiché"). Silencing an assertion is not the same as the
            // component belonging there — exactly the "tire à l'aveugle"
            // mistake called out for it. Real fix: the whole locomotion
            // subsystem lives under one clean container,
            // OVRComprehensiveInteractionRig/Locomotor (verified via
            // live Unity MCP inspection — its own components are just
            // HmdRef/ControllerRef/LocomotionEventsConnection, i.e. input
            // plumbing for movement, nothing ray/poke/grab depends on),
            // so disable that ONE GameObject instead of babysitting each
            // leaf component's asserts. No movement system, no tunneling
            // vignette, no more wiring individual fields here.
            var locomotor = interactionGo.transform.Find("Locomotor");
            if (locomotor != null)
            {
                locomotor.gameObject.SetActive(false);
                Debug.Log("[HDMRBuildHome] disabled Locomotor (free-movement + wall-tunneling-vignette subsystem — not applicable to a static Home menu, and left active it rendered a permanent grey vignette over the whole view)");
            }
            else Debug.LogWarning("[HDMRBuildHome] Locomotor child not found under OVRComprehensiveInteractionRig — prefab structure may have changed");
        }

        var ceCam = rigGo.GetComponent<OVRCameraRig>().centerEyeAnchor.GetComponent<Camera>();

        // 2026-09-16: prototype reference calls for TWO panels at DIFFERENT
        // angles — the tile panel tilted back like a lectern, the stream
        // preview above it standing upright/perpendicular to the ground —
        // not one flat sign. A single object can't hold two local
        // rotations, so SpawnInFrontOfHead now lives on a neutral parent
        // ("HomeAnchor") that just faces the user, and both panels are
        // children with their own fixed local tilt set once here at build
        // time (see SpawnInFrontOfHead.cs's own updated header comment).
        var homeAnchorGo = new GameObject("HomeAnchor");
        homeAnchorGo.AddComponent<Hdmr.Home.SpawnInFrontOfHead>();

        var canvasPrefab = LoadUniquePrefab("EmptyUIBackplateWithCanvas");
        if (canvasPrefab != null)
        {
            var canvasGo = (GameObject)PrefabUtility.InstantiatePrefab(canvasPrefab);
            canvasGo.name = "HomeCanvas (placeholder)";
            canvasGo.transform.localScale *= 2.5f; // the 0.25m default panel also read as too small/hard to find
            canvasGo.transform.SetParent(homeAnchorGo.transform, false);
            // Tile panel sits BELOW the anchor's own facing point and
            // tilts back like a lectern (see SetupStreamPreviewPanel for
            // the sibling panel above it, upright). -0.35m / 18° are eyeballed
            // against the prototype composition, not measured — tune by eye
            // once seen in-headset.
            // CORRECTED 2026-09-16, UI QA pass: -0.35 was eyeballed before
            // either backplate had its final fitted size. Measured via
            // RectTransform.GetWorldCorners() on both backplates: with the
            // old -0.35/+0.45 pair the gap between the tile panel's top
            // edge and the preview panel's bottom edge was 0.434m — the
            // Hinge (meant to sit centered in a SMALL gap, "a nod to the
            // 3DS's physical hinge") ended up almost touching the preview
            // panel with a huge unexplained void below it instead.
            // Recomputed from the measured backplate half-heights (tile
            // 0.617m, preview 0.430m) for a tight, intentional 0.04m gap
            // with the composition's overall vertical center kept roughly
            // where it was. See AddHinge/SetupStreamPreviewPanel for the
            // matching correction on the other two.
            canvasGo.transform.localPosition = new Vector3(0f, -0.153f, 0f);
            canvasGo.transform.localRotation = Quaternion.Euler(PodiumTiltDegrees, 0f, 0f);

            // Same "nothing auto-wires the camera reference since we skip
            // the real wizard" issue as OVRCameraRigRef/TunnelingEffect/
            // FirstPersonLocomotor above — a WorldSpace Canvas's own
            // worldCamera field ships null. PointerInputModule-derived
            // classes (PointableCanvasModule included) use the event
            // camera for their raycast math; leaving this null risks the
            // same "looks right, nothing is actually clickable" failure
            // mode the missing EventSystem caused. Found while checking
            // whether the EventSystem fix was really enough — not yet
            // confirmed this was actually broken before (no device to
            // click-test with), but it's a one-line, safe, consistent fix
            // either way.
            var canvasComp = canvasGo.GetComponentInChildren<Canvas>(true);
            if (canvasComp != null) canvasComp.worldCamera = ceCam;

            Debug.Log("[HDMRBuildHome] EmptyUIBackplateWithCanvas placeholder added (spawns relative to head at runtime)");

            // FOUND 2026-09-16: this used to run AFTER AddHomeTiles/
            // AddTabbar. ApplyUiTheme -> ApplyCurrentTheme ->
            // ApplyAnimators calls animator.Update(0) on every Animator
            // under the canvas UNCONDITIONALLY (it doesn't check
            // animator.enabled — Animator.Update() is a direct API call,
            // not gated by the component's enabled flag) — which silently
            // overwrote AddTileRow's manual Background.color override the
            // moment it ran, even though the animator itself was already
            // disabled. Running the theme pass BEFORE the tiles exist
            // means their (disabled) Animators are never touched by it at
            // all, so AddTileRow's direct color assignment is the last
            // word, as intended.
            ApplyUiTheme(canvasGo);
            AddHomeTiles(canvasGo);
            AddTabbar(canvasGo);
            FitBackplateToContent(canvasGo);
            ApplyBackplateGradient(canvasGo); // colors/radius now come from HDMRHomeDesignConfig, not hardcoded here -- see that asset
            AddStatusBar(canvasGo);
            LockInUiColors(canvasGo);

            Debug.Log("[HDMRBuildHome] home canvas fully populated (tiles, tabbar, backplate fit)");
        }

        SetupStreamPreviewPanel(homeAnchorGo, ceCam);
        AddHinge(homeAnchorGo);
        SetupPassthroughAndAura(rigGo, homeAnchorGo);

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
    }

    // Prototype reference (2026-09-16): above the tile panel, a second
    // panel shows the raw capture feed — "FLUX DE CAPTURE - SWITCH 2"
    // label, an orange "Placeholder" badge (there's no real feed to show
    // yet — see the capture pipeline's own status in hdmr-project.md),
    // and a stereo split (two halves + a center divider) with a flat
    // dark-violet fill standing in for the eventual purple/dark gradient
    // placeholder art. Standing upright (perpendicular to the ground) —
    // a plain local-identity rotation on HomeAnchor's child, since the
    // anchor itself already faces the user with no tilt.
    static void SetupStreamPreviewPanel(GameObject anchorGo, Camera ceCam)
    {
        var previewPrefab = LoadUniquePrefab("EmptyUIBackplateWithCanvas");
        if (previewPrefab == null) return;

        var previewGo = (GameObject)PrefabUtility.InstantiatePrefab(previewPrefab);
        previewGo.name = "StreamPreviewCanvas (placeholder)";
        previewGo.transform.localScale *= 2.5f;
        previewGo.transform.SetParent(anchorGo.transform, false);
        // Above the tile panel (which sits at localPosition.y = -0.35, see
        // AddInteractionAndUI) with a small gap between them — eyeballed
        // against the prototype composition, not measured; tune by eye.
        // CORRECTED 2026-09-16 — see the matching comment on canvasGo's
        // localPosition for the measurement this came from.
        previewGo.transform.localPosition = new Vector3(0f, 0.253f, 0f);

        var canvasComp = previewGo.GetComponentInChildren<Canvas>(true);
        if (canvasComp != null) canvasComp.worldCamera = ceCam;

        ApplyUiTheme(previewGo);

        var canvasRoot = previewGo.transform.Find("CanvasRoot");
        if (canvasRoot == null)
        {
            var canvas = previewGo.GetComponentInChildren<Canvas>(true);
            canvasRoot = canvas != null ? canvas.transform : null;
        }
        if (canvasRoot == null) { Debug.LogError("[HDMRBuildHome] CanvasRoot not found for stream preview panel"); return; }

        // Same HorizontalLayoutGroup-clobbers-manual-layout issue AddHomeTiles
        // already found and works around.
        var layoutGroup = canvasRoot.GetComponent<UnityEngine.UI.LayoutGroup>();
        if (layoutGroup != null) layoutGroup.enabled = false;

        var baloo = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>("Assets/UI/Fonts/Baloo2 SDF.asset");

        // ADDED 2026-09-16, per the user's own paper nav-schema
        // (research/3ds-ui/nav-schema.md, "Écran du Menu Principal"):
        // "dropdown résolution en haut-gauche (liste des résolutions par
        // jeu, mise à jour en direct) + indicateur FPS en haut-droite" —
        // requirements the interactive HTML prototype never actually
        // built (it only has the static .tag/.ph stand-ins). No real
        // capture pipeline is wired into THIS scene to drive a live
        // resolution list or a real per-source fps (nuvc capture is a
        // separate app — see hdmr-project.md) — the resolution chip and
        // the latency figure are honest UI-shell placeholders (click the
        // chip to cycle, it's not decorative-only), but the FPS counter
        // IS real: Hdmr.Home.FpsCounter reads Unity's own frame rate live.
        var resChipGo = new GameObject("ResolutionChip", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
        resChipGo.transform.SetParent(canvasRoot, false);
        var resChipRt = resChipGo.GetComponent<RectTransform>();
        resChipRt.anchorMin = resChipRt.anchorMax = resChipRt.pivot = new Vector2(0.5f, 0.5f);
        resChipRt.anchoredPosition = new Vector2(-125f, 195f);
        resChipRt.sizeDelta = new Vector2(180f, 28f);
        // Rounded to match the badge right next to it (5px, same as
        // .showcase .ph) — no prototype reference for this chip
        // specifically (it comes from the paper schema, not the HTML
        // prototype), but a sharp rect sitting beside a rounded one would
        // be its own visible inconsistency, caught in the same sweep.
        var resChipImg = resChipGo.GetComponent<UnityEngine.UI.Image>();
        float resChipRadius = Hdmr.Editor.HDMRHomeDesignConfig.LoadOrCreate().badgeChipRadius;
        var resChipTex = GenerateRoundedRectTexture(180, 28, resChipRadius, new Color(1f, 1f, 1f, 0.08f), new Color(1f, 1f, 1f, 0.08f));
        resChipImg.sprite = CreateSlicedRoundedSprite(resChipTex, 180, 28, resChipRadius);
        resChipImg.type = UnityEngine.UI.Image.Type.Sliced;
        resChipImg.color = Color.white;
        var resChipLabelRt = CreatePreviewLabel(resChipGo.transform, "Label", "1920×1080  v", Vector2.zero, new Vector2(170f, 24f), 13f, HexColor(0xd6e2e6), baloo);
        var resChip = resChipGo.AddComponent<Hdmr.Home.ResolutionChip>();
        var resChipSo = new SerializedObject(resChip);
        resChipSo.FindProperty("_text").objectReferenceValue = resChipLabelRt.GetComponent<TMPro.TextMeshProUGUI>();
        resChipSo.ApplyModifiedPropertiesWithoutUndo();
        UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(resChipGo.GetComponent<UnityEngine.UI.Button>().onClick, resChip.CycleNext);

        var fpsRt = CreatePreviewLabel(canvasRoot, "FpsReadout", "-- fps", new Vector2(160f, 195f), new Vector2(90f, 26f), 14f, HexColor(0x8ba1a9), baloo);
        fpsRt.GetComponent<TMPro.TextMeshProUGUI>().alignment = TMPro.TextAlignmentOptions.Right;
        var fpsCounter = fpsRt.gameObject.AddComponent<Hdmr.Home.FpsCounter>();
        var fpsSo = new SerializedObject(fpsCounter);
        fpsSo.FindProperty("_text").objectReferenceValue = fpsRt.GetComponent<TMPro.TextMeshProUGUI>();
        fpsSo.ApplyModifiedPropertiesWithoutUndo();

        // Latency: placeholder value only — real glass-to-glass latency
        // measurement lives with the separate nuvc capture app (see
        // hdmr-project.md's latency notes), not this Home scaffold.
        var latencyRt = CreatePreviewLabel(canvasRoot, "LatencyReadout", "-- ms", new Vector2(160f, 172f), new Vector2(90f, 22f), 12f, HexColor(0x5a6f79), baloo);
        latencyRt.GetComponent<TMPro.TextMeshProUGUI>().alignment = TMPro.TextAlignmentOptions.Right;

        // CORRECTED 2026-09-16 against the actual prototype artifact
        // ("HDMR Home", not just the earlier reference screenshot): the
        // real label is ".tag { ... }" text "Flux de capture · Switch 2"
        // (mixed case, middle dot) — "FLUX DE CAPTURE - SWITCH 2" was a
        // misreading. --text-dim (.tag's own color), not --tile. Moved
        // down a row from its first placement to make room for the
        // resolution chip above it.
        var labelRt = CreatePreviewLabel(canvasRoot, "PreviewLabel", "Flux de capture · Switch 2",
            new Vector2(-40f, 155f), new Vector2(300f, 26f), 14f, HexColor(0x8ba1a9), baloo);
        labelRt.GetComponent<TMPro.TextMeshProUGUI>().alignment = TMPro.TextAlignmentOptions.Left;

        var badgeGo = new GameObject("PlaceholderBadge", typeof(RectTransform), typeof(UnityEngine.UI.Image));
        badgeGo.transform.SetParent(canvasRoot, false);
        var badgeRt = badgeGo.GetComponent<RectTransform>();
        badgeRt.anchorMin = badgeRt.anchorMax = badgeRt.pivot = new Vector2(0.5f, 0.5f);
        badgeRt.anchoredPosition = new Vector2(150f, 155f);
        badgeRt.sizeDelta = new Vector2(100f, 24f);
        // FOUND 2026-09-16, second visual-sweep pass after the user's
        // "you barely did 30 minutes" correction — this badge was still a
        // sharp rectangle; the real prototype specs `.showcase .ph {
        // border-radius: 5px }`. Small but real, caught by an actual
        // element-by-element zoomed screenshot comparison, not skipped.
        var badgeImg = badgeGo.GetComponent<UnityEngine.UI.Image>();
        float badgeRadius = Hdmr.Editor.HDMRHomeDesignConfig.LoadOrCreate().badgeChipRadius;
        var badgeTex = GenerateRoundedRectTexture(100, 24, badgeRadius, AmberAccent, AmberAccent);
        badgeImg.sprite = CreateSlicedRoundedSprite(badgeTex, 100, 24, badgeRadius);
        badgeImg.type = UnityEngine.UI.Image.Type.Sliced;
        badgeImg.color = Color.white;
        CreatePreviewLabel(badgeGo.transform, "BadgeLabel", "Placeholder", Vector2.zero, new Vector2(92f, 20f), 12f, HexColor(0x0c1519), baloo);

        // CORRECTED 2026-09-16: the real prototype's showcase is ONE 16:9
        // video area (a single <canvas>, aspect-ratio:16/9) with a faked
        // moving gradient + a faint center line to suggest an SBS stereo
        // frame — NOT two separate solid-color blocks with a hard divider,
        // which is what this used to build (misread from the reference
        // screenshot alone, before reading the actual prototype source).
        // drawFeedInto() in the prototype draws a diagonal
        // hsl(200-255, 40-46%, 10-16%) gradient — reproducing that exact
        // diagonal gradient needs a generated texture, out of scope for
        // this placeholder pass; a flat approximation at the gradient's
        // rough midpoint (dark blue-violet) stands in for it, flagged
        // here rather than silently guessed. Shifted down slightly to
        // clear the two new status rows above it.
        var feedRt = CreatePreviewRect(canvasRoot, "FeedPlaceholder", new Vector2(0f, 0f), new Vector2(360f, 190f), HexColor(0x1b2540));

        var dividerGo = new GameObject("FeedCenterLine", typeof(RectTransform), typeof(UnityEngine.UI.Image));
        dividerGo.transform.SetParent(canvasRoot, false);
        var dividerRt = dividerGo.GetComponent<RectTransform>();
        dividerRt.anchorMin = dividerRt.anchorMax = dividerRt.pivot = new Vector2(0.5f, 0.5f);
        dividerRt.anchoredPosition = new Vector2(0f, 0f);
        dividerRt.sizeDelta = new Vector2(1f, 190f);
        dividerGo.GetComponent<UnityEngine.UI.Image>().color = new Color(1f, 1f, 1f, 0.06f); // rgba(255,255,255,0.06) — the prototype's own centerline value, exact

        // Fit backplate around everything — same math as
        // FitBackplateToContent, generalized past its "Tile_" name filter
        // since this panel has no tiles.
        var backplate = canvasRoot.Find("UIBackplate");
        if (backplate != null)
        {
            var backplateRt = backplate.GetComponent<RectTransform>();
            var rects = new[] { labelRt, badgeRt, feedRt, resChipRt, fpsRt, latencyRt };
            float xMin = 0, xMax = 0, yMin = 0, yMax = 0;
            for (int i = 0; i < rects.Length; i++)
            {
                var rt = rects[i];
                float rtXMin = rt.anchoredPosition.x - rt.sizeDelta.x * rt.pivot.x;
                float rtXMax = rtXMin + rt.sizeDelta.x;
                float rtYMin = rt.anchoredPosition.y - rt.sizeDelta.y * rt.pivot.y;
                float rtYMax = rtYMin + rt.sizeDelta.y;
                if (i == 0) { xMin = rtXMin; xMax = rtXMax; yMin = rtYMin; yMax = rtYMax; }
                else { xMin = Mathf.Min(xMin, rtXMin); xMax = Mathf.Max(xMax, rtXMax); yMin = Mathf.Min(yMin, rtYMin); yMax = Mathf.Max(yMax, rtYMax); }
            }
            const float pad = 20f;
            xMin -= pad; xMax += pad; yMin -= pad; yMax += pad;
            // FOUND 2026-09-17 after the user caught the feed rect visibly
            // poking out past this panel's edges in a real screenshot:
            // this backplate's anchorMin/anchorMax were still the
            // PREFAB'S OWN DEFAULT — (0,0), bottom-left corner — never
            // touched, only `.pivot` was ever set here. `anchoredPosition`
            // is measured from the ANCHOR point, not the pivot; with a
            // corner anchor and this "(xMin+xMax)/2" math (which only
            // makes sense for a CENTER anchor, matching every other
            // element in this panel), the backplate silently drifted
            // toward the bottom-left corner of its parent instead of
            // actually centering on its content — confirmed live via
            // GetWorldCorners(): the backplate's world X range didn't
            // even overlap the center of its own content. Root cause of
            // the "blue rectangle overlapping the preview" the user
            // reported. Must set anchorMin/anchorMax to match pivot, not
            // pivot alone.
            backplateRt.anchorMin = backplateRt.anchorMax = backplateRt.pivot = new Vector2(0.5f, 0.5f);
            backplateRt.anchoredPosition = new Vector2((xMin + xMax) / 2f, (yMin + yMax) / 2f);
            backplateRt.sizeDelta = new Vector2(xMax - xMin, yMax - yMin);

            // CORRECTED 2026-09-16: `.showcase { background: #0c1519 }` in
            // the prototype — a flat near-black, NOT the shared "deck"
            // backplate gradient (--backplate/--backplate-hi) the tile
            // panel uses. Overriding the Image directly rather than the
            // shared theme asset, since the theme is shared with the tile
            // panel and changing it there would wrongly recolor that too.
            //
            // FOUND live: a plain `.color =` assignment here read back
            // correctly in Edit Mode right after the rebuild, but reset to
            // pure white every time Play Mode was entered — even with
            // UIThemeManager already destroyed and confirmed absent
            // scene-wide (not the same clobber as the tile-text bug).
            // Neither UIThemeManager.cs nor anything else in the SDK
            // touches the "QDSUIBackplateGradient" tag or the
            // "RoundedBoxGradientUIDark" material this object ships with
            // (checked via source search) — root mechanism unconfirmed,
            // but this object is the only one in the scene using that
            // tag/material combination. Since this panel was never meant
            // to have the QDS gradient look anyway (the real design is a
            // flat color), the robust fix is to stop using that material
            // path entirely rather than keep fighting whatever resets it.
            backplate.gameObject.tag = "Untagged";

            // FOUND live, second attempt: even after clearing the tag and
            // setting `.material = null` + `.color`, BOTH properties still
            // read back as the original "RoundedBoxGradientUIDark"/white
            // the moment Play Mode starts (domain reload) — while the tag
            // change on the same GameObject DOES persist. That split (a
            // GameObject-level property survives, a Component-level one on
            // the very same object doesn't) points at something re-driving
            // THIS Image specifically on (re)enable, not a one-off value
            // that silently failed to save. The two candidates on this
            // object beyond a plain Image are Mask and RoundedBoxUIProperties
            // — neither is needed here (nothing under this backplate needs
            // clipping, and the rounded-gradient vertex effect is exactly
            // the QDS look we're trying to get OFF of) — so remove both
            // and rebuild the Image component fresh rather than fight
            // whatever the interaction is.
            var oldMask = backplate.GetComponent<UnityEngine.UI.Mask>();
            if (oldMask != null) UnityEngine.Object.DestroyImmediate(oldMask);
            var oldRoundedBox = backplate.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
            if (oldRoundedBox != null) UnityEngine.Object.DestroyImmediate(oldRoundedBox);
            var oldImg = backplate.GetComponent<UnityEngine.UI.Image>();
            if (oldImg != null) UnityEngine.Object.DestroyImmediate(oldImg);
            var freshImg = backplate.gameObject.AddComponent<UnityEngine.UI.Image>();

            // FOUND 2026-09-16 via an actual rendered comparison against
            // the real prototype (not just its CSS read as text): removing
            // RoundedBoxUIProperties above also removed this panel's
            // rounded corners entirely (`.showcase { border-radius: 16px }`
            // in the real design) — a real regression the color-only fix
            // above never checked for. Baked rounded-rect texture instead
            // of a flat Image.color, same technique as the tile backplate's
            // gradient (see GenerateRoundedRectTexture).
            // CHANGED 2026-09-17: color/radius now sourced from
            // HDMRHomeDesignConfig instead of hardcoded here — see that
            // asset's own header comment.
            var previewConfig = Hdmr.Editor.HDMRHomeDesignConfig.LoadOrCreate();
            var flatColor = previewConfig.previewBackplateColor;
            float previewRadius = previewConfig.previewBackplateRadius;
            int previewBpW = Mathf.RoundToInt(backplateRt.sizeDelta.x);
            int previewBpH = Mathf.RoundToInt(backplateRt.sizeDelta.y);
            var roundedTex = GenerateRoundedRectTexture(previewBpW, previewBpH, previewRadius, flatColor, flatColor); // --showcase radius, flat color (top==bottom, no gradient here)
            // Sliced (9-slice), not Simple — see CreateSlicedRoundedSprite's
            // header comment: keeps the corner radius fixed-size if this
            // backplate is ever resized after build, instead of the
            // corners stretching/flattening with the rest of the sprite.
            freshImg.sprite = CreateSlicedRoundedSprite(roundedTex, previewBpW, previewBpH, previewRadius);
            freshImg.type = UnityEngine.UI.Image.Type.Sliced;
            freshImg.color = Color.white;
        }

        LockInUiColors(previewGo);

        Debug.Log("[HDMRBuildHome] stream preview panel added (placeholder feed content, upright above tile panel)");
    }

    // FOUND 2026-09-16, via an actual rendered comparison against the real
    // prototype artifact (loaded in a browser, not just its CSS read as
    // text) — after the user directly challenged whether this had been
    // done at all: every major surface in the real prototype has visible
    // rounded corners (`--deck-radius: 20px`, `.showcase`/`.tile`
    // `border-radius: 16px`, `.hinge` a full pill) and a constant drop
    // shadow under every tile AT REST, not just selected. This build
    // script's own backplate gradient (a plain 1px-wide vertical strip,
    // stretched — SHARP corners) and the earlier fix that destroyed
    // RoundedBoxUIProperties+Mask on the preview backplate to solve the
    // white-sprite-reset bug (see SetupStreamPreviewPanel) both produced
    // sharp rectangles where the design calls for rounded ones — a real,
    // visible regression that pure numeric/color verification never
    // caught, because nothing in that verification ever asked "is this
    // even the right SHAPE". Replaced the 1px gradient strip with a
    // properly rounded, full-resolution baked texture (AA'd rounded-rect
    // mask, computed pixel by pixel against the backplate's own actual
    // size — no stretching artifacts on the corners).
    // ADDED 2026-09-17 on direct request: every rounded-rect UI element
    // (badge, resolution chip, both panel backplates) was baking its
    // texture at a fixed size via GenerateRoundedRectTexture and assigning
    // it as `Image.Type.Simple` — a whole-sprite stretch. That's fine as
    // long as nobody ever resizes the RectTransform after the build script
    // runs, but the user resized the menu backplate by hand in the Editor
    // afterward and the corners squashed non-uniformly with the rest of
    // the stretch, because Simple has no concept of a fixed-size border.
    // `Image.Type.Sliced` (Unity's standard 9-slice) fixes this properly:
    // the sprite's `border` (in texture pixels, one value per edge) stays
    // constant-size regardless of how the Image is stretched, so the
    // corner radius survives any resize instead of scaling with it. This
    // is a real fix, not a workaround — the same technique a designer
    // would reach for in any UI toolkit, Meta's own QDS sprites included.
    static Sprite CreateSlicedRoundedSprite(Texture2D tex, int w, int h, float radiusPx)
    {
        var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(radiusPx, radiusPx, radiusPx, radiusPx));
        return sprite;
    }

    static Texture2D GenerateRoundedRectTexture(int w, int h, float radiusPx, Color colorTop, Color colorBottom)
    {
        w = Mathf.Max(2, w);
        h = Mathf.Max(2, h);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            float v = y / (float)(h - 1);
            Color baseColor = Color.Lerp(colorBottom, colorTop, v);
            for (int x = 0; x < w; x++)
            {
                float coverage = RoundedRectCoverage(x, y, w, h, radiusPx);
                pixels[y * w + x] = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * coverage);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    // Signed-distance-ish coverage of a rounded rect at pixel (px,py) in a
    // w x h texture, corner radius r, with a 1px antialiasing band —
    // avoids the jagged/aliased edge a hard cutoff would leave.
    static float RoundedRectCoverage(int px, int py, int w, int h, float r)
    {
        r = Mathf.Min(r, Mathf.Min(w, h) / 2f);
        float fx = px + 0.5f, fy = py + 0.5f;
        float cx = Mathf.Clamp(fx, r, w - r);
        float cy = Mathf.Clamp(fy, r, h - r);
        float dist = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
        return Mathf.Clamp01(r - dist + 0.5f);
    }

    // Direct C# port of the prototype's drawBG(), evaluated once at
    // driftT=0 (a static frame — see the caller's comment for why this
    // doesn't animate). LAYERS array, base fill, jitter formula and chaos
    // defaults (chaosRot=1.0, chaosScale=0.5, chaosPos=0) copied exactly
    // from the prototype's own source, not re-derived from memory.
    // CORRECTED 2026-09-17 after the user directly re-quoted the original
    // instruction back ("le fond sera comme une aura transparANTE derrière
    // l'ui") — this was building a fully OPAQUE textured backdrop the
    // whole time (base fill at alpha=1.0, an opaque material). That's not
    // a shading nuance, it's the wrong concept entirely: an "aura" is a
    // soft, see-through glow, not a solid wall with a pattern on it — the
    // difference between something *behind* the UI and something that
    // *replaces the view* behind the UI. Two changes make this a real
    // aura instead of a reskinned opaque panel: (1) the base fill's own
    // alpha is now well under 1 (0.55 peak, chosen to read as present but
    // genuinely see-through, not scientifically derived from anything —
    // tune by eye once seen for real), and (2) a soft radial falloff
    // fades that alpha to fully transparent at the edges, so it reads as
    // a glow centered behind the UI rather than a hard-edged rounded
    // panel that merely happens to be less than 100% opaque. The material
    // this feeds (SetupPassthroughAndAura) was also switched from
    // Unlit/Texture (opaque) to Unlit/Transparent so this alpha channel
    // actually does something at render time.
    static Texture2D GenerateParallaxGridTexture(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[w * h];
        var baseColor = HexColor(0x12212a); // drawBG's own bx.fillStyle, distinct from --void
        const float peakAlpha = 0.55f; // was 1.0 (fully opaque) — an "aura" has to let the environment show through even at its most solid point
        float cx0 = w / 2f, cy0 = h / 2f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float nx = (x + 0.5f - cx0) / cx0; // -1..1
                float ny = (y + 0.5f - cy0) / cy0;
                float r = Mathf.Sqrt(nx * nx + ny * ny);
                float falloff = 1f - Mathf.Clamp01(r); // 1 at center, 0 at/past the ellipse edge — a soft glow, not a hard panel
                falloff = falloff * falloff; // steeper falloff reads more like a glow than a linear ramp
                pixels[y * w + x] = new Color(baseColor.r, baseColor.g, baseColor.b, peakAlpha * falloff);
            }
        }

        float[] cellSizes = { 62f, 86f, 118f };
        float[] squareSizes = { 15f, 21f, 30f };
        float[] alphas = { 0.045f, 0.06f, 0.075f };
        Color[] hues = { HexColor(0x3c5b68), HexColor(0x4a7581), HexColor(0x5f95a1) };
        const float chaosRot = 1.0f, chaosScale = 0.5f; // prototype's own DEFAULTS

        for (int li = 0; li < 3; li++)
        {
            float cell = cellSizes[li], sz = squareSizes[li], alpha = alphas[li];
            Color hue = hues[li];
            int cols = Mathf.CeilToInt(w / cell) + 2;
            int rows = Mathf.CeilToInt(h / cell) + 1;
            for (int c = -1; c < cols; c++)
            {
                for (int r = 0; r < rows; r++)
                {
                    float jr = CellJitter(c, r + li * 97);
                    float js = CellJitter(c + 50, r - li * 31);
                    float rot = (jr - 0.5f) * 0.5f * chaosRot;
                    float scale = 1f + (js - 0.5f) * chaosScale;
                    float x = c * cell + cell / 2f;
                    float y = r * cell + cell / 2f;
                    float s = sz * Mathf.Max(0.15f, scale);
                    // These squares still draw at their own alpha
                    // regardless of the base falloff underneath — matches
                    // the prototype's own layering (squares drawn as a
                    // separate pass over the fill) and keeps them visible
                    // even where the base glow has faded, same as the
                    // real thing does over its opaque fill.
                    DrawRoundedSquare(pixels, w, h, x, y, s, rot, hue, alpha);
                }
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    static float CellJitter(float a, float b)
    {
        float v = Mathf.Sin(a * 127.1f + b * 311.7f) * 43758.5453f;
        return v - Mathf.Floor(v);
    }

    // Rasterizes one rotated rounded square (SDF-based, 1px AA band) onto
    // the pixel buffer, alpha-blended over whatever's already there —
    // matches the prototype's own `rrect(...); fill()` with
    // globalAlpha=alpha, corner radius = size*0.32 exactly as in drawBG().
    static void DrawRoundedSquare(Color[] pixels, int texW, int texH, float cx, float cy, float size, float rotRad, Color color, float alpha)
    {
        float half = size / 2f;
        float radius = size * 0.32f;
        float boundRadius = half * 1.5f + 2f; // covers rotated corners with margin
        int minX = Mathf.Max(0, Mathf.FloorToInt(cx - boundRadius));
        int maxX = Mathf.Min(texW - 1, Mathf.CeilToInt(cx + boundRadius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(cy - boundRadius));
        int maxY = Mathf.Min(texH - 1, Mathf.CeilToInt(cy + boundRadius));
        if (minX > maxX || minY > maxY) return;
        float cosR = Mathf.Cos(-rotRad), sinR = Mathf.Sin(-rotRad);
        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                float dx = (px + 0.5f) - cx;
                float dy = (py + 0.5f) - cy;
                float lx = dx * cosR - dy * sinR;
                float ly = dx * sinR + dy * cosR;
                float qx = Mathf.Max(Mathf.Abs(lx) - (half - radius), 0f);
                float qy = Mathf.Max(Mathf.Abs(ly) - (half - radius), 0f);
                float dist = Mathf.Sqrt(qx * qx + qy * qy) - radius;
                float coverage = Mathf.Clamp01(0.5f - dist);
                if (coverage <= 0f) continue;
                int idx = py * texW + px;
                pixels[idx] = Color.Lerp(pixels[idx], color, alpha * coverage);
            }
        }
    }

    // Proper rounded-rect SDF (correct on straight edges AND corners,
    // unlike RoundedRectCoverage's corner-only distance) — needed here
    // because the border ring has to trace the boundary accurately all
    // the way around, not just look right in the corners. Dash pattern
    // applied along the straight-edge zones only (corners render solid —
    // standard practice for dashed rounded rects, and the corners are a
    // small fraction of the perimeter anyway).
    static Texture2D GenerateDashedBorderTexture(int w, int h, float radius, float thickness, Color color, float dashLen, float gapLen)
    {
        w = Mathf.Max(2, w); h = Mathf.Max(2, h);
        radius = Mathf.Min(radius, Mathf.Min(w, h) / 2f);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[w * h]; // defaults transparent
        float halfW = w / 2f - radius, halfH = h / 2f - radius;
        float period = dashLen + gapLen;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) - w / 2f;
                float fy = (y + 0.5f) - h / 2f;
                float dx = Mathf.Abs(fx) - halfW;
                float dy = Mathf.Abs(fy) - halfH;
                float outsideDist = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) + Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                float insideDist = Mathf.Min(Mathf.Max(dx, dy), 0f);
                float sdf = outsideDist + insideDist - radius;
                float borderCoverage = Mathf.Clamp01(thickness / 2f - Mathf.Abs(sdf) + 0.5f);
                if (borderCoverage <= 0f) continue;

                bool inCorner = dx > 0f && dy > 0f;
                float dashCoverage = 1f;
                if (!inCorner)
                {
                    float alongCoord = dx > dy ? fy : fx; // nearer a vertical edge -> dash along Y; nearer a horizontal edge -> dash along X
                    float posInPeriod = Mathf.Repeat(alongCoord, period);
                    dashCoverage = posInPeriod < dashLen ? 1f : 0f;
                }
                float finalAlpha = color.a * borderCoverage * dashCoverage;
                if (finalAlpha <= 0f) continue;
                pixels[y * w + x] = new Color(color.r, color.g, color.b, finalAlpha);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    static void ApplyBackplateGradient(GameObject canvasGo)
    {
        // CHANGED 2026-09-17, second pass: colors/radius used to be
        // hardcoded literals, then briefly a separate ScriptableObject
        // asset — the user correctly wanted them on the object itself
        // instead (see HDMRBackplateStyle.cs and
        // CaptureTileBackplateStyleBeforeWipe's comment for why a plain
        // component needs that capture step to survive this script's
        // full-scene-wipe rebuild). Falls back to the original design
        // values only on the very first build, when there's nothing to
        // carry forward yet.
        var top = _capturedTileBackplateStyle.found ? _capturedTileBackplateStyle.top : HexColor(0x2c4653);
        var bottom = _capturedTileBackplateStyle.found ? _capturedTileBackplateStyle.bottom : HexColor(0x21353f);
        float radius = _capturedTileBackplateStyle.found ? _capturedTileBackplateStyle.radius : 20f;

        var canvasRoot = canvasGo.transform.Find("CanvasRoot");
        var backplate = canvasRoot != null ? canvasRoot.Find("UIBackplate") : null;
        var backplateRt = backplate != null ? backplate.GetComponent<RectTransform>() : null;
        if (backplate == null || backplateRt == null) { Debug.LogWarning("[HDMRBuildHome] ApplyBackplateGradient: UIBackplate not found"); return; }

        // FOUND 2026-09-16, same root cause already diagnosed and fixed on
        // the PREVIEW panel's backplate (SetupStreamPreviewPanel) but never
        // applied here: this backplate ALSO ships tagged
        // "QDSUIBackplateGradient" with the "RoundedBoxGradientUIDark"
        // material, and setting `.sprite` on the EXISTING Image component
        // silently doesn't stick (confirmed null even in Edit Mode right
        // after rebuild, not just a Play Mode reset this time). Same fix:
        // strip the QDS baggage and rebuild the Image component fresh
        // rather than fight whatever keeps clearing it.
        backplate.gameObject.tag = "Untagged";
        var oldMask = backplate.GetComponent<UnityEngine.UI.Mask>();
        if (oldMask != null) UnityEngine.Object.DestroyImmediate(oldMask);
        var oldRoundedBox = backplate.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
        if (oldRoundedBox != null) UnityEngine.Object.DestroyImmediate(oldRoundedBox);
        var oldImg = backplate.GetComponent<UnityEngine.UI.Image>();
        if (oldImg != null) UnityEngine.Object.DestroyImmediate(oldImg);
        var img = backplate.gameObject.AddComponent<UnityEngine.UI.Image>();

        int w = Mathf.RoundToInt(backplateRt.sizeDelta.x);
        int h = Mathf.RoundToInt(backplateRt.sizeDelta.y);
        var tex = GenerateRoundedRectTexture(w, h, radius, top, bottom); // --deck-radius, from config
        // Sliced (9-slice) instead of Simple, on direct request: this is
        // the "menu backplate" the corners were observed flattening on
        // when resized by hand in the Editor after build. Sliced keeps the
        // corner radius fixed-size under any resize — see
        // CreateSlicedRoundedSprite's header comment for why.
        img.sprite = CreateSlicedRoundedSprite(tex, w, h, radius);
        img.type = UnityEngine.UI.Image.Type.Sliced;
        img.color = Color.white;

        // Put the actual tunable values on the object itself — select
        // this backplate in the Hierarchy, edit color/radius/size right
        // in its own Inspector like any other Unity component. Re-applies
        // the (possibly carried-forward) values here too so the component
        // and the rendered sprite can never drift apart.
        var style = backplate.GetComponent<Hdmr.Home.HDMRBackplateStyle>();
        if (style == null) style = backplate.gameObject.AddComponent<Hdmr.Home.HDMRBackplateStyle>();
        style.topColor = top;
        style.bottomColor = bottom;
        style.cornerRadius = radius;
        style.useManualSize = _capturedTileBackplateStyle.found && _capturedTileBackplateStyle.useManualSize;
        style.manualSize = _capturedTileBackplateStyle.found ? _capturedTileBackplateStyle.manualSize : backplateRt.sizeDelta;
    }

    // ADDED 2026-09-16, previously missing entirely: the prototype's
    // ".hinge" — a small pill between the preview and the tile panel, "a
    // nod to the 3DS's physical hinge between its two screens" per the
    // prototype's own comment. 7px tall / ~38% of the deck's width in CSS,
    // background: linear-gradient(180deg, var(--backplate-hi), var(--backplate)).
    //
    // FOUND 2026-09-16 via an actual rendered comparison against the real
    // prototype: this first shipped as a sharp-cornered flat-color quad —
    // the real element is a full PILL (fully rounded ends) with the same
    // gradient as the deck, not a flat rectangle. A 3D Quad can't round
    // its own corners the way a UI Image can (no builtin masking), so this
    // now textures it with a baked rounded/pill alpha mask (radius = half
    // the texture height = a true pill, not just rounded corners) on an
    // unlit transparent material, reusing the same generator as the two
    // canvas backplates.
    // FOUND 2026-09-17, prompted by a direct question about whether
    // anything got moved: the Hinge and Aura Y positions used to be
    // hardcoded constants (-0.223 / -0.317) tuned once against the
    // backplates' world position AT THE TIME. That position itself was
    // silently wrong back then — the backplate anchor bug (anchorMin/
    // anchorMax never set) meant the backplate wasn't actually centered on
    // its content — and fixing THAT bug this session moved the backplate's
    // real top/bottom edges without touching these two constants, which
    // desynced instantly and invisibly (no error, no console warning, the
    // hinge just silently stopped rendering in the gap it was supposed to
    // sit in — confirmed via a screenshot of the gap showing nothing there
    // and a numeric check showing the hinge landing ~0.28m away from
    // either backplate edge). A hardcoded value tuned against another
    // system's CURRENT geometry will always go stale the next time that
    // geometry changes for any reason — computing it from the real,
    // current backplate corners each rebuild removes that failure mode
    // entirely instead of just re-tuning the same fragile constant again.
    static float TileBackplateTopY(GameObject anchorGo)
    {
        var rt = anchorGo.transform.Find("HomeCanvas (placeholder)/CanvasRoot/UIBackplate") as RectTransform;
        if (rt == null) return float.NaN;
        var c = new Vector3[4]; rt.GetWorldCorners(c);
        return (c[1].y + c[2].y) / 2f; // TL, TR
    }
    static float TileBackplateBottomY(GameObject anchorGo)
    {
        var rt = anchorGo.transform.Find("HomeCanvas (placeholder)/CanvasRoot/UIBackplate") as RectTransform;
        if (rt == null) return float.NaN;
        var c = new Vector3[4]; rt.GetWorldCorners(c);
        return (c[0].y + c[3].y) / 2f; // BL, BR
    }
    static float PreviewBackplateBottomY(GameObject anchorGo)
    {
        var rt = anchorGo.transform.Find("StreamPreviewCanvas (placeholder)/CanvasRoot/UIBackplate") as RectTransform;
        if (rt == null) return float.NaN;
        var c = new Vector3[4]; rt.GetWorldCorners(c);
        return (c[0].y + c[3].y) / 2f; // BL, BR
    }
    static float PreviewBackplateTopY(GameObject anchorGo)
    {
        var rt = anchorGo.transform.Find("StreamPreviewCanvas (placeholder)/CanvasRoot/UIBackplate") as RectTransform;
        if (rt == null) return float.NaN;
        var c = new Vector3[4]; rt.GetWorldCorners(c);
        return (c[1].y + c[2].y) / 2f; // TL, TR
    }

    static void AddHinge(GameObject anchorGo)
    {
        var hingeGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
        hingeGo.name = "Hinge";
        UnityEngine.Object.DestroyImmediate(hingeGo.GetComponent<Collider>());
        hingeGo.transform.SetParent(anchorGo.transform, false);
        float tileTopY = TileBackplateTopY(anchorGo);
        float previewBottomY = PreviewBackplateBottomY(anchorGo);
        float hingeY = (!float.IsNaN(tileTopY) && !float.IsNaN(previewBottomY))
            ? (tileTopY + previewBottomY) / 2f
            : -0.223f; // fallback if the backplates aren't found — should not happen given call order in Build()
        hingeGo.transform.localPosition = new Vector3(0f, hingeY, 0f); // centered in the ACTUAL current gap, measured fresh every rebuild
        hingeGo.transform.localRotation = Quaternion.identity;
        hingeGo.transform.localScale = new Vector3(0.22f, 0.015f, 1f);

        const int texW = 220, texH = 16;
        var hingeConfig = Hdmr.Editor.HDMRHomeDesignConfig.LoadOrCreate();
        var tex = GenerateRoundedRectTexture(texW, texH, texH / 2f, hingeConfig.hingeTop, hingeConfig.hingeBottom); // full pill, colors from config
        var mat = new Material(Shader.Find("Unlit/Transparent"));
        mat.mainTexture = tex;
        hingeGo.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    static RectTransform CreatePreviewLabel(Transform parent, string name, string text, Vector2 anchoredPos, Vector2 size, float fontSize, Color color, TMPro.TMP_FontAsset font)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        var tmp = go.AddComponent<TMPro.TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        if (font != null) tmp.font = font;
        return rt;
    }

    static RectTransform CreatePreviewRect(Transform parent, string name, Vector2 anchoredPos, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        go.GetComponent<UnityEngine.UI.Image>().color = color;
        return rt;
    }

    // Wires up the "Mode MR" tile's real effect (see PassthroughController.cs
    // and LogTileClickTarget's "mr" case): a real OVRPassthroughLayer, a
    // stand-in "aura" visual for when it's off, and the reference floor
    // (created earlier in EnsureScene()) that only makes sense while
    // passthrough is off. Split into its own method (rather than living
    // inside the canvasPrefab block above) since none of this actually
    // needs the canvas — it needs the camera rig and the floor, which
    // already exist by the time this runs.
    static void SetupPassthroughAndAura(GameObject rigGo, GameObject anchorGo)
    {
        var ceCam = rigGo.GetComponent<OVRCameraRig>().centerEyeAnchor.GetComponent<Camera>();

        // Per Meta's own passthrough getting-started doc (verified via
        // meta_docs_get_page this session): "Create a new empty GameObject
        // in the scene... Add Component... OVR Passthrough Layer". Starts
        // disabled — PassthroughController.Awake() calls Apply(false) and
        // would disable it anyway, but authoring it disabled here means the
        // Scene view (before Play) doesn't show a passthrough layer that's
        // about to be forced off.
        var passthroughGo = new GameObject("PassthroughLayer");
        var passthroughLayer = passthroughGo.AddComponent<OVRPassthroughLayer>();
        passthroughLayer.enabled = false;

        // Stand-in for the "transparent aura behind the UI" the prototype
        // calls for. NOT the real soft-glow effect described — that needs
        // an actual shader/gradient pass, out of scope for a placeholder —
        // just a flat-colored quad behind the two panels so passthrough-off
        // has SOME backdrop instead of the raw sky gradient.
        //
        // FIXED while verifying with a screenshot: first version set
        // transform.position/.rotation directly in WORLD space (0,0,2.4)
        // — the exact "fixed world coordinate" bug SpawnInFrontOfHead.cs's
        // own header comment already warns about, just repeated here for
        // the aura. From the actual intended viewing distance (~1.2m,
        // where the anchor puts the user) that also came out FAR too big
        // (4x3m) and swallowed the whole view, hiding both panels behind
        // it. Real fix: parent under the same HomeAnchor the panels use,
        // sized to just wrap their combined footprint (~0.65m wide,
        // ~1.4m tall stacked) with a modest halo margin, not a wall.
        var auraGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
        auraGo.name = "AuraBackground (placeholder)";
        UnityEngine.Object.DestroyImmediate(auraGo.GetComponent<Collider>());
        auraGo.transform.SetParent(anchorGo.transform, false);
        // FIXED AGAIN after a second screenshot: even shrunk to 1.6x1.8m
        // at only 0.6m behind the anchor, it still filled the ENTIRE
        // frame from the intended ~1.2m viewing distance — at VR-scale
        // proximity, angular size dominated over "looks reasonably
        // panel-sized" intuition from a top-down mental image. Pushed
        // further back (more distance shrinks the subtended angle faster
        // than shrinking the mesh does) and sized to the panels' actual
        // combined footprint (~0.6m wide, ~1.4m tall stacked) plus a
        // halo margin, not a guessed round number.
        // CORRECTED 2026-09-17 — this used to be a hardcoded -0.317
        // tuned once against the backplates' position at the time. Same
        // failure mode as the Hinge (see TileBackplateTopY's comment):
        // fixing the backplate anchor bug this session moved the real
        // panel edges without this constant knowing, so the aura silently
        // stopped being centered on the composition it's supposed to frame.
        // Computed fresh from the actual current backplate edges instead.
        float tileBottomY = TileBackplateBottomY(anchorGo);
        float previewTopY = PreviewBackplateTopY(anchorGo);
        float auraY = (!float.IsNaN(tileBottomY) && !float.IsNaN(previewTopY))
            ? (tileBottomY + previewTopY) / 2f
            : -0.317f; // fallback if the backplates aren't found — should not happen given call order in Build()
        auraGo.transform.localPosition = new Vector3(0f, auraY, 1.2f);
        auraGo.transform.localRotation = Quaternion.identity; // same facing as the panels — anchor already orients the whole group
        auraGo.transform.localScale = new Vector3(1.1f, 1.7f, 1f);
        var auraRenderer = auraGo.GetComponent<MeshRenderer>();
        // CORRECTED 2026-09-16 after the user directly caught that this
        // was still a flat placeholder color with none of the real
        // prototype's visual richness: the real `#bg` is an animated
        // 3-layer parallax grid of small rounded squares (read directly
        // from drawBG() in the prototype's own source, not approximated —
        // see GenerateParallaxGridTexture). Baked here as a single STATIC
        // frame (driftT=0, no live scroll/parallax) rather than a live
        // per-frame redraw — a deliberate simplification for a background
        // element, not a missed requirement: a live-scrolling backdrop
        // behind a head-locked VR menu risks vection/discomfort the
        // prototype (a flat 2D web page) never had to consider, and Meta's
        // own comfort guidance is exactly "avoid unnecessary background
        // motion". Still uses the exact same per-cell shapes, jitter
        // formula, colors and alphas as the real thing, not a guess.
        var auraTex = GenerateParallaxGridTexture(384, 594); // matches the quad's own ~1.1:1.7 aspect ratio
        // CORRECTED 2026-09-17: was Unlit/Texture (fully opaque — ignores
        // the texture's own alpha channel entirely), which is exactly why
        // this rendered as a solid wall no matter what alpha values the
        // texture itself carried. Unlit/Transparent actually blends by
        // alpha, which the radial-falloff + reduced peak alpha in
        // GenerateParallaxGridTexture depend on to read as a glow instead
        // of a panel.
        var auraMat = new Material(Shader.Find("Unlit/Transparent"));
        auraMat.mainTexture = auraTex;
        auraRenderer.sharedMaterial = auraMat;

        var floorGo = GameObject.Find("Floor (reference)");

        var pcGo = new GameObject("PassthroughController");
        var pc = pcGo.AddComponent<Hdmr.Home.PassthroughController>();
        var pcSo = new SerializedObject(pc);
        pcSo.FindProperty("_passthroughLayer").objectReferenceValue = passthroughLayer;
        pcSo.FindProperty("_centerEyeCamera").objectReferenceValue = ceCam;
        pcSo.FindProperty("_referenceFloor").objectReferenceValue = floorGo;
        pcSo.FindProperty("_auraBackground").objectReferenceValue = auraGo;
        pcSo.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[HDMRBuildHome] passthrough infra wired: layer=" + (passthroughLayer != null) +
            " floor=" + (floorGo != null) + " aura=" + (auraGo != null));
    }

    // FOUND 2026-09-16, via Unity MCP live inspection — every tile came out
    // visually as Meta's raw default QDS "Quest_Dark" plate because
    // EmptyUIBackplateWithCanvas's UIThemeManager ships with an EMPTY
    // _themes array and _currentThemeIndex = -1 (its own ApplyTheme() logs
    // an error and no-ops on that). This is the actual, Meta-supported
    // re-skinning mechanism (Oculus.Interaction.UITheme /
    // Oculus.Interaction.UIThemeManager, see Runtime/Sample/Objects/UISet/
    // Scripts/ + Themes/*.asset) — NOT something to hand-roll by tinting
    // child Images: UIThemeManager.ApplyTheme() explicitly skips
    // QDSUIToggleButton-tagged images ("Colors are applied though
    // animation clips") and instead swaps each button's
    // RuntimeAnimatorController per theme. Quest_Light's plate colour
    // (bright "paper", dark ink text) is a close match for the prototype's
    // --tile:#e9eef0 palette — assign it wholesale via the real API rather
    // than hand-tinting individual Images, which the manager would fight
    // (or silently no-op on) every time ApplyTheme/RefreshAnimators runs.
    // Exact hex-for-hex palette porting (a real custom UITheme asset with
    // our --void/--backplate/--amber/--aqua values) is a separate, larger
    // pass — this gets the UI out of "unthemed default" for now.
    const string HdmrThemePath = "Assets/UI/Themes/UIThemeHDMR.asset";

    // Degrees the tile panel tilts back (rotated around local X) so its
    // face angles up toward the user like a lectern/podium, per the
    // prototype reference image (2026-09-16). Sign verified live via
    // RectTransform.GetWorldCorners() in Play Mode: bottom edge closer to
    // the user, top edge farther away — the intended lectern read. Exact
    // magnitude not measured off the image — a plausible reading-angle
    // value, tune by eye.
    const float PodiumTiltDegrees = 18f;

    // The prototype's real palette (read from the published "HDMR Home"
    // Artifact's CSS custom properties on 2026-09-16 — not guessed):
    // --void #0f1b24, --backplate #21353f, --backplate-hi #2c4653,
    // --tile #e9eef0, --tile-ink #17262e, --amber #ff9d4d (select/decide
    // glow), --aqua #47c4cf (cursor highlight). Only `backplateColor` is a
    // plain Color field UIThemeManager applies directly (see
    // ApplyUiTheme's comment) — button-plate colors are baked into
    // per-theme animator clips (acToggleButton etc.), a separate, bigger
    // job for an exact per-element match. This gets the single biggest,
    // most visible piece right cheaply: a dark deck behind light tiles,
    // matching the prototype's actual composition, instead of Quest_Light's
    // all-white panel.
    static Color HexColor(uint rgb) => new Color32(
        (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);

    static Oculus.Interaction.UITheme EnsureHdmrTheme()
    {
        var theme = AssetDatabase.LoadAssetAtPath<Oculus.Interaction.UITheme>(HdmrThemePath);
        if (theme == null)
        {
            var guids = AssetDatabase.FindAssets("UIThemeQuest_Light t:Oculus.Interaction.UITheme");
            if (guids.Length == 0) { Debug.LogError("[HDMRBuildHome] UIThemeQuest_Light asset not found — package missing/not resolved?"); return null; }
            string srcPath = AssetDatabase.GUIDToAssetPath(guids[0]);

            Directory.CreateDirectory("Assets/UI/Themes");
            if (!AssetDatabase.CopyAsset(srcPath, HdmrThemePath))
            { Debug.LogError($"[HDMRBuildHome] failed to copy {srcPath} -> {HdmrThemePath}"); return null; }

            AssetDatabase.ImportAsset(HdmrThemePath);
            theme = AssetDatabase.LoadAssetAtPath<Oculus.Interaction.UITheme>(HdmrThemePath);
            if (theme == null) { Debug.LogError("[HDMRBuildHome] failed to load copied HDMR theme asset"); return null; }
        }

        // Re-applied every call (not just on first creation) — this theme
        // is actively being iterated on this session, so keep it in sync
        // with the palette/font below rather than only setting it once.
        theme.backplateColor = HexColor(0x21353f); // --backplate

        // --round (Baloo 2) is the prototype's in-experience label face —
        // see reference-pack.md. Downloaded from Google Fonts' own GitHub
        // mirror (google/fonts, OFL) 2026-09-16 and generated as a DYNAMIC
        // TMP SDF atlas (populates glyphs on demand) specifically so French
        // accented characters (é/è/à/ç…) render without hand-listing a
        // character set. UIThemeManager.ApplyTheme() only ever reads
        // textFontMedium (see its source — Regular/Bold are set for
        // completeness/future use but not currently consumed), so that's
        // the field that actually matters for tile/tabbar labels.
        var baloo = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>("Assets/UI/Fonts/Baloo2 SDF.asset");
        if (baloo != null)
        {
            theme.textFontMedium = baloo;
            theme.textFontRegular = baloo;
            theme.textFontBold = baloo;
        }
        else Debug.LogWarning("[HDMRBuildHome] Baloo2 SDF font asset not found at Assets/UI/Fonts/Baloo2 SDF.asset — labels stay on the default TMP font");

        EditorUtility.SetDirty(theme);
        AssetDatabase.SaveAssets();
        Debug.Log("[HDMRBuildHome] created " + HdmrThemePath + " (backplateColor = --backplate #21353f, font=" + (baloo != null ? "Baloo2" : "default") + ")");
        return theme;
    }

    static void ApplyUiTheme(GameObject canvasGo)
    {
        var themeManager = canvasGo.GetComponentInChildren<Oculus.Interaction.UIThemeManager>(true);
        if (themeManager == null) { Debug.LogWarning("[HDMRBuildHome] no UIThemeManager found on canvas — theme not applied"); return; }

        var themeAsset = EnsureHdmrTheme();
        if (themeAsset == null) return;

        var so = new SerializedObject(themeManager);
        var themesProp = so.FindProperty("_themes");
        themesProp.arraySize = 1;
        themesProp.GetArrayElementAtIndex(0).objectReferenceValue = themeAsset;
        so.FindProperty("_currentThemeIndex").intValue = 0;
        so.ApplyModifiedPropertiesWithoutUndo();

        themeManager.ApplyCurrentTheme();
        Debug.Log("[HDMRBuildHome] UI theme applied: " + themeAsset.name);
    }

    // FOUND 2026-09-16 comparing a live Play Mode screenshot against the
    // real prototype: every tile's label text rendered as a flat neutral
    // gray (0.153,0.153,0.153) instead of the intended per-tile ink color
    // (--tile-ink on light tiles, --text/--text-dim on the tabbar) — even
    // though AddTileRow demonstrably sets the right color at BUILD time
    // (confirmed correct by reading it back in Edit Mode). Root cause:
    // UIThemeManager (still a live, enabled component on the canvas after
    // ApplyUiTheme() runs) calls its OWN ApplyTheme() again from Start(),
    // which unconditionally sets `text.color = selectedTheme.textPrimaryColor`
    // on every TMP_Text under the canvas — our theme asset never set that
    // field, so it fell back to whatever gray came from the copied
    // UIThemeQuest_Light source theme. Confirmed by reading
    // UIThemeManager.cs directly, not guessed. This is the exact same
    // class of bug as the Background-Animator clobber already fixed
    // above (a live component re-binding its own defaults at runtime) —
    // same fix: destroy the component outright once we're done using its
    // build-time API surface, so its Start() can never fire again. Must
    // run AFTER every AddTileRow/AddStatusBar call for this canvas, not
    // before.
    static void LockInUiColors(GameObject canvasGo)
    {
        var themeManager = canvasGo.GetComponentInChildren<Oculus.Interaction.UIThemeManager>(true);
        if (themeManager != null) UnityEngine.Object.DestroyImmediate(themeManager);
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
    // sub = the prototype's actual .sub caption per tile (hdmr-home.html
    // TILES array) — was never ported before (the prefab's "Label (1)"
    // subtitle element sat on its default placeholder text "Secondary"
    // the whole time, found 2026-09-16 while chasing the plate-color bug).
    static readonly (string label, string key, string sub)[] PrimaryTiles =
    {
        ("Lancer le mode\nStéréo VR", "vr",  "0dof · head-locked"),
        ("Calibrage\nstéréo",         "cal", "k1 k2 split"),
    };
    static readonly (string label, string key, string sub)[] SecondaryTiles =
    {
        ("Mode MR",        "mr",           "6dof"),
        ("Cinéma",         "cine",         "ambiance"),
        ("Profil 1",       "profile0",     "switch2"),
        ("Nouveau profil", "profile1-new", "+ créer"),
        ("Étalonnage",     "etal",         "gain gamma lift"),
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

        // FIXED 2026-09-16 — EmptyUIBackplateWithCanvas ships CanvasRoot
        // with an ACTIVE HorizontalLayoutGroup (spacing=50). Confirmed via
        // live Unity MCP inspection: it silently overrides every
        // RectTransform.anchoredPosition AddTileRow sets below, collapsing
        // the 3 intended rows (primary tiles / secondary tiles / tabbar)
        // into a single horizontal line. Matches the user's exact report
        // ("des boutons alignés"). We do manual absolute per-row
        // positioning, so disable whatever auto-layout the sample shipped
        // with rather than fight it.
        var layoutGroup = canvasRoot.GetComponent<UnityEngine.UI.LayoutGroup>();
        if (layoutGroup != null)
        {
            layoutGroup.enabled = false;
            Debug.Log($"[HDMRBuildHome] disabled {layoutGroup.GetType().Name} on CanvasRoot (was overriding manual tile layout)");
        }

        var tileButtonPrefab = LoadUniquePrefab("TextTileButton_IconAndLabel_Regular_UnityUIButton");
        if (tileButtonPrefab == null) return;

        // CORRECTED 2026-09-16 against the real prototype CSS:
        // `.tile.primary { aspect-ratio: 5/3 }` — wider than tall, not
        // square (180x108, not 180x180). `.tile.secondary.wide` (MR /
        // Cinéma specifically) are narrower than the other secondary
        // tiles so the pair spans one primary column's width together:
        // 2 * wideW + gap(14) = primary tileW(180) -> wideW = 83.
        int n1 = AddTileRow(canvasRoot, tileButtonPrefab, PrimaryTiles, y: 80f, tileW: 180f, gap: 20f, primaryAccent: true, tileH: 108f);
        int n2 = AddTileRow(canvasRoot, tileButtonPrefab, SecondaryTiles, y: -100f, tileW: 100f, gap: 14f,
            widthOverride: key => (key == "mr" || key == "cine") ? 83f : 100f);
        Debug.Log($"[HDMRBuildHome] tiles added under {canvasRoot.name}: {n1} primary, {n2} secondary");
    }

    // Prototype's bottom bar: Profils | Accueil | Paramètres. Same
    // placeholder-button approach as the tiles — real 3DS-style dividers,
    // centring and the "flush with the bounding box" look (per the user's
    // explicit description this session) are layout/skin, not structure.
    static readonly (string label, string key, string sub)[] TabbarItems =
    {
        ("Profils",    "profils",    ""),
        ("Accueil",    "accueil",    ""),
        ("Paramètres", "parametres", ""),
    };

    static void AddTabbar(GameObject canvasGo)
    {
        var canvasRoot = canvasGo.transform.Find("CanvasRoot");
        if (canvasRoot == null) { Debug.LogError("[HDMRBuildHome] CanvasRoot not found for tabbar"); return; }
        var tileButtonPrefab = LoadUniquePrefab("TextTileButton_IconAndLabel_Regular_UnityUIButton");
        if (tileButtonPrefab == null) return;

        int n = AddTileRow(canvasRoot, tileButtonPrefab, TabbarItems, y: -240f, tileW: 130f, gap: 0f, flush: true);
        Debug.Log($"[HDMRBuildHome] tabbar added under {canvasRoot.name}: {n} items");
    }

    // CORRECTED 2026-09-16 against the actual prototype artifact ("HDMR
    // Home", not the earlier reference screenshot alone): the real
    // element is `<p class="hint">` — ONE combined line
    // "↑↓←→ naviguer ↵ sélectionner", not two separate hints split left
    // and right ("1↓→ naviguer" / "○ sélectionner" was a misreading of a
    // different, controller-specific rendering nowhere in the actual
    // prototype source). It's also a SIBLING of the deck panel in the
    // prototype's DOM, sitting below it on the bare background — not
    // printed inside the backplate. Reproduced here by positioning below
    // the ALREADY-FIT backplate's bottom edge (must run after
    // FitBackplateToContent, not before) rather than folding it into that
    // panel's own bounds.
    static void AddStatusBar(GameObject canvasGo)
    {
        var canvasRoot = canvasGo.transform.Find("CanvasRoot");
        if (canvasRoot == null) { Debug.LogError("[HDMRBuildHome] CanvasRoot not found for status bar"); return; }
        var backplate = canvasRoot.Find("UIBackplate");
        if (backplate == null) { Debug.LogError("[HDMRBuildHome] UIBackplate not found for status bar placement"); return; }
        var backplateRt = backplate.GetComponent<RectTransform>();
        float belowBackplateY = backplateRt.anchoredPosition.y - backplateRt.sizeDelta.y / 2f - 30f;

        // FOUND live in Play Mode, twice: neither Baloo2 SDF (the --round
        // label font used everywhere else) NOR TMP's own default
        // LiberationSans SDF fallback has a ↵ glyph — both silently
        // substituted a missing-glyph box. Spelling it out as "Entrée"
        // instead of the prototype's literal ↵ character keeps the same
        // meaning without depending on a symbol this project's fonts
        // don't carry.
        var dimColor = HexColor(0x8ba1a9); // --text-dim
        CreatePreviewLabel(canvasRoot, "StatusHint", "↑↓←→ naviguer  ·  Entrée sélectionner",
            new Vector2(0f, belowBackplateY), new Vector2(420f, 24f), 14f, dimColor, null);

        Debug.Log("[HDMRBuildHome] status hint added below panel (not inside backplate — matches prototype's DOM placement)");
    }

    // FOUND 2026-09-16, via Unity MCP live RectTransform.GetWorldCorners
    // measurement + a real manage_camera screenshot
    // (screenshot-20260915-222153.png): UIBackplate — the dark background
    // plate EmptyUIBackplateWithCanvas ships behind CanvasRoot's content —
    // still has its ORIGINAL prefab pivot/anchoredPosition/sizeDelta
    // (pivot (1,0), sized to exactly cover CanvasRoot's own declared
    // 500x500 box). AddTileRow's absolute tile placement (see
    // PrimaryTiles/SecondaryTiles/TabbarItems y/tileW/gap above) spans a
    // real content box roughly 556x475 in the same anchoredPosition space
    // and off-center from that original 500x500 box, so the two were
    // never aligned. Nothing clips (Canvas has no Mask), so this isn't
    // "invisible" — it's a real, visible bug confirmed in the screenshot:
    // the backplate only shows behind the top-left sliver of the first
    // primary tile, and every other tile plus the whole tabbar render with
    // no backdrop at all.
    //
    // This does NOT touch tile placement (AddTileRow) or CanvasRoot's own
    // size — both tiles and UIBackplate anchor off the same CanvasRoot
    // corner (anchorMin/Max (0,0)), so resizing CanvasRoot itself would
    // shift both by the same amount and fix nothing; the only thing
    // actually wrong is UIBackplate's own rect. Fixing it in this build
    // script (not just live in the open scene) is required because Build()
    // re-instantiates EmptyUIBackplateWithCanvas from the prefab and reruns
    // AddHomeTiles/AddTabbar every time — a scene-only edit would be lost
    // on the next rebuild.
    static void FitBackplateToContent(GameObject canvasGo)
    {
        var canvasRoot = canvasGo.transform.Find("CanvasRoot");
        if (canvasRoot == null) { Debug.LogWarning("[HDMRBuildHome] FitBackplateToContent: CanvasRoot not found"); return; }
        var backplate = canvasRoot.Find("UIBackplate");
        if (backplate == null) { Debug.LogWarning("[HDMRBuildHome] FitBackplateToContent: UIBackplate not found"); return; }
        var backplateRt = backplate.GetComponent<RectTransform>();
        if (backplateRt == null) return;

        bool any = false;
        float xMin = 0, xMax = 0, yMin = 0, yMax = 0;
        foreach (RectTransform rt in canvasRoot)
        {
            if (!rt.gameObject.name.StartsWith("Tile_")) continue;
            float rtXMin = rt.anchoredPosition.x - rt.sizeDelta.x * rt.pivot.x;
            float rtXMax = rtXMin + rt.sizeDelta.x;
            float rtYMin = rt.anchoredPosition.y - rt.sizeDelta.y * rt.pivot.y;
            float rtYMax = rtYMin + rt.sizeDelta.y;
            if (!any) { xMin = rtXMin; xMax = rtXMax; yMin = rtYMin; yMax = rtYMax; any = true; }
            else
            {
                xMin = Mathf.Min(xMin, rtXMin); xMax = Mathf.Max(xMax, rtXMax);
                yMin = Mathf.Min(yMin, rtYMin); yMax = Mathf.Max(yMax, rtYMax);
            }
        }
        if (!any) { Debug.LogWarning("[HDMRBuildHome] FitBackplateToContent: no Tile_ children found under CanvasRoot"); return; }

        const float pad = 40f; // visual breathing room around the outermost tile edges
        xMin -= pad; xMax += pad; yMin -= pad; yMax += pad;

        // FOUND 2026-09-17: this only ever set `.pivot`, never
        // anchorMin/anchorMax — same bug just found and fixed on the
        // preview panel's backplate (see SetupStreamPreviewPanel's
        // comment for the full story), present here since this method was
        // first written. `anchoredPosition` is measured from the ANCHOR,
        // not the pivot; with the prefab's original corner anchor left in
        // place, this "(xMin+xMax)/2" center-content math was silently
        // computing the wrong position the whole time. Less visually
        // obvious here than on the preview panel (this backplate's
        // content genuinely is roughly centered in the CanvasRoot by
        // coincidence of the tile layout), but it's the same bug and
        // needs the same fix.
        backplateRt.anchorMin = backplateRt.anchorMax = backplateRt.pivot = new Vector2(0.5f, 0.5f);
        backplateRt.anchoredPosition = new Vector2((xMin + xMax) / 2f, (yMin + yMax) / 2f);

        // ADDED 2026-09-17: if the backplate had a manual size set on its
        // own HDMRBackplateStyle component (captured before this rebuild's
        // scene wipe — see CaptureTileBackplateStyleBeforeWipe), honor it
        // instead of always recomputing a tight content-fit. Still centers
        // on the actual content either way — only the SIZE is overridden,
        // not the position, so a manual size doesn't drift off-center as
        // tile content changes.
        backplateRt.sizeDelta = (_capturedTileBackplateStyle.found && _capturedTileBackplateStyle.useManualSize)
            ? _capturedTileBackplateStyle.manualSize
            : new Vector2(xMax - xMin, yMax - yMin);
        Debug.Log($"[HDMRBuildHome] UIBackplate refit: center=({backplateRt.anchoredPosition.x:F0},{backplateRt.anchoredPosition.y:F0}) size=({backplateRt.sizeDelta.x:F0}x{backplateRt.sizeDelta.y:F0}) manualSize={(_capturedTileBackplateStyle.found && _capturedTileBackplateStyle.useManualSize)}");
    }

    // --tile / --tile-ink from the prototype's real palette (see
    // ApplyUiTheme's comment for where these came from).
    static readonly Color TilePlateColor = HexColor(0xe9eef0);
    static readonly Color TileInkColor = HexColor(0x17262e);

    // The prototype's real per-tile icons (hdmr-home.html's iconHeadset()/
    // iconStereo()/etc., all simple `stroke="currentColor"` line-art SVGs,
    // viewBox 0 0 24 24) — extracted from the published Artifact source
    // 2026-09-16, rendered to 128x128 PNG via `npx resvg-cli` (no local
    // SVG tooling was otherwise available), imported as Sprites under
    // Assets/UI/Icons/. Tabbar items intentionally have no entry here —
    // the prototype's tabbar is plain text, no icons; they keep the
    // prefab's default placeholder icon (a known, separate gap — see
    // memory notes on the tabbar's flush/divided look not being ported).
    static readonly System.Collections.Generic.Dictionary<string, string> TileIcons =
        new System.Collections.Generic.Dictionary<string, string>
    {
        { "vr",           "icon_headset" },
        { "cal",          "icon_stereo" },
        { "mr",           "icon_mr" },
        { "cine",         "icon_cinema" },
        { "profile0",     "icon_profile" },
        { "profile1-new", "icon_addprofile" },
        { "etal",         "icon_sun" },
    };

    // flush=true renders the prototype's tabbar look instead of a tile
    // card: no background plate (`background: none` in .tabbar button),
    // no icon (the tabbar is plain text — nothing in TileIcons targets
    // tabbar keys anyway), text --text-dim except the one starting tile
    // marked "active" reads --text (prototype's `[aria-current="true"]`
    // — Accueil starts active, matching hdmr-home.html's default tab).
    // The real divider lines / flush-with-panel-edge look from the
    // prototype's CSS is still not ported — that needs restructuring away
    // from individually-carded buttons entirely, a bigger layout change
    // left for later; this at least stops the tabbar looking like 3 more
    // action tiles, which was confusing next to the real ones.
    // primaryAccent=true tints the icon --amber (#ff9d4d, the prototype's
    // real "select/decide glow" accent) instead of --tile-ink — used only
    // for the 2 primary tiles. This bypasses the theme pipeline entirely
    // (same reasoning as TilePlateColor/TileInkColor above: routing amber
    // through the real QDSUIAccentColor tag + UIThemeManager.ApplyTheme()
    // would mean calling ApplyTheme() again after tiles exist, which
    // re-opens the Animator-clobbers-Background-color bug ApplyUiTheme's
    // call-order fix exists specifically to avoid). --aqua is NOT used
    // anywhere — its documented prototype role is a moving cursor
    // highlight between focused tiles, which needs a selection/navigation
    // state machine this port doesn't have yet; a static aqua placement
    // would be faking a state that doesn't exist, not a real port of it.
    // CHANGED 2026-09-17: was `static readonly` (evaluated once per domain
    // load) — now a computed property reading HDMRHomeDesignConfig fresh
    // every time, so editing the config asset's amberAccent and rebuilding
    // (without also forcing a recompile) actually picks up the new value.
    static Color AmberAccent => Hdmr.Editor.HDMRHomeDesignConfig.LoadOrCreate().amberAccent;

    // CORRECTED 2026-09-16 against the real prototype's CSS: this used to
    // lay out every tile in a row at the SAME square size (tileW x tileW)
    // on a fixed grid step. The real prototype uses variable widths in a
    // LEFT-PACKED row (flexbox `justify-content: flex-start`, not an
    // evenly-spaced grid — matches nav-schema.md's own "alignées à
    // gauche, pas une grille régulière" note): `.tile.primary` is
    // aspect-ratio 5/3 (wider than tall, not square), and MR/Cinéma
    // (`.tile.secondary.wide`) are narrower than the other secondary
    // tiles so the pair together spans one primary column's width.
    // `tileH`<=0 means square (height = tileW); `widthOverride` lets
    // specific tiles (by key) claim a different width than the row's
    // default while keeping the same height and left-packed flow.
    static int AddTileRow(Transform parent, GameObject tileButtonPrefab, (string label, string key, string sub)[] tiles, float y, float tileW, float gap, bool flush = false, bool primaryAccent = false, float tileH = -1f, System.Func<string, float> widthOverride = null)
    {
        float h = tileH > 0f ? tileH : tileW;
        var widths = new float[tiles.Length];
        float totalW = gap * (tiles.Length - 1);
        for (int i = 0; i < tiles.Length; i++)
        {
            widths[i] = widthOverride != null ? widthOverride(tiles[i].key) : tileW;
            totalW += widths[i];
        }
        float cursorX = -totalW / 2f;
        var centerXs = new float[tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
        {
            centerXs[i] = cursorX + widths[i] / 2f;
            cursorX += widths[i] + gap;
        }

        for (int i = 0; i < tiles.Length; i++)
        {
            var (label, key, sub) = tiles[i];
            var tileGo = (GameObject)PrefabUtility.InstantiatePrefab(tileButtonPrefab, parent);
            tileGo.name = "Tile_" + key;
            tileGo.AddComponent(typeof(Hdmr.Home.HomeTileFocus));
            var rt = tileGo.GetComponent<RectTransform>();
            if (rt != null)
            {
                // FOUND 2026-09-17, prompted by a direct question about
                // whether anything got moved by the backplate/aura fixes:
                // this button prefab has the EXACT SAME anchor bug that was
                // found and fixed on both backplates earlier — anchorMin/
                // anchorMax left at the prefab default (0,0) (parent's
                // bottom-left corner), only pivot ever set to (0.5,0.5).
                // AddTileRow's centerXs[]/y math assumes anchoredPosition is
                // measured from the PARENT'S CENTER (a (0.5,0.5) anchor),
                // so with anchor stuck at (0,0) every tile actually renders
                // offset by (parentRect.width/2, parentRect.height/2) from
                // its intended position — confirmed via GetWorldCorners()
                // showing tiles genuinely spilling outside the backplate's
                // edges on both the primary and secondary rows, not just a
                // rounding-level discrepancy. Never caught before because
                // every previous check measured GAPS BETWEEN tiles (a
                // relative difference, which cancels out a constant offset
                // shared by every tile under the same parent), never a
                // tile's absolute position against the backplate itself.
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(centerXs[i], y);
                rt.sizeDelta = new Vector2(widths[i], h);
            }

            // FOUND 2026-09-16 via a real manage_camera screenshot (data
            // checks alone — animator controller name, SerializedObject
            // color reads — all looked fine and still missed this): the
            // QDS ToggleButton's "Background" plate sits at ~7% alpha at
            // rest regardless of which theme (Light/Dark) is applied —
            // colors there are baked into the Animator's state clips, and
            // whatever state actually renders at rest is nowhere near
            // opaque. Our prototype wants an always-visible flat card, not
            // a hover-reveal ghost button.
            //
            // REGRESSION FOUND AND FIXED, same night — `enabled = false`
            // was NOT enough: confirmed live via Unity MCP that entering
            // Play Mode (not just Editor/Edit-mode screenshots — those
            // never actually re-run Animator initialization) still snapped
            // Background.color back to the ~7% baked value even with the
            // Animator disabled. A disabled Animator still binds and
            // applies its default-state values once during Play Mode's
            // own Awake/OnEnable pass — `enabled` only gates its per-frame
            // Update ticking after that, not the initial bind. Destroying
            // the component outright (not just disabling it) removes any
            // possibility of that initial bind ever happening again.
            var tileAnimator = tileGo.GetComponent<Animator>();
            if (tileAnimator != null) UnityEngine.Object.DestroyImmediate(tileAnimator);

            // Profile tiles read as a distinct, quieter register in the
            // prototype (.tile.secondary.profile / .profile.empty in
            // hdmr-home.html) — inverted/dark instead of the bright "paper"
            // used for actual actions, and an empty slot quieter still.
            // Detected by key convention (profileN / profileN-new), same
            // as PROFILES/MAX_PROFILES in the prototype's own script.
            bool isProfile = key.StartsWith("profile");
            bool isEmptyProfile = isProfile && key.EndsWith("-new");
            Color plateColor, inkColor;
            if (isEmptyProfile) { plateColor = Color.clear; inkColor = HexColor(0x8ba1a9); } // transparent + --text-dim, dashed border added below (see isEmptyProfile block after bg/icon wiring)
            else if (isProfile) { plateColor = new Color(1f, 1f, 1f, 0.045f); inkColor = HexColor(0xd6e2e6); } // rgba(255,255,255,.045) + --text
            else { plateColor = TilePlateColor; inkColor = TileInkColor; } // regular action tile — --tile / --tile-ink

            if (flush)
            {
                plateColor = Color.clear; // .tabbar button { background: none }
                inkColor = (key == "accueil") ? HexColor(0xd6e2e6) : HexColor(0x8ba1a9); // --text if active (Accueil starts current) else --text-dim
            }

            UnityEngine.UI.Image bg = null, icon = null;
            foreach (var img in tileGo.GetComponentsInChildren<UnityEngine.UI.Image>(true))
            {
                if (img.gameObject.name == "Background") bg = img;
                else if (img.gameObject.name == "Icon") icon = img;
            }
            if (bg != null) bg.color = plateColor;

            // FOUND 2026-09-16, second visual-sweep pass: this exact gap
            // was already named in a comment ("no dashed-border equivalent
            // yet") but never actually built — cataloguing a known gap
            // isn't the same as fixing it, and the corrected methodology
            // this pass is about is closing gaps, not just re-confirming
            // they're still open. `.tile.secondary.profile.empty { border:
            // 1px dashed var(--edge-soft) }`.
            if (isEmptyProfile && bg != null)
            {
                var dashRt = bg.GetComponent<RectTransform>();
                // FOUND live, twice: bg's actual size was still 0 when the
                // texture got baked, even after forcing a rebuild — because
                // I first forced the rebuild on "Background" ITSELF, but
                // Background doesn't own the VerticalLayoutGroup that sizes
                // it — "Content" (its parent) does. ForceRebuildLayoutImmediate
                // rebuilds a rect transform's OWN children, not layout
                // driven onto it BY an ancestor — has to be called on the
                // object that actually HAS the layout group, same lesson
                // as the primary-tile text fix but pointed at the wrong
                // object here since the tile hierarchy has TWO nested
                // layout groups (Content -> Background size; Elements ->
                // its own children's position) and this one needed Content.
                if (bg.transform.parent != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(bg.transform.parent.GetComponent<RectTransform>());
                int bw = Mathf.RoundToInt(dashRt.rect.width);
                int bh = Mathf.RoundToInt(dashRt.rect.height);
                var dashTex = GenerateDashedBorderTexture(bw, bh, 16f, 1f, new Color(1f, 1f, 1f, 0.09f), 4f, 3f); // --edge-soft-ish alpha, 16px radius matching the tile's own rounding
                var dashGo = new GameObject("EmptyProfileDashedBorder", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                dashGo.transform.SetParent(bg.transform, false);
                var dRt = dashGo.GetComponent<RectTransform>();
                dRt.anchorMin = Vector2.zero; dRt.anchorMax = Vector2.one; dRt.offsetMin = Vector2.zero; dRt.offsetMax = Vector2.zero;
                var dashImg = dashGo.GetComponent<UnityEngine.UI.Image>();
                dashImg.sprite = Sprite.Create(dashTex, new Rect(0, 0, bw, bh), new Vector2(0.5f, 0.5f));
                dashImg.type = UnityEngine.UI.Image.Type.Simple;
                dashImg.raycastTarget = false; // decorative only — must not intercept the tile's own click/poke
            }

            if (flush && icon != null) icon.gameObject.SetActive(false); // tabbar is plain text in the prototype, no icon slot
            if (!flush && icon != null)
            {
                icon.color = primaryAccent ? AmberAccent : inkColor; // primary-tile icons get the real --amber accent; everything else keeps --tile-ink
                if (TileIcons.TryGetValue(key, out var iconName))
                {
                    var iconSprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/UI/Icons/" + iconName + ".png");
                    if (iconSprite != null) icon.sprite = iconSprite;
                    else Debug.LogWarning($"[HDMRBuildHome] icon sprite '{iconName}' not found for tile '{key}'");
                }
            }

            var texts = tileGo.GetComponentsInChildren<TMPro.TMP_Text>(true);
            TMPro.TMP_Text titleText = null, subText = null;
            foreach (var t in texts)
            {
                if (t.CompareTag("QDSUITextSecondaryColor")) { t.text = sub; subText = t; } // subtitle — never ported before, was stuck on the prefab's placeholder "Secondary"
                else { t.text = label; t.color = inkColor; titleText = t; } // primary label
            }
            if (texts.Length == 0) Debug.LogWarning($"[HDMRBuildHome] no TMP_Text found on tile '{key}' — prefab structure may have changed");

            // UI QA pass, 2026-09-16: the primary tiles' 2-line titles
            // ("Lancer le mode\nStéréo VR", "Calibrage\nstéréo") measured to
            // OVERLAP the subtitle below by ~2 units (TMP_Text.textBounds,
            // real rendered extents — not the declared box size, which
            // stays the same 20px regardless of wrap).
            //
            // TWO failed fix attempts before this one, both caught by
            // re-measuring rather than trusting the edit:
            //  1. Nudging anchoredPosition directly — reverted every time,
            //     because the "Elements" VerticalLayoutGroup recomputes
            //     child positions on its own layout pass (same "live
            //     component silently reverts a direct override" pattern
            //     already hit with Animator/UIThemeManager/Mask this
            //     session, a different component each time).
            //  2. LayoutElement.preferredHeight — had ZERO effect, because
            //     "Elements"'s VerticalLayoutGroup has childControlHeight
            //     = FALSE (confirmed by reading the live component), which
            //     means it ignores LayoutElement height hints entirely and
            //     just stacks children using their OWN existing
            //     RectTransform.sizeDelta.y.
            // Real fix, matching what the layout group actually reads:
            // grow the title's own sizeDelta.y directly. Scoped to
            // multi-line titles only — every other tile's single-line
            // label already clears this with margin.
            if (titleText != null && label.Contains("\n"))
            {
                var titleRt2 = titleText.GetComponent<RectTransform>();
                titleRt2.sizeDelta = new Vector2(titleRt2.sizeDelta.x, 34f); // measured real rendered height was 31.74; a little headroom
            }

            var btn = tileGo.GetComponentInChildren<UnityEngine.UI.Button>(true);
            if (btn != null)
            {
                string capturedKey = key; // local copy for the closure — foreach/for pitfall
                UnityEditor.Events.UnityEventTools.AddStringPersistentListener(
                    btn.onClick, LogTileClickTarget, capturedKey);
            }
            else Debug.LogWarning($"[HDMRBuildHome] no Button found on tile '{key}' — prefab structure may have changed");

            // Flush rows (the tabbar) are meant to read as one continuous
            // bar with thin separators between segments, not floating cards
            // (see FitBackplateToContent's header comment for the general
            // "backplate must actually match the real layout" lesson this
            // project keeps re-learning). gap is 0 for flush rows so tiles
            // sit edge-to-edge; drop a divider on each internal seam.
            //
            // CORRECTED 2026-09-16 against the real prototype source
            // (.tabbar button::before): color is var(--edge) =
            // rgba(255,255,255,0.09), not the "--text-dim at ~30%" guess
            // this used to carry (flagged as unsourced in hdmr-project.md
            // — now sourced and fixed). Height is also inset top/bottom
            // 22% each (`top:22%;bottom:22%`), not the tile's full height.
            if (flush && i > 0 && rt != null)
            {
                var dividerGo = new GameObject("TabbarDivider_" + i, typeof(RectTransform), typeof(UnityEngine.UI.Image));
                dividerGo.transform.SetParent(parent, false);
                var dividerRt = dividerGo.GetComponent<RectTransform>();
                dividerRt.anchorMin = rt.anchorMin;
                dividerRt.anchorMax = rt.anchorMax;
                dividerRt.pivot = rt.pivot;
                dividerRt.anchoredPosition = new Vector2(centerXs[i] - widths[i] / 2f - gap / 2f, y);
                dividerRt.sizeDelta = new Vector2(1f, h * 0.56f); // 100% - 22% - 22% inset
                dividerGo.GetComponent<UnityEngine.UI.Image>().color = new Color(1f, 1f, 1f, 0.09f); // --edge, exact
            }

            // CORRECTED 2026-09-16, same source: the active tab isn't just
            // brighter text (`.tabbar button[aria-current="true"]::after`)
            // — it also gets a 2px accent bar along the TOP of that one
            // segment, in --text. Only "accueil" starts active, matching
            // the prototype's own default (aria-current="true" on that
            // button in the markup).
            if (flush && key == "accueil" && rt != null)
            {
                var accentGo = new GameObject("TabbarActiveAccent", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                accentGo.transform.SetParent(parent, false);
                var accentRt = accentGo.GetComponent<RectTransform>();
                accentRt.anchorMin = rt.anchorMin;
                accentRt.anchorMax = rt.anchorMax;
                accentRt.pivot = new Vector2(0.5f, 1f);
                accentRt.anchoredPosition = new Vector2(rt.anchoredPosition.x, y + h / 2f);
                accentRt.sizeDelta = new Vector2(widths[i] - 20f, 2f); // `left:10px;right:10px` inset
                accentGo.GetComponent<UnityEngine.UI.Image>().color = HexColor(0xd6e2e6); // --text
            }
        }
        return tiles.Length;
    }

    // Placeholder click target, wired as a real persistent UnityEvent
    // listener (survives into the build, not just an Editor-only closure)
    // so the button genuinely does something observable on-device before
    // real navigation exists.
    public static void LogTileClickTarget(string key)
    {
        Debug.Log("[HDMRBuildHome] tile clicked: " + key);
        // "Mode MR" is the one tile with a real, immediate effect right now
        // (every other tile is still a placeholder destination) — toggles
        // real Insight Passthrough via the single PassthroughController
        // instance rather than each tile carrying its own wiring.
        if (key == "mr")
        {
            var pc = UnityEngine.Object.FindAnyObjectByType<Hdmr.Home.PassthroughController>();
            if (pc != null) pc.TogglePassthrough();
            else Debug.LogWarning("[HDMRBuildHome] 'mr' tile clicked but no PassthroughController found in scene");
        }
    }

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
