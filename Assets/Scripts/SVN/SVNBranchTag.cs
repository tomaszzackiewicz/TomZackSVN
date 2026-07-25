using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase, IDisposable
    {
        private float _lastDeleteBranchClickTime = -10f;
        private float _lastDeleteTagClickTime = -10f;
        private string _cachedRepoRoot;

        private CancellationTokenSource _refreshCts;

        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            manager.OnProjectChanged += OnProjectChangedHandler;
        }

        public void Dispose()
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;

            if (svnManager != null)
                svnManager.OnProjectChanged -= OnProjectChangedHandler;
        }

        private void OnProjectChangedHandler(SVNProject project)
        {
            _cachedRepoRoot = null; // wyczyść cache starego projektu
            _ = RefreshOnProjectLoadedAsync();
        }

        private async Task RefreshOnProjectLoadedAsync()
        {
            try
            {
                await Task.Delay(100);
                await RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                LogWarning($"[BranchTag] Auto-refresh failed: {ex.Message}");
            }
        }

        private async Task<string[]> GetRepoListAsync(string url, CancellationToken token = default)
        {
            try
            {
                string currentKey = SvnRunner.KeyPath;
                string sshArgs = "-o BatchMode=yes -o StrictHostKeyChecking=no";
                if (!string.IsNullOrEmpty(currentKey))
                    sshArgs = $"-i \"{currentKey}\" {sshArgs}";

                string command = $"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" list \"{url}\" --non-interactive";
                string output = await SvnRunner.RunAsync(command, svnManager.WorkingDir, false, token);

                if (string.IsNullOrWhiteSpace(output))
                    return Array.Empty<string>();

                return output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimEnd('/'))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => !x.StartsWith("*"))
                    .Where(x => x.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) < 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch { return Array.Empty<string>(); }
        }

        private string EnsureRepoRoot()
        {
            if (!string.IsNullOrWhiteSpace(_cachedRepoRoot)) return _cachedRepoRoot;
            if (svnManager == null || string.IsNullOrWhiteSpace(svnManager.WorkingDir))
                return null;

            try
            {
                _cachedRepoRoot = svnManager.GetRepoRoot()?.Trim().TrimEnd('/');
            }
            catch (Exception ex)
            {
                LogWarning($"[SVNBranchTag] GetRepoRoot failed: {ex.Message}");
            }
            return _cachedRepoRoot;
        }

        public async Task RefreshIfEmpty()
        {
            if (!IsReady()) return;
            if (svnUI?.BranchesDropdown == null) return;

            var options = svnUI.BranchesDropdown.options;
            if (options.Count == 0 || options.All(o => IsPlaceholder(o.text)))
            {
                await RefreshUnifiedList();
            }
        }

        private bool IsReady()
        {
            if (svnManager == null) return false;
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir)) return false;
            if (!Directory.Exists(svnManager.WorkingDir)) return false;
            if (string.IsNullOrWhiteSpace(SvnRunner.KeyPath)) return false;
            return true;
        }

        public async Task CreateBranchFromTrunk()
        {
            if (!TryStart()) return;
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                if (!ValidateCreateInputs(out string name, out string subFolder)) return;

                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot)) return;

                string revision = svnUI.RevisionInput?.text?.Trim();
                bool hasRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

                string sourceUrl = $"{repoRoot}/trunk";
                string targetUrl = $"{repoRoot}/{subFolder}/{EscapeSvnPath(name)}";

                if (hasRevision)
                {
                    LogInfo($"[Create @ rev] trunk@{revision} → {subFolder}/{name}");
                    string cmd = $"copy \"{sourceUrl}@{revision}\" \"{targetUrl}\" -m \"Created {subFolder}/{name} from trunk@{revision}\" --parents";
                    await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, CancellationToken.None);
                    LogSuccess($"Created: {name} from trunk at revision {revision}");
                }
                else
                {
                    LogInfo($"[Create] Copying from TRUNK → {subFolder}");
                    string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{name}\" --parents";
                    await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, CancellationToken.None);
                    LogSuccess($"Created: {name}");
                }
                await RefreshUnifiedList();
            }
            catch (Exception ex) { LogErrorLocal($"[Create Error] {ex.Message}"); }
            finally { End(); }
        }

        public async Task CreateBranchFromSelected()
        {
            if (!TryStart()) return;
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                if (!ValidateCreateInputs(out string newName, out string subFolder)) return;

                TMP_Dropdown sourceDropdown = subFolder == "branches" ? svnUI?.BranchesDropdown : svnUI?.TagsDropdown;
                if (sourceDropdown == null || sourceDropdown.options.Count == 0)
                {
                    LogErrorLocal($"[Error] No {subFolder} available.");
                    return;
                }
                if (sourceDropdown.value < 0 || sourceDropdown.value >= sourceDropdown.options.Count)
                {
                    LogErrorLocal($"[Error] Invalid {subFolder} selection.");
                    return;
                }

                string sourceName = sourceDropdown.options[sourceDropdown.value].text;
                if (IsPlaceholder(sourceName) || string.IsNullOrEmpty(sourceName))
                {
                    LogErrorLocal($"[Error] Invalid source {subFolder}.");
                    return;
                }

                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot)) return;

                string sourceUrl = sourceName.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{EscapeSvnPath(sourceName)}";

                string revision = svnUI.RevisionInput?.text?.Trim();
                bool hasRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

                if (hasRevision)
                {
                    sourceUrl = $"{sourceUrl}@{revision}";
                    LogInfo($"[Create @ rev] {sourceName}@{revision} → {subFolder}/{newName}");
                }
                else
                {
                    LogInfo($"[Create] {sourceName} → {subFolder}/{newName}");
                }

                string targetUrl = $"{repoRoot}/{subFolder}/{EscapeSvnPath(newName)}";
                string message = hasRevision
                    ? $"Created {subFolder}/{newName} from {sourceName}@{revision}"
                    : $"Created {subFolder}/{newName} from {sourceName}";

                string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"{message}\" --parents";
                await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, CancellationToken.None);

                LogSuccess(hasRevision
                    ? $"Created {newName} from {sourceName} at revision {revision}"
                    : $"Created {newName} from {sourceName}");

                await RefreshUnifiedList();
            }
            catch (Exception ex) { LogErrorLocal($"[Create Error] {ex.Message}"); }
            finally { End(); }
        }

        public async Task RefreshUnifiedList()
        {
            if (svnUI?.BranchesDropdown == null && svnUI?.TagsDropdown == null)
                return;

            if (!IsReady())
            {
                UpdateDropdown(svnUI.BranchesDropdown, Array.Empty<string>(), "Loading...", true);
                UpdateDropdown(svnUI.TagsDropdown, Array.Empty<string>(), "Loading...", false);
                return;
            }

            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
            var token = _refreshCts.Token;

            try
            {
                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogWarning("[Refresh] Repo root not available.");
                    UpdateDropdown(svnUI.BranchesDropdown, Array.Empty<string>(), "Not ready", true);
                    UpdateDropdown(svnUI.TagsDropdown, Array.Empty<string>(), "Not ready", false);
                    return;
                }

                LogInfo("[Refresh] Syncing lists with server...");

                string branchesUrl = $"{repoRoot}/branches";
                string tagsUrl = $"{repoRoot}/tags";

                var branchesTask = GetRepoListAsync(branchesUrl, token);
                var tagsTask = GetRepoListAsync(tagsUrl, token);
                await Task.WhenAll(branchesTask, tagsTask);

                if (token.IsCancellationRequested) return;

                UpdateDropdown(svnUI.BranchesDropdown, branchesTask.Result, "No branches", true);
                UpdateDropdown(svnUI.TagsDropdown, tagsTask.Result, "No tags", false);

                LogSuccess("[Refresh Complete] UI synchronized.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[Refresh] Cancelled.");
            }
            catch (Exception ex)
            {
                string msg = ex.Message ?? "";
                if (msg.Contains("Permission denied") || msg.Contains("publickey") || msg.Contains("E170013") || msg.Contains("E210002"))
                    LogWarning("[Refresh] SSH connection failed. Check your key.");
                else
                    LogErrorLocal($"[Refresh Error] {msg}");

                UpdateDropdown(svnUI.BranchesDropdown, Array.Empty<string>(), "Error", true);
                UpdateDropdown(svnUI.TagsDropdown, Array.Empty<string>(), "Error", false);
            }
        }

        public async Task DiffWithCurrent(bool isTag)
        {
            if (!TryStart()) return;
            if (!IsReady()) { LogWarning("[Diff] SVN not ready."); End(); return; }

            string tempFilePath = null;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                TMP_Dropdown dropdown = isTag ? svnUI?.TagsDropdown : svnUI?.BranchesDropdown;
                if (dropdown == null || dropdown.options.Count == 0)
                {
                    LogErrorLocal("[Diff] No items available.");
                    return;
                }
                if (dropdown.value < 0 || dropdown.value >= dropdown.options.Count)
                {
                    LogErrorLocal("[Diff] Invalid selection.");
                    return;
                }

                string selected = dropdown.options[dropdown.value].text;
                if (string.IsNullOrEmpty(selected) || IsPlaceholder(selected))
                {
                    LogErrorLocal("[Diff] Please select a valid branch/tag.");
                    return;
                }

                string subFolder = isTag ? "tags" : "branches";
                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Diff] Repo root missing.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(currentUrl))
                {
                    LogErrorLocal("[Diff] Could not determine current URL.");
                    return;
                }

                string selectedUrl = selected.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{EscapeSvnPath(selected)}";

                if (NormalizeUrl(currentUrl) == NormalizeUrl(selectedUrl))
                {
                    LogWarning($"[Diff] You are already on '{selected}'. Comparison skipped.");
                    return;
                }

                string currentName = GetBranchNameFromUrl(currentUrl, repoRoot);
                LogInfo($"[Diff] {currentName} vs {selected}");

                string args = $"diff --summarize \"{currentUrl}\" \"{selectedUrl}\"";
                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, false, CancellationToken.None);

                if (!string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith("svn: E", StringComparison.OrdinalIgnoreCase))
                {
                    LogErrorLocal($"[Diff Error] {output}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogSuccess($"[Diff] No differences found.");
                    return;
                }

                var sb = new StringBuilder(4096);
                sb.AppendLine("=== SVN DIFF SUMMARY ===");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Current : {currentUrl}");
                sb.AppendLine($"Selected: {selectedUrl}");
                sb.AppendLine(new string('-', 60));

                int added = 0, modified = 0, deleted = 0;

                foreach (string line in (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length < 2) continue;
                    char status = line[0];
                    string path = line.Substring(2).Trim();
                    if (string.IsNullOrEmpty(path)) continue;

                    switch (status)
                    {
                        case 'A': added++; break;
                        case 'M': modified++; break;
                        case 'D': deleted++; break;
                    }
                    sb.AppendLine($"[{status}] {Uri.UnescapeDataString(path)}");
                }

                sb.AppendLine(new string('-', 60));
                sb.AppendLine($"Added: {added} | Modified: {modified} | Deleted: {deleted}");
                sb.AppendLine($"Total: {added + modified + deleted}");

                string fileName = $"Diff_{currentName}_vs_{selected}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                tempFilePath = Path.Combine(Application.temporaryCachePath, fileName);
                await File.WriteAllTextAsync(tempFilePath, sb.ToString());

                try
                {
                    using var process = Process.Start(new ProcessStartInfo(tempFilePath) { UseShellExecute = true });
                    if (process == null)
                        LogWarning("[Diff] Could not open diff file (no default handler).");
                }
                catch (Exception ex)
                {
                    LogWarning($"[Diff] Could not open file: {ex.Message}");
                }

                LogSuccess($"[Diff] Exported: {fileName}");
                LogInfo($"<color=#55FF55>+{added}</color>  <color=#FFFF55>~{modified}</color>  <color=#FF5555>-{deleted}</color>");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Diff Error] {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFilePath))
                {
                    try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                }
                End();
            }
        }

        public async Task ShowDetailsForSelected()
        {
            if (!TryStart()) return;
            if (!IsReady()) { LogWarning("[Details] SVN not ready."); End(); return; }

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";
                TMP_Dropdown dropdown = subFolder == "branches" ? svnUI?.BranchesDropdown : svnUI?.TagsDropdown;

                if (dropdown == null || dropdown.options.Count == 0)
                {
                    LogErrorLocal("[Details] No items available.");
                    return;
                }
                if (dropdown.value < 0 || dropdown.value >= dropdown.options.Count)
                {
                    LogErrorLocal("[Details] Invalid selection.");
                    return;
                }

                string selected = dropdown.options[dropdown.value].text;
                if (string.IsNullOrEmpty(selected) || IsPlaceholder(selected))
                {
                    LogErrorLocal("[Details] Please select a valid branch/tag.");
                    return;
                }

                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot)) return;

                string branchUrl = selected.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{EscapeSvnPath(selected)}";

                LogInfo($"[Details] {selected} @ {branchUrl}");

                string logOutput = await SvnRunner.RunAsync(
                    $"log \"{branchUrl}\" -r 1:HEAD --limit 1 --xml",
                    svnManager.WorkingDir, false, CancellationToken.None);

                string firstAuthor = "unknown";
                string firstDate = "unknown";
                string sourceBranch = "trunk (default)";

                if (!string.IsNullOrWhiteSpace(logOutput))
                {
                    try
                    {
                        using var stringReader = new StringReader(logOutput);
                        using var reader = System.Xml.XmlReader.Create(stringReader);
                        if (reader.ReadToDescendant("logentry"))
                        {
                            if (reader.ReadToDescendant("author"))
                                firstAuthor = reader.ReadElementContentAsString();
                            if (reader.ReadToNextSibling("date"))
                                firstDate = reader.ReadElementContentAsString();
                        }
                    }
                    catch { }

                    try
                    {
                        string verboseLog = await SvnRunner.RunAsync(
                            $"log \"{branchUrl}\" -r 1:HEAD --limit 1 --verbose --xml",
                            svnManager.WorkingDir, false, CancellationToken.None);

                        if (!string.IsNullOrWhiteSpace(verboseLog))
                        {
                            using var stringReader = new StringReader(verboseLog);
                            using var reader = System.Xml.XmlReader.Create(stringReader);
                            while (reader.Read())
                            {
                                if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "path")
                                {
                                    string copyFrom = reader.GetAttribute("copyfrom-path");
                                    if (!string.IsNullOrEmpty(copyFrom))
                                    {
                                        sourceBranch = ExtractBranchName(copyFrom);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                LogSuccess($"Name       : {selected}");
                LogInfo($"Created by : {firstAuthor}");

                if (DateTime.TryParseExact(firstDate, "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed))
                {
                    LogInfo($"Created on : {parsed.ToLocalTime():yyyy-MM-dd HH:mm:ss} (local)");
                }
                else
                {
                    LogInfo($"Created on : {firstDate}");
                }

                LogInfo($"Source     : {sourceBranch}");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Details Error] {ex.Message}");
            }
            finally { End(); }
        }

        public async Task SwitchToSelectedBranch()
        {
            if (!TryStart()) return;
            if (!IsReady()) { LogWarning("[Switch] SVN not ready."); End(); return; }
            try
            {
                if (svnUI?.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) return;
                if (svnUI.BranchesDropdown.value < 0 || svnUI.BranchesDropdown.value >= svnUI.BranchesDropdown.options.Count) return;
                string selected = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
                if (IsPlaceholder(selected)) return;
                if (!await CanPerformSwitch()) return;
                await ExecuteUnifiedSwitch(selected, "branches");
            }
            finally { End(); }
        }

        public async Task SwitchToSelectedTag()
        {
            if (!TryStart()) return;
            if (!IsReady()) { LogWarning("[Switch] SVN not ready."); End(); return; }
            try
            {
                if (svnUI?.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;
                if (svnUI.TagsDropdown.value < 0 || svnUI.TagsDropdown.value >= svnUI.TagsDropdown.options.Count) return;
                string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                if (IsPlaceholder(selected)) return;
                if (!await CanPerformSwitch()) return;
                await ExecuteUnifiedSwitch(selected, "tags");
            }
            finally { End(); }
        }

        public async Task DeleteSelectedBranch()
        {
            if (!TryStart()) return;
            if (!IsReady()) { LogWarning("[Delete] SVN not ready."); End(); return; }
            try
            {
                if (svnUI?.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0)
                {
                    LogErrorLocal("Delete aborted: invalid dropdown state.");
                    return;
                }
                if (svnUI.BranchesDropdown.value < 0 || svnUI.BranchesDropdown.value >= svnUI.BranchesDropdown.options.Count)
                {
                    LogErrorLocal("Delete aborted: invalid selection.");
                    return;
                }

                string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text?.Trim();
                if (string.IsNullOrEmpty(selectedBranch) || IsProtectedBranch(selectedBranch))
                {
                    LogErrorLocal("SECURITY BLOCK: 'trunk' is protected and cannot be deleted.");
                    return;
                }

                if (!ConfirmDelete(ref _lastDeleteBranchClickTime, selectedBranch)) return;
                await ExecuteRemoteDeleteTask(selectedBranch, "branches");
            }
            catch (Exception ex) { LogErrorLocal($"[Delete Error] {ex.Message}"); }
            finally { End(); }
        }

        public async Task DeleteSelectedTag()
        {
            if (!TryStart()) return;
            if (!IsReady()) { LogWarning("[Delete] SVN not ready."); End(); return; }
            try
            {
                if (svnUI?.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;
                if (svnUI.TagsDropdown.value < 0 || svnUI.TagsDropdown.value >= svnUI.TagsDropdown.options.Count)
                {
                    LogErrorLocal("Delete aborted: invalid selection.");
                    return;
                }
                string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                if (IsPlaceholder(selected)) return;

                if (!ConfirmDelete(ref _lastDeleteTagClickTime, selected)) return;
                await ExecuteRemoteDeleteTask(selected, "tags");
            }
            finally { End(); }
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir, CancellationToken token = default)
        {
            string output = await SvnRunner.RunAsync("status", workingDir, false, token);
            var stats = new SvnStats();
            foreach (string line in (output ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 1) continue;
                switch (line[0])
                {
                    case 'M': stats.ModifiedCount++; break;
                    case 'A': stats.AddedCount++; break;
                    case 'D': stats.DeletedCount++; break;
                    case 'C': stats.ConflictsCount++; break;
                    case '?': stats.NewFilesCount++; break;
                    case 'I': stats.IgnoredCount++; break;
                }
            }
            return stats;
        }

        public static async Task<string> SwitchAsync(string workingDir, string targetUrl, CancellationToken token = default)
        {
            string currentKey = SvnRunner.KeyPath;
            string sshArgs = "-o BatchMode=yes -o StrictHostKeyChecking=no";
            if (!string.IsNullOrEmpty(currentKey))
                sshArgs = $"-i \"{currentKey}\" {sshArgs}";

            string command =
                $"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" " +
                $"switch \"{targetUrl}\" \"{workingDir}\" " +
                $"--ignore-ancestry --accept theirs-full --non-interactive";

            return await SvnRunner.RunAsync(command, workingDir, true, token);
        }

        public static async Task<string> CopyAsync(string workingDir, string sourceUrl, string destUrl, string message, CancellationToken token = default)
        {
            string cmd = $"copy \"{sourceUrl}\" \"{destUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(cmd, workingDir, false, token);
        }

        public static async Task<string> DeleteRemotePathAsync(string workingDir, string remoteUrl, string message, CancellationToken token = default)
        {
            string args = $"rm \"{remoteUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(args, workingDir, false, token);
        }

        private bool ValidateCreateInputs(out string name, out string subFolder)
        {
            name = svnUI?.BranchNameInput?.text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                LogErrorLocal("[Error] Please enter a valid name.");
                subFolder = null;
                return false;
            }
            subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";
            return true;
        }

        private static string EscapeSvnPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return path.Replace("\"", "\\\"");
        }

        private bool ConfirmDelete(ref float lastClickTime, string targetName)
        {
            float timeSinceLastClick = Time.time - lastClickTime;
            if (timeSinceLastClick > 5f)
            {
                lastClickTime = Time.time;
                LogWarning($"[Delete] Are you sure? This will permanently delete '{targetName}'.");
                LogWarning("Press the button again within 5 seconds to confirm.");
                return false;
            }
            lastClickTime = -10f;
            return true;
        }

        private async Task ExecuteUnifiedSwitch(string targetName, string subFolder)
        {
            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                if (!SVNAssetLocator.IsWorkingCopy(svnManager.WorkingDir))
                {
                    LogErrorLocal("Working directory is not a valid SVN working copy.");
                    return;
                }

                LogInfo($"[Switch] Switching to {targetName}...");
                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Switch] Repo root missing.");
                    return;
                }

                string targetUrl = targetName.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{EscapeSvnPath(targetName)}";

                string result = await SwitchAsync(svnManager.WorkingDir, targetUrl);

                string safeResult = result ?? "";
                if (!safeResult.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                    !safeResult.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    LogSuccess($"Switch Complete: {targetName}");
                    var bar = svnManager.GetModule<SVNBar>();
                    if (bar != null)
                        await bar.ShowProjectInfo(null, svnManager.WorkingDir);
                    await svnManager.RefreshStatus();
                }
                else
                {
                    LogErrorLocal($"[Switch Failed]\n{safeResult}");
                }
            }
            catch (Exception ex) { LogErrorLocal($"[Switch Error] {ex.Message}"); }
        }

        private async Task<bool> CanPerformSwitch()
        {
            LogInfo("Validating safety...");
            var stats = await GetStatsAsync(svnManager.WorkingDir);
            if (stats.ConflictsCount > 0)
            {
                LogErrorLocal("ERROR: Unresolved conflicts!");
                return false;
            }
            if (stats.ModifiedCount > 0 || stats.AddedCount > 0 || stats.DeletedCount > 0)
            {
                LogWarning("You have uncommitted changes. They will be left in your working copy but won't be on the target branch.");
            }
            return true;
        }

        private async Task ExecuteRemoteDeleteTask(string targetName, string subFolder)
        {
            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Delete] Repo root missing.");
                    return;
                }

                string targetUrl = $"{repoRoot}/{subFolder}/{EscapeSvnPath(targetName)}";

                if (NormalizeUrl(currentUrl) == NormalizeUrl(targetUrl))
                {
                    LogErrorLocal("ABORTED: Active branch/tag cannot be deleted!");
                    return;
                }

                string msg = $"Deleted {subFolder}: {targetName} via Unity SVN Tool";
                await DeleteRemotePathAsync(svnManager.WorkingDir, targetUrl, msg);
                LogSuccess($"Deleted: {targetName}");
                await RefreshUnifiedList();
            }
            catch (Exception ex) { LogErrorLocal($"[Delete Error] {ex.Message}"); }
        }

        private static bool IsProtectedBranch(string name) =>
            string.Equals(name?.Trim(), "trunk", StringComparison.OrdinalIgnoreCase);

        private static bool IsPlaceholder(string text) =>
            text?.Contains("Loading") == true ||
            text?.Contains("No ") == true ||
            text?.Contains("None") == true;

        private static void UpdateDropdown(TMP_Dropdown dropdown, string[] items, string emptyMsg, bool includeTrunk)
        {
            if (dropdown == null) return;
            dropdown.ClearOptions();

            var options = new List<string>(capacity: (items?.Length ?? 0) + 2);
            if (includeTrunk) options.Add("trunk");

            if (items != null)
            {
                foreach (var item in items)
                {
                    string clean = item?.Trim().TrimEnd('/');
                    if (!string.IsNullOrEmpty(clean) && !clean.Equals("trunk", StringComparison.OrdinalIgnoreCase))
                        options.Add(clean);
                }
            }

            if (options.Count == 0) options.Add(emptyMsg);
            dropdown.AddOptions(options);
            dropdown.RefreshShownValue();
        }

        private static string NormalizeUrl(string url)
        {
            return (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        }

        private static string GetBranchNameFromUrl(string url, string repoRoot)
        {
            if (string.IsNullOrEmpty(url)) return "unknown";
            url = url.TrimEnd('/');
            if (url.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase)) return "trunk";

            string relative = url.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                ? url.Substring(repoRoot.Length).TrimStart('/')
                : url;

            if (relative.StartsWith("branches/", StringComparison.OrdinalIgnoreCase))
                return relative.Substring("branches/".Length);
            if (relative.StartsWith("tags/", StringComparison.OrdinalIgnoreCase))
                return relative.Substring("tags/".Length);

            return relative;
        }

        private static string ExtractBranchName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "trunk (default)";

            if (path.Contains("/branches/"))
            {
                string name = path.Substring(path.LastIndexOf("/branches/") + "/branches/".Length);
                return name.TrimEnd('/');
            }
            if (path.Contains("/tags/"))
                return "tag: " + path.Substring(path.LastIndexOf("/tags/") + "/tags/".Length).TrimEnd('/');
            if (path.Contains("/trunk"))
                return "trunk";

            return "trunk (default)";
        }

        protected override TMP_Text GetConsole() => svnUI?.BranchTagConsoleText;
    }
}