using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNIgnore : SVNBase
    {
        private List<string> _cachedIgnoreRules = new List<string>();

        public SVNIgnore(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async Task<Dictionary<string, (string status, string size)>> GetIgnoredOnlyAsync(string workingDir)
        {
            workingDir = workingDir.Replace("\\", "/").TrimEnd('/');
            var ignoredDict = new Dictionary<string, (string status, string size)>();

            string output = await SvnRunner.RunAsync("status --no-ignore", workingDir);
            if (!string.IsNullOrEmpty(output))
            {
                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Length >= 8 && line[0] == 'I')
                    {
                        string rawPath = line.Substring(8).Trim();
                        string cleanPath = SvnRunner.CleanSvnPath(rawPath).Replace("\\", "/");
                        ignoredDict[cleanPath] = ("I", Directory.Exists(Path.Combine(workingDir, cleanPath)) ? "DIR" : "FILE");
                    }
                }
            }

            List<string> activeRules = await GetIgnoreRulesFromSvnAsync(workingDir);
            foreach (var rule in _cachedIgnoreRules) if (!activeRules.Contains(rule)) activeRules.Add(rule);

            if (activeRules.Count > 0)
            {
                string[] allEntries = Directory.GetFileSystemEntries(workingDir, "*", SearchOption.AllDirectories);
                foreach (var entry in allEntries)
                {
                    string relPath = entry.Replace(workingDir, "").TrimStart('\\', '/').Replace('\\', '/');
                    if (relPath.Contains(".svn") || ignoredDict.ContainsKey(relPath)) continue;

                    string name = Path.GetFileName(entry);
                    bool isIgnored = activeRules.Any(rule =>
                        name.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                        relPath.Split('/').Any(part => part.Equals(rule, StringComparison.OrdinalIgnoreCase)) ||
                        (rule.Contains("*") && IsMatch(name, rule))
                    );

                    if (isIgnored)
                    {
                        ignoredDict[relPath] = ("I", Directory.Exists(entry) ? "DIR" : "FILE");
                    }
                }
            }

            return ignoredDict;
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

        public async Task<List<string>> GetIgnoreRulesFromSvnAsync(string workingDir)
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
            catch (Exception e) { Debug.LogError(e.Message); }
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
    }
}