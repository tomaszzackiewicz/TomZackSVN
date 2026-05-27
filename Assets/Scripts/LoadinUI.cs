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
        [SerializeField] private float waveAmplitude = 0.3f; // Amplituda fizycznej fali (góra/dół) w jednostkach em

        private StringBuilder _sb = new StringBuilder();
        private string[] _hexCache;

        private void Awake()
        {
            // Cache dla wartości Hex Alpha (00-FF), aby wyeliminować ToString() w Update
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

            _sb.Clear();
            _sb.Append("<nobr><size=150%>");

            float currentTime = Time.time;

            for (int i = 0; i < dotCount; i++)
            {
                float offset = i * waveTightness;
                float angle = currentTime * pulseSpeed - offset;

                // 1. Naprawa pozycji: voffset wewnątrz pętli tworzy prawdziwy ruch falowy
                float vOffset = Mathf.Sin(angle) * waveAmplitude;

                // 2. Pobieranie przezroczystości z bezśmieciowego cache
                float alphaFactor = (Mathf.Sin(angle) + 1f) / 2f;
                alphaFactor = Mathf.Pow(alphaFactor, 2);
                int alphaInt = Mathf.RoundToInt(alphaFactor * 255);
                string alphaHex = _hexCache[alphaInt];

                // 3. Łączenie tagów bez alokacji nowych stringów za pomocą Append
                _sb.Append("<voffset=").Append(vOffset.ToString("F2")).Append("em><alpha=#")
                   .Append(alphaHex).Append(">.</voffset>");
            }

            _sb.Append("</size></nobr>");
            animatedDots.text = _sb.ToString();
        }
    }
}