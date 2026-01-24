using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMerge : SVNBase
    {
        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager) { }


        public async void CompareWithTrunk()
        {
            if (IsProcessing) return;

            IsProcessing = true;
            svnUI.LogText.text += "<b>Comparing current branch with Trunk...</b>\n";

            try
            {
                // 1. Get current URL and determine Trunk URL
                string repoUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                repoUrl = repoUrl.TrimEnd('/');

                if (repoUrl.EndsWith("/trunk"))
                {
                    svnUI.LogText.text += "<color=yellow>System:</color> You are already on Trunk. Nothing to compare.\n";
                    return;
                }

                // Determine Trunk URL safely
                string baseUrl = repoUrl.Contains("/branches/")
                    ? repoUrl.Substring(0, repoUrl.IndexOf("/branches/"))
                    : repoUrl.Substring(0, repoUrl.LastIndexOf('/'));

                string trunkUrl = $"{baseUrl}/trunk";

                // 2. Run Comparison
                // We look for revisions that are in trunk but NOT in current branch
                svnUI.LogText.text += "Checking for missing updates from Trunk...\n";
                string missingInBranch = await SvnRunner.RunAsync($"log {trunkUrl} --incremental --limit 10", svnManager.WorkingDir);

                // We look for revisions that are in branch but NOT in trunk
                svnUI.LogText.text += "Checking for local branch changes to merge...\n";
                string branchOnlyChanges = await SvnRunner.RunAsync($"log {repoUrl} --incremental --limit 10", svnManager.WorkingDir);

                // 3. Simple report
                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);

                svnUI.LogText.text += "--------------------------------------\n";
                svnUI.LogText.text += $"Branch status vs Trunk:\n";
                svnUI.LogText.text += $" - <color=#FFD700>Incoming changes (Trunk):</color> {missingCount} revision(s)\n";
                svnUI.LogText.text += $" - <color=#00FF00>Outgoing changes (Branch):</color> {localCount} revision(s)\n";

                if (missingCount > 0)
                    svnUI.LogText.text += "<color=orange>Tip: You should probably merge from Trunk to stay up to date.</color>\n";
                else
                    svnUI.LogText.text += "<color=green>Your branch is up to date with Trunk!</color>\n";

                svnUI.LogText.text += "--------------------------------------\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Comparison Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        // Helper to count revisions in svn log output
        private int CountRevisions(string logOutput)
        {
            if (string.IsNullOrWhiteSpace(logOutput)) return 0;
            // SVN log revisions start with 'r' followed by numbers (e.g., r1234 | user | ...)
            return logOutput.Split(new[] { "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                            .Count(line => line.StartsWith("r") && line.Contains(" | "));
        }

        /// <summary>
        /// Executes the merge operation. 
        /// </summary>
        /// <param name="sourceUrl">The URL of the branch/trunk to merge from.</param>
        /// <param name="isDryRun">If true, only simulates the merge without changing files.</param>
        public async void ExecuteMerge(string sourceUrl, bool isDryRun)
        {
            if (IsProcessing) return;

            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                svnUI.LogText.text += "<color=red>Error:</color> Please provide a valid Source URL.\n";
                return;
            }

            string root = svnManager.WorkingDir;
            IsProcessing = true;

            string modeText = isDryRun ? "<color=yellow>SIMULATING</color>" : "<color=orange>EXECUTING</color>";
            svnUI.LogText.text = $"<b>{modeText} Merge from:</b> {sourceUrl}\n";

            try
            {
                // 1. Prepare arguments
                // --non-interactive prevents SVN from hanging on prompts
                string args = $"merge {sourceUrl}";
                if (isDryRun) args += " --dry-run";

                // 2. Run Command
                string output = await SvnRunner.RunAsync(args, root);

                // 3. Process Output
                ParseMergeOutput(output, isDryRun);

                // 4. Refresh status only if files were actually changed
                if (!isDryRun)
                {
                    svnManager.Button_RefreshStatus();
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Merge Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void ParseMergeOutput(string output, bool isDryRun)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                svnUI.LogText.text += "No changes to merge (already up to date).\n";
                return;
            }

            // Look for conflicts in the output lines
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int conflicts = lines.Count(l => l.StartsWith("C  ") || l.Contains("conflicts"));
            int updated = lines.Count(l => l.StartsWith("U  ") || l.StartsWith("A  ") || l.StartsWith("D  ") || l.StartsWith("G  "));

            if (isDryRun)
            {
                svnUI.LogText.text += $"<color=cyan><b>Simulation Result:</b></color> {updated} files would be updated, {conflicts} conflicts would occur.\n";
                svnUI.LogText.text += "<size=80%>Full simulation log below:</size>\n" + output + "\n";
            }
            else
            {
                svnUI.LogText.text += $"<color=green>Merge finished.</color> Files affected: {updated}.\n";
                if (conflicts > 0)
                {
                    svnUI.LogText.text += $"<color=red><b>CRITICAL: {conflicts} CONFLICTS DETECTED!</b></color>\n";
                    svnUI.LogText.text += "Please resolve conflicts before committing.\n";
                }
                svnUI.LogText.text += output + "\n";
            }
        }

        // Dodaj do SVNMerge.cs
        public async Task<string[]> FetchAvailableBranches()
        {
            try
            {
                // 1. Pobieramy info o aktualnym repozytorium, ¿eby znaæ Root URL
                string info = await SvnRunner.RunAsync("info --xml", svnManager.WorkingDir);
                // (Tutaj mo¿na u¿yæ prostego Regexa lub XML parsera, by wyci¹gn¹æ <repository><root>)
                // Dla uproszczenia za³ó¿my, ¿e svnManager ma ju¿ RootURL:
                string rootUrl = svnUI.SettingsWorkingDirInput.text;

                if (string.IsNullOrEmpty(rootUrl)) return new string[0];

                svnUI.LogText.text += "Fetching branch list from server...\n";
                return await SvnRunner.ListBranchesAsync(rootUrl);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN Merge] Failed to fetch branches: {ex.Message}");
                return new string[0];
            }
        }
    }
}