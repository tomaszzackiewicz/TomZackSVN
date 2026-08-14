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

        public void RefreshIgnoredPanel() => SafeFireAndForget(() => RefreshIgnoredPanelAsync());
        public void OpenIgnoredFilesInEditor() => SafeFireAndForget(() => OpenIgnoredFilesInEditorAsync());
        public void ReloadIgnoreRules()
        {
            if (svnManager != null && !string.IsNullOrEmpty(svnManager.WorkingDir))
                LoadIgnoreRulesFromFile(svnManager.WorkingDir);
            else
                SVNLogBridge.LogErrorToOutput("[SVN] Cannot reload: WorkingDir is null or empty.");
        }

        public void PushLocalRulesToSvn() => SafeFireAndForget(() => PushLocalRulesToSvnAsync());

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
                    if (relPath.Contains(".svn") || ignoredDict.ContainsKey(relPath)) continue;

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

                string ignoreFilePath = Path.Combine(root, ".svnignore");
                var sb = new StringBuilder(4096);

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
                    sb.AppendLine("  <color=#FF4444>No rules loaded. Click 'Reload' if you just added the file.</color>");
                }
                else
                {
                    foreach (var rule in activeRules)
                    {
                        bool isFromFile;
                        lock (_cacheLock) { isFromFile = _cachedIgnoreRules.Contains(rule); }
                        string color = isFromFile ? "#00FFFF" : "#00FF99";
                        sb.AppendLine($"<color={color}>  {(isFromFile ? "[FILE]" : "[SVN]")} {rule}</color>");
                    }
                }

                sb.AppendLine("\n<color=yellow><i>Use 'Open in Editor' to generate and view the full list of ignored files on disk.</i></color>");

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

            try
            {
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root))
                {
                    PostUI(() => UpdateStatusInUI("Error: Working directory not set!"));
                    return;
                }

                PostUI(() => UpdateStatusInUI("<color=yellow><b>[DISK SCAN]</b> Scanning all local files against ignore rules...\nThis may take 10-30 seconds for large projects. Please wait.</color>"));

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

                var fullIgnoredList = new List<string>();

                if (activeRules.Count > 0 && Directory.Exists(root))
                {
                    string[] allEntries = await Task.Run(() =>
                        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories), token).ConfigureAwait(false);

                    foreach (var entry in allEntries)
                    {
                        string name = Path.GetFileName(entry);
                        string relPath = entry.Replace(root, "").TrimStart('\\', '/').Replace('\\', '/');
                        if (relPath.Contains(".svn")) continue;

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
            catch (Exception ex)
            {
                PostUI(() => UpdateStatusInUI($"<color=red>Error opening editor:</color> {ex.Message}"));
            }
            finally
            {
                ExitProcessing();
            }
        }

        private void OpenFullIgnoredListInEditor(List<string> ignoredFiles)
        {
            try
            {
                string tempFilePath = Path.Combine(Path.GetTempPath(), $"svn_ignored_list_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
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

        public async Task<List<string>> GetIgnoreRulesFromSvnAsync(string workingDir, CancellationToken token = default)
        {
            var rules = new List<string>();
            try
            {
                string globalOutput = await SvnRunner.RunAsync("propget svn:global-ignores -R .", workingDir, false, token).ConfigureAwait(false);
                string standardOutput = await SvnRunner.RunAsync("propget svn:ignore -R .", workingDir, false, token).ConfigureAwait(false);

                string combinedOutput = (globalOutput ?? "") + "\n" + (standardOutput ?? "");

                if (string.IsNullOrWhiteSpace(combinedOutput) || combinedOutput.Contains("ERROR"))
                    return rules;

                foreach (var line in combinedOutput.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries))
                {
                    string pattern = line;
                    int separatorIndex = line.IndexOf(" - ");
                    if (separatorIndex >= 0)
                    {
                        pattern = line.Substring(separatorIndex + 3);
                    }

                    string trimmed = pattern.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.Contains(" ") && !rules.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        rules.Add(trimmed);
                    }
                }
            }
            catch (Exception e) { SVNLogBridge.LogError(e.Message); }
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
                    PostUI(() => UpdateStatusInUI("SUCCESS: Global ignores set. Commit the root folder."));
                    await RefreshIgnoredPanelAsync(token).ConfigureAwait(false);
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
                        SVNLogBridge.LogToOutput($"<color=#00FFFF>[SVN]</color> Loaded {_cachedIgnoreRules.Count} rules from .svnignore");
                    }
                    catch (Exception e) { SVNLogBridge.LogErrorToOutput($"[SVN] File read error: {e.Message}"); }
                }
                else
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN] .svnignore file not found at: {workingDir}");
                }
            }
        }

        private static bool IsIgnoredByRules(string name, string relPath, List<string> rules)
        {
            foreach (var rule in rules)
            {
                if (name.Equals(rule, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (relPath.Split('/').Any(part => part.Equals(rule, StringComparison.OrdinalIgnoreCase)))
                    return true;

                if (rule.Contains("*") && IsMatch(name, rule))
                    return true;
            }
            return false;
        }

        private static bool IsMatch(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern == "*") return true;

            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase);
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