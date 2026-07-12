using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase
    {
        private float _lastDeleteBranchClickTime = -10f;
        private float _lastDeleteTagClickTime = -10f;

        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async Task CreateBranchFromTrunk()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string name = svnUI.BranchNameInput.text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    LogErrorLocal("[Error] Please enter a valid name.");
                    return;
                }

                string subFolder = (svnUI.TypeSelector.value == 0) ? "branches" : "tags";
                string repoRoot = svnManager.GetRepoRoot()?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Error] Repo root missing.");
                    return;
                }

                string revision = svnUI.RevisionInput?.text?.Trim();
                bool hasRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

                string sourceUrl = $"{repoRoot}/trunk";
                string targetUrl = $"{repoRoot}/{subFolder}/{name}";

                if (hasRevision)
                {
                    string sourceUrlWithRev = $"{sourceUrl}@{revision}";
                    LogInfo($"[Create @ rev] trunk@{revision} → {subFolder}/{name}");
                    LogInfo($"Source: {sourceUrlWithRev}");
                    LogInfo($"Target: {targetUrl}");

                    string cmd = $"copy \"{sourceUrlWithRev}\" \"{targetUrl}\" -m \"Created {subFolder}/{name} from trunk@{revision}\" --parents";
                    await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);
                    LogSuccess($"Created: {name} from trunk at revision {revision}");
                }
                else
                {
                    LogInfo($"[Create] Copying from TRUNK → {subFolder}");
                    LogInfo($"Source: {sourceUrl}");
                    LogInfo($"Target: {targetUrl}");

                    string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{name}\" --parents";
                    await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);
                    LogSuccess($"Created: {name}");
                }

                await RefreshUnifiedList();
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
                await svnManager.CancelBackgroundTasksAsync();

                string subFolder = (svnUI.TypeSelector.value == 0) ? "branches" : "tags";

                TMP_Dropdown sourceDropdown = subFolder == "branches"
                    ? svnUI.BranchesDropdown
                    : svnUI.TagsDropdown;

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

                string newName = svnUI.BranchNameInput.text.Trim();
                if (string.IsNullOrEmpty(newName))
                {
                    LogErrorLocal("[Error] Please enter a name for the new branch.");
                    return;
                }

                string repoRoot = svnManager.GetRepoRoot()?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Error] Repo root missing.");
                    return;
                }

                string sourceUrl = sourceName.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{sourceName}";

                string revision = svnUI.RevisionInput?.text?.Trim();
                bool hasValidRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

                if (hasValidRevision)
                {
                    sourceUrl = $"{sourceUrl}@{revision}";
                    LogInfo($"[Create @ rev] {sourceName}@{revision} → {subFolder}/{newName}");
                }
                else
                {
                    LogInfo($"[Create] {sourceName} → {subFolder}/{newName}");
                }

                string targetUrl = $"{repoRoot}/{subFolder}/{newName}";

                LogInfo($"Source: {sourceUrl}");
                LogInfo($"Target: {targetUrl}");

                string message = hasValidRevision
                    ? $"Created {subFolder}/{newName} from {sourceName}@{revision}"
                    : $"Created {subFolder}/{newName} from {sourceName}";

                string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"{message}\" --parents";
                await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

                LogSuccess(hasValidRevision
                    ? $"Created {newName} from {sourceName} at revision {revision}"
                    : $"Created {newName} from {sourceName}");

                await RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Create Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        public async Task RefreshUnifiedList()
        {
            if (svnUI == null || (svnUI.BranchesDropdown == null && svnUI.TagsDropdown == null))
                return;

            try
            {
                LogInfo("[Refresh] Syncing lists with server...");

                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string branchesUrl = $"{repoRoot}/branches";
                string tagsUrl = $"{repoRoot}/tags";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var branchesTask = SvnRunner.GetRepoListAsync(svnManager.WorkingDir, branchesUrl);
                var tagsTask = SvnRunner.GetRepoListAsync(svnManager.WorkingDir, tagsUrl);
                await Task.WhenAll(branchesTask, tagsTask);

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
                await svnManager.CancelBackgroundTasksAsync();

                TMP_Dropdown dropdown = isTag ? svnUI.TagsDropdown : svnUI.BranchesDropdown;
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
                string repoRoot = svnManager.GetRepoRoot()?.TrimEnd('/');
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
                    : $"{repoRoot}/{subFolder}/{selected}";

                if (NormalizeUrl(currentUrl) == NormalizeUrl(selectedUrl))
                {
                    LogWarning($"[Diff] You are already on '{selected}'. Comparison skipped.");
                    return;
                }

                string currentName = GetBranchNameFromUrl(currentUrl, repoRoot);
                string selectedName = selected;

                LogInfo($"====================================");
                LogInfo($"[Diff] Exporting differences to file...");
                LogInfo($"Current : {currentName}");
                LogInfo($"Selected: {selectedName}");
                LogInfo("====================================");

                string args = $"diff --summarize \"{currentUrl}\" \"{selectedUrl}\"";
                LogInfo($"[Diff] Executing: svn {args}");

                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogSuccess($"[Diff] No differences found between {currentName} and {selectedName}.");
                    return;
                }

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var sb = new System.Text.StringBuilder();

                sb.AppendLine($"=== SVN DIFF SUMMARY ===");
                sb.AppendLine($"Generated: {DateTime.Now}");
                sb.AppendLine($"Current ({currentName}): {currentUrl}");
                sb.AppendLine($"Selected ({selectedName}): {selectedUrl}");
                sb.AppendLine(new string('-', 60));

                int added = 0, modified = 0, deleted = 0;

                foreach (string line in lines)
                {
                    if (line.Length < 2) continue;
                    char status = line[0];
                    string path = line.Substring(2).Trim();

                    string decodedPath = Uri.UnescapeDataString(path);

                    switch (status)
                    {
                        case 'A': added++; break;
                        case 'M': modified++; break;
                        case 'D': deleted++; break;
                    }

                    sb.AppendLine($"[{status}] {decodedPath}");
                }

                sb.AppendLine(new string('-', 60));
                sb.AppendLine($"Summary: Added: {added} | Modified: {modified} | Deleted: {deleted}");
                sb.AppendLine($"Total changes: {added + modified + deleted}");

                string fileName = $"Diff_{currentName}_vs_{selectedName}_{DateTime.Now:yyyyMMdd_HHmm}.txt";
                string filePath = Path.Combine(Application.temporaryCachePath, fileName);
                File.WriteAllText(filePath, sb.ToString());

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = filePath,
                    UseShellExecute = true
                });

                LogSuccess($"[Diff] Full diff exported to: {fileName}");
                LogInfo($"Summary: <color=#55FF55>Added: {added}</color>  <color=#FFFF55>Modified: {modified}</color>  <color=#FF5555>Deleted: {deleted}</color>");
                LogInfo($"Total changes: {added + modified + deleted}");
                LogInfo("====================================");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Diff Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        private string GetBranchNameFromUrl(string url, string repoRoot)
        {
            if (string.IsNullOrEmpty(url)) return "unknown";

            url = url.TrimEnd('/');
            if (url.EndsWith("/trunk")) return "trunk";

            string relative = url;
            if (url.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                relative = url.Substring(repoRoot.Length).TrimStart('/');

            if (relative.StartsWith("branches/", StringComparison.OrdinalIgnoreCase))
                return relative.Substring("branches/".Length);
            else if (relative.StartsWith("tags/", StringComparison.OrdinalIgnoreCase))
                return relative.Substring("tags/".Length);

            return relative;
        }

        private string NormalizeUrl(string url)
        {
            return (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        }

        public async Task ShowDetailsForSelected()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string subFolder = (svnUI.TypeSelector.value == 0) ? "branches" : "tags";
                TMP_Dropdown dropdown = subFolder == "branches"
                    ? svnUI.BranchesDropdown
                    : svnUI.TagsDropdown;

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

                string repoRoot = svnManager.GetRepoRoot()?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Details] Repo root missing.");
                    return;
                }

                string branchUrl = selected.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{selected}";

                LogInfo($"====================================");
                LogInfo($"[Details] Fetching info for: {selected}");
                LogInfo($"URL: {branchUrl}");
                LogInfo("====================================");

                string logOutput = await SvnRunner.RunAsync(
                    $"log \"{branchUrl}\" -r 1:HEAD --limit 1 --xml",
                    svnManager.WorkingDir);

                string firstAuthor = "unknown";
                string firstDate = "unknown";
                if (!string.IsNullOrWhiteSpace(logOutput))
                {
                    var doc = System.Xml.Linq.XDocument.Parse(logOutput);
                    var logEntry = doc.Descendants("logentry").FirstOrDefault();
                    if (logEntry != null)
                    {
                        firstAuthor = logEntry.Element("author")?.Value ?? "unknown";
                        firstDate = logEntry.Element("date")?.Value ?? "unknown";
                    }
                }

                string sourceBranch = "trunk (default)";
                try
                {
                    string verboseLog = await SvnRunner.RunAsync(
                        $"log \"{branchUrl}\" -r 1:HEAD --limit 1 --verbose --xml",
                        svnManager.WorkingDir);

                    if (!string.IsNullOrWhiteSpace(verboseLog))
                    {
                        var doc2 = System.Xml.Linq.XDocument.Parse(verboseLog);
                        var pathElements = doc2.Descendants("path").ToList();

                        bool found = false;

                        foreach (var path in pathElements)
                        {
                            string copyFromPath = path.Attribute("copyfrom-path")?.Value ?? "";
                            if (!string.IsNullOrEmpty(copyFromPath))
                            {
                                sourceBranch = ExtractBranchName(copyFromPath);
                                found = true;
                                break;
                            }

                            string action = path.Attribute("action")?.Value ?? "";
                            if (action == "A" && !string.IsNullOrEmpty(copyFromPath))
                            {
                                sourceBranch = ExtractBranchName(copyFromPath);
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            foreach (var path in pathElements)
                            {
                                string pathValue = path.Value ?? "";
                                int fromIdx = pathValue.IndexOf("(from ");
                                if (fromIdx >= 0)
                                {
                                    string fromPart = pathValue.Substring(fromIdx + 6).TrimEnd(')');
                                    fromPart = fromPart.Split(':')[0].Trim();

                                    if (fromPart.Contains("/branches/"))
                                    {
                                        sourceBranch = fromPart.Substring(fromPart.LastIndexOf("/branches/") + "/branches/".Length);
                                        found = true;
                                        break;
                                    }
                                    else if (fromPart.Contains("/tags/"))
                                    {
                                        sourceBranch = "tag: " + fromPart.Substring(fromPart.LastIndexOf("/tags/") + "/tags/".Length);
                                        found = true;
                                        break;
                                    }
                                    else if (fromPart.Contains("/trunk"))
                                    {
                                        sourceBranch = "trunk";
                                        found = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch {}

                LogSuccess($"Name       : {selected}");
                LogInfo($"Created by : {firstAuthor}");

                string friendlyDate = firstDate;
                if (DateTime.TryParse(firstDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                {
                    TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(parsed);
                    string sign = offset >= TimeSpan.Zero ? "+" : "-";
                    string offsetStr = $"{sign}{offset.Hours:D2}:{offset.Minutes:D2}";
                    friendlyDate = parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + $" (UTC{offsetStr})";
                }
                LogInfo($"Created on : {friendlyDate}");
                LogInfo($"Source     : {sourceBranch}");
                LogInfo("====================================");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Details Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        private string ExtractBranchName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "trunk (default)";

            if (path.Contains("/branches/"))
            {
                string name = path.Substring(path.LastIndexOf("/branches/") + "/branches/".Length);
                return name.TrimEnd('/');
            }
            else if (path.Contains("/tags/"))
            {
                return "tag: " + path.Substring(path.LastIndexOf("/tags/") + "/tags/".Length).TrimEnd('/');
            }
            else if (path.Contains("/trunk"))
            {
                return "trunk";
            }

            return "trunk (default)";
        }

        public async Task SwitchToSelectedBranch()
        {
            if (!TryStart()) return;
            try
            {
                if (svnUI.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0)
                    return;
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
            try
            {
                if (svnUI.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0)
                    return;
                string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                if (IsPlaceholder(selected)) return;
                if (!await CanPerformSwitch()) return;
                await ExecuteUnifiedSwitch(selected, "tags");
            }
            finally { End(); }
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
                string repoRoot = svnManager.GetRepoRoot();
                string targetUrl = (targetName.ToLower() == "trunk")
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{targetName}";
                LogInfo($"... Target: {targetUrl}");

                string result = await SwitchAsync(svnManager.WorkingDir, targetUrl);

                if (!result.ToLower().Contains("error") && !result.ToLower().Contains("failed"))
                {
                    LogSuccess($"Switch Complete: {targetName}");
                    await svnManager.GetModule<SVNBar>().ShowProjectInfo(null, svnManager.WorkingDir);
                    await svnManager.RefreshStatus();
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

                float timeSinceLastClick = Time.time - _lastDeleteBranchClickTime;
                if (timeSinceLastClick > 5f)
                {
                    _lastDeleteBranchClickTime = Time.time;
                    LogWarning($"[Delete] Are you sure? This will permanently delete the branch '{selectedBranch}'.");
                    LogWarning("Press the button again within 5 seconds to confirm.");
                    return;
                }
                _lastDeleteBranchClickTime = -10f;

                LogWarning($"[Delete] Requested branch removal: {selectedBranch}");
                await ExecuteRemoteDeleteTask(selectedBranch, "branches");
            }
            catch (Exception ex) { LogErrorLocal($"[Delete Error] {ex.Message}"); }
            finally { End(); }
        }

        public async Task DeleteSelectedTag()
        {
            if (!TryStart()) return;
            try
            {
                if (svnUI.TagsDropdown.options.Count == 0) return;
                string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                if (IsPlaceholder(selected)) return;

                float timeSinceLastClick = Time.time - _lastDeleteTagClickTime;
                if (timeSinceLastClick > 5f)
                {
                    _lastDeleteTagClickTime = Time.time;
                    LogWarning($"[Delete] Are you sure? This will permanently delete the tag '{selected}'.");
                    LogWarning("Press the button again within 5 seconds to confirm.");
                    return;
                }
                _lastDeleteTagClickTime = -10f;

                await ExecuteRemoteDeleteTask(selected, "tags");
            }
            finally { End(); }
        }

        private async Task ExecuteRemoteDeleteTask(string targetName, string subFolder)
        {
            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                LogInfo($"[Delete] Removing {subFolder}: {targetName}");
                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string repoRoot = svnManager.GetRepoRoot();
                string targetUrl = $"{repoRoot}/{subFolder}/{targetName}";

                if (NormalizeUrl(currentUrl) == NormalizeUrl(targetUrl))
                {
                    LogErrorLocal("ABORTED: Active branch cannot be deleted!");
                    return;
                }

                string msg = $"Deleted {subFolder}: {targetName} via Unity SVN Tool";
                await DeleteRemotePathAsync(svnManager.WorkingDir, targetUrl, msg);
                LogSuccess($"Deleted: {targetName}");
                await RefreshUnifiedList();
            }
            catch (Exception ex) { LogErrorLocal($"[Delete Error] {ex.Message}"); }
        }

        private bool IsProtectedBranch(string name) =>
            string.Equals(name?.Trim(), "trunk", StringComparison.OrdinalIgnoreCase);

        public static async Task<SvnStats> GetStatsAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status", workingDir);
            SvnStats stats = new SvnStats();
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

        private bool IsPlaceholder(string text) =>
            text.Contains("Loading") || text.Contains("No ") || text.Contains("None");

        private void UpdateDropdown(TMP_Dropdown dropdown, string[] items, string emptyMsg, bool includeTrunk)
        {
            if (dropdown == null) return;
            dropdown.ClearOptions();

            var options = new List<string>();
            if (includeTrunk) options.Add("trunk");

            if (items != null)
            {
                foreach (var item in items)
                {
                    string clean = item.Trim().TrimEnd('/');
                    if (!string.IsNullOrEmpty(clean) && clean.ToLower() != "trunk")
                        options.Add(clean);
                }
            }

            if (options.Count == 0) options.Add(emptyMsg);
            dropdown.AddOptions(options);
            dropdown.RefreshShownValue();
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

        public static async Task<string> CopyAsync(string workingDir, string sourceUrl, string destUrl, string message)
        {
            string cmd = $"copy \"{sourceUrl}\" \"{destUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(cmd, workingDir);
        }

        public static async Task<string> DeleteRemotePathAsync(string workingDir, string remoteUrl, string message)
        {
            string args = $"rm \"{remoteUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(args, workingDir);
        }

        protected override TMP_Text GetConsole() => svnUI?.BranchTagConsoleText;
    }
}