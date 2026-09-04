using System;
using System.IO;
using UnityEngine;

namespace Hdmr
{
    /// <summary>
    /// Persistent, thread-safe, subsystem-tagged plain-text log.
    ///
    /// adb and the capture card share the one USB-C port, so real usage (card
    /// plugged, no PC) is invisible live — everything must be written to disk.
    ///
    /// Files (all in Application.persistentDataPath, next to hdmr-selftest.txt):
    ///   hdmr-log.txt        managed log (this class), rotates to .1 at 2 MB
    ///   hdmr-logcat.txt     native/JNI crashes etc — a child `logcat --pid` process
    ///                       (the only way a sideloaded app can read its own native log)
    /// </summary>
    public static class HdmrLog
    {
        const long MAX_BYTES = 2 * 1024 * 1024;

        static readonly object _lock = new object();
        static string _path, _bak;
        static StreamWriter _w;
        static long _bytes;
        static bool _inited;
        static AndroidJavaObject _logcatProc;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoInit() => Init();

        public static void Init()
        {
            if (_inited) return;
            _inited = true;
            _path = Path.Combine(Application.persistentDataPath, "hdmr-log.txt");
            _bak = _path + ".1";

            Application.logMessageReceivedThreaded += OnUnityLog;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

            // let the app render as fast as it can — the plugin delivers frames
            // once per render frame, so the render rate caps the video rate
            Application.targetFrameRate = 120;
            QualitySettings.vSyncCount = 0;

            W("APP", "I", $"=== HDMR start {DateTime.Now:u} ===");
            W("APP", "I", $"Unity {Application.unityVersion} · {SystemInfo.deviceModel} · {SystemInfo.operatingSystem}");
            W("APP", "I", $"gfx {SystemInfo.graphicsDeviceType} {SystemInfo.graphicsDeviceVersion} · asyncReadback={SystemInfo.supportsAsyncGPUReadback}");
            W("APP", "I", $"log dir: {Application.persistentDataPath}");

            LogUsbSpeeds();
            StartNativeLogcat();
        }

        // convenience per-subsystem
        public static void Usb(string m) => W("USB", "I", m);
        public static void Uvc(string m) => W("UVC", "I", m);
        public static void Render(string m) => W("RENDER", "I", m);
        public static void Audio(string m) => W("AUDIO", "I", m);
        public static void Cam2(string m) => W("CAMERA2", "I", m);
        public static void SelfTest(string m) => W("SELFTEST", "I", m);
        public static void Info(string sub, string m) => W(sub, "I", m);
        public static void Warn(string sub, string m) => W(sub, "W", m);
        public static void Error(string sub, string m) => W(sub, "E", m);

        // ------------------------------------------------------------------
        static void W(string sub, string lvl, string m)
        {
            lock (_lock)
            {
                try
                {
                    if (_w == null) Open();
                    string line = $"{DateTime.Now:HH:mm:ss.fff} {lvl} [{sub}] {m}";
                    _w.WriteLine(line);
                    _w.Flush();
                    _bytes += line.Length + 2;
                    if (_bytes > MAX_BYTES) Rotate();
                }
                catch { /* never let logging throw */ }
            }
        }

        static void Open()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            _w = new StreamWriter(_path, append: true) { AutoFlush = true };
            _bytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        }

        static void Rotate()
        {
            try { _w?.Dispose(); } catch { }
            _w = null;
            try { if (File.Exists(_bak)) File.Delete(_bak); File.Move(_path, _bak); } catch { }
            Open();
            _bytes = 0;
        }

        static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            // captures Debug.Log/LogWarning/LogError from every thread (incl. UVC callbacks)
            string lvl = type == LogType.Log ? "I" : (type == LogType.Warning ? "W" : "E");
            string sub = condition != null && condition.StartsWith("[HDMR]") ? "HDMR" : "UNITY";
            W(sub, lvl, condition);
            if (type == LogType.Exception || type == LogType.Error)
                if (!string.IsNullOrEmpty(stackTrace)) W(sub, lvl, stackTrace.Replace("\n", " | "));
        }

        static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            W("APP", "E", "UNHANDLED: " + (e.ExceptionObject?.ToString() ?? "null"));
        }

        // ------------------------------------------------------------------
        static void LogUsbSpeeds()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                const string root = "/sys/bus/usb/devices";
                if (!Directory.Exists(root)) { W("USB", "W", "no " + root); return; }
                foreach (var dev in Directory.GetDirectories(root))
                {
                    string vid = TryRead(dev + "/idVendor");
                    if (string.IsNullOrWhiteSpace(vid)) continue;
                    string pid = TryRead(dev + "/idProduct");
                    string prod = TryRead(dev + "/product");
                    string speed = TryRead(dev + "/speed");
                    string ver = TryRead(dev + "/version");
                    W("USB", "I", $"{Path.GetFileName(dev)} {vid?.Trim()}:{pid?.Trim()} '{prod?.Trim()}' " +
                                  $"speed={speed?.Trim()}Mbps usb{ver?.Trim()}");
                }
            }
            catch (Exception e) { W("USB", "E", "speed scan: " + e.Message); }
#endif
        }

        static string TryRead(string p)
        {
            try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; }
        }

        static void StartNativeLogcat()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                int pid;
                using (var proc = new AndroidJavaClass("android.os.Process"))
                    pid = proc.CallStatic<int>("myPid");

                string outp = Path.Combine(Application.persistentDataPath, "hdmr-logcat.txt");
                try { if (File.Exists(outp)) File.Delete(outp); } catch { }
                try { if (File.Exists(outp + ".1")) File.Delete(outp + ".1"); } catch { }

                var cmd = new[] { "logcat", "--pid=" + pid, "-v", "threadtime",
                                  "-f", outp, "-r", "2048", "-n", "4" };
                using (var rt = new AndroidJavaClass("java.lang.Runtime"))
                {
                    var runtime = rt.CallStatic<AndroidJavaObject>("getRuntime");
                    _logcatProc = runtime.Call<AndroidJavaObject>("exec", (object)cmd);
                }
                W("APP", "I", $"native logcat capture: pid={pid} -> {outp}");
            }
            catch (Exception e) { W("APP", "E", "logcat capture failed: " + e.Message); }
#endif
        }
    }
}
