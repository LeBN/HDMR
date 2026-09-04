using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Android;

/// <summary>
/// HDMR Phase-1 spike probe — autonomous edition.
///
/// The Quest 3 has ONE USB-C port, so it can't be cabled to the dev PC and to
/// the capture card at the same time. This build therefore runs standalone:
///   - it keeps rescanning the USB bus every few seconds, so you can plug the
///     capture card in AFTER launching the app
///   - it shows a big green/red verdict panel in the headset
///   - it writes the full result + log to
///         /sdcard/Android/data/com.natis.hdmr/files/hdmr-probe.txt
///     (Application.persistentDataPath) so it can be pulled over adb later
///
/// What it tests:
///   1. runtime CAMERA permission
///   2. whether horizonos.permission.USB_CAMERA is recognised / granted
///   3. UsbManager.getDeviceList() — can a 3rd-party app even see USB devices?
///   4. UsbManager.requestPermission() — does the "Allow USB device" dialog appear?
///   5. once granted: parse raw USB descriptors -> advertised video formats
/// </summary>
public class UsbUvcProbe : MonoBehaviour
{
    const string PREFIX = "[HDMRProbe] ";
    const string ACTION_USB_PERMISSION = "com.natis.hdmr.USB_PERMISSION";
    const string HORIZON_USB_CAMERA = "horizonos.permission.USB_CAMERA";

    const int USB_CLASS_MISC = 0xEF;   // 239 - UVC composite
    const int USB_CLASS_VIDEO = 0x0E;  // 14  - Video interface class
    const int VS_FORMAT_UNCOMPRESSED = 0x04;
    const int VS_FORMAT_MJPEG = 0x06;
    const int VS_FORMAT_FRAME_BASED = 0x10;
    const int CS_INTERFACE = 0x24;

    const int FLAG_UPDATE_CURRENT = 0x08000000;
    const int FLAG_MUTABLE = 0x02000000;

    readonly List<string> _log = new List<string>(512);
    readonly StringBuilder _summary = new StringBuilder();
    Vector2 _scroll;
    GUIStyle _big, _mono, _btn;
    bool _stylesReady;

    string _cameraPerm = "?";
    string _horizonPerm = "?";
    int _usbCount = -1;
    string _uvcVerdict = "waiting for first scan…";
    string _usbPermVerdict = "n/a";
    string _formatVerdict = "n/a";
    Color _banner = new Color(0.15f, 0.15f, 0.18f);
    int _scanCount;
    float _startTime;
    bool _haveWorkingUvc;
    string _filePath = "";

    AndroidJavaObject _activity;
    AndroidJavaObject _usbManager;
    readonly List<AndroidJavaObject> _pendingDevices = new List<AndroidJavaObject>();
    float _lastScan = -999f;

