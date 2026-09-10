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

        // === FIX (perf): cache indent-stringów per głębokość — bez tego rebuild
        // 198 itemów alokuje 198 StringBuilder + 198 string per filtr change.
        // Klucz: int depth. Wartość: niemutowalny string.
        private static readonly System.Collections.Generic.Dictionary<int, string> IndentCache = new();

        // === FIX (perf, dispatcher): cache UnityAction per przycisk — eliminuje
        // ~1200 closures per rebuild. Wcześniej każdy ActivateButton robił
        // `onClick.AddListener(() => action())` (nowy closure) + sam action
        // (kolejny closure dla export). Rebuild 198 itemów × 6 buttonów = ~1200
        // closures → GC pressure, alokacje w hot path zmiany filtra.
        // Teraz: delegaty tworzone RAZ w Awake (method-group assignment, bez
        // closures), Setup tylko podmienia listener.
        private UnityAction _onCopyUrlClick;
        private UnityAction _onCopyRelPathClick;
        private UnityAction _onLogClick;
        private UnityAction _onExportClick;
        private UnityAction _onBlameClick;
        private UnityAction _onCheckoutFolderClick;

        public RepoNode Node => _node;

        private void Awake()
        {
            // Method-group assignment — brak closures, delegate instances stałe
            // na całe życie instancji. RemoveListener porównuje target+method,
            // więc zadziała poprawnie przy każdym Setup.
            _onCopyUrlClick = CopyUrlToClipboard;
            _onCopyRelPathClick = CopyRelativePath;
            _onLogClick = ShowLog;
            _onExportClick = OnExportClick;
            _onBlameClick = ShowBlame;
            _onCheckoutFolderClick = CheckoutFolderAction;
        }

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

            // W filtrach (flat) węzeł nie ma Parent ani Children — rozwijanie
            // nie ma sensu i tylko miga bez efektu. Ukryj strzałkę.
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

            // === FIX: ActivateButton przyjmuje UnityAction — bez `() => action()`
            // wrappowania. Delegaty są cache'owane w polach, więc RemoveListener
            // usuwa KONKRETNY delegat (nie wszystkich), a AddListener nie tworzy
            // nowej closure.
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
        private void CopyUrlToClipboard() { GUIUtility.systemCopyBuffer = _node.FullUrl; }

        // === FIX (perf): method-group wrapper dla exportu — poprzednio
        // `() => SafeFireAndForget(ExportItemAsync)` alokowało closure per
        // Setup call. Teraz delegate jest przypisany raz w Awake.
        private void OnExportClick() => SafeFireAndForget(ExportItemAsync);

        private async Task ExportItemAsync()
        {
            try
            {
                // === FIX drobiazg: guard przed niezaładowanym projektem (SvnRunner
                // rzuciłby "Working Directory is null" z głębi).
                string workingDir = SVNManager.Instance?.WorkingDir;
                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    SVNLogBridge.LogError("[RepoBrowser] No working directory — load a project first.");
                    return;
                }

                string cacheFolder = Path.Combine(SVNPrefs.TemporaryCachePath, "SVN_Exports");
                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                // === FIX K2: unikalna nazwa docelowa — '{_node.Name}' kolidowało
                // dla elementów o tej samej nazwie z różnych gałęzi/katalogów
                // (/branches/A/Assets i /branches/B/Assets → jedno miejsce, drugi
                // eksport CICHO nadpisywał pierwszy przez --force).
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

                // === FIX drobiazg: sprzątanie starych eksportów (spójnie z Diff/
                // Blame/Cache — 24 h), folder inaczej rósł w nieskończoność.
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

        /// <summary>
        /// === FIX (perf): przyjmuje UnityAction bezpośrednio — brak wrappowania
        /// w `() => action()`. RemoveListener usuwa konkretny delegat (nie
        /// wszystkich), co jest bezpieczniejsze przy ewentualnych innych
        /// listener'ach na tym samym przycisku.
        /// </summary>
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
                // === FIX K1: SVNLogBridge.LogWarning(msg, bool append) — drugi
                // parametr to nie kontekst-Object (to sygnatura Debug.LogWarning);
                // przekazanie 'btn' = CS1503. Kontekst tylko przez Debug.
                Debug.LogWarning($"[RepoBrowser] SVNHoverHandler component is missing on button: {btn.name}", btn);
            }
        }
    }
}