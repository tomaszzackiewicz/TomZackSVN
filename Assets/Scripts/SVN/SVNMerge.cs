using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using System.Collections.Generic;

namespace SVN.Core
{
    public class SVNMerge : SVNBase
    {
        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void CompareWithTrunk()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            svnUI.LogText.text += "<b><color=#4FC3F7>[Comparison]</color> Starting analysis against Trunk...</b>\n";

            try
            {
                string repoRoot = svnManager.GetRepoRoot();
                if (string.IsNullOrEmpty(repoRoot)) throw new Exception("Repo Root not found. Please refresh status.");

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";

                svnUI.LogText.text += $"<i>Target: {trunkUrl}</i>\n";

                if (currentUrl.TrimEnd('/') == trunkUrl)
                {
                    svnUI.LogText.text += "<color=yellow>[Info]</color> You are already on Trunk. Comparison skipped.\n";
                    return;
                }

                svnUI.LogText.text += "<color=#444444>... Fetching eligible revisions from Trunk</color>\n";
                string missingCmd = $"mergeinfo \"{trunkUrl}\" --show-revs eligible";
                string missingInBranch = await SvnRunner.RunAsync(missingCmd, svnManager.WorkingDir);

                svnUI.LogText.text += "<color=#444444>... Analyzing local branch changes</color>\n";
                string localCmd = $"mergeinfo . \"{trunkUrl}\" --show-revs eligible";
                string branchOnlyChanges = await SvnRunner.RunAsync(localCmd, svnManager.WorkingDir);

                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);

                svnUI.LogText.text += "--------------------------------------\n";
                svnUI.LogText.text += $"<b>Merge Status:</b>\n";
                svnUI.LogText.text += $" • <color=#FFD700>Incoming (Trunk -> Branch):</color> {missingCount} new commit(s)\n";
                svnUI.LogText.text += $" • <color=#00FF00>Outgoing (Branch -> Trunk):</color> {localCount} local commit(s)\n";

                if (missingCount > 0)
                    svnUI.LogText.text += "<color=orange>Recommendation: Sync your branch with Trunk before continuing.</color>\n";
                else
                    svnUI.LogText.text += "<color=green>Success: Your branch is fully synchronized with Trunk.</color>\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>[Error]</color> Comparison failed: {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }

