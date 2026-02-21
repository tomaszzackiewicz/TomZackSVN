using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase
    {
        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogToPanel(string msg, bool append = true)
        {
            SVNLogBridge.LogLine(msg);
            if (svnUI.BranchTagConsoleText != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.BranchTagConsoleText, msg, "BRANCH/TAG", append);
            }
        }

        public async void CreateRemoteCopy()
        {
            if (IsProcessing) return;

            string name = svnUI.BranchNameInput.text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                LogToPanel("<color=red>[Error]</color> Please enter a valid name.");
                return;
            }

            string subFolder = (svnUI.TypeSelector.value == 0) ? "branches" : "tags";
            IsProcessing = true;

            try
            {
                string sourceUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);

                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string targetUrl = $"{repoRoot}/{subFolder}/{name}";

                LogToPanel($"<b>[Create]</b> Copying current working copy to {subFolder}...");

                string cmd = $"copy \"{svnManager.WorkingDir}\" \"{targetUrl}\" -m \"Created {subFolder}/{name} from local workspace\" --parents";
                await SvnRunner.RunAsync(cmd, svnManager.WorkingDir);

                LogToPanel($"<color=green>Success!</color> Created: {name}");
                RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                LogToPanel($"<color=red>[Create Error]</color> {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        public async void RefreshUnifiedList()
        {
            if (svnUI == null || (svnUI.BranchesDropdown == null && svnUI.TagsDropdown == null)) return;

            LogToPanel("<b><color=#4FC3F7>[Refresh]</color> Syncing lists with server...</b>");

            try
            {
                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string branchesUrl = $"{repoRoot}/branches";
                string tagsUrl = $"{repoRoot}/tags";

                var branches = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, branchesUrl);
                var tags = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, tagsUrl);

                UpdateDropdown(svnUI.BranchesDropdown, branches, "No branches", true);
                UpdateDropdown(svnUI.TagsDropdown, tags, "No tags", false);

                LogToPanel("<b><color=green>[Refresh Complete]</color> UI synchronized.</b>");
            }
            catch (Exception ex)
            {
                LogToPanel($"<color=red>[Refresh Error]</color> {ex.Message}");
                UpdateDropdown(svnUI.BranchesDropdown, null, "Error", true);
            }
        }

        public async void SwitchToSelectedBranch()
        {
            if (IsProcessing) return;
            if (svnUI.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) return;

            string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
            if (IsPlaceholder(selectedBranch)) return;

            if (!await CanPerformSwitch()) return;
            await ExecuteUnifiedSwitch(selectedBranch, "branches");
        }

        public async void SwitchToSelectedTag()
        {
            if (IsProcessing) return;
            if (svnUI.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;

            string selectedTag = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
            if (IsPlaceholder(selectedTag)) return;

            if (!await CanPerformSwitch()) return;
            await ExecuteUnifiedSwitch(selectedTag, "tags");
        }

        private async Task ExecuteUnifiedSwitch(string targetName, string subFolder)
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                LogToPanel($"<b><color=#4FC3F7>[Switch]</color> Switching to {targetName}...</b>", false);

                string repoRoot = svnManager.GetRepoRoot();
                string targetUrl = (targetName.ToLower() == "trunk")
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{targetName}";

                LogToPanel($"<color=#444444>... Target: {targetUrl}</color>");

                string result = await SwitchAsync(svnManager.WorkingDir, targetUrl);

                if (!result.ToLower().Contains("error") && !result.ToLower().Contains("failed"))
                {
                    LogToPanel($"<b><color=green>[Switch Complete]</color> Now on {targetName}.</b>");
                    svnManager.GetModule<SVNStatus>().ShowProjectInfo(null, svnManager.WorkingDir);
                    await svnManager.RefreshStatus();
                }
                else
                {
                    LogToPanel($"<color=red>[Switch Failed]</color>\n{result}");
                }
            }
            catch (Exception ex)
            {
                LogToPanel($"<color=red>[Switch Error]</color> {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        private async Task<bool> CanPerformSwitch()
        {
            LogToPanel("<color=#444444>... Validating safety</color>");
            var stats = await GetStatsAsync(svnManager.WorkingDir);

            if (stats.ConflictsCount > 0)
            {
                LogToPanel("<color=red><b>ERROR:</b> Unresolved conflicts!</color>");
                return false;
            }

            if (stats.ModifiedCount > 0 || stats.AddedCount > 0 || stats.DeletedCount > 0)
            {
                LogToPanel("<color=yellow>[Warning]</color> Uncommitted changes detected.");
            }

            return true;
        }

        public async void DeleteSelectedBranch()
        {
            if (IsProcessing) return;
            if (svnUI.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) return;

            string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
            if (IsPlaceholder(selectedBranch)) return;

            if (selectedBranch.ToLower().Contains("trunk"))
            {
                LogToPanel("<color=red><b>SECURITY:</b> Cannot delete Trunk!</color>");
                return;
            }

            await ExecuteRemoteDeleteTask(selectedBranch, "branches");
        }

        public async void DeleteSelectedTag()
        {
            if (IsProcessing) return;
            if (svnUI.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;

            string selectedTag = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
            if (IsPlaceholder(selectedTag)) return;

            await ExecuteRemoteDeleteTask(selectedTag, "tags");
        }

        private async Task ExecuteRemoteDeleteTask(string targetName, string subFolder)
        {
            IsProcessing = true;
            try
            {
                LogToPanel($"<b><color=#4FC3F7>[Delete]</color> Deleting {subFolder}: {targetName}</b>", false);

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string repoRoot = svnManager.GetRepoRoot();
                string targetUrl = $"{repoRoot}/{subFolder}/{targetName}";

                if (currentUrl.TrimEnd('/') == targetUrl.TrimEnd('/'))
                {
                    LogToPanel("<color=red><b>ABORTED:</b> Active branch cannot be deleted!</color>");
                    return;
                }

                string msg = $"Deleted {subFolder}: {targetName} via Unity SVN Tool";
                await DeleteRemotePathAsync(svnManager.WorkingDir, targetUrl, msg);

                LogToPanel($"<b><color=green>[Success]</color> {targetName} removed from server.</b>");
                RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                LogToPanel($"<color=red>[Delete Error]</color> {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status", workingDir);
            SvnStats stats = new SvnStats();
            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                if (line.Length < 1) continue;
                char statusChar = line[0];
                switch (statusChar)
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

        private bool IsPlaceholder(string text) => text.Contains("Loading") || text.Contains("No ") || text.Contains("None");

        private void UpdateDropdown(TMPro.TMP_Dropdown dropdown, string[] items, string emptyMsg, bool includeTrunk)
        {
            if (dropdown == null) return;
            dropdown.Hide();
            dropdown.ClearOptions();
            List<string> options = new List<string>();

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
            if (!string.IsNullOrEmpty(currentKey)) sshArgs = $"-i \"{currentKey}\" {sshArgs}";

            string command = $"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" " +
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
    }
}