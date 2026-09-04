using System.Collections;
using UnityEngine;

namespace Hdmr
{
    /// <summary>
    /// Automated all-modes check. Because resolution changes require a full app
    /// relaunch, the self-test is a relaunch loop driven by HdmrSession.SelfTestIndex:
    /// each app run tests exactly one mode, logs the result ([SELFTEST]), then
    /// relaunches for the next. Kick it off with Run(); pull hdmr-log.txt when done.
    /// </summary>
    public class HdmrSelfTest : MonoBehaviour
    {
        public HdmrController ctrl;
        public HdmrQuadDrawer drawer;

        public bool Running => HdmrSession.SelfTestIndex >= 0;
        public string Progress { get; private set; } = "";

        public void Run()
        {
            HdmrSession.SelfTestIndex = 0;
            HdmrLog.SelfTest("=== self-test start (relaunch loop) ===");
            ctrl.Relaunch();
        }

        void Start()
        {
            if (HdmrSession.SelfTestIndex >= 0) StartCoroutine(RunOneMode());
        }

        IEnumerator RunOneMode()
        {
            int idx = HdmrSession.SelfTestIndex;

            // wait for the device + mode list
            float t = 0;
            while ((ctrl.Cam == null || ctrl.Sizes.Count == 0) && t < 20f) { t += Time.deltaTime; yield return null; }
            if (ctrl.Cam == null || ctrl.Sizes.Count == 0)
            {
                HdmrLog.SelfTest("FAIL (no device / no modes) — aborting self-test");
                HdmrSession.SelfTestIndex = -1;
                yield break;
            }

            if (idx >= ctrl.Sizes.Count)
            {
                HdmrLog.SelfTest($"=== self-test done ({ctrl.Sizes.Count} modes) ===");
                HdmrSession.SelfTestIndex = -1;
                HdmrSession.ShowStream = false;
                yield break;
            }

            var s = ctrl.Sizes[idx];
            Progress = $"self-test {idx + 1}/{ctrl.Sizes.Count}: {HdmrController.Describe(s)}";

            // make sure we're actually streaming this mode
            if (!s.IsSameValue(ctrl.Cam.CurrentSize))
            {
                HdmrLog.SelfTest($"[{idx + 1}/{ctrl.Sizes.Count}] want {HdmrController.Describe(s)} — relaunching to it");
                HdmrSession.SetDesired(s.FrameType, s.Width, s.Height);
                HdmrSession.ShowStream = true;
                ctrl.Relaunch();
                yield break;
            }

            // measure ~7s
            float fps = 0, iv = 0; bool live = false; int w = 0, h = 0;
            for (float e = 0; e < 7f; e += 0.25f)
            {
                yield return new WaitForSeconds(0.25f);
                if (drawer.CurrentTex != null) { w = drawer.CurrentTex.width; h = drawer.CurrentTex.height; }
                if (drawer.FramesLive) { live = true; fps = Mathf.Max(fps, drawer.Fps); iv = drawer.FrameIntervalMs; }
            }
            HdmrLog.SelfTest($"[{idx + 1}/{ctrl.Sizes.Count}] {(live ? "PASS" : "FAIL")}  {HdmrController.Describe(s),-22}  tex {w}x{h}  {fps:0} fps  {iv:0}ms  (see HDMRnative log for latency)");

            HdmrSession.SelfTestIndex = idx + 1;
            ctrl.Relaunch();
        }
    }
}