        public async void ExecuteMerge(string sourceInput, bool isDryRun)
        {
            if (IsProcessing) return;
            if (string.IsNullOrWhiteSpace(sourceInput)) return;

            IsProcessing = true;
            string sourceUrl = "";

            try
            {
                svnUI.LogText.text += "<b><color=#4FC3F7>[Merge]</color> Initializing process...</b>\n";

                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                svnUI.LogText.text += "<color=#444444>... Resolving repository paths</color>\n";

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                currentUrl = currentUrl.TrimEnd('/');

                if (sourceInput.Contains("://"))
                {
                    sourceUrl = sourceInput;
                }
                else if (sourceInput.ToLower() == "trunk")
                {
                    sourceUrl = $"{repoRoot}/trunk";
                }
                else
                {
                    sourceUrl = $"{repoRoot}/branches/{sourceInput}";
                }

                if (sourceUrl.TrimEnd('/') == currentUrl)
                {
                    svnUI.LogText.text += "<color=red>[Aborted]</color> Cannot merge a branch into itself.\n";
                    return;
                }

                string targetName = currentUrl.EndsWith("/trunk") ? "<color=#FFD700>Trunk</color>" : $"branch <color=#4FC3F7>{currentUrl.Split('/').Last()}</color>";
                string sourceName = sourceUrl.EndsWith("/trunk") ? "<color=#FFD700>Trunk</color>" : $"branch <color=#4FC3F7>{sourceUrl.Split('/').Last()}</color>";

                string modeText = isDryRun ? "<color=yellow>[SIMULATION]</color>" : "<color=orange>[LIVE MERGE]</color>";
                svnUI.LogText.text += $"{modeText} Source: {sourceName} —> Target: {targetName}\n";
                svnUI.LogText.text += "<color=#444444>... Executing SVN command (please wait)</color>\n";

                string args = $"merge \"{sourceUrl}\" --non-interactive --accept postpone --force";
                if (isDryRun) args += " --dry-run";

                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                ParseMergeOutput(output, isDryRun);

                if (!isDryRun)
                {
                    svnUI.LogText.text += "<color=#444444>... Refreshing local status</color>\n";
                    await svnManager.RefreshStatus();
                    svnUI.LogText.text += "<b><color=green>[Complete]</color> Changes merged locally. Verify and Commit to server.</b>\n";
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("E200004") || ex.Message.Contains("ancestry"))
                {
                    svnUI.LogText.text += "<color=yellow>[Notice]</color> Ancestry check failed. Attempting force-merge (--ignore-ancestry)...\n";
                    try
                    {
                        string forceArgs = $"merge \"{sourceUrl}\" . --ignore-ancestry --non-interactive --accept postpone";
                        if (isDryRun) forceArgs += " --dry-run";

                        string output = await SvnRunner.RunAsync(forceArgs, svnManager.WorkingDir);
                        ParseMergeOutput(output, isDryRun);
                        svnUI.LogText.text += "<b><color=green>[Complete]</color> Force-merge finished.</b>\n";
                    }
                    catch (Exception ex2)
                    {
                        svnUI.LogText.text += $"<color=red>[Critical Error]</color> Force-merge also failed: {ex2.Message}\n";
                    }
                }
                else
                {
                    svnUI.LogText.text += $"<color=red>[Error]</color> Merge process interrupted: {ex.Message}\n";
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void ParseMergeOutput(string output, bool isDryRun)
        {
            if (string.IsNullOrWhiteSpace(output) || output.ToLower().Contains("already up to date"))
            {
                svnUI.LogText.text += "Result: <color=green>Everything is already up to date.</color>\n";
                return;
            }

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int conflicts = lines.Count(l => l.Length >= 1 && (l[0] == 'C' || l.StartsWith("   C")));
            int updated = lines.Count(l => l.Length >= 1 && "UADG".Contains(l[0]));

            if (isDryRun)
            {
                svnUI.LogText.text += $"<color=cyan>[Simulation Result]</color> Files to update: {updated}, Potential conflicts: {conflicts}\n";
            }
            else
            {
                svnUI.LogText.text += $"<color=green>[Merge Result]</color> Successfully processed {updated} items.\n";
                if (conflicts > 0)
                {
                    svnUI.LogText.text += $"<color=red><b>[WARNING] {conflicts} CONFLICTS DETECTED!</b></color>\n";
                    svnUI.LogText.text += "Please use External Merge Tool to resolve files marked with 'C'.\n";
                }
                svnUI.LogText.text += "<size=80%>" + output + "</size>\n";
            }
        }

        private int CountRevisions(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return 0;
            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                         .Count(line => !string.IsNullOrWhiteSpace(line));
        }

        public async Task RevertMerge()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                svnUI.LogText.text += "<b><color=yellow>[Rollback]</color> Starting Revert process...</b>\n";
                svnUI.LogText.text += "<color=#444444>... Cleaning up all local modifications</color>\n";

                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir);

                svnUI.LogText.text += "<color=#444444>... Synchronizing status</color>\n";
                await svnManager.RefreshStatus();

                svnUI.LogText.text += "<color=green>[Success]</color> Local workspace is now clean. All merge changes discarded.\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>[Error]</color> Revert failed: {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }

        public async Task<string[]> FetchAvailableBranches()
        {
            try
            {
                svnUI.LogText.text += "<color=#444444>... Scanning repository for branches</color>\n";

                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                if (string.IsNullOrEmpty(repoRoot))
                {
                    Debug.LogWarning("[SVN] Cannot fetch branches: Repo Root is empty.");
                    return new string[0];
                }

                var branches = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, "branches");

                if (branches == null || branches.Length == 0)
                {
                    svnUI.LogText.text += "<color=yellow>[Info]</color> No branches found on server.\n";
                    return new string[0];
                }

                svnUI.LogText.text += $"<color=green>[Success]</color> Found {branches.Length} branch(es) available for merge.\n";
                return branches;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SVN Merge] FetchAvailableBranches failed: {ex.Message}");
                svnUI.LogText.text += $"<color=red>[Error]</color> Could not retrieve branches list: {ex.Message}\n";
                return new string[0];
            }
        }
    }
}