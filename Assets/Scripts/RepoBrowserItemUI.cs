using System;
using System.IO;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class RepoBrowserItemUI : MonoBehaviour
    {
        [Header("UI References")]
        public TextMeshProUGUI indentText;
        public TextMeshProUGUI nameText;
        public Button foldButton;
        public TextMeshProUGUI foldArrowText;

        [Header("Action Buttons")]
        public Button logBtn;
        public Button blameBtn;
        public Button exportBtn;
        public Button copyPathBtn;

        [Header("Repo Browser Actions")]
        public Button checkoutFolderBtn;
        public Button copyRelPathBtn;

        private RepoNode _node;
        private SVNRepoBrowser _browser;

        private static readonly Color DirNameColor = new(0f, 0.2f, 0.4f);
        private static readonly Color FileNameColor = new(0.7f, 0.85f, 1f);

        public RepoNode Node => _node;

        public void Initialize(RepoNode node, SVNRepoBrowser browser)
        {
            _node = node;
            _browser = browser;

            RenderIndent();
            RenderName();
            SetupFoldButton();
            SetupActionButtons();

            UpdateArrowVisual(node.IsExpanded);
        }

        private void RenderIndent()
        {
            if (indentText == null) return;

            int depth = _node.Depth;
            float targetWidth = depth <= 0 ? 0f : depth * 30f;
            indentText.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);

            if (depth <= 0)
            {
                indentText.text = string.Empty;
                return;
            }

            var sb = new System.Text.StringBuilder(depth * 4);
            for (int i = 0; i < depth; i++)
                sb.Append(i == depth - 1 ? "└─ " : " |  ");

            indentText.text = sb.ToString();
        }

        private void RenderName()
        {
            if (nameText == null) return;

            string metaData = "";
            if (!string.IsNullOrEmpty(_node.LastChangedRev))
            {
                metaData = $"  <color=#BBBBBB><size=12>[ r{_node.LastChangedRev} - {_node.LastChangedAuthor} ]</size></color>";
            }

            string displayName = _node.Name + (_node.IsDirectory ? "/" : "");
            nameText.text = displayName + metaData;

            nameText.color = _node.IsDirectory ? DirNameColor : FileNameColor;
            nameText.fontStyle = _node.IsDirectory ? FontStyles.Bold : FontStyles.Normal;
        }

        private void SetupFoldButton()
        {
            if (foldButton == null) return;

            if (!_node.IsDirectory)
            {
                foldButton.gameObject.SetActive(false);
                return;
            }

            foldButton.gameObject.SetActive(true);
            foldButton.onClick.RemoveAllListeners();
            foldButton.onClick.AddListener(OnFoldClick);
        }

        private void OnFoldClick()
        {
            _browser?.ToggleNode(_node);
        }

        private void SetupActionButtons()
        {
            SetButtonActive(logBtn, false); SetButtonActive(blameBtn, false);
            SetButtonActive(exportBtn, false); SetButtonActive(copyPathBtn, false);
            SetButtonActive(checkoutFolderBtn, false); SetButtonActive(copyRelPathBtn, false);

            if (copyPathBtn != null) ActivateButton(copyPathBtn, CopyUrlToClipboard, "Copy full URL.");
            if (copyRelPathBtn != null) ActivateButton(copyRelPathBtn, CopyRelativePath, "Copy relative path (without server URL).");
            if (logBtn != null) ActivateButton(logBtn, ShowLog, "View Log.");

            if (exportBtn != null) ActivateButton(exportBtn, () => SafeFireAndForget(ExportItemAsync), "Export file or folder to disk.");

            if (_node.IsDirectory)
            {
                if (checkoutFolderBtn != null) ActivateButton(checkoutFolderBtn, CheckoutFolderAction, "Checkout this specific folder to disk.");
            }
            else
            {
                if (blameBtn != null) ActivateButton(blameBtn, ShowBlame, "Blame.");
            }
        }

        private void CopyRelativePath()
        {
            _browser?.CopyRelativePath(_node);
        }

        private void CheckoutFolderAction()
        {
            _browser?.CheckoutFolder(_node);
        }

        private void ShowLog() => SVNManager.Instance?.GetModule<SVNLog>()?.ShowLogForPath(_node.FullUrl);
        private void ShowBlame() => SVNManager.Instance?.GetModule<SVNBlame>()?.ShowBlameInMainConsole(_node.FullUrl);
        private void CopyUrlToClipboard() { GUIUtility.systemCopyBuffer = _node.FullUrl; }

        private async Task ExportItemAsync()
        {
            try
            {
                string cacheFolder = Path.Combine(Application.temporaryCachePath, "SVN_Exports");
                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                string tempPath = Path.Combine(cacheFolder, _node.Name);
                string command = $"export \"{_node.FullUrl}\" \"{tempPath}\" --force";

                SVNLogBridge.LogLine($"<color=yellow>[RepoBrowser]</color> Exporting {_node.Name}...");

                await SvnRunner.RunAsync(command, SVNManager.Instance.WorkingDir);

                if (Directory.Exists(tempPath) || File.Exists(tempPath))
                {
                    SVNManager.Instance?.GetModule<SVNExternal>()?.OpenInExplorerAndSelect(tempPath);
                    SVNLogBridge.LogLine($"<color=green>[RepoBrowser]</color> Exported successfully to: {tempPath}");
                }
                else
                {
                    SVNLogBridge.LogError("Failed to export item.");
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Export failed: {ex.Message}");
            }
        }

        public void UpdateArrowVisual(bool isExpanded)
        {
            if (foldArrowText != null && _node != null && _node.IsDirectory)
            {
                foldArrowText.text = "▼";
                foldArrowText.rectTransform.localRotation = Quaternion.Euler(0, 0, isExpanded ? 0f : -90f);
            }
        }

        private void SafeFireAndForget(Func<Task> operation) { _ = FireAndForget(operation); }
        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation(); }
            catch (Exception ex) { SVNLogBridge.LogError($"[RepoBrowserItem] {ex.Message}"); }
        }

        private static void SetButtonActive(Button btn, bool active)
        {
            if (btn != null) btn.gameObject.SetActive(active);
        }

        private static void ActivateButton(Button btn, Action action, string tooltip)
        {
            if (btn == null || action == null) return;
            btn.gameObject.SetActive(true);
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => action());
            BindHoverStatic(btn, tooltip);
        }

        private static void BindHoverStatic(Button btn, string tooltipText)
        {
            if (btn == null) return;

            var handler = btn.GetComponent<SVNHoverHandler>();

            if (handler != null)
            {
                handler.SetTooltip(tooltipText);
            }
            else
            {

                Debug.LogWarning($"[RepoBrowser] SVNHoverHandler component is missing on button: {btn.name}", btn);
            }
        }
    }
}