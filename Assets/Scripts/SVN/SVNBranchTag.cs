using System;
using UnityEngine;
using System.Linq;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase
    {
        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void CreateRemoteCopy()
        {
            if (IsProcessing) return;

            string name = svnUI.BranchNameInput.text.Trim();
            string message = string.IsNullOrWhiteSpace(svnUI.BranchCommitMsgInput.text)
                ? $"Created {name} via Unity SVN Tool"
                : svnUI.BranchCommitMsgInput.text;

            string subFolder = (svnUI.TypeSelector.value == 0) ? "branches" : "tags";

            if (string.IsNullOrEmpty(name))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Name cannot be empty!\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text += $"Checking repository structure...\n";

            try
            {
                // 1. Get current URL and determine Base URL
                string repoUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                repoUrl = repoUrl.TrimEnd('/');

                string baseUrl = "";
                if (repoUrl.Contains("/trunk")) baseUrl = repoUrl.Replace("/trunk", "");
                else if (repoUrl.Contains("/branches/")) baseUrl = repoUrl.Substring(0, repoUrl.IndexOf("/branches/"));
                else if (repoUrl.Contains("/tags/")) baseUrl = repoUrl.Substring(0, repoUrl.IndexOf("/tags/"));
                else baseUrl = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));

                string targetUrl = $"{baseUrl}/{subFolder}/{name}";

                // 2. CHECK IF TARGET EXISTS (The "Is it already there?" safety check)
                // We use 'svn list' on the target URL. If it doesn't throw an error, it means it exists.
                bool exists = await SvnRunner.RemotePathExistsAsync(svnManager.WorkingDir, targetUrl);

                if (exists)
                {
                    svnUI.LogText.text += $"<color=orange>Aborted:</color> The {subFolder} '<color=white>{name}</color>' already exists on the server.\n";
                    return;
                }

                svnUI.LogText.text += $"Creating {subFolder}: <color=cyan>{name}</color>...\n";

                // 3. Execute Remote Copy
                await SvnRunner.CopyAsync(svnManager.WorkingDir, repoUrl, targetUrl, message);

                svnUI.LogText.text += $"<color=green>Success!</color> Created new {subFolder} entry.\n";

                RefreshUnifiedList();
                svnUI.BranchNameInput.text = "";
                svnUI.BranchCommitMsgInput.text = "";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Creation Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void SwitchToSelectedBranch()
        {
            if (IsProcessing) return;

            // 1. Validate selection
            if (svnUI.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) return;

            string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
            if (IsPlaceholder(selectedBranch)) return;

            // 2. Pre-check status (Safety first)
            if (!await CanPerformSwitch()) return;

            // 3. Execute
            await ExecuteSwitchTask(selectedBranch, "branches");
        }

        public async void SwitchToSelectedTag()
        {
            if (IsProcessing) return;

            // 1. Validate selection
            if (svnUI.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;

            string selectedTag = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
            if (IsPlaceholder(selectedTag)) return;

            // 2. Pre-check status (Safety first)
            if (!await CanPerformSwitch()) return;

            // 3. Execute
            await ExecuteSwitchTask(selectedTag, "tags");
        }

        private async System.Threading.Tasks.Task<bool> CanPerformSwitch()
        {
            var stats = await SvnRunner.GetStatsAsync(svnManager.WorkingDir);

            if (stats.ConflictsCount > 0)
            {
                svnUI.LogText.text += "<color=red><b>CRITICAL:</b> Unresolved conflicts! Resolve them before switching.</color>\n";
                return false;
            }

            if (stats.ModifiedCount > 0 || stats.AddedCount > 0 || stats.DeletedCount > 0)
            {
                svnUI.LogText.text += "<color=orange><b>WARNING:</b> Uncommitted changes detected. Proceeding with switch...</color>\n";
            }

            return true;
        }

        private async System.Threading.Tasks.Task ExecuteSwitchTask(string targetName, string subFolder)
        {
            IsProcessing = true;
            svnUI.LogText.text += $"Switching to {subFolder.ToUpper()}: <color=#4FC3F7>{targetName}</color>...\n";

            try
            {
                // Build URL
                string repoUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string baseUrl = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));
                string targetUrl = $"{baseUrl}/{subFolder}/{targetName}";

                // Run SVN Switch
                await SvnRunner.SwitchAsync(svnManager.WorkingDir, targetUrl);

                svnUI.LogText.text += $"<color=green>SUCCESS:</color> Switched to {targetName}.\n";

                // Refresh UI
                svnManager.RefreshStatus();
                svnManager.UpdateBranchInfo();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Switch Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private bool IsPlaceholder(string text)
        {
            return text.Contains("Loading") || text.Contains("No ") || text.Contains("None");
        }

        public async void DeleteSelectedBranch()
        {
            if (IsProcessing) return;

            // 1. Get selection from the Branch dropdown
            if (svnUI.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) return;

            string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
            if (IsPlaceholder(selectedBranch)) return;

            // 2. CRITICAL SAFETY: Prevent trunk deletion via this button
            if (selectedBranch.ToLower().Contains("trunk"))
            {
                svnUI.LogText.text += "<color=red><b>SECURITY ERROR:</b> Trunk cannot be deleted from this panel!</color>\n";
                return;
            }

            // 3. Confirm and Execute
            await ExecuteRemoteDeleteTask(selectedBranch, "branches");
        }

        public async void DeleteSelectedTag()
        {
            if (IsProcessing) return;

            // 1. Get selection from the Tag dropdown
            if (svnUI.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;

            string selectedTag = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
            if (IsPlaceholder(selectedTag)) return;

            // 2. Confirm and Execute
            await ExecuteRemoteDeleteTask(selectedTag, "tags");
        }

        private async System.Threading.Tasks.Task ExecuteRemoteDeleteTask(string targetName, string subFolder)
        {
            string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);

            string repoRoot = currentUrl.Substring(0, currentUrl.LastIndexOf('/'));
            string targetUrl = $"{repoRoot}/{subFolder}/{targetName}";

            if (targetUrl.ToLower().EndsWith("/trunk"))
            {
                svnUI.LogText.text += "<color=red><b>DENIED:</b> You cannot delete the Trunk!</color>\n";
                return;
            }

            if (currentUrl.TrimEnd('/') == targetUrl.TrimEnd('/'))
            {
                svnUI.LogText.text += "<color=red><b>DENIED:</b> You cannot delete the branch/tag you are currently using!</color>\n";
                return;
            }

            IsProcessing = true;
            try
            {
                string msg = $"Deleted {subFolder}: {targetName} via Unity SVN Tool";
                await SvnRunner.DeleteRemotePathAsync(svnManager.WorkingDir, targetUrl, msg);
                svnUI.LogText.text += $"<color=green>Deleted {subFolder}: {targetName}</color>\n";
                RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Error:</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }



        public async void Button_SmartSwitch()
        {
            if (IsProcessing) return;

            // 1. Working Copy Validation
            var stats = await SvnRunner.GetStatsAsync(svnManager.WorkingDir);

            // Check for critical conflicts
            if (stats.ConflictsCount > 0)
            {
                svnUI.LogText.text += "<color=red><b>CRITICAL:</b> Unresolved conflicts detected. Resolve them before switching!</color>\n";
                return;
            }

            // Check for uncommitted work
            if (stats.ModifiedCount > 0 || stats.AddedCount > 0 || stats.DeletedCount > 0)
            {
                svnUI.LogText.text += "<color=orange><b>WARNING:</b> You have uncommitted changes. Switching might lead to complex merges.</color>\n";
            }

            // 2. Determine target based on UI Selection (Dropdowns for Branch vs Tag)
            // You can use a Toggle or a Tab to decide which dropdown to read from
            bool isTagSelected = svnUI.TypeSelector.value == 1; // 0 = Branch, 1 = Tag
            var activeDropdown = isTagSelected ? svnUI.TagsDropdown : svnUI.BranchesDropdown;
            string subFolder = isTagSelected ? "tags" : "branches";

            if (activeDropdown == null || activeDropdown.options.Count == 0) return;

            string selectedName = activeDropdown.options[activeDropdown.value].text;

            // Safety check for placeholder values
            if (selectedName.Contains("None") || selectedName.Contains("Loading") || selectedName.Contains("No "))
            {
                svnUI.LogText.text += "<color=yellow>System:</color> Please select a valid Branch or Tag from the list.\n";
                return;
            }

            // 3. Execute the switch
            ExecuteUnifiedSwitch(selectedName, subFolder);
        }

        private async void ExecuteUnifiedSwitch(string targetName, string subFolder)
        {
            IsProcessing = true;
            svnUI.LogText.text += $"Switching working copy to {subFolder.ToUpper()}: <color=#4FC3F7>{targetName}</color>...\n";

            try
            {
                // Get Repo Root (to build the full URL)
                string repoUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string baseUrl = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));

                // Final URL: e.g., https://server/repo/branches/feature-x or /tags/v1.0
                string targetUrl = $"{baseUrl}/{subFolder}/{targetName}";

                // Execute SVN Switch command
                string output = await SvnRunner.SwitchAsync(svnManager.WorkingDir, targetUrl);

                svnUI.LogText.text += $"<color=green>Switch successful!</color> You are now on {subFolder}: <b>{targetName}</b>\n";

                // Refresh the whole UI to reflect new file states
                svnManager.RefreshStatus();
                svnManager.UpdateBranchInfo(); // Update the "Current Branch" display
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Switch Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        

        public void Button_DeleteBranch()
        {
            DeleteRemoteCopy(false);
        }

        public void Button_DeleteTag()
        {
            DeleteRemoteCopy(true);
        }

        private async void DeleteRemoteCopy(bool isTag)
        {
            var dropdown = isTag ? svnUI.TagsDropdown : svnUI.BranchesDropdown;
            string subFolder = isTag ? "tags" : "branches";
            string typeLabel = isTag ? "Tag" : "Branch";

            if (dropdown == null || dropdown.options.Count == 0) return;

            string selectedName = dropdown.options[dropdown.value].text;

            // Safety check for placeholder strings
            if (selectedName.Contains("None") || selectedName.Contains("Loading")) return;

            // CRITICAL SAFETY: Prevent trunk deletion
            if (selectedName.ToLower().Contains("trunk"))
            {
                svnUI.LogText.text += "<color=red><b>ERROR:</b> 'trunk' deletion is strictly prohibited!</color>\n";
                return;
            }

            IsProcessing = true;
            //loadingOverlay?.SetActive(true);

            try
            {
                string repoUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string baseUrl = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));
                string fullUrl = $"{baseUrl}/{subFolder}/{selectedName}";

                // Secondary URL verification
                if (fullUrl.EndsWith("/trunk") || fullUrl.Contains("/trunk/"))
                {
                    svnUI.LogText.text += "<color=red><b>CRITICAL:</b> Trunk URL detected in deletion path. Aborting.</color>\n";
                    return;
                }

                string msg = $"Deleted {typeLabel}: {selectedName} via Unity SVN Tool";
                await SvnRunner.DeleteRemotePathAsync(svnManager.WorkingDir, fullUrl, msg);

                svnUI.LogText.text += $"<color=green>Successfully deleted {typeLabel}: {selectedName}</color>\n";

                // Refresh both lists to reflect changes
                RefreshUnifiedList();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Deletion Error:</color> {ex.Message}\n";
            }
            finally
            {
                //loadingOverlay?.SetActive(false);
                IsProcessing = false;
            }
        }

        public async void RefreshUnifiedList()
        {
            if (svnUI.BranchesDropdown == null || svnUI.TagsDropdown == null) return;

            svnUI.BranchesDropdown.ClearOptions();
            svnUI.TagsDropdown.ClearOptions();

            svnUI.BranchesDropdown.options.Add(new TMPro.TMP_Dropdown.OptionData("Loading branches..."));
            svnUI.TagsDropdown.options.Add(new TMPro.TMP_Dropdown.OptionData("Loading tags..."));

            try
            {
                var branches = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, "branches");
                var tags = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, "tags");

                svnUI.BranchesDropdown.ClearOptions();
                svnUI.TagsDropdown.ClearOptions();

                if (branches.Length > 0) svnUI.BranchesDropdown.AddOptions(branches.ToList());
                else svnUI.BranchesDropdown.options.Add(new TMPro.TMP_Dropdown.OptionData("No branches found"));

                if (tags.Length > 0) svnUI.TagsDropdown.AddOptions(tags.ToList());
                else svnUI.TagsDropdown.options.Add(new TMPro.TMP_Dropdown.OptionData("No tags found"));
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>List Refresh Error:</color> {ex.Message}\n";
            }

            svnUI.BranchesDropdown.RefreshShownValue();
            svnUI.TagsDropdown.RefreshShownValue();
        }

        public async void Button_CreateTag()
        {
            if (IsProcessing) return;

            string tagName = svnUI.BranchNameInput.text;
            string baseUrl = svnManager.RepositoryUrl;

            if (string.IsNullOrWhiteSpace(tagName))
            {
                svnUI.LogText.text += "<color=red>Error: Provide a tag name!</color>\n";
                return;
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                svnUI.LogText.text += "<color=red>Error: Missing repository URL!</color>\n";
                return;
            }

            IsProcessing = true;
            svnUI.LogText.text = $"Creating tag (snapshot): {tagName}...\n";

            try
            {
                baseUrl = baseUrl.TrimEnd('/');
                string rootUrl = baseUrl.Contains("/") ? baseUrl.Substring(0, baseUrl.LastIndexOf('/')) : baseUrl;
                string destUrl = $"{rootUrl}/tags/{tagName}";

                svnUI.LogText.text += $"Copying from: {baseUrl}\nTo: {destUrl}\n";

                string output = await SvnRunner.CopyAsync(svnManager.RepositoryUrl, baseUrl, destUrl, $"Release Tag: {tagName}");

                svnUI.LogText.text += $"<color=green>Success!</color> Tag {tagName} has been created.\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Tagging Error:</color> {ex.Message}\n";
            }

            IsProcessing = false;
        }

        public async void Button_SwitchToBranch()
        {
            if (IsProcessing) return;

            string branchName = svnUI.BranchNameInput.text;
            string baseUrl = svnManager.RepositoryUrl;

            if (string.IsNullOrWhiteSpace(branchName))
            {
                svnUI.LogText.text += "<color=red>Error: Provide a branch name or full URL!</color>\n";
                return;
            }

            IsProcessing = true;
            //loadingOverlay?.SetActive(true);
            svnUI.LogText.text = "Starting Switch operation...\n";

            try
            {
                string rootPath = svnManager.RepositoryUrl;
                string targetUrl;

                // 1. Target address determination logic
                if (branchName.Contains("://"))
                {
                    // If the user pasted a full URL
                    targetUrl = branchName;
                }
                else
                {
                    // If they only provided a name, build the URL based on baseUrl (Trunk)
                    string cleanBase = baseUrl.TrimEnd('/');
                    string repoRoot = cleanBase.Contains("/") ? cleanBase.Substring(0, cleanBase.LastIndexOf('/')) : cleanBase;
                    targetUrl = $"{repoRoot}/branches/{branchName}";
                }

                svnUI.LogText.text += $"Switching working folder to:\n<color=#4FC3F7>{targetUrl}</color>\n";

                // 2. Call SvnRunner.SwitchAsync
                string output = await SvnRunner.SwitchAsync(rootPath, targetUrl);

                svnUI.LogText.text += "<color=green>Switched successfully!</color>\n";

                // 3. After switching, we must refresh the tree as files on disk have changed
                svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Switch Error:</color> {ex.Message}\n";
            }

            // Update current branch info in the UI
            svnManager.UpdateBranchInfo();

            //loadingOverlay?.SetActive(false);
            IsProcessing = false;
        }

    }
}
