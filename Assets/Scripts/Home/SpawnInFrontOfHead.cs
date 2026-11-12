using System.Collections;
using UnityEngine;

namespace Hdmr.Home
{
    /// <summary>
    /// Positions this object a fixed distance in front of wherever the
    /// camera is actually facing, instead of at a fixed world-space
    /// coordinate. A build script has no way to know the player's
    /// Guardian-calibrated forward direction (it's not a fixed Unity world
    /// axis — it's whatever direction the user faced when they set up their
    /// room), so anything placed at an assumed world position like
    /// (0, 1.4, 1.6) can end up entirely out of view.
    ///
    /// First version of this script did the positioning in Start() and
    /// still ended up out of view — a DIFFERENT bug: Start() runs before
    /// OVRManager delivers the camera's first real tracked pose, so
    /// Camera.main.transform.forward can still be some default/near-vertical
    /// value at that exact moment, tripping the "looking straight up" edge
    /// case and placing the object almost directly overhead instead of in
    /// front. Confirmed on-device: user reported "a small bit of black far
    /// above my head". Real fix: wait a short real delay for a real pose to
    /// land before reading the camera transform at all, not just handle the
    /// vertical-look edge case once.
    /// </summary>
    public class SpawnInFrontOfHead : MonoBehaviour
    {
        [Tooltip("Meters in front of the head (flattened forward direction) to spawn at.")]
        [SerializeField] float _distance = 1.2f;
        [Tooltip("Meters added/subtracted from the camera's own height.")]
        [SerializeField] float _heightOffset = 0f;
        [Tooltip("Seconds to wait before reading the camera pose — avoids the near-vertical default pose before OVRManager delivers a real tracked pose.")]
        [SerializeField] float _initialDelaySeconds = 0.5f;

        // 2026-09-16: this component now sits on a neutral "HomeAnchor"
        // parent (see HDMRBuildHome.SetupPassthroughAndAura's sibling,
        // the anchor built in AddInteractionAndUI) — the tile panel's
        // podium tilt and the stream-preview panel's upright angle are
        // both fixed LOCAL rotations on the anchor's two children, set
        // once at build time, not runtime state here. Keeping this
        // component itself tilt-free means it stays reusable for any
        // future head-locked panel that just needs "in front, facing me",
        // without every user of it inheriting a lectern-specific angle.
        IEnumerator Start()
        {
            yield return new WaitForSeconds(_initialDelaySeconds);

            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[SpawnInFrontOfHead] no Camera.main found — leaving position as authored"); yield break; }

            var camT = cam.transform;
            Vector3 flatForward = camT.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.0001f) flatForward = camT.up; // still looking straight up/down after the delay — fall back
            flatForward.Normalize();

            transform.position = camT.position + flatForward * _distance + Vector3.up * _heightOffset;
            transform.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
            Debug.Log($"[SpawnInFrontOfHead] positioned at {transform.position}, cam was at {camT.position} facing {camT.forward}");
        }
    }
}
