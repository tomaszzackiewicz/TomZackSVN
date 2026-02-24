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

        private StringBuilder _sb = new StringBuilder();

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
            _sb.Append("<nobr><size=150%><voffset=50em>");

            for (int i = 0; i < dotCount; i++)
            {
                string alpha = GetPulseAlpha(i * waveTightness);
                _sb.Append($"<alpha=#{alpha}>.");
            }

            _sb.Append("</voffset></size></nobr>");
            animatedDots.text = _sb.ToString();
        }

        private string GetPulseAlpha(float offset)
        {
            float alpha = (Mathf.Sin(Time.time * pulseSpeed - offset) + 1f) / 2f;
            alpha = Mathf.Pow(alpha, 2);
            int alphaInt = Mathf.RoundToInt(alpha * 255);
            return alphaInt.ToString("X2");
        }
    }
}