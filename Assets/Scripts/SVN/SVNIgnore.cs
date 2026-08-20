using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNIgnore : SVNBase
    {
        private List<string> _cachedIgnoreRules = new List<string>();
        private readonly object _cacheLock = new object();
        private int _processingFlag;
        private readonly SynchronizationContext _mainThreadContext;
        private static readonly Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>();
        private static readonly object _regexCacheLock = new object();
        private float _lastDeletePropertyClickTime = -10f;

        public SVNIgnore(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        private bool TryEnterProcessing()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        private void PostUI(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => action(), null);
            else
                action();
        }

        private void SafeFireAndForget(Func<Task> operation)
        {
            _ = FireAndForget(operation);
        }

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception ex) { PostUI(() => SVNLogBridge.LogErrorToOutput($"[SVN] Unhandled: {ex.Message}")); }
        }

        private void TriggerStatusRefresh()
        {
            var statusModule = svnManager?.GetModule<SVNStatus>();
            if (statusModule != null)
            {
                statusModule.ShowOnlyModified();
            }
        }

        public void RefreshIgnoredPanel() => SafeFireAndForget(() => RefreshIgnoredPanelAsync());
        public void OpenIgnoredFilesInEditor() => SafeFireAndForget(() => OpenIgnoredFilesInEditorAsync());
        public void PushLocalRulesToSvn() => SafeFireAndForget(() => PushLocalRulesToSvnAsync());
        public void ReloadIgnoreRules() => SafeFireAndForget(() => ReloadIgnoreRulesAsync());
        
        public void DeleteSvnGlobalIgnoreProperty()
        {
            float clickTime = Time.unscaledTime;
            if (!ConfirmAction(clickTime, ref _lastDeletePropertyClickTime,
                "<color=#FFAA00><b>[Delete Property]</b></color> This will remove 'svn:global-ignores' from SVN.\n" +
                "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            SafeFireAndForget(() => DeleteSvnGlobalIgnorePropertyAsync());
        }

        public void OpenIgnoreConfigInEditor() => OpenIgnoreConfigInEditorAction();

        private async Task ReloadIgnoreRulesAsync()
        {
            if (svnManager == null || string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                SVNLogBridge.LogToOutput("<color=#FFAA00>[SVN]</color> Cannot reload: WorkingDir is null or empty.");
                PostUI(() => UpdateStatusInUI("<color=#FFAA00>Error:</color> Working directory is not set."));
                return;
            }

            LoadIgnoreRulesFromFile(svnManager.WorkingDir);
            await RefreshIgnoredPanelAsync();
            TriggerStatusRefresh();
        }

        public bool IsPathIgnoredLocally(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            relativePath = relativePath.Replace("\\", "/").TrimStart('/');
            string name = Path.GetFileName(relativePath);

            List<string> rulesCopy;
            lock (_cacheLock)
            {
                if (_cachedIgnoreRules.Count == 0)
                    return false;

                rulesCopy = new List<string>(_cachedIgnoreRules);
            }

            return IsIgnoredByRules(name, relativePath, rulesCopy);
        }

        public void FilterOutLocallyIgnored<T>(Dictionary<string, T> statusDict)
        {
            if (statusDict == null || statusDict.Count == 0)
                return;

            var toRemove = new List<string>();

            foreach (var key in statusDict.Keys)
            {
                if (IsPathIgnoredLocally(key))
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
                statusDict.Remove(key);
        }

        public void FilterOutLocallyIgnored(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return;

            for (int i = paths.Count - 1; i >= 0; i--)
            {
                if (IsPathIgnoredLocally(paths[i]))
                    paths.RemoveAt(i);
            }
        }

        public async Task<Dictionary<string, (string status, string size)>> GetIgnoredOnlyAsync(string workingDir, CancellationToken token = default)
        {
            workingDir = NormalizePath(workingDir);
            var ignoredDict = new Dictionary<string, (string status, string size)>(StringComparer.OrdinalIgnoreCase);

            string output = await SvnRunner.RunAsync("status --no-ignore", workingDir, false, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(output))
            {
                foreach (var line in output.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length >= 8 && line[0] == 'I')
                    {
                        string rawPath = line.Substring(8).Trim();
                        string cleanPath = SvnRunner.CleanSvnPath(rawPath).Replace("\\", "/");
                        ignoredDict[cleanPath] = ("I", Directory.Exists(Path.Combine(workingDir, cleanPath)) ? "DIR" : "FILE");
                    }
                }
            }

            List<string> activeRules = await GetIgnoreRulesFromSvnAsync(workingDir, token).ConfigureAwait(false);

            lock (_cacheLock)
            {
                foreach (var rule in _cachedIgnoreRules)
                {
                    if (!activeRules.Contains(rule, StringComparer.OrdinalIgnoreCase))
                        activeRules.Add(rule);
                }
            }

            if (activeRules.Count > 0 && Directory.Exists(workingDir))
            {
                string[] allEntries = await Task.Run(() =>
                    Directory.GetFileSystemEntries(workingDir, "*", SearchOption.AllDirectories), token).ConfigureAwait(false);

                foreach (var entry in allEntries)
                {
                    string relPath = entry.Replace(workingDir, "").TrimStart('\\', '/').Replace('\\', '/');
                    if (relPath.Split('/').Any(seg => seg == ".svn") || ignoredDict.ContainsKey(relPath))
                        continue;

                    string name = Path.GetFileName(entry);
                    if (IsIgnoredByRules(name, relPath, activeRules))
                    {
                        ignoredDict[relPath] = ("I", Directory.Exists(entry) ? "DIR" : "FILE");
                    }
                }
            }

            return ignoredDict;
        }

        public async Task RefreshIgnoredPanelAsync(CancellationToken token = default)
        {
            if (!TryEnterProcessing()) return;

            try
            {
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root))
                {
                    PostUI(() => UpdateStatusInUI("Error: Working directory not set!"));
                    return;
                }

                LoadIgnoreRulesFromFile(root);

                string ignoreFilePath = Path.Combine(root, ".svnignore");
                var sb = new StringBuilder(4096);

                int fileRuleCount;
                lock (_cacheLock)
                {
                    fileRuleCount = _cachedIgnoreRules.Count;
                }

                sb.AppendLine($"<color=#00FF99><b>Rules loaded from file: {fileRuleCount}</b></color>\n");

                sb.AppendLine("<color=#444444><b>System Info:</b></color>");
                sb.AppendLine($"<color=#555555>Working Dir:</color> <color=#FFFFFF>{root}</color>");
                sb.AppendLine($"<color=#555555>Config File:</color> <color=#FFFFFF>{ignoreFilePath}</color>");

                bool fileExists = File.Exists(ignoreFilePath);
                string fileStatus = fileExists ? "<color=green>FOUND</color>" : "<color=#FFAA00>NOT FOUND</color>";
                sb.AppendLine($"<color=#555555>File Status:</color> {fileStatus}");
                sb.AppendLine("--------------------------------------------------\n");

                if (!fileExists)
                {
                    sb.AppendLine("<color=#FFCC00><b>[!] ACTION REQUIRED</b></color>");
                    sb.AppendLine("Please ensure <b>.svnignore</b> is located in the folder above to load local rules.");
                    sb.AppendLine("--------------------------------------------------\n");
                }

                List<string> activeRules = await GetIgnoreRulesFromSvnAsync(root, token).ConfigureAwait(false);

                lock (_cacheLock)
                {
                    if (_cachedIgnoreRules != null)
                    {
                        foreach (var fileRule in _cachedIgnoreRules)
                        {
                            if (!activeRules.Contains(fileRule, StringComparer.OrdinalIgnoreCase))
                                activeRules.Add(fileRule);
                        }
                    }
                }

                sb.AppendLine("<color=#FFA500><b>Active Ignore Rules:</b></color>");

                if (activeRules.Count == 0)
                {
                    sb.AppendLine(" <color=#FFAA00>No rules loaded. Click 'Load New Rules' to read .svnignore, or add rules via SVN properties.</color>");
                }
                else
                {
                    foreach (var rule in activeRules)
                    {
                        bool isFromFile;
                        lock (_cacheLock) { isFromFile = _cachedIgnoreRules.Contains(rule); }
                        string color = isFromFile ? "#00FFFF" : "#00FF99";
                        sb.AppendLine($"<color={color}> {(isFromFile ? "[FILE]" : "[SVN]")} {rule}</color>");
                    }
                }

                sb.AppendLine("\n<color=yellow><i>Use 'Open in Editor' to generate and view the full list of ignored files on disk.</i></color>");

                lock (_cacheLock)
                {
                    if (_cachedIgnoreRules.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"<color=#00FF99><b>Successfully loaded {_cachedIgnoreRules.Count} rules from:</b></color>");
                        sb.AppendLine($"<color=#00FF99><b>{ignoreFilePath}</b></color>");
                        sb.AppendLine("<color=yellow><i>These rules are now active for filtering commit list (local only).</i></color>");
                    }
                    else if (!fileExists)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"<color=#FFAA00><b>No rules loaded – .svnignore file missing at:</b></color>");
                        sb.AppendLine($"<color=#FFAA00><b>{ignoreFilePath}</b></color>");
                    }
                }

                string result = sb.ToString();

                PostUI(() =>
                {
                    if (svnUI?.IgnoredText != null)
                        SVNLogBridge.UpdateUIField(svnUI.IgnoredText, result, "IGNORED", append: false);
                });
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task OpenIgnoredFilesInEditorAsync(CancellationToken token = default)
        {
            if (!TryEnterProcessing())
            {
                PostUI(() => UpdateStatusInUI("<color=orange>Please wait... Another operation is currently running.</color>"));
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            CancellationToken linkedToken = cts.Token;

            try
            {
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root))
                {
                    PostUI(() => UpdateStatusInUI("Error: Working directory not set!"));
                    return;
                }

                PostUI(() => UpdateStatusInUI("<color=yellow><b>[DISK SCAN]</b> Scanning all local files against ignore rules...\nThis may take 10-30 seconds for large projects. Please wait.</color>"));

                LoadIgnoreRulesFromFile(root);

                List<string> activeRules = await GetIgnoreRulesFromSvnAsync(root, linkedToken).ConfigureAwait(false);

                lock (_cacheLock)
                {
                    if (_cachedIgnoreRules != null)
                    {
                        foreach (var fileRule in _cachedIgnoreRules)
                        {
                            if (!activeRules.Contains(fileRule, StringComparer.OrdinalIgnoreCase))
                                activeRules.Add(fileRule);
                        }
                    }
                }

                var fullIgnoredList = new List<string>();

                if (activeRules.Count > 0 && Directory.Exists(root))
                {
                    string[] allEntries = await Task.Run(() =>
                        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories), linkedToken).ConfigureAwait(false);

                    foreach (var entry in allEntries)
                    {
                        linkedToken.ThrowIfCancellationRequested();

                        string name = Path.GetFileName(entry);
                        string relPath = entry.Replace(root, "").TrimStart('\\', '/').Replace('\\', '/');

                        if (relPath.Split('/').Any(seg => seg == ".svn"))
                            continue;

                        if (IsIgnoredByRules(name, relPath, activeRules))
                        {
                            fullIgnoredList.Add(relPath);
                        }
                    }
                }

                if (fullIgnoredList.Count == 0)
                {
                    PostUI(() => UpdateStatusInUI("No ignored files found to export."));
                    return;
                }

                OpenFullIgnoredListInEditor(fullIgnoredList);
                PostUI(() => UpdateStatusInUI($"<color=green>Success!</color> Exported {fullIgnoredList.Count} ignored files to text editor."));
            }
            catch (OperationCanceledException)
            {
                PostUI(() => UpdateStatusInUI("<color=yellow>Operation canceled or timed out.</color>"));
            }
            catch (Exception ex)
            {
                PostUI(() => UpdateStatusInUI($"<color=#FFAA00>Error opening editor:</color> {ex.Message}"));
            }
            finally
            {
                ExitProcessing();
            }
        }

        private void OpenFullIgnoredListInEditor(List<string> ignoredFiles)
        {
            string tempFilePath = null;
            try
            {
                CleanupOldTempFiles();

                tempFilePath = Path.Combine(Path.GetTempPath(), $"svn_ignored_list_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                var fileSb = new StringBuilder();
                fileSb.AppendLine($"# SVN Ignored Files Report");
                fileSb.AppendLine($"# Root: {svnManager?.WorkingDir}");
                fileSb.AppendLine($"# Generated: {DateTime.Now:G}");
                fileSb.AppendLine($"# Total ignored items: {ignoredFiles.Count}");
                fileSb.AppendLine(new string('-', 60));

                foreach (var path in ignoredFiles)
                {
                    fileSb.AppendLine(path);
                }

                File.WriteAllText(tempFilePath, fileSb.ToString(), new UTF8Encoding(false));

                string editorPath = svnManager?.MergeToolPath;

                try
                {
                    if (!string.IsNullOrWhiteSpace(editorPath) && File.Exists(editorPath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = editorPath,
                            Arguments = $"\"{tempFilePath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = tempFilePath,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN Ignore] Could not open text editor: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN Ignore] Could not create report: {ex.Message}");
            }
        }

        private static void CleanupOldTempFiles()
        {
            try
            {
                string temp = Path.GetTempPath();
                foreach (var file in Directory.GetFiles(temp, "svn_ignored_list_*.txt"))
                {
                    if (File.Exists(file))
                    {
                        var fi = new FileInfo(file);
                        if (fi.CreationTime < DateTime.Now.AddHours(-24))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public async Task<List<string>> GetIgnoreRulesFromSvnAsync(string workingDir, CancellationToken token = default)
        {
            var rules = new List<string>();
            try
            {
                string globalOutput = await SvnRunner.RunAsync("propget svn:global-ignores -R .", workingDir, false, token).ConfigureAwait(false);
                string standardOutput = await SvnRunner.RunAsync("propget svn:ignore -R .", workingDir, false, token).ConfigureAwait(false);

                string combinedOutput = (globalOutput ?? "") + "\n" + (standardOutput ?? "");

                if (string.IsNullOrWhiteSpace(combinedOutput))
                    return rules;

                if (combinedOutput.TrimStart().StartsWith("svn:", StringComparison.OrdinalIgnoreCase) ||
                    combinedOutput.TrimStart().StartsWith("propget:", StringComparison.OrdinalIgnoreCase))
                {
                    SVNLogBridge.LogToOutput("<color=#FFAA00>[SVN Ignore]</color> SVN returned an error while fetching ignore rules.");
                    return rules;
                }

                foreach (var line in combinedOutput.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries))
                {
                    string pattern = line;
                    int separatorIndex = line.IndexOf(" - ");
                    if (separatorIndex >= 0)
                    {
                        pattern = line.Substring(0, separatorIndex);
                    }

                    string trimmed = pattern.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#") && !rules.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        rules.Add(trimmed);
                    }
                }
            }
            catch (Exception e)
            {
                SVNLogBridge.LogToOutput($"<color=#FFAA00>[SVN Ignore]</color> Error fetching rules: {e.Message}");
            }

            return rules;
        }

        private async Task PushLocalRulesToSvnAsync()
        {
            if (!TryEnterProcessing()) return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            CancellationToken token = cts.Token;

            try
            {
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root))
                {
                    PostUI(() => UpdateStatusInUI("Error: Working directory not set!"));
                    return;
                }

                string ignoreFilePath = Path.Combine(root, ".svnignore");
                if (!File.Exists(ignoreFilePath))
                {
                    PostUI(() => UpdateStatusInUI("Error: .svnignore file missing!"));
                    return;
                }

                string rules = await File.ReadAllTextAsync(ignoreFilePath, token).ConfigureAwait(false);
                bool success = await SetSvnGlobalIgnorePropertyAsync(root, rules, token).ConfigureAwait(false);

                if (success)
                {
                    PostUI(() => UpdateStatusInUI(
                        "<color=#00FF99><b>SUCCESS:</b> Global ignores set. Commit the root folder.</color>\n" +
                        "<color=#FFFF00>You can now remove the <b>.svnignore</b> file if you no longer need it.</color>"));

                    await RefreshIgnoredPanelAsync(token).ConfigureAwait(false);
                    TriggerStatusRefresh();
                }
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task DeleteSvnGlobalIgnorePropertyAsync()
        {
            if (!TryEnterProcessing()) return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken token = cts.Token;

            try
            {
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root))
                {
                    PostUI(() => UpdateStatusInUI("<color=#FFAA00>Error:</color> Working directory not set!"));
                    return;
                }

                bool success = await RunPropDelAsync("svn:global-ignores", root, token).ConfigureAwait(false);

                if (success)
                {
                    PostUI(() => UpdateStatusInUI(
                        "<color=#00FF99><b>SUCCESS:</b> svn:global-ignores deleted locally.\n" +
                        "<color=#FFFF00>Don't forget to COMMIT this change to propagate it to the team!</color>"));

                    await RefreshIgnoredPanelAsync(token).ConfigureAwait(false);
                    TriggerStatusRefresh();
                }
                else
                {
                    PostUI(() => UpdateStatusInUI("<color=#FFAA00>Error:</color> Failed to delete property. It might not exist or SVN returned an error."));
                }
            }
            finally
            {
                ExitProcessing();
            }
        }

        public static async Task<bool> SetSvnGlobalIgnorePropertyAsync(string workingDir, string rulesRawText, CancellationToken token = default)
        {
            string tempFilePath = Path.Combine(workingDir, "temp_global_ignore.txt");
            try
            {
                await File.WriteAllTextAsync(tempFilePath, rulesRawText.Replace("\r\n", "\n"), token).ConfigureAwait(false);
                string result = await SvnRunner.RunAsync($"propset svn:global-ignores -F \"{tempFilePath}\" .", workingDir, false, token).ConfigureAwait(false);
                return !result.StartsWith("ERROR");
            }
            finally
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
            }
        }

        private static async Task<bool> RunPropDelAsync(string propertyName, string workingDir, CancellationToken token = default)
        {
            string result = await SvnRunner.RunAsync($"propdel {propertyName} .", workingDir, false, token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(result) || !result.TrimStart().StartsWith("svn:", StringComparison.OrdinalIgnoreCase);
        }

        public void LoadIgnoreRulesFromFile(string workingDir)
        {
            lock (_cacheLock)
            {
                _cachedIgnoreRules.Clear();
                string ignoreFilePath = Path.Combine(workingDir, ".svnignore");

                if (File.Exists(ignoreFilePath))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(ignoreFilePath))
                        {
                            string trimmed = line.Trim();
                            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#") && !_cachedIgnoreRules.Contains(trimmed))
                            {
                                _cachedIgnoreRules.Add(trimmed);
                            }
                        }

                        int count = _cachedIgnoreRules.Count;
                        SVNLogBridge.LogToOutput($"<color=#00FFFF>[SVN]</color> Loaded {count} rules from: {ignoreFilePath}");
                    }
                    catch (Exception e)
                    {
                        SVNLogBridge.LogToOutput($"<color=#FFAA00>[SVN]</color> File read error: {e.Message}");
                        PostUI(() => UpdateStatusInUI($"<color=#FFAA00>Error:</color> Could not read .svnignore file."));
                    }
                }
                else
                {
                    SVNLogBridge.LogToOutput($"<color=#FFAA00>[SVN]</color> .svnignore file not found at: {ignoreFilePath}");
                    PostUI(() => UpdateStatusInUI("<color=#FFAA00>Not found:</color> .svnignore file does not exist."));
                }
            }
        }

        private void OpenIgnoreConfigInEditorAction()
        {
            string root = svnManager?.WorkingDir;
            if (string.IsNullOrWhiteSpace(root))
            {
                PostUI(() => UpdateStatusInUI("<color=#FFAA00>Error:</color> Working directory not set!"));
                return;
            }

            string filePath = Path.Combine(root, ".svnignore");

            if (!File.Exists(filePath))
            {
                try
                {
                    File.Create(filePath).Dispose();
                    SVNLogBridge.LogToOutput("<color=#00FFFF>[SVN]</color> Created new .svnignore file.");
                }
                catch (Exception ex)
                {
                    PostUI(() => UpdateStatusInUI($"<color=#FFAA00>Error:</color> Could not create .svnignore file: {ex.Message}"));
                    return;
                }
            }

            try
            {
                string editorPath = svnManager?.MergeToolPath;

                if (!string.IsNullOrWhiteSpace(editorPath) && File.Exists(editorPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editorPath,
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN Ignore] Could not open text editor: {ex.Message}");
            }
        }

        private bool ConfirmAction(float currentTime, ref float lastClickTime, string warningMessage)
        {
            const float ConfirmationWindow = 5f;
            const float MinDoubleClickDelay = 0.30f;

            float elapsed = currentTime - lastClickTime;

            if (elapsed > ConfirmationWindow || lastClickTime < 0f)
            {
                lastClickTime = currentTime;
                PostUI(() => UpdateStatusInUI(warningMessage));
                return false;
            }

            if (elapsed < MinDoubleClickDelay)
            {
                lastClickTime = currentTime;
                PostUI(() => UpdateStatusInUI("<color=#FFAA00><b>[Ignore]</b></color> Confirmation too fast — press once again."));
                return false;
            }

            lastClickTime = -10f;
            return true;
        }

        private static bool IsIgnoredByRules(string name, string relPath, List<string> rules)
        {
            if (string.IsNullOrEmpty(relPath) || rules == null || rules.Count == 0)
                return false;

            relPath = relPath.Replace("\\", "/").Trim('/');
            name = Path.GetFileName(relPath);

            bool finalDecision = false;

            foreach (var rawRule in rules)
            {
                string rule = rawRule.Trim();
                if (string.IsNullOrEmpty(rule) || rule.StartsWith("#"))
                    continue;

                bool isNegation = rule.StartsWith("!");
                if (isNegation)
                    rule = rule.Substring(1).Trim();

                bool isDirectoryOnly = rule.EndsWith("/");
                if (isDirectoryOnly)
                    rule = rule.TrimEnd('/');

                bool mustBeRoot = rule.StartsWith("/");
                if (mustBeRoot)
                    rule = rule.TrimStart('/');

                if (string.IsNullOrEmpty(rule))
                    continue;

                bool matches = false;

                if (!mustBeRoot && !rule.Contains("/"))
                {
                    var segments = relPath.Split('/');
                    if (segments.Any(seg => seg.Equals(rule, StringComparison.OrdinalIgnoreCase)))
                        matches = true;
                }

                if (!matches && mustBeRoot)
                {
                    if (relPath.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                        relPath.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                    }
                }

                if (!matches && rule.Contains("*") && !rule.Contains("/") && !mustBeRoot)
                {
                    if (IsMatch(name, rule))
                        matches = true;
                }

                if (!matches && rule.Contains("*"))
                {
                    if (IsMatch(relPath, rule))
                        matches = true;
                }
                else if (!matches && rule.Contains("/"))
                {
                    if (relPath.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                        relPath.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                    }
                }

                if (matches)
                {
                    finalDecision = !isNegation;
                }
            }

            return finalDecision;
        }

        private static bool IsMatch(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            Regex regex;
            lock (_regexCacheLock)
            {
                if (!_regexCache.TryGetValue(pattern, out regex))
                {
                    string regexPattern = Regex.Escape(pattern);
                    regexPattern = regexPattern.Replace("\\*\\*", "§DOUBLESTAR§");
                    regexPattern = regexPattern.Replace("\\*", "[^/]*");
                    regexPattern = regexPattern.Replace("\\?", "[^/]");
                    regexPattern = regexPattern.Replace("§DOUBLESTAR§", ".*");

                    regexPattern = "^" + regexPattern + "$";
                    regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    _regexCache[pattern] = regex;
                }
            }
            return regex.IsMatch(text);
        }

        private void UpdateStatusInUI(string message)
        {
            if (svnUI?.IgnoredText != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.IgnoredText, $"<color=#FFFF00>{message}</color>\n", "IGNORED", append: true);
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace("\\", "/").TrimEnd('/');
        }

        private static readonly char[] NewLineChars = new[] { '\n', '\r' };
    }
}