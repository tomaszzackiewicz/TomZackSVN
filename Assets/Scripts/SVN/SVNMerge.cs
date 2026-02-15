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
                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                currentUrl = currentUrl.TrimEnd('/');

                // 1. Rozwiązywanie URL źródłowego
                if (sourceInput.Contains("://")) sourceUrl = sourceInput;
                else if (sourceInput.ToLower() == "trunk") sourceUrl = $"{repoRoot}/trunk";
                else sourceUrl = $"{repoRoot}/branches/{sourceInput}";

                if (sourceUrl.TrimEnd('/') == currentUrl)
                {
                    SVNLogBridge.LogLine("<color=red>[Aborted]</color> Cannot merge a branch into itself.");
                    return;
                }

                string targetName = currentUrl.EndsWith("/trunk") ? "Trunk" : $"branch {currentUrl.Split('/').Last()}";
                string sourceName = sourceUrl.EndsWith("/trunk") ? "Trunk" : $"branch {sourceUrl.Split('/').Last()}";
                string modeText = isDryRun ? "<color=yellow>[SIMULATION]</color>" : "<color=orange>[LIVE MERGE]</color>";

                SVNLogBridge.LogLine($"{modeText} Source: {sourceName} —> Target: {targetName}");

                // 2. Próba standardowego Merga (Automatic Merge)
                SVNLogBridge.LogLine("<color=#444444>... Trying automatic merge</color>");
                string args = $"merge \"{sourceUrl}\" --non-interactive --accept postpone";
                if (isDryRun) args += " --dry-run";

                try
                {
                    string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                    ParseMergeOutput(output, isDryRun);
                }
                catch (Exception ex) when (ex.Message.Contains("E200004") || ex.Message.Contains("ancestry") || ex.Message.Contains("heritage"))
                {
                    // 3. Fallback: Force Merge (Ignore Ancestry)
                    SVNLogBridge.LogLine("<color=yellow>[Notice]</color> Standard merge failed. Trying with --ignore-ancestry...");

                    // Przy --ignore-ancestry SVN nie może zarządzać mergeinfo, więc wymuszamy brak zapisu tych danych
                    string forceArgs = $"merge \"{sourceUrl}\" --ignore-ancestry --non-interactive --accept postpone";
                    if (isDryRun) forceArgs += " --dry-run";

                    try
                    {
                        string output = await SvnRunner.RunAsync(forceArgs, svnManager.WorkingDir);
                        ParseMergeOutput(output, isDryRun);
                    }
                    catch (Exception ex2)
                    {
                        // 4. Ostateczny Fallback: 2-Point Diff Merge (Brute force)
                        // Porównuje stan źródła ze stanem lokalnym całkowicie ignorując historię
                        SVNLogBridge.LogLine("<color=orange>[Critical Fallback]</color> Ancestry ignored, but still failed. Trying 2-point comparison...");

                        string bruteArgs = $"merge \"{currentUrl}\" \"{sourceUrl}\" . --non-interactive --accept postpone";
                        if (isDryRun) bruteArgs += " --dry-run";

                        string output = await SvnRunner.RunAsync(bruteArgs, svnManager.WorkingDir);
                        ParseMergeOutput(output, isDryRun);
                    }
                }

                // 5. Finalizacja
                if (!isDryRun)
                {
                    await svnManager.RefreshStatus();
                    SVNLogBridge.LogLine("<b><color=green>[Complete]</color> Process finished. Check Unity for changes.</b>");
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>[Error]</color> Merge process failed: {ex.Message}");
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
            if (IsProcessing) return new string[0];
            IsProcessing = true;

            try
            {
                string repoRoot = svnManager.GetRepoRoot().TrimEnd('/');
                string branchesUrl = $"{repoRoot}/branches";

                SVNLogBridge.LogLine($"<color=#4FC3F7>[Debug]</color> Scanning URL: {branchesUrl}");

                // POPRAWKA TUTAJ: 
                // 1. Komenda to "list" + "URL"
                // 2. Ścieżka robocza to lokalny folder projektu (svnManager.WorkingDir)
                string args = $"list \"{branchesUrl}\" --non-interactive";
                string rawOutput = await SvnRunner.RunAsync(args, svnManager.WorkingDir);

                if (string.IsNullOrEmpty(rawOutput))
                {
                    SVNLogBridge.LogLine("<color=yellow>[Info]</color> Branches folder is empty.");
                    return new string[0];
                }

                // Parser (usuwa banner i gwiazdki)
                var branchList = rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !line.StartsWith("*") && !line.Contains("WARNING"))
                    .Select(line => line.TrimEnd('/'))
                    .ToArray();

                SVNLogBridge.LogLine($"<color=green>[Success]</color> Found {branchList.Length} branch(es).");
                return branchList;
            }
            catch (System.Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>[Critical Error]</color> Check if SvnRunner.RunAsync handles URLs correctly. Details: {ex.Message}");
                return new string[0];
            }
            finally { IsProcessing = false; }
        }
    }
}