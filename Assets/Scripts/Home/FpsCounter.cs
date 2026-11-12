using UnityEngine;

namespace Hdmr.Home
{
    /// <summary>
    /// Real, live render-fps readout for the preview panel's top-right
    /// corner (per the user's own paper nav-schema: "indicateur FPS en
    /// haut-droite"). This is Unity's own frame rate, not the capture
    /// card's — there is no real HDMI feed wired into this scene yet
    /// (nuvc capture lives in the separate com.natis.hdmr.nuvc app), so a
    /// per-source fps isn't available here. Averaged over a short window
    /// rather than 1/deltaTime every frame, which jitters too much to read.
    /// </summary>
    public class FpsCounter : MonoBehaviour
    {
        [SerializeField] TMPro.TMP_Text _text;
        [Tooltip("Seconds between readout updates — averaged over this window rather than per-frame, which jitters too much to read.")]
        [SerializeField] float _updateInterval = 0.5f;

        int _frames;
        float _timer;

        void Update()
        {
            _frames++;
            _timer += Time.unscaledDeltaTime;
            if (_timer < _updateInterval) return;

            float fps = _frames / _timer;
            if (_text != null) _text.text = Mathf.RoundToInt(fps) + " fps";
            _frames = 0;
            _timer = 0f;
        }
    }
}
