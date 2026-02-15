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

            if (svnUI.TreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Refreshing...", "TREE", append: false);
            }

            if (svnUI.CommitTreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing...", "COMMIT_TREE", append: false);
            }

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

            Action<string, bool> LogSmart = (msg, isHeader) =>
            {
                SVNLogBridge.LogLine(msg, append: !isHeader);
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, msg, "COMMIT_CONSOLE", append: true);
            };

            LogSmart($"{timestamp} <color=#0FF>Refreshing status...</color>\n", true);

            try
            {
                var statusDict = _isCurrentViewIgnored
                    ? await GetIgnoredOnlyAsync(root)
                    : await GetChangesDictionaryAsync(root);

                UpdateFilesStatus(statusDict);

                if (statusDict == null || statusDict.Count == 0)
                {
                    string emptyMsg = _isCurrentViewIgnored
                        ? "<i>No ignored files found.</i>"
                        : "<i>No changes detected. (Everything up to date)</i>";

                    if (svnUI.TreeDisplay != null)
                    {
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, emptyMsg, "TREE", append: false);
                    }

                    if (svnUI.CommitTreeDisplay != null)
                    {
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, emptyMsg, "COMMIT_TREE", append: false);
                    }

                    if (svnUI.CommitSizeText != null)
                    {
                        SVNLogBridge.UpdateUIField(svnUI.CommitSizeText, "Total Size: 0 KB", "STATS", append: false);
                    }

                    UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);
                    LogSmart("<color=green>Workspace is clean.</color>\n", false);
                    return;
                }

                LogSmart($"Found {statusDict.Count} entries. Processing...\n", false);

                svnManager.ExpandedPaths.Clear();
                svnManager.ExpandedPaths.Add("");

                foreach (var item in statusDict)
                {
                    string path = item.Key;
                    string stat = item.Value.status;
                    bool isChange = !string.IsNullOrEmpty(stat) && "MA?!DCR~".Contains(stat);
                    bool shouldExpand = _isCurrentViewIgnored ? (stat == "I") : isChange;

                    if (shouldExpand)
                    {
                        AddParentFoldersToExpanded(path);
                    }
                }

                string report = await SvnRunner.GetCommitSizeReportAsync(root);
                if (svnUI.CommitSizeText != null)
                {
                    string sizeMsg = $"<color=yellow>Total Size of Changes: {report}</color>";
                    SVNLogBridge.UpdateUIField(svnUI.CommitSizeText, sizeMsg, "STATS", append: false);
                }

                var result = await GetVisualTreeWithStatsAsync(root, svnManager.ExpandedPaths, _isCurrentViewIgnored);
                string treeResult = string.IsNullOrEmpty(result.tree) ? "<i>No changes detected.</i>" : result.tree;

                if (svnUI.TreeDisplay != null)
                {
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, treeResult, "TREE", append: false);
                }

                if (svnUI.CommitTreeDisplay != null)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, treeResult, "COMMIT_TREE", append: false);
                }

                UpdateAllStatisticsUI(result.stats, _isCurrentViewIgnored);
                LogSmart("<color=green>Refresh finished.</color>\n", false);
            }
            catch (Exception ex)
            {
                string errorMsg = $"<color=red>Refresh Error:</color> {ex.Message}\n";
                LogSmart(errorMsg, false);
                Debug.LogError($"[SVN] {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void UpdateFilesStatus(Dictionary<string, (string status, string size)> newStatus)
        {
            if (newStatus == null) return;
            svnManager.CurrentStatusDict = newStatus;
        }

        public void UpdateAllStatisticsUI(SvnStats stats, bool isIgnoredView)
        {
            if (svnUI == null) return;

            if (svnUI.StatsText != null)
            {
                string statsContent = isIgnoredView
                    ? $"<color=#444444><b>VIEW: IGNORED</b></color> | Folders: {stats.IgnoredFolderCount} | Files: {stats.IgnoredFileCount} | Total Ignored: <color=#FFFFFF>{stats.IgnoredCount}</color>"
                    : $"Folders: {stats.FolderCount} | Files: {stats.FileCount} | <color=#FFD700>Mod (M): {stats.ModifiedCount}</color> | <color=#00FF00>Add (A): {stats.AddedCount}</color> | <color=#00E5FF>New (?): {stats.NewFilesCount}</color> | <color=#FF4444>Del (D/!): {stats.DeletedCount}</color> | <color=#FF00FF>Conf (C): {stats.ConflictsCount}</color>";

                SVNLogBridge.UpdateUIField(svnUI.StatsText, statsContent, "STATS", append: false);
            }

            if (svnUI.CommitStatsText != null)
            {
                if (isIgnoredView)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText, "<color=#FFCC00>Switch to 'Modified' view to see commit details.</color>", "STATS", append: false);
                }
                else
                {
                    int totalToCommit = stats.ModifiedCount + stats.AddedCount + stats.NewFilesCount + stats.DeletedCount;
                    string conflictPart = stats.ConflictsCount > 0 ? $" | <color=#FF0000><b> CONFLICTS (C): {stats.ConflictsCount} (Resolve first!)</b></color>" : "";
                    string commitStats = $"<b>Pending Changes:</b> <color=#FFD700>M: {stats.ModifiedCount}</color> | <color=#00FF00>A: {stats.AddedCount}</color> | <color=#00E5FF>?: {stats.NewFilesCount}</color> | <color=#FF4444>D/!: {stats.DeletedCount}</color> | <color=#FFFFFF><b>Total: {totalToCommit}</b></color>{conflictPart}";

                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText, commitStats, "STATS", append: false);
                }
            }
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetChangesDictionaryAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status", workingDir);
            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 8) continue;
                char rawCode = line[0];
                string stat = rawCode.ToString().ToUpper();
                if ("MA?!DC".Contains(stat))
                {
                    string rawPath = line.Substring(8).Trim();
                    string cleanPath = SvnRunner.CleanSvnPath(rawPath);
                    string fullPath = Path.Combine(workingDir, cleanPath);
                    statusDict[cleanPath] = (stat, SvnRunner.GetFileSizeSafe(fullPath));
                }
            }
            return statusDict;
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetIgnoredOnlyAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status --no-ignore", workingDir);
            var ignoredDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return ignoredDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 8) continue;
                if (line[0] == 'I')
                {
                    string rawPath = line.Substring(8).Trim();
                    string cleanPath = SvnRunner.CleanSvnPath(rawPath);
                    ignoredDict[cleanPath] = ("I", "<IGNORED>");
                }
            }
            return ignoredDict;
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
            if (svnUI.TreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes found.</i>", "TREE", append: false);
            }

            if (svnUI.CommitTreeDisplay != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, string.Empty, "COMMIT_TREE", append: false);
            }

            if (svnUI.CommitSizeText != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.CommitSizeText, "<color=yellow>Total Size: 0 B</color>", "STATS", append: false);
            }
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
                LoadIgnoreRulesFromFile(svnManager.WorkingDir);
            else
                Debug.LogError("[SVN] Cannot reload: WorkingDir is null or empty.");
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

            List<string> activeRules = await GetIgnoreRulesFromSvnAsync(root);

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
                    sb.AppendLine($"<color={color}>  {(isFromFile ? "[FILE]" : "[SVN]")} {rule}</color>");
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
                        sb.AppendLine($"<color=#555555>[I]</color> <color=#FFFFFF>{relPath}</color>");
                        count++;
                        if (count > 200) { sb.AppendLine("<color=#FFFF00>... truncated</color>"); break; }
                    }
                }
            }

            if (count == 0 && activeRules.Count > 0)
                sb.AppendLine("<color=green>No files match the active rules.</color>");

            if (svnUI != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.IgnoredText, sb.ToString(), "IGNORED", append: false);
            }
        }

        public static async Task<List<string>> GetIgnoreRulesFromSvnAsync(string workingDir)
        {
            List<string> rules = new List<string>();
            try
            {
                string globalOutput = await SvnRunner.RunAsync("propget svn:global-ignores -R .", workingDir);
                string standardOutput = await SvnRunner.RunAsync("propget svn:ignore -R .", workingDir);
                string combinedOutput = globalOutput + "\n" + standardOutput;

                if (string.IsNullOrEmpty(combinedOutput) || combinedOutput.Contains("ERROR"))
                    return rules;

                string[] lines = combinedOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string pattern = line;
                    if (line.Contains(" - "))
                    {
                        var parts = line.Split(new[] { " - " }, StringSplitOptions.None);
                        pattern = parts.Length > 1 ? parts[1] : parts[0];
                    }

                    string trimmed = pattern.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.Contains(" ") && !rules.Contains(trimmed))
                    {
                        rules.Add(trimmed);
                    }
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogError(e.Message); }
            return rules;
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
            bool success = await SetSvnGlobalIgnorePropertyAsync(root, rules);

            if (success)
            {
                UpdateStatusInUI("SUCCESS: Global ignores set. Commit the root folder.");
                _ = RefreshIgnoredPanelAsync();
            }
        }

        public static async Task<bool> SetSvnGlobalIgnorePropertyAsync(string workingDir, string rulesRawText)
        {
            string tempFilePath = Path.Combine(workingDir, "temp_global_ignore.txt");
            File.WriteAllText(tempFilePath, rulesRawText.Replace("\r\n", "\n"));

            string result = await SvnRunner.RunAsync($"propset svn:global-ignores -F \"{tempFilePath}\" .", workingDir);

            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);

            if (result.StartsWith("ERROR"))
            {
                UnityEngine.Debug.LogError(result);
                return false;
            }
            return true;
        }

        private void UpdateStatusInUI(string message)
        {
            if (svnUI != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.IgnoredText, $"<color=#FFFF00>{message}</color>\n", "IGNORED", append: true);
            }
        }

        public async void ShowProjectInfo(SVNProject svnProject, string path, bool forceOutdatedCheck = false, bool isRefreshing = false)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (svnUI == null) return;

            if (svnProject != null && !string.IsNullOrEmpty(svnProject.projectName))
                _lastKnownProjectName = svnProject.projectName;

            string displayName = !string.IsNullOrEmpty(_lastKnownProjectName)
                ? _lastKnownProjectName
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            string initialColor = isRefreshing ? "#FFFF00" : "#FFFF00"; // Yellow dot
            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color={initialColor}>●</color></size> <color=#555555>Initializing {displayName}...</color>", "INFO", append: false);

            string sizeText = "---";
            string rawInfo = "";
            int retryCount = 0;
            int maxRetries = 8;

            while (retryCount < maxRetries)
            {
                try
                {
                    if (!Directory.Exists(Path.Combine(path, ".svn")))
                    {
                        retryCount++;
                        SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color=#FFFF00>●</color></size> <color=#555555>Waiting for .svn metadata... ({retryCount}/{maxRetries})</color>"); //Yellow dot
                        await Task.Delay(1000);
                        continue;
                    }

                    var infoTask = SvnRunner.GetInfoAsync(path);
                    var sizeTask = GetFolderSizeAsync(path);
                    await Task.WhenAll(infoTask, sizeTask);

                    rawInfo = infoTask.Result;
                    sizeText = sizeTask.Result;

                    if (!string.IsNullOrEmpty(rawInfo) && rawInfo != "unknown") break;
                }
                catch (Exception)
                {
                    retryCount++;
                    await Task.Delay(1000);
                }
            }

            if (string.IsNullOrEmpty(rawInfo) || rawInfo == "unknown")
            {
                SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color= #000000>●</color></size> <b>{displayName}</b> | <color=#FF8888>Not a working copy yet</color>", "INFO", append: false); //Black dot
                return;
            }

            string revision = ExtractValue(rawInfo, "Revision:");
            string author = ExtractValue(rawInfo, "Last Changed Author:");
            string fullDate = ExtractValue(rawInfo, "Last Changed Date:");
            string relUrl = ExtractValue(rawInfo, "Relative URL:");
            string absUrl = ExtractValue(rawInfo, "URL:");
            string repoRootUrl = ExtractValue(rawInfo, "Repository Root:");

            bool isOutdated = false;
            string remoteRevision = revision;

            try
            {
                string remoteRevRaw = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", path);
                if (!string.IsNullOrEmpty(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                {
                    remoteRevision = remoteRevRaw.Trim();
                    if (int.TryParse(revision, out int localRev) && int.TryParse(remoteRevision, out int remRev))
                    {
                        isOutdated = remRev > localRev;
                    }
                }
            }
            catch { }

            string statusColor = "#4ca74c"; // Green dot
            if (isRefreshing) statusColor = "#FFFF00"; // Yellow dot
            else if (isOutdated) statusColor = "#FF1A1A"; // red dot

            if (string.IsNullOrEmpty(_lastKnownProjectName) || _lastKnownProjectName == displayName)
            {
                if (repoRootUrl != "unknown")
                {
                    _lastKnownProjectName = repoRootUrl.Split('/').Last(s => !string.IsNullOrEmpty(s));
                    displayName = _lastKnownProjectName;
                }
            }

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

            string currentUser = svnManager.CurrentUserName ?? "Unknown";

            string revDisplay = isOutdated
                ? $"<color=#FF5555>{revision}</color> <color=#FF8888>(HEAD: {remoteRevision})</color>"
                : revision;

            string statusLine = $"<size=150%><color={statusColor}>●</color></size> <color=orange> <b>{displayName}</b> ({sizeText})</color> | " +
                                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{currentUser}</color> | " +
                                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{branchName}</color> | " +
                                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{revDisplay}</color> | " +
                                $"<color=#00E5FF>By:</color> <color=#E6E6E6>{author}</color> | " +
                                $"<color=#E6E6E6> {shortDate}</color> | " +
                                $"<color=#E6E6E6>Srv: {serverHost}</color> | " +
                                $"<color=#E6E6E6>App: {appVersion}</color> | " +
                                $"<color=#E6E6E6>SVN: {_svnVersionCached}</color>";

            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, statusLine, "INFO", append: false);
        }

        public async Task<string> GetFolderSizeAsync(string path)
        {
            return await Task.Run(() =>
            {
                try
                {
                    DirectoryInfo dir = new DirectoryInfo(path);
                    if (!dir.Exists) return "0 GB";
                    long bytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                    double gigabytes = (double)bytes / (1024 * 1024 * 1024);
                    return $"{gigabytes:F2} GB";
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Size calculation error: {ex.Message}");
                    return "Size unknown";
                }
            });
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
                catch { _svnVersionCached = "?.?.?"; }
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
                    if (line.StartsWith(key)) return line.Replace(key, "").Trim();
                }
            }
            return "unknown";
        }

        public static async Task<(string tree, SvnStats stats)> GetVisualTreeWithStatsAsync(string workingDir, HashSet<string> expandedPaths, bool showIgnored = false)
        {
            Dictionary<string, (string status, string size)> statusDict = await SvnRunner.GetFullStatusDictionaryAsync(workingDir, true);
            var sb = new StringBuilder();
            var stats = new SvnStats();

            if (!Directory.Exists(workingDir)) return ("Path error.", stats);

            HashSet<string> foldersWithRelevantContent = new HashSet<string>();
            foreach (var item in statusDict)
            {
                string stat = item.Value.status;
                bool isInteresting = showIgnored ? (stat == "I") : (!string.IsNullOrEmpty(stat) && stat != "I");
                if (isInteresting)
                {
                    string[] parts = item.Key.Split('/');
                    string currentFolder = "";
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        currentFolder = string.IsNullOrEmpty(currentFolder) ? parts[i] : $"{currentFolder}/{parts[i]}";
                        foldersWithRelevantContent.Add(currentFolder);
                    }
                }
            }

            SvnRunner.BuildTreeString(workingDir, workingDir, 0, statusDict, sb, stats, expandedPaths, new bool[128], showIgnored, foldersWithRelevantContent);
            return (sb.ToString(), stats);
        }

        public async Task<int> GetRemoteRevisionInternal()
        {
            string output = await SvnRunner.RunAsync("info --show-item last-changed-revision", svnManager.WorkingDir);
            if (int.TryParse(output.Trim(), out int rev))
            {
                return rev;
            }
            return -1;
        }
    }
}