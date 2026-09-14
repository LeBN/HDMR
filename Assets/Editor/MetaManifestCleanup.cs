using System.Linq;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// The Meta XR Core SDK's own build hook (OVRGradleGeneration, callbackOrder
/// 99999) unconditionally stamps every Android build as a VR app —
/// &lt;uses-feature android:name="android.hardware.vr.headtracking" required="true"/&gt;
/// plus a com.oculus.intent.category.VR launcher category — even when the
/// scene has zero OVR/XR components. That's what started popping the
/// "controller required" system dialog on the plain 2D build the moment the
/// package was added (never happened before — see
/// HDMR/research/unity-port-plan.md for the diagnosis).
///
/// Runs AFTER OVRGradleGeneration (higher callbackOrder) and strips that back
/// out for the 2D/XR builds, which don't go through OVRManager/OVRPlugin at
/// all — the SDK is only in this project because the future Home build will
/// need it, and it lives in the same project as nuvc/nuvc.xr.
/// </summary>
public class MetaManifestCleanup : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 200000; // after OVRGradleGeneration's 99999
    const string A = "http://schemas.android.com/apk/res/android";

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        if (!HDMRBuildNuvc.Active && !HDMRBuildNuvcXR.Active) return; // Home build wants the real thing

        string manifestPath = System.IO.Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!System.IO.File.Exists(manifestPath)) return;

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(manifestPath);
        var root = doc.DocumentElement;
        bool changed = false;

        // never hard-require head-tracking hardware to launch either — the
        // 2D build has no XR at all, and the XR build's own OpenXR feature
        // group already declares what it actually needs.
        foreach (XmlElement uf in root.GetElementsByTagName("uses-feature").Cast<XmlElement>().ToList())
        {
            if (uf.GetAttribute("name", A) == "android.hardware.vr.headtracking" &&
                uf.GetAttribute("required", A) == "true")
            {
                uf.SetAttribute("required", A, "false");
                changed = true;
                Debug.Log("[HDMR] MetaManifestCleanup: android.hardware.vr.headtracking -> required=false");
            }
        }

        // the 2D build isn't a VR app at all — this launcher category is what
        // actually trips the "controller required" dialog on it.
        if (HDMRBuildNuvc.Active)
        {
            foreach (XmlElement cat in doc.GetElementsByTagName("category").Cast<XmlElement>().ToList())
            {
                if (cat.GetAttribute("name", A) == "com.oculus.intent.category.VR")
                {
                    cat.ParentNode.RemoveChild(cat);
                    changed = true;
                    Debug.Log("[HDMR] MetaManifestCleanup: removed com.oculus.intent.category.VR from 2D build");
                }
            }
        }

        if (changed) doc.Save(manifestPath);
    }
}
