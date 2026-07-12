using UnityEngine;
using TMPro;
using SVN.Core;
using System.Text;

namespace SVN.UI
{
    public class LoadingUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TextMeshProUGUI animatedDots;
        [SerializeField] private SVNManager svnManager;

        [Header("Wave Settings")]
        [SerializeField] private int dotCount = 50;
        [SerializeField] private float pulseSpeed = 5f;
        [SerializeField] private float waveTightness = 0.2f;
        [SerializeField] private float waveAmplitude = 0.3f;

        private StringBuilder _sb = new StringBuilder();
        private string[] _hexCache;
        private int frameCounter = 0;
        private const int framesBetweenUpdates = 3;

        private void Awake()
        {
            _hexCache = new string[256];
            for (int i = 0; i < 256; i++)
            {
                _hexCache[i] = i.ToString("X2");
            }
        }

        private void Start()
        {
            if (svnManager != null)
            {
                svnManager.OnProcessingStateChanged += HandleProcessingStateChanged;
            }
        }

        private void OnDestroy()
        {
            if (svnManager != null)
                svnManager.OnProcessingStateChanged -= HandleProcessingStateChanged;
        }

        private void HandleProcessingStateChanged(bool isProcessing)
        {
            if (animatedDots != null && !isProcessing) animatedDots.text = "";

            if (isProcessing)
            {
                WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Indeterminate);
            }
            else
            {
                WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.NoProgress);
                WindowsTaskbarProgress.Flash();
            }
        }

        private void Update()
        {
            if (svnManager == null || !svnManager.IsProcessing || animatedDots == null) return;

            frameCounter++;
            if (frameCounter < framesBetweenUpdates) return;
            frameCounter = 0;

            _sb.Clear();
            _sb.Append("<nobr><size=150%>");

            float currentTime = Time.time;

            for (int i = 0; i < dotCount; i++)
            {
                float offset = i * waveTightness;
                float angle = currentTime * pulseSpeed - offset;

                float vOffset = Mathf.Sin(angle) * waveAmplitude;

                float alphaFactor = (Mathf.Sin(angle) + 1f) / 2f;
                alphaFactor = Mathf.Pow(alphaFactor, 2);
                int alphaInt = Mathf.RoundToInt(alphaFactor * 255);
                string alphaHex = _hexCache[alphaInt];

                _sb.Append("<voffset=").Append(vOffset.ToString("F2")).Append("em><alpha=#")
                   .Append(alphaHex).Append(">.</voffset>");
            }

            _sb.Append("</size></nobr>");
            animatedDots.text = _sb.ToString();
        }
    }
}