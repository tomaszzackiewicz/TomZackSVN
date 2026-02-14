using System;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase
    {
        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void CreateRemoteCopy()
        {
            if (IsProcessing) return;

            string name = svnUI.BranchNameInput.text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                svnUI.LogText.text += "<color=red>[Error]</color> Please enter a valid name for the new copy.\n";
                return;
            }

            string subFolder = (svnUI.TypeSelector.value == 0) ? "branches" : "tags";
            IsProcessing = true;

            try
            {
                svnUI.LogText.text += $"<b><color=#4FC3F7>[Remote Copy]</color> Initializing creation of {subFolder}...</b>\n";

                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string sourceUrl = $"{repoRoot}/trunk";
                string targetUrl = $"{repoRoot}/{subFolder}/{name}";

                svnUI.LogText.text += $"<color=#444444>... Source: {sourceUrl}</color>\n";
                svnUI.LogText.text += $"<color=#444444>... Target: {targetUrl}</color>\n";
                svnUI.LogText.text += "<color=#444444>... Performing remote SVN Copy operation (please wait)</color>\n";

                await CopyAsync(svnManager.WorkingDir, sourceUrl, targetUrl, $"Created {subFolder}/{name}");

                svnUI.LogText.text += $"<b><color=green>[Success]</color> Remote {subFolder} '{name}' created successfully!</b>\n";

                RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>[Create Error]</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }

        public async void RefreshUnifiedList()
        {
            if (svnUI == null || svnUI.BranchesDropdown == null) return;

            svnUI.LogText.text += "<b><color=#4FC3F7>[Refresh]</color> Updating Branch and Tag lists from server...</b>\n";
            svnUI.BranchesDropdown.Hide();
            svnUI.BranchesDropdown.ClearOptions();

            try
            {
                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string branchesUrl = $"{repoRoot}/branches";
                string tagsUrl = $"{repoRoot}/tags";

                svnUI.LogText.text += "<color=#444444>... Fetching folder entries from repository root</color>\n";
                Debug.Log($"[SVN] Fetching from: {branchesUrl}");

                var branches = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, branchesUrl);
                var tags = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, tagsUrl);

                UpdateDropdown(svnUI.BranchesDropdown, branches, "No branches", true);
                UpdateDropdown(svnUI.TagsDropdown, tags, "No tags", false);

                svnUI.LogText.text += "<b><color=green>[Refresh Complete]</color> UI Dropdowns synchronized with server.</b>\n";
            }
            catch (Exception ex)
            {
                Debug.LogError($"Refresh error: {ex.Message}");
                svnUI.LogText.text += $"<color=red>[Refresh Error]</color> {ex.Message}\n";
                UpdateDropdown(svnUI.BranchesDropdown, null, "Error", true);
            }
        }

        private string GetRepoRoot(string currentUrl)
        {
            currentUrl = currentUrl.TrimEnd('/');
            if (currentUrl.EndsWith("/trunk")) return currentUrl.Replace("/trunk", "");
            if (currentUrl.Contains("/branches/")) return currentUrl.Substring(0, currentUrl.IndexOf("/branches/"));
            if (currentUrl.Contains("/tags/")) return currentUrl.Substring(0, currentUrl.IndexOf("/tags/"));

            return currentUrl.Substring(0, currentUrl.LastIndexOf('/'));
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

        private async System.Threading.Tasks.Task ExecuteUnifiedSwitch(string targetName, string subFolder)
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                svnUI.LogText.text += $"<b><color=#4FC3F7>[Switch]</color> Preparing switch to {subFolder}: {targetName}</b>\n";

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string repoRoot = svnManager.GetRepoRoot();

                string targetUrl = (targetName.ToLower() == "trunk")
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/{subFolder}/{targetName}";

                svnUI.LogText.text += $"<color=#444444>... Destination URL: {targetUrl}</color>\n";
                svnUI.LogText.text += "<color=#444444>... Updating local working copy files (please wait)</color>\n";

                string result = await SwitchAsync(svnManager.WorkingDir, targetUrl);

                if (!result.ToLower().Contains("error") && !result.ToLower().Contains("failed"))
                {
                    svnUI.LogText.text += $"<b><color=green>[Switch Complete]</color> Working copy is now on {targetName}.</b>\n";

                    svnManager.GetModule<SVNStatus>().ShowProjectInfo(null, svnManager.WorkingDir);
                    await svnManager.RefreshStatus();
                }
                else
                {
                    svnUI.LogText.text += $"<color=red>[Switch Failed]</color>\n{result}\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>[Switch Error]</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }

        private async System.Threading.Tasks.Task<bool> CanPerformSwitch()
        {
            svnUI.LogText.text += "<color=#444444>... Checking working copy safety</color>\n";
            var stats = await GetStatsAsync(svnManager.WorkingDir);

            if (stats.ConflictsCount > 0)
            {
                svnUI.LogText.text += "<color=red><b>CRITICAL ERROR:</b> Unresolved conflicts detected! You must resolve them before switching branches.</color>\n";
                return false;
            }

            if (stats.ModifiedCount > 0 || stats.AddedCount > 0 || stats.DeletedCount > 0)
            {
                svnUI.LogText.text += "<color=yellow>[Warning]</color> Uncommitted changes detected. SVN will attempt to merge them into the target branch.\n";
            }

            return true;
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status", workingDir);

            SvnStats stats = new SvnStats();
            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                if (line.Length < 8) continue;
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

        private bool IsPlaceholder(string text)
        {
            return text.Contains("Loading") || text.Contains("No ") || text.Contains("None");
        }

        public async void DeleteSelectedBranch()
        {
            if (IsProcessing) return;

            if (svnUI.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) return;

            string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
            if (IsPlaceholder(selectedBranch)) return;

            if (selectedBranch.ToLower().Contains("trunk"))
            {
                svnUI.LogText.text += "<color=red><b>SECURITY DENIED:</b> The Trunk branch cannot be deleted via this interface!</color>\n";
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

        private async System.Threading.Tasks.Task ExecuteRemoteDeleteTask(string targetName, string subFolder)
        {
            IsProcessing = true;
            try
            {
                svnUI.LogText.text += $"<b><color=#4FC3F7>[Delete]</color> Initializing remote deletion of {subFolder}: {targetName}</b>\n";

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string projectName = GetProjectNameFromUrl(currentUrl);

                string rootWithProject = currentUrl.Split(new[] { projectName }, StringSplitOptions.None)[0] + projectName;
                string targetUrl = $"{rootWithProject}/{subFolder}/{targetName}";

                if (currentUrl.TrimEnd('/') == targetUrl.TrimEnd('/'))
                {
                    svnUI.LogText.text += "<color=red><b>ABORTED:</b> Cannot delete the active branch/tag you are currently working on!</color>\n";
                    return;
                }

                svnUI.LogText.text += $"<color=#444444>... Removing remote path: {targetUrl}</color>\n";

                string msg = $"Deleted {subFolder}: {targetName} via Unity SVN Tool";
                await DeleteRemotePathAsync(svnManager.WorkingDir, targetUrl, msg);

                svnUI.LogText.text += $"<b><color=green>[Success]</color> Remote {subFolder} '{targetName}' has been removed.</b>\n";
                RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>[Delete Error]</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }

        private string GetProjectNameFromUrl(string url)
        {
            url = url.TrimEnd('/');
            string key = "/repos/";
            if (!url.Contains(key)) return url.Split('/').Last();

            string relativePath = url.Substring(url.IndexOf(key) + key.Length);
            return relativePath.Split('/')[0];
        }

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

            if (!string.IsNullOrEmpty(currentKey))
            {
                sshArgs = $"-i \"{currentKey}\" {sshArgs}";
            }

            // Używamy formatu --config-option dla tunelu SSH
            string command = $"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" " +
                             $"switch \"{targetUrl}\" \"{workingDir}\" " +
                             $"--ignore-ancestry --accept theirs-full --non-interactive";

            UnityEngine.Debug.Log($"[SvnCommands] Executing Switch to: {targetUrl}");
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