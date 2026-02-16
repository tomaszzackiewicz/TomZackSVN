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

        private void Log(string message, bool append = true)
        {
            SVNLogBridge.LogLine(message);

            if (svnUI.MergeConsoleText != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.MergeConsoleText, message, "MERGE", append);
            }
        }

        public async void CompareWithTrunk()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            Log("<b><color=#4FC3F7>[Comparison]</color> Starting analysis against Trunk...</b>", false);

            try
            {
                string repoRoot = svnManager.GetRepoRoot();
                if (string.IsNullOrEmpty(repoRoot)) throw new Exception("Repo Root not found. Please refresh status.");

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";

                Log($"<i>Target: {trunkUrl}</i>");

                if (currentUrl.TrimEnd('/') == trunkUrl)
                {
                    Log("<color=yellow>[Info]</color> You are already on Trunk. Comparison skipped.");
                    return;
                }

                Log("<color=#444444>... Fetching eligible revisions from Trunk</color>");
                string missingCmd = $"mergeinfo \"{trunkUrl}\" --show-revs eligible";
                string missingInBranch = await SvnRunner.RunAsync(missingCmd, svnManager.WorkingDir);

                Log("<color=#444444>... Analyzing local branch changes</color>");
                string localCmd = $"mergeinfo . \"{trunkUrl}\" --show-revs eligible";
                string branchOnlyChanges = await SvnRunner.RunAsync(localCmd, svnManager.WorkingDir);

                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);

                Log("--------------------------------------");
                Log("<b>Merge Status:</b>");
                Log($" • <color=#FFD700>Incoming (Trunk -> Branch):</color> {missingCount} new commit(s)");
                Log($" • <color=#00FF00>Outgoing (Branch -> Trunk):</color> {localCount} local commit(s)");

                if (missingCount > 0)
                    Log("<color=orange>Recommendation: Sync your branch with Trunk before continuing.</color>");
                else
                    Log("<color=green>Success: Your branch is fully synchronized with Trunk.</color>");
            }
            catch (Exception ex)
            {
                Log($"<color=red>[Error]</color> Comparison failed: {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        public async void ExecuteMerge(string sourceInput, bool isDryRun)
        {
            if (IsProcessing || string.IsNullOrWhiteSpace(sourceInput)) return;
            IsProcessing = true;

            Log("<b><color=#4FC3F7>[Merge]</color> Initializing process...</b>", false);

            try
            {
                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                currentUrl = currentUrl.TrimEnd('/');

                string sourceUrl = sourceInput.Contains("://") ? sourceInput :
                                  (sourceInput.ToLower() == "trunk" ? $"{repoRoot}/trunk" : $"{repoRoot}/branches/{sourceInput}");

                if (sourceUrl.TrimEnd('/') == currentUrl)
                {
                    Log("<color=red>[Aborted]</color> Cannot merge a branch into itself.");
                    return;
                }

                string modeText = isDryRun ? "<color=yellow>[SIMULATION]</color>" : "<color=orange>[LIVE MERGE]</color>";
                Log($"{modeText} Source: {sourceInput} —> Target Local");

                Log("<color=#444444>... Trying automatic merge</color>");
                string args = $"merge \"{sourceUrl}\" --non-interactive --accept postpone";
                if (isDryRun) args += " --dry-run";

                try
                {
                    string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                    ParseMergeOutput(output, isDryRun);
                }
                catch (Exception ex) when (ex.Message.Contains("E200004") || ex.Message.Contains("ancestry"))
                {
                    Log("<color=yellow>[Notice]</color> Ancestry error. Trying --ignore-ancestry...");
                    string forceArgs = $"merge \"{sourceUrl}\" --ignore-ancestry --non-interactive --accept postpone";
                    if (isDryRun) forceArgs += " --dry-run";

                    try
                    {
                        string output = await SvnRunner.RunAsync(forceArgs, svnManager.WorkingDir);
                        ParseMergeOutput(output, isDryRun);
                    }
                    catch (Exception)
                    {
                        Log("<color=orange>[Critical Fallback]</color> Still failing. Trying 2-point comparison...");
                        string bruteArgs = $"merge \"{currentUrl}\" \"{sourceUrl}\" . --non-interactive --accept postpone";
                        if (isDryRun) bruteArgs += " --dry-run";

                        string output = await SvnRunner.RunAsync(bruteArgs, svnManager.WorkingDir);
                        ParseMergeOutput(output, isDryRun);
                    }
                }

                if (!isDryRun)
                {
                    await svnManager.RefreshStatus();
                    Log("<b><color=green>[Complete]</color> Merge finished.</b>");
                }
            }
            catch (Exception ex)
            {
                Log($"<color=red>[Error]</color> Merge failed: {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        private void ParseMergeOutput(string output, bool isDryRun)
        {
            if (string.IsNullOrWhiteSpace(output) || output.ToLower().Contains("already up to date"))
            {
                Log("Result: <color=green>Everything is already up to date.</color>");
                return;
            }

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int conflicts = lines.Count(l => l.Length >= 1 && (l[0] == 'C' || l.StartsWith("   C")));
            int updated = lines.Count(l => l.Length >= 1 && "UADG".Contains(l[0]));

            if (isDryRun)
                Log($"<color=green>[Simulation Result]</color> Files: {updated}, Potential Conflicts: {conflicts}");
            else
            {
                Log($"<color=green>[Merge Result]</color> Successfully processed {updated} items.");
                if (conflicts > 0) Log($"<color=red><b>[WARNING] {conflicts} CONFLICTS DETECTED!</b></color>");
                Log("<size=80%>" + output + "</size>");
            }
        }

        public async Task<string[]> FetchAvailableBranches()
        {
            if (IsProcessing) return new string[0];
            IsProcessing = true;

            try
            {
                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string branchesUrl = $"{repoRoot}/branches";

                Log($"<color=#4FC3F7>[Debug]</color> Scanning branches at: {branchesUrl}", false);

                string args = $"list \"{branchesUrl}\" --non-interactive";
                string rawOutput = await SvnRunner.RunAsync(args, svnManager.WorkingDir);

                if (string.IsNullOrEmpty(rawOutput))
                {
                    Log("<color=yellow>[Info]</color> Branches folder is empty.");
                    return new string[0];
                }

                var branchList = rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !line.StartsWith("*") && !line.Contains("WARNING"))
                    .Select(line => line.TrimEnd('/'))
                    .ToArray();

                Log($"<color=green>[Success]</color> Found {branchList.Length} branch(es).");
                return branchList;
            }
            catch (Exception ex)
            {
                Log($"<color=red>[Critical Error]</color> Scan failed: {ex.Message}");
                return new string[0];
            }
            finally { IsProcessing = false; }
        }

        public async Task RevertMerge()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                Log("<b><color=yellow>[Rollback]</color> Starting Revert...</b>", false);
                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir);
                await svnManager.RefreshStatus();
                Log("<color=green>[Success]</color> Local workspace cleaned.");
            }
            catch (Exception ex) { Log($"<color=red>[Error]</color> Revert failed: {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        private int CountRevisions(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return 0;
            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                         .Count(line => !string.IsNullOrWhiteSpace(line));
        }
    }
}