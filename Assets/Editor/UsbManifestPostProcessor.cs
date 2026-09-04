using System.Xml;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// Post-processes the generated Gradle project (unityLibrary module):
///  - adds  uses-permission horizonos.permission.USB_CAMERA  (Horizon OS v76+, undocumented)
///  - drops res/xml/device_filter_uvc.xml, referenced by uvc-plugin-10.17.0.aar's
///    manifest but unresolvable from the sibling aandusb AAR (local AARs carry no
///    dependency metadata)
///  - adds jniLibs.pickFirsts for libc++_shared.so, provided by both Unity and the
///    aandusb AAR
/// </summary>
public class UsbManifestPostProcessor : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 1;
    const string A = "http://schemas.android.com/apk/res/android";

    const string DeviceFilterUvc =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<resources>\n" +
        "    <usb-device class=\"239\" subclass=\"2\" />\n" +
        "    <usb-device class=\"14\" subclass=\"1\" />\n" +
        "    <usb-device class=\"14\" subclass=\"2\" />\n</resources>\n";

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        // 1) device_filter_uvc.xml
        string xmlDir = Path.Combine(path, "src", "main", "res", "xml");
        Directory.CreateDirectory(xmlDir);
        string xmlPath = Path.Combine(xmlDir, "device_filter_uvc.xml");
        File.WriteAllText(xmlPath, DeviceFilterUvc);
        Debug.Log("[HDMR] wrote " + xmlPath);

        // 2) manifest permission
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (File.Exists(manifestPath))
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(manifestPath);
            var root = doc.DocumentElement;
            foreach (var perm in new[] { "horizonos.permission.USB_CAMERA", "android.permission.CAMERA" })
            {
                if (root.GetElementsByTagName("uses-permission").Cast<XmlElement>()
                        .Any(e => e.GetAttribute("name", A) == perm)) continue;
                var el = doc.CreateElement("uses-permission");
                el.SetAttribute("name", A, perm);
                root.AppendChild(el);
            }
            // nuvc build: aandusb normally contributes the USB_DEVICE_ATTACHED
            // intent-filter (auto-launch + auto-grant on plug-in). Add it to the
            // MAIN activity here.
            if (HDMRBuildNuvc.Active)
            {
                XmlElement main = null;
                foreach (XmlElement a in doc.GetElementsByTagName("activity"))
                    if (a.GetElementsByTagName("action").Cast<XmlElement>()
                         .Any(e => e.GetAttribute("name", A) == "android.intent.action.MAIN"))
                        { main = a; break; }

                bool hasAttach = main != null && main.GetElementsByTagName("action").Cast<XmlElement>()
                    .Any(e => e.GetAttribute("name", A) == "android.hardware.usb.action.USB_DEVICE_ATTACHED");

                if (main != null && !hasAttach)
                {
                    var f = doc.CreateElement("intent-filter");
                    var act = doc.CreateElement("action");
                    act.SetAttribute("name", A, "android.hardware.usb.action.USB_DEVICE_ATTACHED");
                    f.AppendChild(act);
                    main.AppendChild(f);

                    var md = doc.CreateElement("meta-data");
                    md.SetAttribute("name", A, "android.hardware.usb.action.USB_DEVICE_ATTACHED");
                    md.SetAttribute("resource", A, "@xml/device_filter_uvc");
                    main.AppendChild(md);
                    Debug.Log("[HDMR] injected USB_DEVICE_ATTACHED into MAIN activity");
                }
                else Debug.Log("[HDMR] USB_DEVICE_ATTACHED: main=" + (main != null) + " hasAttach=" + hasAttach);
            }

            doc.Save(manifestPath);
            Debug.Log("[HDMR] patched manifest:\n" + File.ReadAllText(manifestPath));
        }

        // 3) jniLibs pickFirst for libc++_shared.so in every module's build.gradle
        string gradleRoot = Directory.GetParent(path).FullName;
        foreach (var mod in new[] { "unityLibrary", "launcher" })
        {
            string bg = Path.Combine(gradleRoot, mod, "build.gradle");
            if (!File.Exists(bg)) continue;
            string txt = File.ReadAllText(bg);
            if (txt.Contains("libc++_shared.so")) continue;

            if (Regex.IsMatch(txt, @"jniLibs\s*\{"))
                txt = new Regex(@"(jniLibs\s*\{)").Replace(txt,
                    "$1\n            pickFirsts += ['**/libc++_shared.so']", 1);
            else if (Regex.IsMatch(txt, @"\n\s*packaging\s*\{"))
                txt = new Regex(@"(\n\s*packaging\s*\{)").Replace(txt,
                    "$1\n        jniLibs { pickFirsts += ['**/libc++_shared.so'] }", 1);
            else
                txt = new Regex(@"(\nandroid\s*\{)").Replace(txt,
                    "$1\n    packaging { jniLibs { pickFirsts += ['**/libc++_shared.so'] } }", 1);

            File.WriteAllText(bg, txt);
            Debug.Log("[HDMR] added libc++ pickFirst to " + bg);
        }
    }
}
