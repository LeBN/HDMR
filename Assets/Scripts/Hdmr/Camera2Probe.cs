using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Android;

namespace Hdmr
{
    /// <summary>
    /// Spike step 1 (enumeration only — pure JNI, no callbacks/proxies):
    /// does the Guermok capture card appear through Android Camera2 as a
    /// LENS_FACING_EXTERNAL camera, and what resolutions / formats does its
    /// StreamConfigurationMap advertise?  (Do we get past MJPEG 720p?)
    ///
    /// The open + capture-session + latency test needs a small Java bridge
    /// (Camera2's StateCallbacks are abstract classes, not interfaces, so
    /// AndroidJavaProxy can't stand in). We only build that if enumeration
    /// here says the card is reachable. All output -> HdmrLog [CAMERA2].
    /// </summary>
    public class Camera2Probe : MonoBehaviour
    {
        const int LENS_FACING_EXTERNAL = 2;

        // the Quest 3's own cameras (baseline seen with NO capture card connected)
        static readonly HashSet<string> QuestBuiltInIds = new HashSet<string> { "1", "50", "51" };

        static string HwLevel(int l) => l switch
        {
            0 => "LIMITED", 1 => "FULL", 2 => "LEGACY", 3 => "LEVEL_3", 4 => "EXTERNAL", _ => "?" + l
        };

        readonly List<string> _screen = new List<string>();
        Vector2 _scroll;
        GUIStyle _big, _mono;
        bool _styles;
        string _verdict = "probing…";
        Color _banner = new Color(0.05f, 0.06f, 0.09f);

        void Start()
        {
            HdmrLog.Cam2("=== Camera2 enumeration probe ===");
            Application.targetFrameRate = 72;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                S("requesting CAMERA permission…");
                Permission.RequestUserPermission(Permission.Camera);
                float t = 0;
                while (!Permission.HasUserAuthorizedPermission(Permission.Camera) && t < 30f) { t += Time.deltaTime; yield return null; }
            }
            bool cam = Permission.HasUserAuthorizedPermission(Permission.Camera);
            HdmrLog.Cam2("CAMERA permission granted=" + cam);
            S("CAMERA permission: " + cam);

            AndroidJavaObject cm;
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                cm = up.GetStatic<AndroidJavaObject>("currentActivity").Call<AndroidJavaObject>("getSystemService", "camera");

            // rescan for ~15s — a freshly-plugged UVC camera can take a moment to register
            int newCamMaxArea = 0;
            string newCamLabel = "", newCamId = null;
            var seen = new HashSet<string>();
            for (int pass = 0; pass < 10; pass++)
            {
                try
                {
                    var ids = cm.Call<string[]>("getCameraIdList");
                    if (pass == 0) { HdmrLog.Cam2($"getCameraIdList => {ids.Length}: [{string.Join(", ", ids)}]"); S($"cameras: [{string.Join(", ", ids)}]"); }
                    foreach (var id in ids)
                    {
                        bool isNew = !QuestBuiltInIds.Contains(id);
                        if (!seen.Add(id) && !isNew) continue;
                        int area = 0; string label = "";
                        int facing = Dump(cm, id, ref area, ref label, isNew);
                        if (isNew && area > newCamMaxArea)
                        {
                            newCamMaxArea = area; newCamLabel = label; newCamId = id;
                            HdmrLog.Cam2($"### NON-QUEST camera id={id} facing={facing} — CANDIDATE capture device, max {label}");
                            S($"### NEW camera id={id}  max {label}");
                        }
                    }
                }
                catch (Exception e) { HdmrLog.Error("CAMERA2", "enumerate pass " + pass + ": " + e); }
                if (newCamId != null) break;
                yield return new WaitForSeconds(1.5f);
            }

            if (newCamId == null)
            {
                _verdict = "NO non-Quest camera appeared — the UVC card is NOT exposed via Camera2. Stay on UVC4UnityAndroid.";
                _banner = new Color(0.5f, 0.12f, 0.12f);
            }
            else
            {
                bool beatsUvc = newCamMaxArea > 1280 * 720;
                _verdict = $"Camera2 SEES the card as id={newCamId} · max {newCamLabel} · " +
                           (beatsUvc ? "BEATS the UVC 720p ceiling → build the capture bridge" : "still <=720p, no gain");
                _banner = beatsUvc ? new Color(0.10f, 0.42f, 0.16f) : new Color(0.45f, 0.35f, 0.10f);
            }
            HdmrLog.Cam2("VERDICT: " + _verdict);
            S(_verdict);
#else
            yield break;
#endif
        }

