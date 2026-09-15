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
        [SerializeField] float _distance = 1.2f;
        [SerializeField] float _heightOffset = 0f;
        [SerializeField] float _initialDelaySeconds = 0.5f;

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
