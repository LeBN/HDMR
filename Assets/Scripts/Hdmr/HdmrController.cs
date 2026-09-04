using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Serenegiant.UVC;

namespace Hdmr
{
    public enum HdmrPage { NoDevice, Config, Streaming }

    /// <summary>
    /// 3-page state machine. Resolution is applied by the plugin's auto-start
    /// (DefaultWidth/Height set in Awake from HdmrSession) — to change it we
    /// relaunch the whole app, because the plugin's mid-stream SetVideoSize()
    /// SIGABRTs inside aandusb's closed USB event thread.
    /// </summary>
    public class HdmrController : MonoBehaviour
    {
        public UVCManager manager;
        public HdmrQuadDrawer drawer;

        public HdmrPage Page { get; private set; } = HdmrPage.NoDevice;
        public UVCManager.CameraInfo Cam { get; private set; }
        public List<UVCVideoSize> Sizes { get; private set; } = new List<UVCVideoSize>();
        public string DeviceLine { get; private set; } = "";
        public string CurrentModeLabel => Cam != null ? Describe(Cam.CurrentSize) : "(none)";

        float _lastPerfLog;

        void Awake()
        {
            if (HdmrSession.HasDesiredMode)
            {
                manager.DefaultWidth = HdmrSession.DesiredW;
                manager.DefaultHeight = HdmrSession.DesiredH;
                manager.PreferH264 = HdmrSession.DesiredFrameType == HdmrFormats.H264
                                     || HdmrSession.DesiredFrameType == HdmrFormats.H264F;
            }
            else
            {
                manager.DefaultWidth = 1280;
                manager.DefaultHeight = 720;
                manager.PreferH264 = false;
            }
            manager.UACEnabled = HdmrSession.WantSound;
            drawer.SetFlip(HdmrSession.FlipX, HdmrSession.FlipY);
            HdmrLog.Info("CTRL", $"awake: desired={(HdmrSession.HasDesiredMode ? $"{HdmrSession.DesiredW}x{HdmrSession.DesiredH} ft=0x{HdmrSession.DesiredFrameType:X}" : "none")} " +
                                 $"show={HdmrSession.ShowStream} sound={HdmrSession.WantSound} selftest={HdmrSession.SelfTestIndex}");
        }

        void Update()
        {
            var list = manager != null ? manager.GetAttachedDevices() : null;
            var cam = (list != null && list.Count > 0) ? list[0] : null;
            if (!ReferenceEquals(cam, Cam)) { Cam = cam; RebuildSizes(); }

            if (cam == null) { Page = HdmrPage.NoDevice; DeviceLine = "no HDMI source detected"; }
            else
            {
                DeviceLine = $"{cam.DeviceName}  {cam.Vid:X4}:{cam.Pid:X4}   ·   {Describe(cam.CurrentSize)}";
                Page = HdmrSession.ShowStream ? HdmrPage.Streaming : HdmrPage.Config;
            }

            drawer.QuadVisible = (Page == HdmrPage.Streaming);

            if (Page == HdmrPage.Streaming && Time.time - _lastPerfLog > 3f)
            {
                _lastPerfLog = Time.time;
                HdmrLog.Render($"{CurrentModeLabel}  video={drawer.Fps:0}fps interval={drawer.FrameIntervalMs:0}ms  app={drawer.AppFps:0}fps  live={drawer.FramesLive}");
            }
        }

        void RebuildSizes()
        {
            Sizes.Clear();
            if (Cam == null) return;
            try
            {
                var arr = Cam.SupportedSize;
                if (arr != null)
                    Sizes.AddRange(arr
                        .Where(s => s != null && s.IsValid)
                        .GroupBy(s => (s.FrameType, s.Width, s.Height))
                        .Select(g => g.First())
                        .OrderBy(s => HdmrFormats.IsCompressed(s.FrameType) ? 0 : 1)
                        .ThenByDescending(s => (long)s.Width * s.Height)
                        .ThenByDescending(MaxFps));
            }
            catch (System.Exception e) { HdmrLog.Warn("CTRL", "size list: " + e.Message); }
        }

        // ---- UI actions -------------------------------------------------
        public void StartStream(UVCVideoSize m)
        {
            if (Cam == null || m == null || !m.IsValid) return;
            bool same = m.IsSameValue(Cam.CurrentSize);
            HdmrSession.SetDesired(m.FrameType, m.Width, m.Height);
            HdmrSession.ShowStream = true;
            if (same) return;                       // already at this exact mode
            HdmrLog.Info("CTRL", "relaunch to apply " + Describe(m));
            Relaunch();
        }

        public void BackToConfig() => HdmrSession.ShowStream = false;   // just hide the quad; stream keeps running

        public void SetFlip(bool x, bool y)
        {
            HdmrSession.FlipX = x; HdmrSession.FlipY = y;
            drawer.SetFlip(x, y);
        }

        public void SetSound(bool on)
        {
            if (HdmrSession.WantSound == on) return;
            HdmrSession.WantSound = on;
            Relaunch();   // UAC is negotiated at attach
        }

        public void ReloadScene() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

        public void Relaunch()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var act = up.GetStatic<AndroidJavaObject>("currentActivity");
                var pm = act.Call<AndroidJavaObject>("getPackageManager");
                string pkg = act.Call<string>("getPackageName");
                var intent = pm.Call<AndroidJavaObject>("getLaunchIntentForPackage", pkg);
                intent.Call<AndroidJavaObject>("addFlags", 0x10000000 | 0x00008000); // NEW_TASK | CLEAR_TASK
                act.Call("startActivity", intent);
                using var proc = new AndroidJavaClass("android.os.Process");
                proc.CallStatic("killProcess", proc.CallStatic<int>("myPid"));
            }
            catch (System.Exception e) { HdmrLog.Error("CTRL", "relaunch: " + e); }
#endif
        }

        // ---- helpers --------------------------------------------------
        public static float MaxFps(UVCVideoSize s)
        {
            float f = 0f;
            if (s != null && s.Fps != null) foreach (var x in s.Fps) if (x > f) f = x;
            return f;
        }

        public static string Describe(UVCVideoSize s)
        {
            if (s == null || !s.IsValid) return "(none)";
            float fps = MaxFps(s);
            return $"{HdmrFormats.Name(s.FrameType)} {s.Width}x{s.Height} @{(fps > 240f ? ">240" : fps.ToString("0"))}";
        }
    }
}