        int Dump(AndroidJavaObject cm, string id, ref int maxArea, ref string maxLabel, bool verbose)
        {
            try
            {
                var cc = cm.Call<AndroidJavaObject>("getCameraCharacteristics", id);
                using var K = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics");

                int facing = GetInt(cc, K, "LENS_FACING", -1);
                int hwl = GetInt(cc, K, "INFO_SUPPORTED_HARDWARE_LEVEL", -1);
                bool ext = facing == LENS_FACING_EXTERNAL;
                string facingStr = facing == 0 ? "FRONT" : facing == 1 ? "BACK" : ext ? "EXTERNAL" : $"?{facing}";
                string tag = QuestBuiltInIds.Contains(id) ? "(quest built-in)" : "(NON-QUEST)";
                HdmrLog.Cam2($"--- id={id} {tag}  LENS_FACING={facingStr}  HW_LEVEL={HwLevel(hwl)}");
                S($"id={id} {tag}: {facingStr}  hw={HwLevel(hwl)}");
                if (!verbose && QuestBuiltInIds.Contains(id)) return facing; // skip the noisy dump for known cams on rescans

                var scKey = K.GetStatic<AndroidJavaObject>("SCALER_STREAM_CONFIGURATION_MAP");
                var scmap = cc.Call<AndroidJavaObject>("get", scKey);
                if (scmap == null) { HdmrLog.Cam2($"   id={id}: no StreamConfigurationMap"); return facing; }

                var formats = scmap.Call<int[]>("getOutputFormats");
                HdmrLog.Cam2($"   formats={string.Join(",", formats)}  (34=PRIVATE 35=YUV_420_888 256=JPEG 20=YUY2 37=RAW10)");
                foreach (var fmt in formats)
                {
                    AndroidJavaObject[] sizes;
                    try { sizes = scmap.Call<AndroidJavaObject[]>("getOutputSizes", fmt); }
                    catch { continue; }
                    if (sizes == null) continue;
                    var sb = new StringBuilder();
                    foreach (var sz in sizes)
                    {
                        int w = sz.Call<int>("getWidth"), h = sz.Call<int>("getHeight");
                        long minDur = 0;
                        try { minDur = scmap.Call<long>("getOutputMinFrameDuration", fmt, sz); } catch { }
                        double maxFps = minDur > 0 ? 1e9 / minDur : 0;
                        sb.Append($"{w}x{h}@{maxFps:0} ");
                        if (w * h > maxArea) { maxArea = w * h; maxLabel = $"{w}x{h}@{maxFps:0} (fmt {fmt})"; }
                    }
                    HdmrLog.Cam2($"   fmt {fmt}: {sb}");
                    S($"  fmt {fmt}: {sb}");
                }

                var fpsKey = K.GetStatic<AndroidJavaObject>("CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES");
                var fpsRanges = cc.Call<AndroidJavaObject[]>("get", fpsKey);
                if (fpsRanges != null)
                {
                    var sb = new StringBuilder();
                    foreach (var r in fpsRanges) sb.Append(r.Call<string>("toString")).Append(' ');
                    HdmrLog.Cam2($"   AE fps ranges: {sb}");
                }
                return facing;
            }
            catch (Exception e) { HdmrLog.Error("CAMERA2", $"id={id}: {e}"); return -99; }
        }

        static int GetInt(AndroidJavaObject cc, AndroidJavaClass K, string keyName, int dflt)
        {
            try
            {
                var key = K.GetStatic<AndroidJavaObject>(keyName);
                var v = cc.Call<AndroidJavaObject>("get", key);
                return v != null ? v.Call<int>("intValue") : dflt;
            }
            catch { return dflt; }
        }

        void S(string s) { lock (_screen) { _screen.Add(s); if (_screen.Count > 300) _screen.RemoveAt(0); } }

        void OnGUI()
        {
            if (!_styles)
            {
                _big = new GUIStyle(GUI.skin.label) { fontSize = 26, wordWrap = true, richText = true };
                _big.normal.textColor = Color.white;
                _mono = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
                _mono.normal.textColor = new Color(.85f, .9f, .95f);
                _styles = true;
            }
            GUI.color = _banner;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(24, 20, Screen.width - 48, Screen.height - 40));
            GUILayout.Label("<b>HDMR — Camera2 enumeration probe</b>", _big);
            GUILayout.Label(_verdict, _big);
            GUILayout.Space(8);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            lock (_screen) foreach (var l in _screen) GUILayout.Label(l, _mono);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
