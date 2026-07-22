using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase
    {
        private float _lastDeleteBranchClickTime = -10f;
        private float _lastDeleteTagClickTime = -10f;

        // Cache dla repo root – unikamy wielokrotnych wywołań SVN
        private string _cachedRepoRoot;

        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        #region Public API

        public async Task CreateBranchFromTrunk()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                if (!ValidateCreateInputs(out string name, out string subFolder))
                    return;

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
                    await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);
                    LogSuccess($"Created: {name} from trunk at revision {revision}");
                }
                else
                {
                    LogInfo($"[Create] Copying from TRUNK → {subFolder}");
                    string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{name}\" --parents";
                    await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);
                    LogSuccess($"Created: {name}");
                }

                await RefreshUnifiedList().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Create Error] {ex.Message}");
            }
            finally { End(); }
        }

        public async Task CreateBranchFromSelected()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                if (!ValidateCreateInputs(out string newName, out string subFolder))
                    return;

                TMP_Dropdown sourceDropdown = subFolder == "branches"
                    ? svnUI?.BranchesDropdown
                    : svnUI?.TagsDropdown;

                if (sourceDropdown == null || sourceDropdown.options.Count == 0)
                {
                    LogErrorLocal($"[Error] No {subFolder} available.");
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
                await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);

                LogSuccess(hasRevision
                    ? $"Created {newName} from {sourceName} at revision {revision}"
                    : $"Created {newName} from {sourceName}");

                await RefreshUnifiedList().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Create Error] {ex.Message}");
            }
            finally { End(); }
        }

        public async Task RefreshUnifiedList()
        {
            if (svnUI?.BranchesDropdown == null && svnUI?.TagsDropdown == null)
                return;

            try
            {
                LogInfo("[Refresh] Syncing lists with server...");

                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Refresh] Repo root missing.");
                    return;
                }

                string branchesUrl = $"{repoRoot}/branches";
                string tagsUrl = $"{repoRoot}/tags";

                var branchesTask = SvnRunner.GetRepoListAsync(svnManager.WorkingDir, branchesUrl);
                var tagsTask = SvnRunner.GetRepoListAsync(svnManager.WorkingDir, tagsUrl);
                await Task.WhenAll(branchesTask, tagsTask).ConfigureAwait(false);

                UpdateDropdown(svnUI.BranchesDropdown, branchesTask.Result ?? Array.Empty<string>(), "No branches", true);
                UpdateDropdown(svnUI.TagsDropdown, tagsTask.Result ?? Array.Empty<string>(), "No tags", false);

                LogSuccess("[Refresh Complete] UI synchronized.");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Refresh Error] {ex.Message}");
                UpdateDropdown(svnUI.BranchesDropdown, Array.Empty<string>(), "Error", true);
                UpdateDropdown(svnUI.TagsDropdown, Array.Empty<string>(), "Error", false);
            }
        }

        public async Task DiffWithCurrent(bool isTag)
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                TMP_Dropdown dropdown = isTag ? svnUI?.TagsDropdown : svnUI?.BranchesDropdown;
                if (dropdown == null || dropdown.options.Count == 0)
                {
                    LogErrorLocal("[Diff] No items available.");
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

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
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
                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);

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

                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length < 2) continue;
                    char status = line[0];
                    string path = line.Substring(2).Trim();

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
                string filePath = Path.Combine(Application.temporaryCachePath, fileName);
                await File.WriteAllTextAsync(filePath, sb.ToString()).ConfigureAwait(false);

                using (Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true })) { }

                LogSuccess($"[Diff] Exported: {fileName}");
                LogInfo($"<color=#55FF55>+{added}</color>  <color=#FFFF55>~{modified}</color>  <color=#FF5555>-{deleted}</color>");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Diff Error] {ex.Message}");
            }
            finally { End(); }
        }

        public async Task ShowDetailsForSelected()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";
                TMP_Dropdown dropdown = subFolder == "branches"
                    ? svnUI?.BranchesDropdown
                    : svnUI?.TagsDropdown;

                if (dropdown == null || dropdown.options.Count == 0)
                {
                    LogErrorLocal("[Details] No items available.");
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

                // Pobierz tylko pierwszy commit (najstarszy) – źródło brancha
                string logOutput = await SvnRunner.RunAsync(
                    $"log \"{branchUrl}\" -r 1:HEAD --limit 1 --xml",
                    svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);

                string firstAuthor = "unknown";
                string firstDate = "unknown";
                string sourceBranch = "trunk (default)";

                if (!string.IsNullOrWhiteSpace(logOutput))
                {
                    try
                    {
                        using var reader = XmlReader.Create(new StringReader(logOutput));
                        if (reader.ReadToDescendant("logentry"))
                        {
                            reader.ReadToDescendant("author");
                            firstAuthor = reader.ReadElementContentAsString();
                            reader.ReadToNextSibling("date");
                            firstDate = reader.ReadElementContentAsString();
                        }
                    }
                    catch { /* fallback */ }

                    // Spróbuj pobrać copyfrom-path z verbose log
                    try
                    {
                        string verboseLog = await SvnRunner.RunAsync(
                            $"log \"{branchUrl}\" -r 1:HEAD --limit 1 --verbose --xml",
                            svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(verboseLog))
                        {
                            using var reader = XmlReader.Create(new StringReader(verboseLog));
                            while (reader.Read())
                            {
                                if (reader.NodeType == XmlNodeType.Element && reader.Name == "path")
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
                    catch { /* fallback */ }
                }

                LogSuccess($"Name       : {selected}");
                LogInfo($"Created by : {firstAuthor}");

                // SVN zwraca datę w ISO 8601 (UTC)
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
            try
            {
                if (svnUI?.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0)
                    return;
                string selected = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
                if (IsPlaceholder(selected)) return;
                if (!await CanPerformSwitch().ConfigureAwait(false)) return;
                await ExecuteUnifiedSwitch(selected, "branches").ConfigureAwait(false);
            }
            finally { End(); }
        }

        public async Task SwitchToSelectedTag()
        {
            if (!TryStart()) return;
            try
            {
                if (svnUI?.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0)
                    return;
                string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                if (IsPlaceholder(selected)) return;
                if (!await CanPerformSwitch().ConfigureAwait(false)) return;
                await ExecuteUnifiedSwitch(selected, "tags").ConfigureAwait(false);
            }
            finally { End(); }
        }

        public async Task DeleteSelectedBranch()
        {
            if (!TryStart()) return;
            try
            {
                if (svnUI?.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0)
                {
                    LogErrorLocal("Delete aborted: invalid dropdown state.");
                    return;
                }

                string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text?.Trim();
                if (string.IsNullOrEmpty(selectedBranch) || IsProtectedBranch(selectedBranch))
                {
                    LogErrorLocal("SECURITY BLOCK: 'trunk' is protected and cannot be deleted.");
                    return;
                }

                if (!ConfirmDelete(ref _lastDeleteBranchClickTime, selectedBranch)) return;

                await ExecuteRemoteDeleteTask(selectedBranch, "branches").ConfigureAwait(false);
            }
            catch (Exception ex) { LogErrorLocal($"[Delete Error] {ex.Message}"); }
            finally { End(); }
        }

        public async Task DeleteSelectedTag()
        {
            if (!TryStart()) return;
            try
            {
                if (svnUI?.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;
                string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                if (IsPlaceholder(selected)) return;

                if (!ConfirmDelete(ref _lastDeleteTagClickTime, selected)) return;

                await ExecuteRemoteDeleteTask(selected, "tags").ConfigureAwait(false);
            }
            finally { End(); }
        }

        #endregion

        #region Static Helpers

        public static async Task<SvnStats> GetStatsAsync(string workingDir, CancellationToken token = default)
        {
            string output = await SvnRunner.RunAsync("status", workingDir, false, token).ConfigureAwait(false);
            var stats = new SvnStats();
            foreach (string line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
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

            return await SvnRunner.RunAsync(command, workingDir, true, token).ConfigureAwait(false);
        }

        public static async Task<string> CopyAsync(string workingDir, string sourceUrl, string destUrl, string message, CancellationToken token = default)
        {
            string cmd = $"copy \"{sourceUrl}\" \"{destUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(cmd, workingDir, false, token).ConfigureAwait(false);
        }

        public static async Task<string> DeleteRemotePathAsync(string workingDir, string remoteUrl, string message, CancellationToken token = default)
        {
            string args = $"rm \"{remoteUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(args, workingDir, false, token).ConfigureAwait(false);
        }

        #endregion

        #region Private Helpers

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

        private string EnsureRepoRoot()
        {
            if (!string.IsNullOrWhiteSpace(_cachedRepoRoot)) return _cachedRepoRoot;

            _cachedRepoRoot = svnManager?.GetRepoRoot()?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(_cachedRepoRoot))
            {
                LogErrorLocal("[Error] Repo root missing.");
                return null;
            }
            return _cachedRepoRoot;
        }

        private static string EscapeSvnPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            // SVN toleruje cudzysłowy w URLach, ale spacje wymagają escapowania lub cudzysłowów
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
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                if (!SVNAssetLocator.IsWorkingCopy(svnManager.WorkingDir))
                {
                    LogErrorLocal("Working directory is not a valid SVN working copy.");
                    return;
                }

                LogInfo($"[Switch] Switching to {targetName}...");
                string repoRoot = EnsureRepoRoot();
                string targetUrl = targetName.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{EscapeSvnPath(targetName)}";

                string result = await SwitchAsync(svnManager.WorkingDir, targetUrl).ConfigureAwait(false);

                if (!result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                    !result.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    LogSuccess($"Switch Complete: {targetName}");
                    var bar = svnManager.GetModule<SVNBar>();
                    if (bar != null)
                        await bar.ShowProjectInfo(null, svnManager.WorkingDir).ConfigureAwait(false);
                    await svnManager.RefreshStatus().ConfigureAwait(false);
                }
                else
                {
                    LogErrorLocal($"[Switch Failed]\n{result}");
                }
            }
            catch (Exception ex) { LogErrorLocal($"[Switch Error] {ex.Message}"); }
        }

        private async Task<bool> CanPerformSwitch()
        {
            LogInfo("Validating safety...");
            var stats = await GetStatsAsync(svnManager.WorkingDir).ConfigureAwait(false);
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
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
                string repoRoot = EnsureRepoRoot();
                string targetUrl = $"{repoRoot}/{subFolder}/{EscapeSvnPath(targetName)}";

                if (NormalizeUrl(currentUrl) == NormalizeUrl(targetUrl))
                {
                    LogErrorLocal("ABORTED: Active branch/tag cannot be deleted!");
                    return;
                }

                string msg = $"Deleted {subFolder}: {targetName} via Unity SVN Tool";
                await DeleteRemotePathAsync(svnManager.WorkingDir, targetUrl, msg).ConfigureAwait(false);
                LogSuccess($"Deleted: {targetName}");
                await RefreshUnifiedList().ConfigureAwait(false);
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

        #endregion
    }
}