using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        private bool _isCurrentViewIgnored = false;

        private List<string> _cachedIgnoreRules = new List<string>();
        private bool _fileMissingOnLastLoad = false;

        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void ShowOnlyModified()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = "Refreshing...";
            if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = "Refreshing...";

            _isCurrentViewIgnored = false;
            ExecuteRefreshWithAutoExpand();
        }

        public void ShowOnlyIgnored()
        {
            _isCurrentViewIgnored = true;
            ExecuteRefreshWithAutoExpand();
        }

        public async void ExecuteRefreshWithAutoExpand()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            string root = svnManager.WorkingDir;
            string timestamp = $"[{DateTime.Now:HH:mm:ss}]";

            // Funkcja pomocnicza do pisania w obu logach jednoczeœnie
            Action<string> LogBoth = (msg) => {
                if (svnUI.LogText != null) svnUI.LogText.text += msg;
                if (svnUI.CommitConsoleContent != null) svnUI.CommitConsoleContent.text += msg;
            };

            // Czyœcimy poprzednie logi
            if (svnUI.LogText != null) svnUI.LogText.text = $"{timestamp} <color=#0FF>Starting Refresh...</color>\n";
            if (svnUI.CommitConsoleContent != null) svnUI.CommitConsoleContent.text = $"{timestamp} <color=#0FF>Starting Refresh...</color>\n";

            try
            {
                // KROK 1: Status SVN
                LogBoth("Checking SVN status...\n");
                var statusDict = _isCurrentViewIgnored
                    ? await SvnRunner.GetIgnoredOnlyAsync(root)
                    : await SvnRunner.GetChangesDictionaryAsync(root);

                if (statusDict == null)
                {
                    LogBoth("<color=red>Error: statusDict is null!</color>\n");
                    return;
                }

                LogBoth($"Found {statusDict.Count} entries. Processing paths...\n");

                // KROK 2: Przetwarzanie œcie¿ek
                svnManager.ExpandedPaths.Clear();
                svnManager.ExpandedPaths.Add("");

                foreach (var item in statusDict)
                {
                    string path = item.Key;
                    string stat = item.Value.status;
                    bool isChange = !string.IsNullOrEmpty(stat) && (stat == "M" || stat == "A" || stat == "?" || stat == "C" || stat == "!" || stat == "D");
                    bool shouldExpand = _isCurrentViewIgnored ? (stat == "I") : isChange;

                    if (shouldExpand)
                        AddParentFoldersToExpanded(path);
                }

                // KROK 3: Raport rozmiaru
                LogBoth("Calculating commit size...\n");
                string report = await SvnRunner.GetCommitSizeReportAsync(root);
                if (svnUI.CommitSizeText != null)
                    svnUI.CommitSizeText.text = $"<color=yellow>Total Size of Changes to Commit: {report}</color>\n";

                // KROK 4: Generowanie drzewa
                LogBoth("Building visual tree...\n");
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(root, svnManager.ExpandedPaths, _isCurrentViewIgnored);

                // KROK 5: Aktualizacja wyœwietlaczy drzewa
                string treeResult = string.IsNullOrEmpty(result.tree) ? "<i>No changes detected.</i>" : result.tree;

                if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = treeResult;
                if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = treeResult;

                svnManager.UpdateAllStatisticsUI(result.stats, _isCurrentViewIgnored);

                LogBoth("<color=green>Refresh Complete!</color>\n");
            }
            catch (Exception ex)
            {
                string errorMsg = $"<color=red>Exception:</color> {ex.Message}\n";
                LogBoth(errorMsg);
                Debug.LogError($"[SVN] Refresh Error: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void AddParentFoldersToExpanded(string filePath)
        {
            string[] parts = filePath.Split('/');
            string currentPath = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : $"{currentPath}/{parts[i]}";
                if (!svnManager.ExpandedPaths.Contains(currentPath))
                    svnManager.ExpandedPaths.Add(currentPath);
            }
        }

        public void CollapseAll()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = "Collapsing...";
            if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = "Collapsing...";

            RefreshLocal();
        }

        public async void RefreshLocal()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            string timestamp = $"[{DateTime.Now:HH:mm:ss}]";

            // Funkcja pomocnicza dla spójnoœci feedbacku
            Action<string> LogBoth = (msg) => {
                if (svnUI.LogText != null) svnUI.LogText.text += msg;
                if (svnUI.CommitConsoleContent != null) svnUI.CommitConsoleContent.text += msg;
            };

            try
            {
                string root = svnManager.WorkingDir;

                LogBoth($"{timestamp} <color=green>Local refresh started...</color>\n");

                // Pobieramy dane wizualne (u¿ywaj¹c aktualnego stanu expandedPaths)
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(
                    root,
                    svnManager.ExpandedPaths,
                    _isCurrentViewIgnored
                );

                // Aktualizacja UI G³ównego
                if (string.IsNullOrEmpty(result.tree))
                {
                    string cleanMsg = "<i>Working copy clean.</i>";
                    if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = cleanMsg;
                    if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = cleanMsg;
                    LogBoth("-> View is clean.\n");
                }
                else
                {
                    if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = result.tree;
                    if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = result.tree;
                    LogBoth($"-> View updated ({result.stats.FileCount} files shown).\n");
                }

                // Aktualizacja statystyk na dole ekranu
                svnManager.UpdateAllStatisticsUI(result.stats, _isCurrentViewIgnored);

                LogBoth("<color=#55FF55>Done.</color>\n");
            }
            catch (Exception ex)
            {
                string err = $"<color=red>[View Error]:</color> {ex.Message}\n";
                LogBoth(err);
                Debug.LogError($"[SVN] Collapse/Refresh Error: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void ClearUI()
        {
            if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = "<i>No changes found.</i>";
            if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = "";
            if (svnUI.CommitSizeText != null) svnUI.CommitSizeText.text = "<color=yellow>Total Size: 0 B</color>";

            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");
        }

        public void RefreshIgnoredPanel()
        {
            _ = RefreshIgnoredPanelAsync();
        }

        public void ReloadIgnoreRules()
        {
            if (svnManager != null && !string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                LoadIgnoreRulesFromFile(svnManager.WorkingDir);
            }
            else
            {
                Debug.LogError("[SVN] Cannot reload: WorkingDir is null or empty.");
            }
        }


        public void LoadIgnoreRulesFromFile(string workingDir)
        {
            _cachedIgnoreRules.Clear();
            string ignoreFilePath = Path.Combine(workingDir, ".svnignore");

            if (File.Exists(ignoreFilePath))
            {
                _fileMissingOnLastLoad = false;
                try
                {
                    string[] lines = File.ReadAllLines(ignoreFilePath);
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                        {
                            if (!_cachedIgnoreRules.Contains(trimmed))
                                _cachedIgnoreRules.Add(trimmed);
                        }
                    }
                    Debug.Log($"<color=#00FFFF>[SVN]</color> Loaded {_cachedIgnoreRules.Count} rules from .svnignore");
                }
                catch (Exception e) { Debug.LogError($"[SVN] File read error: {e.Message}"); }
            }
            else
            {
                _fileMissingOnLastLoad = true;
                Debug.LogWarning($"[SVN] .svnignore file not found at: {workingDir}");
            }
        }

        public async Task RefreshIgnoredPanelAsync()
        {
            string root = svnManager.WorkingDir;
            string ignoreFilePath = Path.Combine(root, ".svnignore");

            StringBuilder sb = new StringBuilder();

            // --- DIAGNOSTIC SECTION ---
            sb.AppendLine("<color=#AAAAAA><b>System Info:</b></color>");
            sb.AppendLine($"<color=#666666>Working Dir:</color> <color=#FFFFFF>{root}</color>");
            sb.AppendLine($"<color=#666666>Config File:</color> <color=#FFFFFF>{ignoreFilePath}</color>");

            // Check if file actually exists at that path
            bool fileExists = File.Exists(ignoreFilePath);
            string fileStatus = fileExists ? "<color=green>FOUND</color>" : "<color=red>NOT FOUND</color>";
            sb.AppendLine($"<color=#666666>File Status:</color> {fileStatus}");
            sb.AppendLine("--------------------------------------------------\n");

            if (!fileExists)
            {
                sb.AppendLine("<color=#FFCC00><b>[!] ACTION REQUIRED</b></color>");
                sb.AppendLine($"Please ensure <b>.svnignore</b> is located in the folder above to load local rules.");
                sb.AppendLine("--------------------------------------------------\n");
            }

            // --- FETCH RULES ---
            List<string> activeRules = await SvnRunner.GetIgnoreRulesFromSvnAsync(root);

            if (_cachedIgnoreRules != null)
            {
                foreach (var fileRule in _cachedIgnoreRules)
                {
                    if (!activeRules.Contains(fileRule)) activeRules.Add(fileRule);
                }
            }

            // --- ACTIVE RULES LIST ---
            sb.AppendLine("<color=#FFA500><b>Active Ignore Rules:</b></color>");
            if (activeRules.Count == 0)
            {
                sb.AppendLine("  <color=#FF4444>No rules loaded. Click 'Reload' if you just added the file.</color>");
            }
            else
            {
                foreach (var rule in activeRules)
                {
                    bool isFromFile = _cachedIgnoreRules.Contains(rule);
                    string color = isFromFile ? "#00FFFF" : "#00FF99";
                    sb.AppendLine($"<color={color}>  {(isFromFile ? "[FILE]" : "[SVN]")} {rule}</color>");
                }
            }

            sb.AppendLine("\n<color=#FF4444><b>Files currently ignored on disk:</b></color>");

            // --- SCAN DISK (Same as before) ---
            int count = 0;
            if (activeRules.Count > 0 && Directory.Exists(root))
            {
                string[] allEntries = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
                foreach (var entry in allEntries)
                {
                    string name = Path.GetFileName(entry);
                    string relPath = entry.Replace(root, "").TrimStart('\\', '/').Replace('\\', '/');
                    if (relPath.Contains(".svn")) continue;

                    bool isIgnored = activeRules.Any(rule =>
                        name.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                        relPath.Split('/').Any(part => part.Equals(rule, StringComparison.OrdinalIgnoreCase)) ||
                        (rule.Contains("*") && IsMatch(name, rule))
                    );

                    if (isIgnored)
                    {
                        sb.AppendLine($"<color=#666666>[I]</color> <color=#FFFFFF>{relPath}</color>");
                        count++;
                        if (count > 200) { sb.AppendLine("<color=#FFFF00>... truncated</color>"); break; }
                    }
                }
            }

            if (count == 0 && activeRules.Count > 0)
                sb.AppendLine("<color=green>No files match the active rules.</color>");

            // --- UPDATE UI ---
            if (svnUI != null && svnUI.IgnoredText != null)
            {
                svnUI.IgnoredText.text = sb.ToString();
            }
        }

        private bool IsMatch(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern == "*") return true;

            string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public async void PushLocalRulesToSvn()
        {
            string root = svnManager.WorkingDir;
            string ignoreFilePath = Path.Combine(root, ".svnignore");

            if (!File.Exists(ignoreFilePath))
            {
                UpdateStatusInUI("Error: .svnignore file missing!");
                return;
            }

            string rules = File.ReadAllText(ignoreFilePath);
            bool success = await SvnRunner.SetSvnGlobalIgnorePropertyAsync(root, rules);

            if (success)
            {
                UpdateStatusInUI("SUCCESS: Global ignores set. Commit the root folder.");
                _ = RefreshIgnoredPanelAsync();
            }
        }

        private void UpdateStatusInUI(string message)
        {
            // Check if IgnoredPanelDisplay exists in SVNUI
            if (svnUI != null && svnUI.IgnoredText != null)
            {
                svnUI.IgnoredText.text = $"<color=#FFFF00>{message}</color>\n" + svnUI.IgnoredText.text;
            }
        }
    }
}