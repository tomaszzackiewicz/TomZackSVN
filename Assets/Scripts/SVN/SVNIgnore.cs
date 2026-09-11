using System;
using System.Collections.Generic;
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
        private static readonly Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>();
        private static readonly object _regexCacheLock = new object();
        private float _lastDeletePropertyClickTime = -10f;

        private static readonly char[] NewLineChars = new[] { '\n', '\r' };

        public SVNIgnore(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
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
            UnityMainThreadDispatcher.Enqueue(action);
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
                $"<color={SVNColors.Warning}><b>[Delete Property]</b></color> This will remove 'svn:global-ignores' from SVN.\n" +
                "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            SafeFireAndForget(() => DeleteSvnGlobalIgnorePropertyAsync());
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
                    PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error:</color> Working directory not set!"));
                    return;
                }

                bool success = await RunPropDelAsync("svn:global-ignores", root, token).ConfigureAwait(false);

                if (success)
                {
                    PostUI(() => UpdateStatusInUI(
                        $"<color={SVNColors.Success}><b>SUCCESS:</b> svn:global-ignores deleted locally.\n" +
                        $"<color={SVNColors.Warning}>Don't forget to COMMIT this change to propagate it to the team!</color>"));

                    await RefreshIgnoredPanelCoreAsync(token).ConfigureAwait(false);
                    TriggerStatusRefresh();
                }
                else
                {
                    PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error:</color> Failed to delete property. It might not exist or SVN returned an error."));
                }
            }
            catch (OperationCanceledException)
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Delete cancelled / timed out.</color>"));
            }
            catch (Exception ex)
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error deleting property:</color> {ex.Message}"));
            }
            finally
            {
                ExitProcessing();
            }
        }

        public void OpenIgnoreConfigInEditor() => OpenIgnoreConfigInEditorAction();

        private async Task ReloadIgnoreRulesAsync()
        {
            if (svnManager == null || string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                SVNLogBridge.LogToOutput($"<color={SVNColors.Warning}>[SVN]</color> Cannot reload: WorkingDir is null or empty.");
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error:</color> Working directory is not set."));
                return;
            }

            LoadIgnoreRulesFromFile(svnManager.WorkingDir);
            await RefreshIgnoredPanelCoreAsync().ConfigureAwait(false);
            TriggerStatusRefresh();
        }

        // === FIX (perf): publiczne dla pojedynczych zapytań — kopia reguł per call.
        // Do FILTROWANIA CAŁEJ LISTY używaj FilterOutLocallyIgnored (jedna kopia).
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

        // =====================================================================
        //  === FIX (perf): JEDNA kopia reguł dla CAŁEJ listy plików.
        //
        //  Wcześniej SVNStatus.ExecuteRefreshWithAutoExpand wołał
        //  FilterOutLocallyIgnored(statusDict) który iterował po plikach i dla
        //  KAŻDEGO wołał IsPathIgnoredLocally → new List<string>(_cachedIgnoreRules).
        //  Dla WC z 5000 zmodyfikowanych plików × 10 reguł = 50 000 kopii List.
        //
        //  Teraz: snapshot reguł RAZ, potem IsIgnoredByRules bez kopii.
        // =====================================================================
        public void FilterOutLocallyIgnored<T>(Dictionary<string, T> statusDict)
        {
            if (statusDict == null || statusDict.Count == 0)
                return;

            List<string> rulesSnapshot;
            lock (_cacheLock)
            {
                if (_cachedIgnoreRules.Count == 0)
                    return;

                rulesSnapshot = new List<string>(_cachedIgnoreRules);
            }

            var toRemove = new List<string>();

            foreach (var key in statusDict.Keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;

                string rel = key.Replace("\\", "/").TrimStart('/');
                string name = Path.GetFileName(rel);

                if (IsIgnoredByRules(name, rel, rulesSnapshot))
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
                statusDict.Remove(key);
        }

        public void FilterOutLocallyIgnored(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return;

            List<string> rulesSnapshot;
            lock (_cacheLock)
            {
                if (_cachedIgnoreRules.Count == 0)
                    return;

                rulesSnapshot = new List<string>(_cachedIgnoreRules);
            }

            for (int i = paths.Count - 1; i >= 0; i--)
            {
                string path = paths[i];
                if (string.IsNullOrWhiteSpace(path)) continue;

                string rel = path.Replace("\\", "/").TrimStart('/');
                string name = Path.GetFileName(rel);

                if (IsIgnoredByRules(name, rel, rulesSnapshot))
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
                    token.ThrowIfCancellationRequested();

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
                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true
                };

                string[] allEntries = await Task.Run(() =>
                    Directory.GetFileSystemEntries(workingDir, "*", enumOptions), token).ConfigureAwait(false);

                foreach (var entry in allEntries)
                {
                    token.ThrowIfCancellationRequested();

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
                await RefreshIgnoredPanelCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task RefreshIgnoredPanelCoreAsync(CancellationToken token = default)
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

            sb.AppendLine($"<color={SVNColors.Success}><b>Rules loaded from file: {fileRuleCount}</b></color>\n");

            sb.AppendLine("<color=#444444><b>System Info:</b></color>");
            sb.AppendLine($"<color={SVNColors.Dim}>Working Dir:</color> <color={SVNColors.StatusDefault}>{root}</color>");
            sb.AppendLine($"<color={SVNColors.Dim}>Config File:</color> <color={SVNColors.StatusDefault}>{ignoreFilePath}</color>");

            bool fileExists = File.Exists(ignoreFilePath);
            string fileStatus = fileExists ? $"<color={SVNColors.Success}>FOUND</color>" : $"<color={SVNColors.Warning}>NOT FOUND</color>";
            sb.AppendLine($"<color={SVNColors.Dim}>File Status:</color> {fileStatus}");
            sb.AppendLine("--------------------------------------------------\n");

            if (!fileExists)
            {
                sb.AppendLine($"<color={SVNColors.Warning}><b>[!] ACTION REQUIRED</b></color>");
                sb.AppendLine("Please ensure <b>.svnignore</b> is located in the folder above to load local rules.");
                sb.AppendLine("--------------------------------------------------\n");
            }

            List<string> activeRules = await GetIgnoreRulesFromSvnAsync(root, token).ConfigureAwait(false);

            lock (_cacheLock)
            {
                foreach (var fileRule in _cachedIgnoreRules)
                {
                    if (!activeRules.Contains(fileRule, StringComparer.OrdinalIgnoreCase))
                        activeRules.Add(fileRule);
                }
            }

            sb.AppendLine($"<color={SVNColors.StatusReplaced}><b>Active Ignore Rules:</b></color>");

            if (activeRules.Count == 0)
            {
                sb.AppendLine($" <color={SVNColors.Warning}>No rules loaded. Click 'Load New Rules' to read .svnignore, or add rules via SVN properties.</color>");
            }
            else
            {
                foreach (var rule in activeRules)
                {
                    bool isFromFile;
                    lock (_cacheLock) { isFromFile = _cachedIgnoreRules.Contains(rule); }
                    string color = isFromFile ? SVNColors.Header : SVNColors.Success;
                    sb.AppendLine($"<color={color}> {(isFromFile ? "[FILE]" : "[SVN]")} {rule}</color>");
                }
            }

            sb.AppendLine($"\n<color={SVNColors.Warning}><i>Use 'Open in Editor' to generate and view the full list of ignored files on disk.</i></color>");

            lock (_cacheLock)
            {
                if (_cachedIgnoreRules.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"<color={SVNColors.Success}><b>Successfully loaded {_cachedIgnoreRules.Count} rules from:</b></color>");
                    sb.AppendLine($"<color={SVNColors.Success}><b>{ignoreFilePath}</b></color>");
                    sb.AppendLine($"<color={SVNColors.Warning}><i>These rules are now active for filtering commit list (local only).</i></color>");
                }
                else if (!fileExists)
                {
                    sb.AppendLine();
                    sb.AppendLine($"<color={SVNColors.Warning}><b>No rules loaded – .svnignore file missing at:</b></color>");
                    sb.AppendLine($"<color={SVNColors.Warning}><b>{ignoreFilePath}</b></color>");
                }
            }

            string trailingInfo = await BuildPushBreadcrumbAsync(root, token).ConfigureAwait(false);
            sb.AppendLine();
            sb.AppendLine(trailingInfo);

            string result = sb.ToString();

            PostUI(() =>
            {
                if (svnUI?.IgnoredText != null)
                    SVNLogBridge.UpdateUIField(svnUI.IgnoredText, result, "IGNORED", append: false);
            });
        }

        private async Task<string> BuildPushBreadcrumbAsync(string root, CancellationToken token)
        {
            try
            {
                string status = await SvnRunner.RunAsync("status", root, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(status)) return "";

                foreach (var rawLine in status.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.TrimEnd('\r');

                    if (line.Length < 9) continue;

                    string statusCols = line.Substring(0, 8);
                    string path = line.Substring(8).Trim();

                    bool propertyChanged = statusCols[1] == 'M' && statusCols[0] == ' ';
                    if (propertyChanged && (path == "." || path.Length == 0))
                    {
                        return $"<color={SVNColors.Warning}><b>[ACTION REQUIRED]</b> Ignore rules pushed but NOT committed.\n" +
                               "Commit the project ROOT folder to share them with the team.</color>";
                    }
                }
            }
            catch { }

            return $"<color={SVNColors.Warning}><i>Tip: edit .svnignore → 'Apply' (push). Rules work locally; commit root after push to share.</i></color>";
        }

        private async Task OpenIgnoredFilesInEditorAsync(CancellationToken token = default)
        {
            if (!TryEnterProcessing())
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Error}>Please wait... Another operation is currently running.</color>"));
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

                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}><b>[DISK SCAN]</b> Scanning all local files against ignore rules...\nThis may take 10-30 seconds for large projects. Please wait.</color>"));

                LoadIgnoreRulesFromFile(root);

                List<string> activeRules = await GetIgnoreRulesFromSvnAsync(root, linkedToken).ConfigureAwait(false);

                lock (_cacheLock)
                {
                    foreach (var fileRule in _cachedIgnoreRules)
                    {
                        if (!activeRules.Contains(fileRule, StringComparer.OrdinalIgnoreCase))
                            activeRules.Add(fileRule);
                    }
                }

                var fullIgnoredList = new List<string>();

                if (activeRules.Count > 0 && Directory.Exists(root))
                {
                    var enumOptions = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true
                    };

                    string[] allEntries = await Task.Run(() =>
                        Directory.GetFileSystemEntries(root, "*", enumOptions), linkedToken).ConfigureAwait(false);

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
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Success}>Success!</color> Exported {fullIgnoredList.Count} ignored files to text editor."));
            }
            catch (OperationCanceledException)
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Operation canceled or timed out.</color>"));
            }
            catch (Exception ex)
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error opening editor:</color> {ex.Message}"));
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
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = editorPath,
                            Arguments = $"\"{tempFilePath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    else
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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
                string xmlOutput = await SvnRunner.RunAsync(
                    "propget svn:global-ignores -R --xml .", workingDir, false, token).ConfigureAwait(false);
                string xmlOutput2 = await SvnRunner.RunAsync(
                    "propget svn:ignore -R --xml .", workingDir, false, token).ConfigureAwait(false);

                CollectRulesFromPropgetXml(xmlOutput, rules);
                CollectRulesFromPropgetXml(xmlOutput2, rules);
            }
            catch (Exception e)
            {
                SVNLogBridge.LogToOutput($"<color={SVNColors.Warning}>[SVN Ignore]</color> Error fetching rules: {e.Message}");
            }

            return rules;
        }

        private static void CollectRulesFromPropgetXml(string xml, List<string> rules)
        {
            if (string.IsNullOrWhiteSpace(xml)) return;

            try
            {
                using var sr = new StringReader(xml);
                using var reader = System.Xml.XmlReader.Create(sr, new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    IgnoreWhitespace = true
                });

                while (reader.Read())
                {
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "property")
                    {
                        string data = reader.ReadElementContentAsString();
                        if (string.IsNullOrWhiteSpace(data)) continue;

                        foreach (var rawLine in data.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string trimmed = rawLine.Trim();
                            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#") &&
                                !rules.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                            {
                                rules.Add(trimmed);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color={SVNColors.Warning}>[SVN Ignore]</color> XML parse fallback skipped: {ex.Message}");
            }
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

                var oldRules = await GetIgnoreRulesFromSvnAsync(root, token).ConfigureAwait(false);

                await SetSvnGlobalIgnorePropertyAsync(root, rules, token).ConfigureAwait(false);

                var newRules = rules.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var removed = oldRules
                    .Where(r => !newRules.Contains(r, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var added = newRules
                    .Where(r => !oldRules.Contains(r, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"<color={SVNColors.Success}><b>SUCCESS:</b> svn:global-ignores replaced on project root.</color>\n");

                if (removed.Count > 0)
                {
                    sb.AppendLine($"<color={SVNColors.Warning}><b>Rules REMOVED for the whole team ({removed.Count}):</b></color>");
                    foreach (var r in removed)
                        sb.AppendLine($" <color={SVNColors.StatusObstructed}>- {r}</color>");
                    sb.AppendLine();
                }
                if (added.Count > 0)
                {
                    sb.AppendLine($"<color={SVNColors.Header}><b>Rules ADDED ({added.Count}):</b></color>");
                    foreach (var r in added)
                        sb.AppendLine($" <color={SVNColors.Success}>+ {r}</color>");
                    sb.AppendLine();
                }
                if (removed.Count == 0 && added.Count == 0)
                    sb.AppendLine($"<color={SVNColors.Dim}>No changes — rules identical to the repository.</color>");

                sb.AppendLine($"<color={SVNColors.Warning}><b>NEXT STEP:</b> Commit the project ROOT folder (it now shows 'M' — property change).\n" +
                              "After that, the team runs Update and gets the new rules.</color>");

                PostUI(() => UpdateStatusInUI(sb.ToString()));

                await RefreshIgnoredPanelCoreAsync(token).ConfigureAwait(false);
                TriggerStatusRefresh();
            }
            catch (OperationCanceledException)
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Push cancelled / timed out.</color>"));
            }
            catch (Exception ex)
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error pushing rules:</color> {ex.Message}"));
            }
            finally
            {
                ExitProcessing();
            }
        }

        public static async Task SetSvnGlobalIgnorePropertyAsync(string workingDir, string rulesRawText, CancellationToken token = default)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"svn_global_ignore_{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(tempFilePath, rulesRawText.Replace("\r\n", "\n"), token).ConfigureAwait(false);
                await SvnRunner.RunAsync($"propset svn:global-ignores -F \"{tempFilePath}\" .", workingDir, false, token).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            }
        }

        private static async Task<bool> RunPropDelAsync(string propertyName, string workingDir, CancellationToken token = default)
        {
            try
            {
                await SvnRunner.RunAsync($"propdel {propertyName} .", workingDir, false, token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color={SVNColors.Warning}>[SVN Ignore]</color> propdel failed: {ex.Message}");
                return false;
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

                        int count = _cachedIgnoreRules.Count;
                        SVNLogBridge.LogToOutput($"<color={SVNColors.Header}>[SVN]</color> Loaded {count} rules from: {ignoreFilePath}");
                    }
                    catch (Exception e)
                    {
                        SVNLogBridge.LogToOutput($"<color={SVNColors.Warning}>[SVN]</color> File read error: {e.Message}");
                        PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error:</color> Could not read .svnignore file."));
                    }
                }
                else
                {
                    SVNLogBridge.LogToOutput($"<color={SVNColors.Warning}>[SVN]</color> .svnignore file not found at: {ignoreFilePath}");
                    PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Not found:</color> .svnignore file does not exist."));
                }
            }
        }

        private void OpenIgnoreConfigInEditorAction()
        {
            string root = svnManager?.WorkingDir;
            if (string.IsNullOrWhiteSpace(root))
            {
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error:</color> Working directory not set!"));
                return;
            }

            string filePath = Path.Combine(root, ".svnignore");

            if (!File.Exists(filePath))
            {
                try
                {
                    File.Create(filePath).Dispose();
                    SVNLogBridge.LogToOutput($"<color={SVNColors.Header}>[SVN]</color> Created new .svnignore file.");
                }
                catch (Exception ex)
                {
                    PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}>Error:</color> Could not create .svnignore file: {ex.Message}"));
                    return;
                }
            }

            try
            {
                string editorPath = svnManager?.MergeToolPath;

                if (!string.IsNullOrWhiteSpace(editorPath) && File.Exists(editorPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = editorPath,
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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
                PostUI(() => UpdateStatusInUI($"<color={SVNColors.Warning}><b>[Ignore]</b></color> Confirmation too fast — press once again."));
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
                SVNLogBridge.UpdateUIField(svnUI.IgnoredText, $"<color={SVNColors.Warning}>{message}</color>\n", "IGNORED", append: true);
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace("\\", "/").TrimEnd('/');
        }
    }
}