using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SVN.Core
{
    public class SVNIgnore : SVNBase
    {
        private List<string> _cachedIgnoreRules = new List<string>();
        private readonly object _cacheLock = new object();
        private int _processingFlag;
        private readonly SynchronizationContext _mainThreadContext;

        private static readonly Regex IgnoreRuleRegex = new Regex(@"^[^\s]+$", RegexOptions.Compiled);
        private static readonly Regex WildcardRegex = new Regex(@"[\*\?]", RegexOptions.Compiled);

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
            _ = Task.Run(async () =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (Exception ex) { PostUI(() => SVNLogBridge.LogError($"[SVN] Unhandled: {ex.Message}")); }
            });
        }

        public void RefreshIgnoredPanel()
        {
            SafeFireAndForget(RefreshIgnoredPanelAsync);
        }

        public void ReloadIgnoreRules()
        {
            if (svnManager != null && !string.IsNullOrEmpty(svnManager.WorkingDir))
                LoadIgnoreRulesFromFile(svnManager.WorkingDir);
            else
                SVNLogBridge.LogError("[SVN] Cannot reload: WorkingDir is null or empty.");
        }

        public void PushLocalRulesToSvn()
        {
            SafeFireAndForget(PushLocalRulesToSvnAsync);
        }

        public async Task<Dictionary<string, (string status, string size)>> GetIgnoredOnlyAsync(string workingDir)
        {
            workingDir = NormalizePath(workingDir);
            var ignoredDict = new Dictionary<string, (string status, string size)>(StringComparer.OrdinalIgnoreCase);

            string output = await SvnRunner.RunAsync("status --no-ignore", workingDir, false, CancellationToken.None).ConfigureAwait(false);
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

            List<string> activeRules = await GetIgnoreRulesFromSvnAsync(workingDir).ConfigureAwait(false);
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
                string[] allEntries = Directory.GetFileSystemEntries(workingDir, "*", SearchOption.AllDirectories);
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

        public async Task RefreshIgnoredPanelAsync()
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

                List<string> activeRules = await GetIgnoreRulesFromSvnAsync(root).ConfigureAwait(false);

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

                        if (IsIgnoredByRules(name, relPath, activeRules))
                        {
                            sb.AppendLine($"<color=#555555>[I]</color> <color=#FFFFFF>{relPath}</color>");
                            count++;
                            if (count > 200) { sb.AppendLine("<color=#FFFF00>... truncated</color>"); break; }
                        }
                    }
                }

                if (count == 0 && activeRules.Count > 0)
                    sb.AppendLine("<color=green>No files match the active rules.</color>");

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

        public async Task<List<string>> GetIgnoreRulesFromSvnAsync(string workingDir)
        {
            var rules = new List<string>();
            try
            {
                string globalOutput = await SvnRunner.RunAsync("propget svn:global-ignores -R .", workingDir, false, CancellationToken.None).ConfigureAwait(false);
                string standardOutput = await SvnRunner.RunAsync("propget svn:ignore -R .", workingDir, false, CancellationToken.None).ConfigureAwait(false);

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

                string rules = await File.ReadAllTextAsync(ignoreFilePath).ConfigureAwait(false);
                bool success = await SetSvnGlobalIgnorePropertyAsync(root, rules).ConfigureAwait(false);

                if (success)
                {
                    PostUI(() => UpdateStatusInUI("SUCCESS: Global ignores set. Commit the root folder."));
                    await RefreshIgnoredPanelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                ExitProcessing();
            }
        }

        public static async Task<bool> SetSvnGlobalIgnorePropertyAsync(string workingDir, string rulesRawText)
        {
            string tempFilePath = Path.Combine(workingDir, "temp_global_ignore.txt");
            try
            {
                await File.WriteAllTextAsync(tempFilePath, rulesRawText.Replace("\r\n", "\n")).ConfigureAwait(false);

                string result = await SvnRunner.RunAsync($"propset svn:global-ignores -F \"{tempFilePath}\" .", workingDir, false, CancellationToken.None).ConfigureAwait(false);

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
                        SVNLogBridge.LogLine($"<color=#00FFFF>[SVN]</color> Loaded {_cachedIgnoreRules.Count} rules from .svnignore");
                    }
                    catch (Exception e) { SVNLogBridge.LogError($"[SVN] File read error: {e.Message}"); }
                }
                else
                {
                    SVNLogBridge.LogError($"[SVN] .svnignore file not found at: {workingDir}");
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