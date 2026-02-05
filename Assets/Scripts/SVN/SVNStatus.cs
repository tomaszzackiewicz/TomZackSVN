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
            svnUI.LogText.text += $"[{DateTime.Now:HH:mm:ss}] <color=#0FF>Refreshing...</color>\n";

            try
            {
                // Pobieramy s³ownik (upewnij siê, ¿e SvnRunner.GetFullStatusDictionaryAsync obs³uguje status '!')
                // Wybór dedykowanej metody zale¿nie od trybu widoku
                var statusDict = _isCurrentViewIgnored
                    ? await SvnRunner.GetIgnoredOnlyAsync(root)
                    : await SvnRunner.GetChangesDictionaryAsync(root);

                long totalBytes = 0;
                string normalizedRoot = root.Trim().Replace("\\", "/");
                if (!normalizedRoot.EndsWith("/")) normalizedRoot += "/";

                svnManager.ExpandedPaths.Clear();
                svnManager.ExpandedPaths.Add(""); // Root zawsze rozwiniêty

                foreach (var item in statusDict)
                {
                    string path = item.Key;
                    string stat = item.Value.status;

                    // --- POPRAWKA 1: Rozwijanie folderów dla usuniêtych plików ---
                    // Dodajemy "!" (Missing) oraz "D" (Deleted) do warunku rozwijania drzewa
                    bool isChange = !string.IsNullOrEmpty(stat) && (stat == "M" || stat == "A" || stat == "?" || stat == "C" || stat == "!" || stat == "D");

                    bool shouldExpand = _isCurrentViewIgnored ? (stat == "I") : isChange;

                    if (shouldExpand)
                        AddParentFoldersToExpanded(path);

                    // --- POPRAWKA 2: Bezpieczne liczenie rozmiaru ---
                    // Liczymy rozmiar tylko dla plików, które FIZYCZNIE s¹ na dysku (M, A, ?, C)
                    // Pomijamy "!" i "D", bo File.Exists zwróci false
                    if (!_isCurrentViewIgnored && !string.IsNullOrEmpty(stat) && "MA?C".Contains(stat))
                    {
                        string fullPath = System.IO.Path.GetFullPath(normalizedRoot + path);
                        if (System.IO.File.Exists(fullPath))
                        {
                            totalBytes += new System.IO.FileInfo(fullPath).Length;
                        }
                    }
                }

                string sizeStr = FormatBytes(totalBytes);
                svnUI.CommitSizeText.text = $"<color=yellow>Total Size of Changes to Commit: {sizeStr}</color>\n";

                // 3. Pobranie wizualnego drzewa (wynik tej metody zale¿y od Twojego BuildTreeString)
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(root, svnManager.ExpandedPaths, _isCurrentViewIgnored);

                svnUI.TreeDisplay.text = result.tree;
                svnUI.CommitTreeDisplay.text = result.tree;

                // Wa¿ne: Przekazujemy stats do UI
                svnManager.UpdateAllStatisticsUI(result.stats, _isCurrentViewIgnored);
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Error:</color> {ex.Message}\n";
                Debug.LogError($"[SVN] Refresh Error: {ex}");
            }
            finally { IsProcessing = false; }
        }

        private string FormatBytes(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
            double dblSByte = bytes;
            int i = 0;
            while (dblSByte >= 1024 && i < Suffix.Length - 1)
            {
                i++;
                dblSByte /= 1024;
            }
            return $"{dblSByte:F2} {Suffix[i]}";
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
            RefreshLocal();
        }

        public async void RefreshLocal()
        {
            // 1. Check if busy
            if (IsProcessing)
            {
                Debug.Log("[SVN] Refresh skipped: System is busy.");
                return;
            }

            IsProcessing = true;

            // 2. Immediate UI Feedback (The "Blink")
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            svnUI.LogText.text += $"[{timestamp}] <color=#0FF>Manual Refresh triggered...</color>\n";

            // Temporarily change text so the user knows the app is working
            svnUI.TreeDisplay.text = "<color=orange>Fetching local status...</color>";

            try
            {
                // 3. Run the visual update
                // Note: This uses the CURRENTly expanded paths. 
                // If you want it to find NEW changes automatically, 
                // call ExecuteRefreshWithAutoExpand() here instead!
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(
                    svnManager.WorkingDir,
                    svnManager.ExpandedPaths,
                    _isCurrentViewIgnored
                );

                // 4. Update Displays
                if (string.IsNullOrEmpty(result.tree))
                {
                    svnUI.TreeDisplay.text = "<i>No changes found in current view.</i>";
                }
                else
                {
                    svnUI.TreeDisplay.text = result.tree;
                }

                svnUI.CommitTreeDisplay.text = svnUI.TreeDisplay.text;
                svnManager.UpdateAllStatisticsUI(result.stats, false);

                svnUI.LogText.text += "<color=green>UI Updated.</color>\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Refresh Error:</color> {ex.Message}\n";
                Debug.LogError($"[SVN] Refresh Error: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void RefreshView()
        {
            ExecuteRefreshWithAutoExpand();
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

        // Pomocnicza metoda do wyœwietlania komunikatów w UI zamiast dialogów
        private void UpdateStatusText(string msg)
        {
            if (svnUI.IgnoredText != null)
                svnUI.IgnoredText.text = $"<color=#FFFF00>{msg}</color>\n" + svnUI.IgnoredText.text;
            Debug.Log(msg);
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