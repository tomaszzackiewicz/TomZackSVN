using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase
    {


        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async Task CreateRemoteCopy()
        {
            if (!TryStart()) return;

            try
            {
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

                string cmd =
                    $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{name}\" --parents";

                await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

                LogSuccess($"Created: {name}");

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

                var branchesTask = SvnRunner.GetRepoListAsync(svnManager.WorkingDir, branchesUrl);
                var tagsTask = SvnRunner.GetRepoListAsync(svnManager.WorkingDir, tagsUrl);

                await Task.WhenAll(branchesTask, tagsTask);

                UpdateDropdown(svnUI.BranchesDropdown, branchesTask.Result, "No branches", true);
                UpdateDropdown(svnUI.TagsDropdown, tagsTask.Result, "No tags", false);

                LogSuccess("[Refresh Complete] UI synchronized.");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Refresh Error] {ex.Message}");
                UpdateDropdown(svnUI.BranchesDropdown, null, "Error", true);
            }
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
                LogInfo($"[Switch] Switching to {targetName}...");

                string repoRoot = svnManager.GetRepoRoot();

                string targetUrl =
                    (targetName.ToLower() == "trunk")
                        ? $"{repoRoot}/trunk"
                        : $"{repoRoot}/{subFolder}/{targetName}";

                LogInfo($"... Target: {targetUrl}");

                string result = await SwitchAsync(svnManager.WorkingDir, targetUrl);

                if (!result.ToLower().Contains("error") &&
                    !result.ToLower().Contains("failed"))
                {
                    LogSuccess($"Switch Complete: {targetName}");

                    await svnManager.GetModule<SVNBar>()
                        .ShowProjectInfo(null, svnManager.WorkingDir);

                    await svnManager.RefreshStatus();
                }
                else
                {
                    LogErrorLocal($"[Switch Failed]\n{result}");
                }
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Switch Error] {ex.Message}");
            }
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

            if (stats.ModifiedCount > 0 ||
                stats.AddedCount > 0 ||
                stats.DeletedCount > 0)
            {
                LogWarning("Uncommitted changes detected.");
            }

            return true;
        }

        public async Task DeleteSelectedBranch()
        {
            if (!TryStart()) return;

            try
            {
                if (svnUI?.BranchesDropdown == null ||
                    svnUI.BranchesDropdown.options.Count == 0)
                {
                    LogErrorLocal("Delete aborted: invalid dropdown state.");
                    return;
                }

                string selectedBranch =
                    svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text?.Trim();

                if (string.IsNullOrEmpty(selectedBranch))
                {
                    LogErrorLocal("Delete aborted: empty selection.");
                    return;
                }

                if (IsProtectedBranch(selectedBranch))
                {
                    LogErrorLocal("SECURITY BLOCK: 'trunk' is protected and cannot be deleted.");
                    return;
                }

                if (selectedBranch.Equals("trunk", StringComparison.OrdinalIgnoreCase))
                {
                    LogErrorLocal("SECURITY BLOCK: direct trunk match detected.");
                    return;
                }

                if (selectedBranch.Contains("..") ||
                    selectedBranch.Contains("/") && !selectedBranch.StartsWith("branches"))
                {
                    LogErrorLocal("SECURITY BLOCK: invalid branch path detected.");
                    return;
                }

                LogWarning($"[Delete] Requested branch removal: {selectedBranch}");

                await ExecuteRemoteDeleteTask(selectedBranch, "branches");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Delete Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        private bool IsProtectedBranch(string name)
        {
            return string.Equals(name?.Trim(), "trunk", StringComparison.OrdinalIgnoreCase);
        }

        public async Task DeleteSelectedTag()
        {
            if (!TryStart()) return;

            try
            {
                if (svnUI.TagsDropdown.options.Count == 0) return;
                string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;

                if (IsPlaceholder(selected)) return;

                await ExecuteRemoteDeleteTask(selected, "tags");
            }
            finally { End(); }
        }

        private async Task ExecuteRemoteDeleteTask(string targetName, string subFolder)
        {
            try
            {
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
            catch (Exception ex)
            {
                LogErrorLocal($"[Delete Error] {ex.Message}");
            }
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status", workingDir);

            SvnStats stats = new SvnStats();

            string[] lines = output.Split(
                new[] { '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
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

        private bool IsPlaceholder(string text)
            => text.Contains("Loading") ||
               text.Contains("No ") ||
               text.Contains("None");

        private void UpdateDropdown(
            TMPro.TMP_Dropdown dropdown,
            string[] items,
            string emptyMsg,
            bool includeTrunk)
        {
            if (dropdown == null) return;

            dropdown.ClearOptions();

            List<string> options = new List<string>();

            if (includeTrunk)
                options.Add("trunk");

            if (items != null)
            {
                foreach (var item in items)
                {
                    string clean = item.Trim().TrimEnd('/');

                    if (!string.IsNullOrEmpty(clean) &&
                        clean.ToLower() != "trunk")
                    {
                        options.Add(clean);
                    }
                }
            }

            if (options.Count == 0)
                options.Add(emptyMsg);

            dropdown.AddOptions(options);
            dropdown.RefreshShownValue();
        }

        public static async Task<string> SwitchAsync(
            string workingDir,
            string targetUrl,
            CancellationToken token = default)
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

        public static async Task<string> CopyAsync(
            string workingDir,
            string sourceUrl,
            string destUrl,
            string message)
        {
            string cmd = $"copy \"{sourceUrl}\" \"{destUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(cmd, workingDir);
        }

        public static async Task<string> DeleteRemotePathAsync(
            string workingDir,
            string remoteUrl,
            string message)
        {
            string args = $"rm \"{remoteUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(args, workingDir);
        }

        protected override TMPro.TMP_Text GetConsole()
        {
            return svnUI?.BranchTagConsoleText;
        }
    }
}