using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        private bool _isCurrentViewIgnored = false;
        private string _lastKnownProjectName = "";
        private string _svnVersionCached = "";

        private List<string> _cachedIgnoreRules = new List<string>();

        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void ShowOnlyModified()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = "Refreshing...";
            if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = "Refreshing...";

            _isCurrentViewIgnored = false;
            _ = ExecuteRefreshWithAutoExpand();
        }

        public void ShowOnlyIgnored()
        {
            _isCurrentViewIgnored = true;
            _ = ExecuteRefreshWithAutoExpand();
        }

        public async Task ExecuteRefreshWithAutoExpand()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            string root = svnManager.WorkingDir;
            string timestamp = $"[{DateTime.Now:HH:mm:ss}]";

            Action<string> LogBoth = (msg) =>
            {
                if (svnUI.LogText != null) svnUI.LogText.text += msg;
                if (svnUI.CommitConsoleContent != null) svnUI.CommitConsoleContent.text += msg;
            };

            if (svnUI.LogText != null) svnUI.LogText.text = $"{timestamp} <color=#0FF>Starting Refresh...</color>\n";
            if (svnUI.CommitConsoleContent != null) svnUI.CommitConsoleContent.text = $"{timestamp} <color=#0FF>Starting Refresh...</color>\n";

            try
            {
                LogBoth("Checking SVN status...\n");
                var statusDict = _isCurrentViewIgnored
                    ? await SvnRunner.GetIgnoredOnlyAsync(root)
                    : await SvnRunner.GetChangesDictionaryAsync(root);

                // --- POPRAWKA: Obsługa braku zmian ---
                if (statusDict == null || statusDict.Count == 0)
                {
                    string emptyMsg = _isCurrentViewIgnored
                        ? "<i>No ignored files found.</i>"
                        : "<i>No changes detected. (Everything up to date)</i>";

                    if (svnUI.TreeDisplay != null) svnUI.TreeDisplay.text = emptyMsg;
                    if (svnUI.CommitTreeDisplay != null) svnUI.CommitTreeDisplay.text = emptyMsg;
                    if (svnUI.CommitSizeText != null) svnUI.CommitSizeText.text = "Total Size: 0 KB";

                    // Zerujemy statystyki (ikony/liczniki na dole)
                    svnManager.UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);

                    LogBoth("<color=yellow>Nothing to display.</color>\n");
                    return; // Kończymy wcześniej
                }
                // -------------------------------------

                LogBoth($"Found {statusDict.Count} entries. Processing paths...\n");

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

                LogBoth("Calculating commit size...\n");
                string report = await SvnRunner.GetCommitSizeReportAsync(root);
                if (svnUI.CommitSizeText != null)
                    svnUI.CommitSizeText.text = $"<color=yellow>Total Size of Changes to Commit: {report}</color>\n";

                LogBoth("Building visual tree...\n");
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(root, svnManager.ExpandedPaths, _isCurrentViewIgnored);

                // Jeśli mimo wszystko tree przyszło puste z buildera:
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

        public void RefreshLocal()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            ShowProjectInfo(null, svnManager.WorkingDir);

            _ = ExecuteRefreshWithAutoExpand();
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
                Debug.LogWarning($"[SVN] .svnignore file not found at: {workingDir}");
            }
        }

        public async Task RefreshIgnoredPanelAsync()
        {
            string root = svnManager.WorkingDir;
            string ignoreFilePath = Path.Combine(root, ".svnignore");

            StringBuilder sb = new StringBuilder();

            // Zmiana kolorów na ciemniejsze (#444444 i #555555) dla lepszej widoczności
            sb.AppendLine("<color=#444444><b>System Info:</b></color>");
            sb.AppendLine($"<color=#555555>Working Dir:</color> <color=#FFFFFF>{root}</color>");
            sb.AppendLine($"<color=#555555>Config File:</color> <color=#FFFFFF>{ignoreFilePath}</color>");

            bool fileExists = File.Exists(ignoreFilePath);
            string fileStatus = fileExists ? "<color=green>FOUND</color>" : "<color=red>NOT FOUND</color>";
            sb.AppendLine($"<color=#555555>File Status:</color> {fileStatus}");
            sb.AppendLine("--------------------------------------------------\n");

            if (!fileExists)
            {
                sb.AppendLine("<color=#FFCC00><b>[!] ACTION REQUIRED</b></color>");
                sb.AppendLine($"Please ensure <b>.svnignore</b> is located in the folder above to load local rules.");
                sb.AppendLine("--------------------------------------------------\n");
            }

            List<string> activeRules = await SvnRunner.GetIgnoreRulesFromSvnAsync(root);

            if (_cachedIgnoreRules != null)
            {
                foreach (var fileRule in _cachedIgnoreRules)
                {
                    if (!activeRules.Contains(fileRule)) activeRules.Add(fileRule);
                }
            }

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
                        // Zmiana koloru znacznika [I]
                        sb.AppendLine($"<color=#555555>[I]</color> <color=#FFFFFF>{relPath}</color>");
                        count++;
                        if (count > 200) { sb.AppendLine("<color=#FFFF00>... truncated</color>"); break; }
                    }
                }
            }

            if (count == 0 && activeRules.Count > 0)
                sb.AppendLine("<color=green>No files match the active rules.</color>");

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

        public async void ShowProjectInfo(SVNProject svnProject, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (svnUI == null || svnUI.StatusInfoText == null) return;

            // 1. Priorytetyzacja nazwy: SVNProject > Bufor > Folder na dysku
            if (svnProject != null && !string.IsNullOrEmpty(svnProject.projectName))
            {
                _lastKnownProjectName = svnProject.projectName;
            }

            string displayName = !string.IsNullOrEmpty(_lastKnownProjectName)
                ? _lastKnownProjectName
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // Feedback wizualny (ciemniejszy szary)
            svnUI.StatusInfoText.text = $"<size=120%><color=#FFFF00>●</color></size> <color=#555555>Initializing {displayName}...</color>";

            string sizeText = "---";
            string rawInfo = "";
            int retryCount = 0;
            int maxRetries = 5;

            while (retryCount < maxRetries)
            {
                try
                {
                    var infoTask = SvnRunner.GetInfoAsync(path);
                    var sizeTask = svnManager.GetFolderSizeAsync(path);
                    await Task.WhenAll(infoTask, sizeTask);

                    rawInfo = infoTask.Result;
                    sizeText = sizeTask.Result;

                    if (!string.IsNullOrEmpty(rawInfo) && rawInfo != "unknown") break;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("SSH Key"))
                    {
                        retryCount++;
                        svnUI.StatusInfoText.text = $"<size=120%><color=#FFFF00>●</color></size> <color=#555555>Waiting for SSH keys... ({retryCount}/{maxRetries})</color>";
                        await Task.Delay(500);
                        continue;
                    }
                    break;
                }
            }

            if (string.IsNullOrEmpty(rawInfo))
            {
                svnUI.StatusInfoText.text = $"<size=120%><color=#FF5555>●</color></size> <b>{displayName}</b> | <color=#FF8888>Connection Error</color>";
                return;
            }

            // Ekstrakcja danych
            string revision = ExtractValue(rawInfo, "Revision:");
            string author = ExtractValue(rawInfo, "Last Changed Author:");
            string fullDate = ExtractValue(rawInfo, "Last Changed Date:");
            string relUrl = ExtractValue(rawInfo, "Relative URL:");
            string absUrl = ExtractValue(rawInfo, "URL:");
            string repoRootUrl = ExtractValue(rawInfo, "Repository Root:");

            // 2. Jeśli nazwa to nadal nazwa folderu, spróbuj wyciągnąć nazwę z Root URL repozytorium
            if (string.IsNullOrEmpty(_lastKnownProjectName) || _lastKnownProjectName == displayName)
            {
                if (repoRootUrl != "unknown")
                {
                    _lastKnownProjectName = repoRootUrl.Split('/').Last(s => !string.IsNullOrEmpty(s));
                    displayName = _lastKnownProjectName;
                }
            }

            // Obsługa brancha
            string branchName = "trunk";
            string source = (relUrl != "unknown") ? relUrl : absUrl;
            if (source != "unknown")
            {
                branchName = source.Replace("^/", "").Trim();
                if (branchName.Contains("/"))
                    branchName = Path.GetFileName(branchName.TrimEnd('/'));
                if (string.IsNullOrEmpty(branchName) || branchName == "/") branchName = "trunk";
            }

            string serverHost = "local";
            if (absUrl != "unknown")
            {
                try { serverHost = new Uri(absUrl).Host; } catch { }
            }

            string shortDate = (fullDate != "unknown") ? fullDate.Split('(')[0].Trim() : "no commits";
            string appVersion = Application.version;
            if (string.IsNullOrEmpty(_svnVersionCached)) await EnsureVersionCached();

            svnUI.StatusInfoText.text =
        $"<size=120%><color=#55FF55>●</color></size> <b>{displayName}</b> <color=#E6E6E6>({sizeText})</color> | " +
        $"<color=#00E5FF>Branch:</color> {branchName} | " +
        $"<color=orange>Rev: {revision}</color> | " +
        $"<color=#81BEF7>By: {author}</color> | " +
        $"<color=#AAAAAA>{shortDate}</color> | " +
        $"<color=#888888>Srv: {serverHost}</color> | " +
        $"<color=#666666>App: {appVersion}</color> | " +
        $"<color=#666666>SVN:{_svnVersionCached}</color>";
        }

        private async Task EnsureVersionCached()
        {
            if (string.IsNullOrEmpty(_svnVersionCached))
            {
                try
                {
                    _svnVersionCached = await SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir);
                    _svnVersionCached = _svnVersionCached.Trim();
                }
                catch
                {
                    _svnVersionCached = "?.?.?";
                }
            }
        }

        private string ExtractValue(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return "N/A";

            using (var reader = new System.IO.StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith(key))
                    {
                        return line.Replace(key, "").Trim();
                    }
                }
            }
            return "unknown";
        }
    }
}