    void Awake()
    {
        _startTime = Time.time;
        Application.targetFrameRate = 72;
        _filePath = Path.Combine(Application.persistentDataPath, "hdmr-probe.txt");
        Log($"probe start — Unity {Application.unityVersion}");
        Log($"device: {SystemInfo.deviceModel}  os: {SystemInfo.operatingSystem}");
        Log($"gfx: {SystemInfo.graphicsDeviceType} / {SystemInfo.graphicsDeviceVersion}");
        Log($"log file: {_filePath}");
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                _activity = up.GetStatic<AndroidJavaObject>("currentActivity");
            _usbManager = _activity.Call<AndroidJavaObject>("getSystemService", "usb");
            Log("UsbManager acquired: " + (_usbManager != null));
        }
        catch (Exception e) { Log("JNI init FAILED: " + e); }
#else
        Log("running in Editor — Android probe code is compiled out");
#endif
    }

    void Start() => StartCoroutine(Run());

    IEnumerator Run()
    {
        yield return RequestCameraPermission();
        TryRequestHorizonPermission();
        CheckHorizonPermission();
        yield return new WaitForSeconds(0.3f);
        DoScan();
    }

    void Update()
    {
        // keep scanning so the capture card can be hot-plugged after launch
        if (Time.time - _lastScan > 2.5f)
            DoScan();
    }

    void DoScan()
    {
        _lastScan = Time.time;
        _scanCount++;
        Rescan();
        RequestUsbPermissionForPending();
        PollUsbPermission();
        RefreshSummary();
    }

    // ----------------------------------------------------------------- CAMERA
    IEnumerator RequestCameraPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            _cameraPerm = "GRANTED (already)";
            Log(_cameraPerm);
            yield break;
        }
        Log("requesting android.permission.CAMERA …");
        bool done = false;
        var cb = new PermissionCallbacks();
        cb.PermissionGranted += _ => { _cameraPerm = "GRANTED"; done = true; };
        cb.PermissionDenied += _ => { _cameraPerm = "DENIED"; done = true; };
        cb.PermissionDeniedAndDontAskAgain += _ => { _cameraPerm = "DENIED (don't ask again)"; done = true; };
        Permission.RequestUserPermission(Permission.Camera, cb);
        float t = 0f;
        while (!done && t < 30f) { t += Time.deltaTime; yield return null; }
        if (!done) _cameraPerm = Permission.HasUserAuthorizedPermission(Permission.Camera) ? "GRANTED (poll)" : "NO ANSWER";
        Log("CAMERA => " + _cameraPerm);
#else
        _cameraPerm = "editor";
        yield break;
#endif
    }

    void TryRequestHorizonPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (!Permission.HasUserAuthorizedPermission(HORIZON_USB_CAMERA))
            {
                Log("requesting " + HORIZON_USB_CAMERA + " …");
                Permission.RequestUserPermission(HORIZON_USB_CAMERA);
            }
        }
        catch (Exception e) { Log("request horizon perm threw: " + e.Message); }
#endif
    }

    void CheckHorizonPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            bool has = Permission.HasUserAuthorizedPermission(HORIZON_USB_CAMERA);
            _horizonPerm = has ? "GRANTED" : "NOT granted";
            Log($"{HORIZON_USB_CAMERA} => {_horizonPerm}");
        }
        catch (Exception e) { _horizonPerm = "query threw: " + e.Message; Log(_horizonPerm); }
#endif
    }

    // ------------------------------------------------------------------- USB
    void Rescan()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _pendingDevices.Clear();
        if (_usbManager == null) { _uvcVerdict = "no UsbManager"; return; }
        try
        {
            var map = _usbManager.Call<AndroidJavaObject>("getDeviceList");
            var values = map.Call<AndroidJavaObject>("values");
            var arr = values.Call<AndroidJavaObject[]>("toArray");
            _usbCount = arr != null ? arr.Length : 0;
            bool anyUvc = false;
            var names = new List<string>();
            foreach (var dev in arr)
            {
                int vid = dev.Call<int>("getVendorId");
                int pid = dev.Call<int>("getProductId");
                int dcls = dev.Call<int>("getDeviceClass");
                string prod = SafeStr(() => dev.Call<string>("getProductName"));
                string manu = SafeStr(() => dev.Call<string>("getManufacturerName"));
                int ifaceCount = dev.Call<int>("getInterfaceCount");
                var ifaceClasses = new List<int>();
                bool devIsUvc = dcls == USB_CLASS_MISC;
                for (int i = 0; i < ifaceCount; i++)
                    using (var iface = dev.Call<AndroidJavaObject>("getInterface", i))
                    {
                        int icls = iface.Call<int>("getInterfaceClass");
                        ifaceClasses.Add(icls);
                        if (icls == USB_CLASS_VIDEO) devIsUvc = true;
                    }
                bool hasPerm = _usbManager.Call<bool>("hasPermission", dev);
                LogOnce($"usb {vid:X4}:{pid:X4} '{prod}' by '{manu}' devClass={dcls} ifaces=[{string.Join(",", ifaceClasses)}] uvc={devIsUvc} hasPermission={hasPerm}");
                names.Add($"{vid:X4}:{pid:X4} {prod}{(devIsUvc ? " [UVC]" : "")}");
                if (devIsUvc)
                {
                    anyUvc = true;
                    if (hasPerm) { _usbPermVerdict = "GRANTED"; ParseFormats(dev); }
                    else _pendingDevices.Add(dev);
                }
            }
            _uvcVerdict = _usbCount == 0
                ? "0 USB devices visible to the app"
                : (anyUvc ? "UVC device present: " + string.Join(" | ", names)
                          : "USB seen, none UVC-class: " + string.Join(" | ", names));
        }
        catch (Exception e) { _uvcVerdict = "enumeration threw: " + e; Log(_uvcVerdict); }
