using System;
using System.IO;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
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

        private static readonly System.Collections.Generic.Dictionary<int, string> IndentCache = new();

        private UnityAction _onCopyUrlClick;
        private UnityAction _onCopyRelPathClick;
        private UnityAction _onLogClick;
        private UnityAction _onExportClick;
        private UnityAction _onBlameClick;
        private UnityAction _onCheckoutFolderClick;

        public RepoNode Node => _node;

        private void Awake()
        {
            _onCopyUrlClick = CopyUrlToClipboard;
            _onCopyRelPathClick = CopyRelativePath;
            _onLogClick = ShowLog;
            _onExportClick = OnExportClick;
            _onBlameClick = ShowBlame;
            _onCheckoutFolderClick = CheckoutFolderAction;

            // Initial arrow state = COLLAPSED (▶ visually), matching the most
            // common case at first render. Awake runs synchronously during
            // Instantiate, so this state is what frame 0 shows before
            // UpdateArrowVisual runs. Without this, the prefab's default
            // arrow (or our previous attempt at 0°) would flash wrong for
            // one frame.
            if (foldArrowText != null)
            {
                if (HasTriangleGlyph())
                {
                    foldArrowText.text = "▼";
                    foldArrowText.rectTransform.localEulerAngles = new Vector3(0f, 0f, 90f);
                }
                else
                {
                    foldArrowText.text = ">";
                    foldArrowText.rectTransform.localEulerAngles = Vector3.zero;
                }
            }
        }

        private bool HasTriangleGlyph()
        {
            var font = foldArrowText != null ? foldArrowText.font : null;
            return font != null && font.HasCharacter('▼');
        }

        public void Initialize(RepoNode node, SVNRepoBrowser browser)
        {
            _node = node;
            _browser = browser;

            RenderIndent();
            RenderName();
            SetupFoldButton();
            SetupActionButtons();
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

            if (!IndentCache.TryGetValue(depth, out string cachedIndent))
            {
                var sb = new System.Text.StringBuilder(depth * 4);
                for (int i = 0; i < depth; i++)
                    sb.Append(i == depth - 1 ? "└─ " : " |  ");

                cachedIndent = sb.ToString();
                IndentCache[depth] = cachedIndent;
            }

            indentText.text = cachedIndent;
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

            bool isFlatFilterResult =
                _node.IsDirectory &&
                _node.Parent == null &&
                _node.Depth >= 0;

            if (!_node.IsDirectory || isFlatFilterResult)
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

            if (copyPathBtn != null) ActivateButton(copyPathBtn, _onCopyUrlClick, "Copy full URL.");
            if (copyRelPathBtn != null) ActivateButton(copyRelPathBtn, _onCopyRelPathClick, "Copy relative path (without server URL).");
            if (logBtn != null) ActivateButton(logBtn, _onLogClick, "View Log.");
            if (exportBtn != null) ActivateButton(exportBtn, _onExportClick, "Export file or folder to disk.");

            if (_node.IsDirectory)
            {
                if (checkoutFolderBtn != null) ActivateButton(checkoutFolderBtn, _onCheckoutFolderClick, "Checkout this specific folder to disk.");
            }
            else
            {
                if (blameBtn != null) ActivateButton(blameBtn, _onBlameClick, "Blame.");
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

        private void CopyUrlToClipboard()
        {
            if (_node == null) return;
            GUIUtility.systemCopyBuffer = _node.FullUrl;
        }

        private void OnExportClick() => SafeFireAndForget(ExportItemAsync);

        private async Task ExportItemAsync()
        {
            try
            {
                string workingDir = SVNManager.Instance?.WorkingDir;
                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    SVNLogBridge.LogError("[RepoBrowser] No working directory — load a project first.");
                    return;
                }

                string cacheFolder = Path.Combine(SVNPrefs.TemporaryCachePath, "SVN_Exports");
                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                string safeName = _node.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    ? Guid.NewGuid().ToString("N").Substring(0, 8)
                    : _node.Name;
                string tempPath = Path.Combine(cacheFolder, $"{safeName}_{DateTime.Now:HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 4)}");

                string command = $"export \"{_node.FullUrl}\" \"{tempPath}\" --force";

                SVNLogBridge.LogLine($"<color=yellow>[RepoBrowser]</color> Exporting {_node.Name}...");

                await SvnRunner.RunAsync(command, workingDir);

                if (Directory.Exists(tempPath) || File.Exists(tempPath))
                {
                    SVNManager.Instance?.GetModule<SVNExternal>()?.OpenInExplorerAndSelect(tempPath);
                    SVNLogBridge.LogLine($"<color=green>[RepoBrowser]</color> Exported successfully to: {tempPath}");
                }
                else
                {
                    SVNLogBridge.LogError("Failed to export item.");
                }

                CleanupOldExports(cacheFolder);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Export failed: {ex.Message}");
            }
        }

        private static void CleanupOldExports(string cacheFolder)
        {
            try
            {
                if (!Directory.Exists(cacheFolder)) return;
                foreach (var entry in Directory.GetFileSystemEntries(cacheFolder))
                {
                    try
                    {
                        DateTime writeTime = Directory.Exists(entry)
                            ? Directory.GetLastWriteTimeUtc(entry)
                            : File.GetLastWriteTimeUtc(entry);

                        if (writeTime < DateTime.UtcNow.AddHours(-24))
                        {
                            if (Directory.Exists(entry)) Directory.Delete(entry, true);
                            else File.Delete(entry);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void UpdateArrowVisual(bool isExpanded)
        {
            if (foldArrowText == null || _node == null || !_node.IsDirectory) return;

            if (HasTriangleGlyph())
            {
                // Text stays "▼" (same glyph), only rotation differs.
                // This matches SvnLineController behaviour — one glyph,
                // two visual states via rotation.
                foldArrowText.text = "▼";
                foldArrowText.rectTransform.localEulerAngles =
                    new Vector3(0f, 0f, isExpanded ? 0f : 90f);
            }
            else
            {
                foldArrowText.text = isExpanded ? "v" : ">";
                foldArrowText.rectTransform.localEulerAngles = Vector3.zero;
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

        private static void ActivateButton(Button btn, UnityAction action, string tooltip)
        {
            if (btn == null || action == null) return;
            btn.gameObject.SetActive(true);
            btn.onClick.RemoveListener(action);
            btn.onClick.AddListener(action);
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