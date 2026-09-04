using System.IO;
using System.Text;
using UnityEngine;

namespace Hdmr
{
    /// <summary>
    /// Preferences + desired mode, persisted to a plain file (NOT PlayerPrefs —
    /// PlayerPrefs.Save() + an immediate Process.killProcess() loses the write).
    /// File.WriteAllText is synchronous, so it survives the relaunch we use to
    /// change resolution (mid-stream SetVideoSize SIGABRTs in aandusb).
    /// </summary>
    public static class HdmrSession
    {
        static string Path => System.IO.Path.Combine(Application.persistentDataPath, "hdmr-prefs.txt");
        static System.Collections.Generic.Dictionary<string, string> _kv;

        static System.Collections.Generic.Dictionary<string, string> KV()
        {
            if (_kv != null) return _kv;
            _kv = new System.Collections.Generic.Dictionary<string, string>();
            try
            {
                if (File.Exists(Path))
                    foreach (var line in File.ReadAllLines(Path))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) _kv[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            return _kv;
        }

        static int GetI(string k, int d) { return KV().TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d; }
        static void SetI(string k, int v)
        {
            KV()[k] = v.ToString();
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _kv) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                File.WriteAllText(Path, sb.ToString());
            }
            catch { }
        }

        public static bool HasDesiredMode => GetI("w", 0) > 0;
        public static uint DesiredFrameType => (uint)GetI("ft", 7);
        public static uint DesiredW => (uint)GetI("w", 0);
        public static uint DesiredH => (uint)GetI("h", 0);

        public static bool ShowStream { get => GetI("show", 0) == 1; set => SetI("show", value ? 1 : 0); }
        public static bool FlipX { get => GetI("fx", 0) == 1; set => SetI("fx", value ? 1 : 0); }
        public static bool FlipY { get => GetI("fy", 0) == 1; set => SetI("fy", value ? 1 : 0); }
        public static bool WantSound { get => GetI("snd", 1) == 1; set => SetI("snd", value ? 1 : 0); }  // on by default
        public static int SelfTestIndex { get => GetI("st", -1); set => SetI("st", value); }
        public static int LastCmdSeq { get => GetI("cmdseq", 0); set => SetI("cmdseq", value); }

        public static void SetDesired(uint ft, uint w, uint h)
        {
            SetI("ft", (int)ft); SetI("w", (int)w); SetI("h", (int)h);
        }
    }
}