#else
        _uvcVerdict = "editor";
#endif
    }

    void RequestUsbPermissionForPending()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_pendingDevices.Count == 0)
        {
            if (_usbPermVerdict == "n/a")
                _usbPermVerdict = _usbCount <= 0 ? "no device to ask for" : "no UVC device to ask for";
            return;
        }
        try
        {
            var intent = new AndroidJavaObject("android.content.Intent", ACTION_USB_PERMISSION);
            intent.Call<AndroidJavaObject>("setPackage", _activity.Call<string>("getPackageName"));
            using (var piClass = new AndroidJavaClass("android.app.PendingIntent"))
            {
                var pi = piClass.CallStatic<AndroidJavaObject>(
                    "getBroadcast", _activity, 0, intent, FLAG_UPDATE_CURRENT | FLAG_MUTABLE);
                foreach (var dev in _pendingDevices)
                {
                    LogOnce("calling UsbManager.requestPermission() — system dialog should appear");
                    _usbManager.Call("requestPermission", dev, pi);
                }
            }
            if (!_usbPermVerdict.StartsWith("GRANTED"))
                _usbPermVerdict = "PENDING (dialog requested, scan #" + _scanCount + ")";
        }
        catch (Exception e) { _usbPermVerdict = "requestPermission threw: " + e; Log(_usbPermVerdict); }
#endif
    }

    void PollUsbPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_pendingDevices.Count == 0) return;
        try
        {
            foreach (var dev in _pendingDevices)
                if (_usbManager.Call<bool>("hasPermission", dev))
                {
                    _usbPermVerdict = "GRANTED";
                    LogOnce("USB permission GRANTED");
                    ParseFormats(dev);
                    return;
                }
        }
        catch (Exception e) { Log("poll threw: " + e); }
#endif
    }

    void ParseFormats(AndroidJavaObject dev)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_haveWorkingUvc) return;
        try
        {
            var conn = _usbManager.Call<AndroidJavaObject>("openDevice", dev);
            if (conn == null) { _formatVerdict = "openDevice returned null"; Log(_formatVerdict); return; }
            byte[] raw;
            using (conn) raw = conn.Call<byte[]>("getRawDescriptors");
            if (raw == null || raw.Length == 0) { _formatVerdict = "no raw descriptors"; Log(_formatVerdict); return; }
            var formats = new List<string>();
            int i = 0;
            while (i + 2 < raw.Length)
            {
                int len = raw[i] & 0xFF;
                if (len < 3) break;
                if ((raw[i + 1] & 0xFF) == CS_INTERFACE)
                {
                    switch (raw[i + 2] & 0xFF)
                    {
                        case VS_FORMAT_MJPEG: formats.Add("MJPEG"); break;
                        case VS_FORMAT_UNCOMPRESSED: formats.Add("Uncompressed(" + GuidName(raw, i + 5) + ")"); break;
                        case VS_FORMAT_FRAME_BASED: formats.Add("FrameBased(" + GuidName(raw, i + 5) + ")"); break;
                    }
                }
                i += len;
            }
            _formatVerdict = formats.Count > 0 ? string.Join(", ", formats) : "no VS format descriptors";
            _haveWorkingUvc = true;
            Log("UVC formats advertised: " + _formatVerdict + "  (raw " + raw.Length + " bytes)");
        }
        catch (Exception e) { _formatVerdict = "parse threw: " + e; Log(_formatVerdict); }
