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

            SVNLogBridge.LogLine("<b><color=#4FC3F7>[Comparison]</color> Starting analysis against Trunk...</b>", append: false);

            try
            {
                string repoRoot = svnManager.GetRepoRoot();
                if (string.IsNullOrEmpty(repoRoot)) throw new Exception("Repo Root not found. Please refresh status.");

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";

                SVNLogBridge.LogLine($"<i>Target: {trunkUrl}</i>");

                if (currentUrl.TrimEnd('/') == trunkUrl)
                {
                    SVNLogBridge.LogLine("<color=yellow>[Info]</color> You are already on Trunk. Comparison skipped.");
                    return;
                }

                SVNLogBridge.LogLine("<color=#444444>... Fetching eligible revisions from Trunk</color>");
                string missingCmd = $"mergeinfo \"{trunkUrl}\" --show-revs eligible";
                string missingInBranch = await SvnRunner.RunAsync(missingCmd, svnManager.WorkingDir);

                SVNLogBridge.LogLine("<color=#444444>... Analyzing local branch changes</color>");
                string localCmd = $"mergeinfo . \"{trunkUrl}\" --show-revs eligible";
                string branchOnlyChanges = await SvnRunner.RunAsync(localCmd, svnManager.WorkingDir);

                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);

                SVNLogBridge.LogLine("--------------------------------------");
                SVNLogBridge.LogLine("<b>Merge Status:</b>");
                SVNLogBridge.LogLine($" • <color=#FFD700>Incoming (Trunk -> Branch):</color> {missingCount} new commit(s)");
                SVNLogBridge.LogLine($" • <color=#00FF00>Outgoing (Branch -> Trunk):</color> {localCount} local commit(s)");

                if (missingCount > 0)
                    SVNLogBridge.LogLine("<color=orange>Recommendation: Sync your branch with Trunk before continuing.</color>");
                else
                    SVNLogBridge.LogLine("<color=green>Success: Your branch is fully synchronized with Trunk.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>[Error]</color> Comparison failed: {ex.Message}");
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
                SVNLogBridge.LogLine("<b><color=#4FC3F7>[Merge]</color> Initializing process...</b>", append: false);

                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                SVNLogBridge.LogLine("<color=#444444>... Resolving repository paths</color>");

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
                    SVNLogBridge.LogLine("<color=red>[Aborted]</color> Cannot merge a branch into itself.");
                    return;
                }

                string targetName = currentUrl.EndsWith("/trunk") ? "<color=#FFD700>Trunk</color>" : $"branch <color=#4FC3F7>{currentUrl.Split('/').Last()}</color>";
                string sourceName = sourceUrl.EndsWith("/trunk") ? "<color=#FFD700>Trunk</color>" : $"branch <color=#4FC3F7>{sourceUrl.Split('/').Last()}</color>";

                string modeText = isDryRun ? "<color=yellow>[SIMULATION]</color>" : "<color=orange>[LIVE MERGE]</color>";
                SVNLogBridge.LogLine($"{modeText} Source: {sourceName} —> Target: {targetName}");
                SVNLogBridge.LogLine("<color=#444444>... Executing SVN command (please wait)</color>");

                string args = $"merge \"{sourceUrl}\" --non-interactive --accept postpone --force";
                if (isDryRun) args += " --dry-run";

                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                ParseMergeOutput(output, isDryRun);

                if (!isDryRun)
                {
                    SVNLogBridge.LogLine("<color=#444444>... Refreshing local status</color>");
                    await svnManager.RefreshStatus();
                    SVNLogBridge.LogLine("<b><color=green>[Complete]</color> Changes merged locally. Verify and Commit to server.</b>");
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("E200004") || ex.Message.Contains("ancestry"))
                {
                    SVNLogBridge.LogLine("<color=yellow>[Notice]</color> Ancestry check failed. Attempting force-merge (--ignore-ancestry)...");
                    try
                    {
                        string forceArgs = $"merge \"{sourceUrl}\" . --ignore-ancestry --non-interactive --accept postpone";
                        if (isDryRun) forceArgs += " --dry-run";

                        string output = await SvnRunner.RunAsync(forceArgs, svnManager.WorkingDir);
                        ParseMergeOutput(output, isDryRun);
                        SVNLogBridge.LogLine("<b><color=green>[Complete]</color> Force-merge finished.</b>");
                    }
                    catch (Exception ex2)
                    {
                        SVNLogBridge.LogLine($"<color=red>[Critical Error]</color> Force-merge also failed: {ex2.Message}");
                    }
                }
                else
                {
                    SVNLogBridge.LogLine($"<color=red>[Error]</color> Merge process interrupted: {ex.Message}");
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
                SVNLogBridge.LogLine("Result: <color=green>Everything is already up to date.</color>");
                return;
            }

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int conflicts = lines.Count(l => l.Length >= 1 && (l[0] == 'C' || l.StartsWith("   C")));
            int updated = lines.Count(l => l.Length >= 1 && "UADG".Contains(l[0]));

            if (isDryRun)
            {
                SVNLogBridge.LogLine($"<color=green>[Simulation Result]</color> Files to update: {updated}, Potential conflicts: {conflicts}");
            }
            else
            {
                SVNLogBridge.LogLine($"<color=green>[Merge Result]</color> Successfully processed {updated} items.");
                if (conflicts > 0)
                {
                    SVNLogBridge.LogLine($"<color=red><b>[WARNING] {conflicts} CONFLICTS DETECTED!</b></color>");
                    SVNLogBridge.LogLine("Please use External Merge Tool to resolve files marked with 'C'.");
                }
                SVNLogBridge.LogLine("<size=80%>" + output + "</size>");
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
                SVNLogBridge.LogLine("<b><color=yellow>[Rollback]</color> Starting Revert process...</b>", append: false);
                SVNLogBridge.LogLine("<color=#444444>... Cleaning up all local modifications</color>");

                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir);

                SVNLogBridge.LogLine("<color=#444444>... Synchronizing status</color>");
                await svnManager.RefreshStatus();

                SVNLogBridge.LogLine("<color=green>[Success]</color> Local workspace is now clean. All merge changes discarded.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>[Error]</color> Revert failed: {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        public async Task<string[]> FetchAvailableBranches()
        {
            try
            {
                SVNLogBridge.LogLine("<color=#444444>... Scanning repository for branches</color>");

                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                if (string.IsNullOrEmpty(repoRoot))
                {
                    Debug.LogWarning("[SVN] Cannot fetch branches: Repo Root is empty.");
                    return new string[0];
                }

                var branches = await SvnRunner.GetRepoListAsync(svnManager.WorkingDir, "branches");

                if (branches == null || branches.Length == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>[Info]</color> No branches found on server.");
                    return new string[0];
                }

                SVNLogBridge.LogLine($"<color=green>[Success]</color> Found {branches.Length} branch(es) available for merge.");
                return branches;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SVN Merge] FetchAvailableBranches failed: {ex.Message}");
                SVNLogBridge.LogLine($"<color=red>[Error]</color> Could not retrieve branches list: {ex.Message}");
                return new string[0];
            }
        }
    }
}