using System.Linq;
using UnityEngine;
using Serenegiant.UVC;

namespace Hdmr
{
    /// <summary>
    /// Remote control over adb (incl. adb-over-Wi-Fi, so tests can run with the
    /// capture card plugged and no USB cable): polls the `debug.hdmr.cmd` system
    /// property. Set it with:  adb shell setprop debug.hdmr.cmd "&lt;seq&gt;:&lt;cmd&gt;"
    ///
    /// commands:
    ///   start MJPEG 1920 1080      pick that mode + go to Streaming
    ///   start MJPEG 3840 2160
    ///   config                     back to the Config page
    ///   selftest                   run the all-modes self-test
    ///   flip 1 0                    flipX=1 flipY=0
    ///   sound 1 | sound 0
    ///   relaunch                   reload the scene
    /// </summary>
    public class HdmrRemote : MonoBehaviour
    {
        public HdmrController ctrl;
        public HdmrSelfTest selfTest;

        int _lastSeq;
        float _poll;

        void Start() => _lastSeq = HdmrSession.LastCmdSeq;

        void Update()
        {
            if (Time.unscaledTime - _poll < 1f) return;
            _poll = Time.unscaledTime;

            string raw = GetProp("debug.hdmr.cmd");
            if (string.IsNullOrEmpty(raw)) return;

            int c = raw.IndexOf(':');
            if (c < 0 || !int.TryParse(raw.Substring(0, c), out int seq)) return;
            string cmd = raw.Substring(c + 1).Trim();
            if (seq == _lastSeq) return;
            _lastSeq = seq;
            HdmrSession.LastCmdSeq = seq;   // so a relaunch doesn't re-run it

            if (cmd == "noop") return;
            HdmrLog.Info("REMOTE", "cmd: " + cmd);
            Exec(cmd);
        }

        void Exec(string cmd)
        {
            var t = cmd.Split(' ');
            switch (t[0].ToLowerInvariant())
            {
                case "start":
                    if (t.Length >= 4 && uint.TryParse(t[2], out var w) && uint.TryParse(t[3], out var h))
                    {
                        uint ft = t[1].ToUpperInvariant() switch
                        {
                            "MJPEG" => HdmrFormats.MJPEG, "YUY2" => HdmrFormats.YUY2,
                            "NV12" => HdmrFormats.NV12, "H264" => HdmrFormats.H264,
                            _ => HdmrFormats.MJPEG
                        };
                        var m = ctrl.Sizes.FirstOrDefault(s => s.FrameType == ft && s.Width == w && s.Height == h)
                              ?? ctrl.Sizes.FirstOrDefault(s => s.Width == w && s.Height == h);
                        if (m != null) ctrl.StartStream(m);
                        else HdmrLog.Warn("REMOTE", $"mode {t[1]} {w}x{h} not in the {ctrl.Sizes.Count} advertised");
                    }
                    break;
                case "config": ctrl.BackToConfig(); break;
                case "selftest": selfTest.Run(); break;
                case "flip":
                    ctrl.SetFlip(t.Length > 1 && t[1] == "1", t.Length > 2 && t[2] == "1");
                    break;
                case "sound": ctrl.SetSound(t.Length > 1 && t[1] == "1"); break;
                case "relaunch": ctrl.ReloadScene(); break;
                default: HdmrLog.Warn("REMOTE", "unknown cmd: " + cmd); break;
            }
        }

        static string GetProp(string key)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var sp = new AndroidJavaClass("android.os.SystemProperties");
                return sp.CallStatic<string>("get", key, "");
            }
            catch { return ""; }
#else
            return "";
#endif
        }
    }
}