#endif
    }

    static string GuidName(byte[] b, int off)
    {
        if (off + 4 > b.Length) return "guid?";
        string cc = "";
        for (int k = 0; k < 4; k++) { char c = (char)b[off + k]; cc += (c >= 32 && c < 127) ? c : '.'; }
        cc = cc.Trim().ToUpperInvariant().TrimEnd('.');
        switch (cc)
        {
            case "YUY2": return "YUY2";
            case "NV12": return "NV12";
            case "NV21": return "NV21";
            case "I420": case "YU12": return "I420";
            case "H264": return "H264";
            default: return string.IsNullOrEmpty(cc) ? "raw" : cc;
        }
    }

    static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

    void RefreshSummary()
    {
        bool ok = _haveWorkingUvc || _usbPermVerdict == "GRANTED";
        bool sawUsb = _usbCount > 0;
        _banner = ok ? new Color(0.10f, 0.42f, 0.16f)
                     : (sawUsb ? new Color(0.45f, 0.35f, 0.10f) : new Color(0.5f, 0.12f, 0.12f));
        _summary.Clear();
        _summary.AppendLine($"scan #{_scanCount}   uptime {Time.time - _startTime:0}s");
        _summary.AppendLine($"CAMERA permission : {_cameraPerm}");
        _summary.AppendLine($"{HORIZON_USB_CAMERA} : {_horizonPerm}");
        _summary.AppendLine($"USB devices visible: {_usbCount}");
        _summary.AppendLine($"UVC device        : {_uvcVerdict}");
        _summary.AppendLine($"USB permission    : {_usbPermVerdict}");
        _summary.AppendLine($"UVC video formats : {_formatVerdict}");
        WriteFile();
    }

    void WriteFile()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== HDMR probe ===  " + DateTime.Now);
            sb.Append(_summary);
            sb.AppendLine("---- log ----");
            lock (_log) foreach (var l in _log) sb.AppendLine(l);
            File.WriteAllText(_filePath, sb.ToString());
        }
        catch (Exception e) { Debug.Log(PREFIX + "file write failed: " + e.Message); }
    }

    readonly HashSet<string> _once = new HashSet<string>();
    void LogOnce(string m) { if (_once.Add(m)) Log(m); }

    void Log(string m)
    {
        Debug.Log(PREFIX + m);
        lock (_log) { _log.Add($"[{Time.time - _startTime:0.0}s] {m}"); if (_log.Count > 500) _log.RemoveAt(0); }
    }

    // -------------------------------------------------------------------- GUI
    void EnsureStyles()
    {
        if (_stylesReady) return;
        _big = new GUIStyle(GUI.skin.label) { fontSize = 32, wordWrap = true, richText = true };
        _big.normal.textColor = Color.white;
        _mono = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
        _mono.normal.textColor = new Color(0.85f, 0.9f, 0.95f);
        _btn = new GUIStyle(GUI.skin.button) { fontSize = 26, fixedHeight = 60 };
        _stylesReady = true;
    }

    void OnGUI()
    {
        EnsureStyles();
        var prev = GUI.color;
        GUI.color = _banner;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = prev;

        GUILayout.BeginArea(new Rect(40, 28, Screen.width - 80, Screen.height - 56));
        GUILayout.Label("<b>HDMR — USB / UVC probe</b>", _big);
        GUILayout.Space(6);
        GUILayout.Label(_summary.Length > 0 ? _summary.ToString() : "starting…", _big);

        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Rescan now", _btn, GUILayout.Width(220))) DoScan();
        if (GUILayout.Button("Ask USB again", _btn, GUILayout.Width(300))) RequestUsbPermissionForPending();
        if (GUILayout.Button("Dump log → logcat", _btn, GUILayout.Width(340)))
            lock (_log) foreach (var l in _log) Debug.Log(PREFIX + "· " + l);
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        GUILayout.Label("log (newest first):", _mono);
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        lock (_log) for (int k = _log.Count - 1; k >= 0; k--) GUILayout.Label(_log[k], _mono);
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}
