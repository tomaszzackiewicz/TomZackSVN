using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase
    {
        // Timery dla double‑click (usuwanie)
        private float _lastDeleteBranchClickTime = -10f;
        private float _lastDeleteTagClickTime = -10f;

        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // ============================================================
        // CREATE BRANCH / TAG
        // ============================================================
        public async Task CreateBranchFromTrunk()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();   // 🔥

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

                string sourceUrl = $"{repoRoot}/trunk";
                string targetUrl = $"{repoRoot}/{subFolder}/{name}";

                LogInfo($"[Create] Copying from TRUNK → {subFolder}");
                LogInfo($"Source: {sourceUrl}");
                LogInfo($"Target: {targetUrl}");

                string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{name}\" --parents";
                await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);
                LogSuccess($"Created: {name}");
                await RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Create Error] {ex.Message}");
            }
            finally { End(); }
        }

        /// <summary>
        /// Tworzy nowy branch (lub tag) z aktualnie wybranej gałęzi w dropdownie.
        /// Nazwa nowej gałęzi pobierana jest z pola BranchNameInput.
        /// </summary>
        public async Task CreateBranchFromSelected()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                // Określ, czy tworzymy branch czy tag
                string subFolder = (svnUI.TypeSelector.value == 0) ? "branches" : "tags";

                // Pobierz gałąź źródłową z odpowiedniego dropdowna
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

                // Nazwa nowej gałęzi
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

                // Źródło: jeśli wybrano "trunk" → trunk, inaczej branches/Nazwa (lub tags/Nazwa)
                string sourceUrl = sourceName.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{sourceName}";

                string targetUrl = $"{repoRoot}/{subFolder}/{newName}";

                LogInfo($"[Create] {sourceName} → {subFolder}/{newName}");
                LogInfo($"Source: {sourceUrl}");
                LogInfo($"Target: {targetUrl}");

                string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{newName} from {sourceName}\" --parents";
                await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

                LogSuccess($"Created {newName} from {sourceName}");

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

        // ============================================================
        // REFRESH LIST
        // ============================================================
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

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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

        /// <summary>
        /// Pobiera i wyświetla szczegóły aktualnie wybranej gałęzi (lub taga).
        /// </summary>
        /// <summary>
        /// Pobiera i wyświetla szczegóły aktualnie wybranej gałęzi lub taga.
        /// </summary>
        public async Task ShowDetailsForSelected()
        {
            if (!TryStart()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                // Ustalamy, czy pracujemy na branchach czy tagach
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
                LogInfo($"[Details] {subFolder}: {selected}");
                LogInfo($"URL: {branchUrl}");
                LogInfo("====================================");

                // Pierwszy commit – autor i data utworzenia
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

                // Źródło kopii
                string sourceBranch = "trunk (default)";
                try
                {
                    string verboseLog = await SvnRunner.RunAsync(
                        $"log \"{branchUrl}\" -r 1:HEAD --limit 1 --verbose --xml",
                        svnManager.WorkingDir);

                    if (!string.IsNullOrWhiteSpace(verboseLog))
                    {
                        var doc2 = System.Xml.Linq.XDocument.Parse(verboseLog);
                        var paths = doc2.Descendants("path")
                            .Select(p => p.Value)
                            .Where(p => p.Contains("(from "))
                            .ToList();

                        foreach (string path in paths)
                        {
                            int fromIdx = path.IndexOf("(from ");
                            if (fromIdx >= 0)
                            {
                                string fromPart = path.Substring(fromIdx + 6).TrimEnd(')');
                                if (fromPart.Contains("/branches/"))
                                    sourceBranch = fromPart.Substring(fromPart.LastIndexOf("/branches/") + "/branches/".Length);
                                else if (fromPart.Contains("/tags/"))
                                    sourceBranch = "tag: " + fromPart.Substring(fromPart.LastIndexOf("/tags/") + "/tags/".Length);
                                else if (fromPart.Contains("/trunk"))
                                    sourceBranch = "trunk";
                                break;
                            }
                        }
                    }
                }
                catch { }

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

        // ============================================================
        // SWITCH
        // ============================================================
        public async Task SwitchToSelectedBranch()
        {
            if (!TryStart()) return;
            try
            {
                if (svnUI.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0)
                    return;
                string selected = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
                if (IsPlaceholder(selected)) return;
                if (!await CanPerformSwitch()) return;   // ostrzeżenie, ale nie blokuje
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

        // ============================================================
        // DELETE (z double‑click)
        // ============================================================
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

                // Double‑click
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

                // Double‑click
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

                if (currentUrl.TrimEnd('/') == targetUrl.TrimEnd('/'))
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

        // ============================================================
        // HELPERS
        // ============================================================
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

        // ============================================================
        // STATIC SVN HELPERS
        // ============================================================
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