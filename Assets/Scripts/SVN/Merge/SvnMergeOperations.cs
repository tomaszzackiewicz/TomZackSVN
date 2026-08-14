using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

namespace SVN.Core
{
    public static class SvnMergeOperations
    {
        public static async Task ExecuteMergeAsync(SVNMerge merge, string sourceInput, bool isDryRun)
        {
            if (merge == null || merge.SVNManager == null || merge.SVNUI == null)
            {
                merge.LogErrorLocal("[Error] SVN Manager or UI not initialized.");
                return;
            }

            if (!SvnMergeUrlResolver.ValidateSourceInput(sourceInput))
            {
                merge.LogErrorLocal("SECURITY: Provide only branch/tag name or internal path, not a full URL.");
                return;
            }

            merge._hadLocalChangesBeforeMerge = await merge.HasPendingMergeChanges().ConfigureAwait(false);
            if (merge._hadLocalChangesBeforeMerge)
            {
                merge.LogWarningBlock("MERGE BLOCKED", "Working copy contains uncommitted changes.\nCommit, revert or cleanup before merging again.");
                return;
            }

            if (!merge.TryEnterMerging()) return;
            if (!merge.TryStart()) { merge.ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await merge.EnsureWcRootAsync(token).ConfigureAwait(false);
                merge.LogInfoBlock("MERGE SESSION START", $"Source: {sourceInput}\nMode: {(isDryRun ? "DRY RUN" : "LIVE MERGE")}");

                string repoRoot = await merge.GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot)) { merge.LogErrorLocal("Repo Root not found."); return; }

                string sourceUrl = SvnMergeUrlResolver.ResolveSourceUrl(sourceInput, repoRoot);
                string currentUrl = await SvnRunner.GetRepoUrlAsync(merge.SVNManager.WorkingDir).ConfigureAwait(false);
                merge.LogInfo($"Current URL: {currentUrl}");
                merge.LogInfo($"Source URL : {sourceUrl}");

                bool sourceIsTrunk = SVNMerge.Normalize(sourceUrl).EndsWith("/trunk");
                bool currentIsTrunk = SVNMerge.Normalize(currentUrl).EndsWith("/trunk");

                if (!sourceIsTrunk && currentIsTrunk && !isDryRun)
                {
                    merge.LogInfo("[Merge] Reintegrate detected. Checking synchronization...");
                    string eligible = await SvnRunner.RunAsync(
                        $"{SVNMerge.SshConfigOption}mergeinfo {SvnMergeUrlResolver.EscapeSvnArg($"{repoRoot}/trunk")} {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --show-revs eligible",
                        merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);
                    int missing = CountRevisions(eligible);
                    if (missing > 0)
                    {
                        merge.LogErrorLocal("BRANCH NOT SYNCHRONIZED WITH TRUNK.");
                        merge.LogErrorLocal($"Missing {missing} revisions from trunk. Sync first.");
                        return;
                    }
                    merge.LogSuccess("[Merge] Branch is fully synchronized with trunk.");
                }

                if (sourceIsTrunk && !currentIsTrunk && !isDryRun)
                {
                    merge.LogInfo("[Merge] Sync merge detected. Checking eligible revisions...");
                    string eligible = await SvnRunner.RunAsync(
                        $"{SVNMerge.SshConfigOption}mergeinfo {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} . --show-revs eligible",
                        merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);
                    if (CountRevisions(eligible) == 0)
                    {
                        merge.LogInfoBlock("Merge Blocked", "Branch is already fully synchronized with Trunk.");
                        return;
                    }
                    merge.LogSuccess($"[Merge] Found eligible revisions.");
                }

                if (SVNMerge.Normalize(sourceUrl) == SVNMerge.Normalize(currentUrl))
                {
                    merge.LogErrorLocal("Cannot merge branch into itself.");
                    return;
                }

                string currentUuid = (await SvnRunner.RunAsync($"{SVNMerge.SshConfigOption}info --show-item repos-uuid", merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();
                string sourceUuid = (await SvnRunner.RunAsync($"{SVNMerge.SshConfigOption}info {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --show-item repos-uuid", merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();
                if (!string.Equals(currentUuid, sourceUuid, StringComparison.Ordinal))
                {
                    merge.LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                await SvnRunner.RunAsync("update", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                if (!isDryRun)
                {
                    await TryCaptureMergeSnapshotAsync(merge, sourceUrl, token).ConfigureAwait(false);
                }

                string output = await ExecuteMergeCommandAsync(merge, sourceUrl, isDryRun, token).ConfigureAwait(false);
                await ProcessMergeResultAsync(merge, output, isDryRun, token).ConfigureAwait(false);

                if (!isDryRun)
                {
                    await merge.SVNManager.RefreshStatus().ConfigureAwait(false);
                    await merge.RefreshResolveUI().ConfigureAwait(false);
                    merge.LogSuccess("[Merge Complete]");
                }
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[Merge] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Merge Error] {ex.Message}");
            }
            finally
            {
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCts = null;
                merge.ExitMerging();
                merge.End();
            }
        }

        public static async Task UndoLastMergeAsync(SVNMerge merge, bool autoCommit)
        {
            if (!merge.TryStart()) return;
            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            try
            {
                await ExecuteReverseMergeAsync(merge, false, autoCommit, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[Undo] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal("[Undo Error] " + ex.Message);
            }
            finally
            {
                merge._mergeCts = null;
                merge.End();
            }
        }

        public static async Task CancelLocalMergeAsync(SVNMerge merge)
        {
            if (merge._obstructionsJustDeleted)
            {
                merge.LogErrorLocal("[Blocked] Invalid action sequence.");
                merge.LogWarning("You just deleted tree obstructions (Soft Revert). You MUST click 'Revert to HEAD' or 'Commit' right now.");
                merge.LogWarning("Do NOT run standard 'Cancel Local Merge' in this state, or you will corrupt SVN history!");
                return;
            }

            if (!merge.TryStart()) return;
            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            try
            {
                await ExecuteReverseMergeAsync(merge, true, false, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[Cancel Local Merge] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Cancel Local Merge Error] {ex.Message}");
            }
            finally
            {
                merge._mergeCts = null;
                merge.End();
            }
        }

        private static async Task ExecuteReverseMergeAsync(SVNMerge merge, bool isCancel, bool autoCommit, CancellationToken token)
        {
            string opName = isCancel ? "CANCEL LOCAL MERGE" : "UNDO LAST MERGE";
            merge.LogInfo($"========== {opName} ==========");

            if (await merge.HasPendingMergeChanges(token).ConfigureAwait(false))
            {
                string actionName = isCancel ? "cancelling the local merge" : "undoing the last merge";
                merge.LogWarningBlock($"{opName} Blocked", $"Uncommitted changes detected.\nCommit or cancel current changes before {actionName}.");
                return;
            }

            if (!merge._snapshotManager.HasRollbackPoint)
                merge._snapshotManager.LoadRollbackSnapshot();

            if (!merge._snapshotManager.HasRollbackPoint ||
                string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeSource) ||
                string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeRevisionBefore) ||
                string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeRevisionAfter))
            {
                merge.LogWarning($"[{opName}] No rollback point available. Perform a merge first.");
                return;
            }

            string source = merge._snapshotManager.LastMergeSource;
            string revBefore = merge._snapshotManager.LastMergeRevisionBefore;
            string revAfter = merge._snapshotManager.LastMergeRevisionAfter;

            merge.LogInfo($"[{opName}] Source : {source}");
            merge.LogInfo($"[{opName}] Range  : r{revBefore} → r{revAfter}");
            merge.LogInfo($"[{opName}] Bringing working copy to a uniform revision...");

            try
            {
                await SvnRunner.RunAsync($"{SVNMerge.SshConfigOption}update", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogWarning($"[{opName}] Update failed (non-fatal): {ex.Message}");
            }

            string range = $"{revAfter}:{revBefore}";

            if (revBefore == revAfter)
            {
                merge.LogErrorLocal($"[{opName}] Cannot auto-undo a base-merge snapshot (identical revisions).");
                merge.LogWarning("To undo this, manually revert the 'svn:mergeinfo' property change on the root folder.");
                return;
            }

            string args = $"{SVNMerge.SshConfigOption}merge -r {range} {SvnMergeUrlResolver.EscapeSvnArg(source)} --non-interactive --accept postpone";
            merge.LogInfo($"[{opName}] Executing: svn {args}");

            string output;
            try
            {
                output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("mixed-revision") || ex.Message.Contains("E195020"))
            {
                merge.LogWarning($"[{opName}] Mixed-revision detected – retrying after another update...");
                await SvnRunner.RunAsync($"{SVNMerge.SshConfigOption}update", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
                output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("E155035") || ex.Message.Contains("Attempt to add tree conflict"))
            {
                merge.LogErrorLocal($"[{opName}] Operation blocked by Tree Conflicts.");
                merge.LogWarning("<color=#FFFF00>[CRITICAL SVN LIMITATION]</color>");
                merge.LogWarning("SVN cannot undo a merge that created tree conflicts.");
                merge.LogWarning("You must use 'Revert to HEAD' or manually resolve the tree obstructions before undoing.");
                if (isCancel) return;
                throw;
            }
            catch (Exception ex) when (IsAncestryError(ex))
            {
                merge.LogWarning($"[{opName}] Ancestry issue – retrying with --ignore-ancestry...");
                args = $"{SVNMerge.SshConfigOption}merge -r {range} {SvnMergeUrlResolver.EscapeSvnArg(source)} --ignore-ancestry --non-interactive --accept postpone";
                merge.LogInfo($"[{opName}] Retrying with: svn {args}");
                output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
            }

            if (autoCommit)
            {
                string msg = $"Undo merge from {source} (r{revBefore}→r{revAfter})";
                merge.LogInfo($"[{opName}] Auto-committing: {msg}");
                await SvnRunner.RunAsync($"{SVNMerge.SshConfigOption}commit -m {SvnMergeUrlResolver.EscapeSvnArg(msg)}", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
                merge.LogSuccess($"[{opName}] Changes committed automatically.");
            }

            merge._snapshotManager.ClearRollbackSnapshot();
            await merge.SVNManager.RefreshStatus().ConfigureAwait(false);
            await merge.RefreshResolveUI().ConfigureAwait(false);
            merge.LogSuccessBlock($"{opName} Complete", $"Successfully reverted merge of {source} (r{revBefore}→r{revAfter})");
        }

        public static async Task RevertToHeadAsync(SVNMerge merge)
        {
            float timeSinceLastClick = Time.unscaledTime - merge._lastRevertToHeadClickTime;
            if (timeSinceLastClick > 5f)
            {
                merge._lastRevertToHeadClickTime = Time.unscaledTime;
                merge.LogWarningBlock("Reset to HEAD", "This will discard ALL local changes and update to the latest repository revision.\nPress the button again within 5 seconds to confirm.");
                return;
            }
            merge._lastRevertToHeadClickTime = -10f;

            if (!merge.TryStart()) return;
            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                merge.LogWarning("[Reset to HEAD] Step 1/3 – Updating to HEAD...");
                await SvnRunner.RunAsync("update", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                merge.LogWarning("[Reset to HEAD] Step 2/3 – Reverting all local changes...");
                await SvnRunner.RunAsync("revert -R .", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                merge.LogWarning("[Reset to HEAD] Step 3/3 – Cleaning up...");
                await SvnRunner.RunAsync("cleanup", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                merge._snapshotManager.ClearRollbackSnapshot();
                await merge.SVNManager.RefreshStatus().ConfigureAwait(false);
                await merge.RefreshResolveUI().ConfigureAwait(false);
                merge.LogSuccess("[Reset Complete] Working copy is now at HEAD.");
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[RevertToHead] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Reset Error] {ex.Message}");
            }
            finally
            {
                merge._mergeCts = null;
                merge.End();
            }
        }

        public static async Task CompareWithTrunkAsync(SVNMerge merge)
        {
            if (!merge.TryStart()) return;
            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                merge.LogInfoBlock("Comparison", "Starting analysis against Trunk...");

                string repoRoot = await merge.GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot)) { merge.LogErrorLocal("Repo Root not found."); return; }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(merge.SVNManager.WorkingDir).ConfigureAwait(false);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";
                merge.LogInfo($"Target: {trunkUrl}");

                if (SVNMerge.Normalize(currentUrl) == SVNMerge.Normalize(trunkUrl))
                {
                    merge.LogWarning("Already on Trunk. Comparison skipped.");
                    return;
                }

                merge.LogInfo("Fetching revision differences...");
                string missingInBranch = await SvnRunner.RunAsync(
                    $"{SVNMerge.SshConfigOption}mergeinfo {SvnMergeUrlResolver.EscapeSvnArg(trunkUrl)} --show-revs eligible",
                    merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);
                string branchOnlyChanges = await SvnRunner.RunAsync(
                    $"{SVNMerge.SshConfigOption}mergeinfo . {SvnMergeUrlResolver.EscapeSvnArg(trunkUrl)} --show-revs eligible",
                    merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);

                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);

                merge.LogInfo("--------------------------------------");
                merge.LogInfo($"Incoming (Trunk -> Branch): {missingCount}");
                merge.LogInfo($"Outgoing (Branch -> Trunk): {localCount}");

                if (missingCount > 0 || localCount > 0)
                {
                    merge.LogWarning("DIVERGENCE DETECTED: trunk and branch are out of sync.");
                    if (missingCount == 0) merge.LogSuccess("No incoming changes. You only have local commits to push back.");
                }
                else
                {
                    merge.LogSuccess("Fully synchronized with Trunk. No merge needed.");
                }
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[CompareWithTrunk] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Comparison Error] {ex.Message}");
            }
            finally
            {
                merge._mergeCts = null;
                merge.End();
            }
        }

        public static async Task ForceMergeFromTrunkAsync(SVNMerge merge, string sourceInput)
        {
            float timeSinceLastClick = Time.unscaledTime - merge._lastForceMergeClickTime;
            if (timeSinceLastClick > 5f)
            {
                merge._lastForceMergeClickTime = Time.unscaledTime;
                merge.LogWarningBlock("FORCE MERGE DANGER",
                    "This operation will IGNORE svn:mergeinfo and merge ancestry!\n" +
                    "It may cause duplicate merges and history loss.\n" +
                    "Press the button again within 5 seconds to confirm.");
                return;
            }
            merge._lastForceMergeClickTime = -10f;

            if (!merge.TryEnterMerging())
            {
                merge.LogWarning("[Force Merge] Already running — request ignored.");
                return;
            }
            if (!merge.TryStart()) { merge.ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await merge.EnsureWcRootAsync(token).ConfigureAwait(false);

                string repoRoot = await merge.GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    merge.LogErrorLocal("[Force Merge] Repo root not found.");
                    return;
                }

                string sourceUrl;
                if (!string.IsNullOrWhiteSpace(sourceInput))
                {
                    if (!SvnMergeUrlResolver.ValidateSourceInput(sourceInput))
                    {
                        merge.LogErrorLocal("[Force Merge] SECURITY: Invalid source input.");
                        return;
                    }
                    sourceUrl = SvnMergeUrlResolver.ResolveSourceUrl(sourceInput, repoRoot);
                }
                else
                {
                    sourceUrl = $"{repoRoot}/trunk";
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(merge.SVNManager.WorkingDir).ConfigureAwait(false);
                if (SVNMerge.Normalize(sourceUrl) == SVNMerge.Normalize(currentUrl))
                {
                    merge.LogErrorLocal("Cannot merge source into itself.");
                    return;
                }

                merge.LogInfoBlock("FORCE MERGE",
                    $"Ignoring ancestry and merging changes from {sourceUrl}.\n" +
                    "WARNING: This bypasses normal merge tracking.");

                merge._hadLocalChangesBeforeMerge = await merge.HasPendingMergeChanges(token).ConfigureAwait(false);
                await TryCaptureMergeSnapshotAsync(merge, sourceUrl, token).ConfigureAwait(false);

                string args;
                if (merge._snapshotManager.HasRollbackPoint &&
                    !string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeRevisionBefore) &&
                    !string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeRevisionAfter) &&
                    merge._snapshotManager.LastMergeRevisionBefore != merge._snapshotManager.LastMergeRevisionAfter)
                {
                    string range = $"{merge._snapshotManager.LastMergeRevisionBefore}:{merge._snapshotManager.LastMergeRevisionAfter}";
                    args = $"{SVNMerge.SshConfigOption}merge -r {range} {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    merge.LogInfo($"[Force Merge] Range: {range}");
                }
                else
                {
                    args = $"{SVNMerge.SshConfigOption}merge {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    merge.LogInfo("[Force Merge] No revision range available – merging all changes.");
                }

                merge.LogInfo($"[Force Merge] Executing: svn {args}");
                string output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
                await ProcessMergeResultAsync(merge, output, false, token).ConfigureAwait(false);

                await merge.SVNManager.RefreshStatus().ConfigureAwait(false);
                await merge.RefreshResolveUI().ConfigureAwait(false);

                merge.LogSuccess("[Force Merge Complete] Changes have been applied.");
                merge.LogWarning("PLEASE COMMIT this merge immediately to record the history.");
                merge.LogWarning("Without a commit, SVN may attempt to re-merge the same changes in the future.");
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[ForceMerge] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Force Merge Error] {ex.Message}");
            }
            finally
            {
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCts = null;
                merge.ExitMerging();
                merge.End();
            }
        }

        public static async Task RepairMergeHistoryAsync(SVNMerge merge)
        {
            float timeSinceLastClick = Time.unscaledTime - merge._lastRepairMergeClickTime;
            if (timeSinceLastClick > 5f)
            {
                merge._lastRepairMergeClickTime = Time.unscaledTime;
                merge.LogWarningBlock("REPAIR MERGE HISTORY",
                    "This operation will modify svn:mergeinfo metadata.\n" +
                    "No files will be changed, but repository history will be altered.\n" +
                    "Press the button again within 5 seconds to confirm.");
                return;
            }
            merge._lastRepairMergeClickTime = -10f;

            if (!merge.TryEnterMerging())
            {
                merge.LogWarning("[RepairMergeHistory] Already merging...");
                return;
            }
            if (!merge.TryStart()) { merge.ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await merge.SVNManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string repoRoot = await merge.GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    merge.LogErrorLocal("[RepairMergeHistory] Repo root not found.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(merge.SVNManager.WorkingDir).ConfigureAwait(false);
                if (!SVNMerge.Normalize(currentUrl).EndsWith("/trunk"))
                {
                    merge.LogErrorLocal("[RepairMergeHistory] This operation must be performed on trunk.");
                    merge.LogErrorLocal("Please switch to trunk first and then run this command.");
                    return;
                }

                string sourceUrl = null;

                if (merge._snapshotManager.HasRollbackPoint && !string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeSource))
                {
                    sourceUrl = merge._snapshotManager.LastMergeSource;
                    merge.LogInfo($"[RepairMergeHistory] Using stored merge source: {sourceUrl}");
                }
                else
                {
                    merge.LogWarning("[RepairMergeHistory] No stored merge source found. Falling back to log heuristics...");
                    sourceUrl = await DetermineSourceBranchFromLogAsync(merge, repoRoot, token).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(sourceUrl))
                {
                    merge.LogErrorLocal(
                        "[RepairMergeHistory] Could not determine source branch.\n" +
                        "Please perform a merge first so the source is remembered,\n" +
                        "or manually select the branch and try again.");
                    return;
                }

                merge._hadLocalChangesBeforeMerge = await merge.HasPendingMergeChanges(token).ConfigureAwait(false);

                merge.LogInfo($"[RepairMergeHistory] Source branch: {sourceUrl}");
                string args = $"{SVNMerge.SshConfigOption}merge --record-only --ignore-ancestry {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
                merge.LogInfo($"[RepairMergeHistory] Executing: svn {args}");

                string output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
                if (output.Contains("Recording") || output.Contains("recorded") || string.IsNullOrWhiteSpace(output))
                {
                    merge.LogSuccess("[RepairMergeHistory] Mergeinfo successfully recorded.");
                    merge.LogSuccess("Please commit this change immediately.");
                    merge.LogSuccess("After commit, standard reintegrate from branch to trunk will work correctly.");
                    await merge.SVNManager.RefreshStatus().ConfigureAwait(false);
                }
                else
                {
                    merge.LogErrorLocal($"[RepairMergeHistory] Unexpected output: {output}");
                }
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[RepairMergeHistory] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[RepairMergeHistory Error] {ex.Message}");
            }
            finally
            {
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCts = null;
                merge.ExitMerging();
                merge.End();
            }
        }

        private static async Task TryCaptureMergeSnapshotAsync(SVNMerge merge, string sourceUrl, CancellationToken token)
        {
            string baseRevOutput = await SvnRunner.RunAsync("info --show-item revision", merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);
            if (!long.TryParse(baseRevOutput?.Trim(), out long baseRevision))
            {
                merge.LogWarning("[Snapshot] Could not determine BASE revision.");
                return;
            }

            string eligible = await SvnRunner.RunAsync(
                $"{SVNMerge.SshConfigOption}mergeinfo {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} . --show-revs eligible",
                merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(eligible))
            {
                merge.LogInfo("[Snapshot] No eligible revisions – no rollback point created.");
                return;
            }

            var revisions = ParseRevisionList(eligible);
            if (revisions.Count == 0)
            {
                merge.LogWarning("[Snapshot] Mergeinfo exists but revisions are invalid.");
                return;
            }

            merge._snapshotManager.SetSnapshot(sourceUrl, baseRevision.ToString(), revisions.Last().ToString());
            merge._snapshotManager.SaveRollbackSnapshot();
            merge.LogInfoBlock("MERGE SNAPSHOT CREATED", $"BASE: r{baseRevision}, Last eligible: r{revisions.Last()}");
        }

        private static async Task<string> ExecuteMergeCommandAsync(SVNMerge merge, string sourceUrl, bool isDryRun, CancellationToken token)
        {
            string dryRunFlag = isDryRun ? "--dry-run " : string.Empty;
            string args = $"{SVNMerge.SshConfigOption}merge {dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
            merge.LogInfoBlock("SVN MERGE COMMAND", args);

            try
            {
                return await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, !isDryRun, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("E155015"))
            {
                merge.LogWarning(isDryRun ? "[Merge] Conflicts in simulation." : "[Merge] Conflicts in live merge.");
                return ex.Message;
            }
            catch (Exception ex) when (IsAncestryError(ex))
            {
                merge.LogWarningBlock("ANCESTRY PROBLEM DETECTED", "Retrying with --ignore-ancestry.");
                string retryArgs = $"{SVNMerge.SshConfigOption}merge --ignore-ancestry {dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
                return await SvnRunner.RunAsync(retryArgs, merge.SVNManager.WorkingDir, !isDryRun, token).ConfigureAwait(false);
            }
        }

        private static async Task ProcessMergeResultAsync(
            SVNMerge merge, string output, bool isDryRun, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var result = SvnMergeOutputParser.Parse(output);

            if (result.Files.Count == 0 && result.Conflicts == 0 && !result.MergeInfoUpdated)
            {
                merge.LogSuccess("Everything is already up to date.");
                if (isDryRun) merge.RaiseDryRunCompleted(result);
                return;
            }

            string summary = $"Changes: {result.RealChanges}, Conflicts: {result.Conflicts}, Skipped: {result.Skipped}";
            if (result.MergeInfoUpdated) summary += ", MergeInfo updated";
            if (result.HasTreeConflicts) summary += ", TREE CONFLICTS";

            if (isDryRun)
            {
                merge.LogInfoBlock("DRY RUN RESULT", summary);
                merge.RaiseDryRunCompleted(result);
            }
            else
            {
                if (result.Conflicts > 0)
                    merge.LogErrorLocal("[MERGE COMPLETED WITH CONFLICTS] " + summary);
                else
                    merge.LogSuccessBlock("MERGE COMPLETED", summary);
            }
        }

        private static List<long> ParseRevisionList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<long>();
            return raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.StartsWith("r", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Substring(1))
                .Select(x => long.TryParse(x, out long rev) ? rev : -1)
                .Where(x => x >= 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }

        private static bool IsAncestryError(Exception ex)
        {
            string msg = ex?.Message ?? string.Empty;
            return msg.Contains("ancestry", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("reintegrate", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E195016", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E195012", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E195014", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountRevisions(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return 0;
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(x => x.TrimStart().StartsWith("r", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string> DetermineSourceBranchFromLogAsync(SVNMerge merge, string repoRoot, CancellationToken token)
        {
            try
            {
                merge.LogInfo("[RepairMergeHistory] Searching log for incomplete reintegrate...");
                string logOutput = await SvnRunner.RunAsync(
                    $"{SVNMerge.SshConfigOption}log --stop-on-copy --xml --verbose -l 20",
                    merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                long targetRev = FindIncompleteReintegrateRevisionInXml(logOutput);
                if (targetRev <= 0)
                {
                    merge.LogInfo("[RepairMergeHistory] No incomplete reintegrate found in log.");
                    return null;
                }

                merge.LogInfo($"[RepairMergeHistory] Found candidate at r{targetRev}. Attempting to determine source branch from mergeinfo diff...");
                string diffOutput = await SvnRunner.RunAsync(
                    $"{SVNMerge.SshConfigOption}diff -c {targetRev} --properties-only",
                    merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(diffOutput))
                {
                    foreach (string diffLine in diffOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = diffLine.Trim();
                        if (trimmed.StartsWith("+") && (trimmed.Contains("/branches/") || trimmed.Contains("/tags/")))
                        {
                            string urlPart = trimmed.TrimStart('+').Trim();
                            int colonIdx = urlPart.IndexOf(':');
                            if (colonIdx > 0) urlPart = urlPart.Substring(0, colonIdx);
                            if (urlPart.StartsWith("/")) urlPart = $"{repoRoot}{urlPart}";
                            merge.LogInfo($"[RepairMergeHistory] Determined source: {urlPart}");
                            return urlPart;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                merge.LogWarning($"[RepairMergeHistory] Heuristic failed: {ex.Message}");
            }
            return null;
        }

        private static long FindIncompleteReintegrateRevisionInXml(string xmlOutput)
        {
            if (string.IsNullOrWhiteSpace(xmlOutput)) return -1;
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlOutput);
                var logEntries = doc.SelectNodes("//logentry");
                if (logEntries == null) return -1;

                foreach (XmlNode entry in logEntries)
                {
                    string revStr = entry.Attributes?["revision"]?.Value;
                    if (!long.TryParse(revStr, out long rev)) continue;

                    var paths = entry.SelectNodes(".//path");
                    if (paths == null) continue;

                    foreach (XmlNode pathNode in paths)
                    {
                        string pathText = pathNode.InnerText?.Trim();
                        string propMods = pathNode.Attributes?["prop-mods"]?.Value;

                        if ((pathText == "." || pathText == "") &&
                            string.Equals(propMods, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            string msg = entry.SelectSingleNode(".//msg")?.InnerText ?? "";
                            if (msg.IndexOf("reintegrate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                msg.IndexOf("merge", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return rev;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return -1;
        }

        public static async Task CherryPickMergeAsync(SVNMerge merge, string sourceInput, string revisionInput, bool isDryRun)
        {
            if (merge == null || merge.SVNManager == null || merge.SVNUI == null)
            {
                merge.LogErrorLocal("[Error] SVN Manager or UI not initialized.");
                return;
            }

            if (!SvnMergeUrlResolver.ValidateSourceInput(sourceInput))
            {
                merge.LogErrorLocal("SECURITY: Provide only branch/tag name or internal path, not a full URL.");
                return;
            }

            if (string.IsNullOrWhiteSpace(revisionInput))
            {
                merge.LogErrorLocal("[Cherry-pick] No revision specified.");
                return;
            }

            if (!merge.TryEnterMerging()) return;
            if (!merge.TryStart()) { merge.ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await merge.EnsureWcRootAsync(token).ConfigureAwait(false);

                string repoRoot = await merge.GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot)) { merge.LogErrorLocal("Repo Root not found."); return; }

                string sourceUrl = SvnMergeUrlResolver.ResolveSourceUrl(sourceInput, repoRoot);
                string currentUrl = await SvnRunner.GetRepoUrlAsync(merge.SVNManager.WorkingDir).ConfigureAwait(false);

                if (SVNMerge.Normalize(sourceUrl) == SVNMerge.Normalize(currentUrl))
                {
                    merge.LogErrorLocal("Cannot cherry-pick from the same branch into itself.");
                    return;
                }

                string revisionArg;
                string snapshotBefore;
                string snapshotAfter;

                if (revisionInput.Contains(":"))
                {
                    string[] parts = revisionInput.Split(':');
                    if (parts.Length != 2 || !long.TryParse(parts[0], out long rStart) || !long.TryParse(parts[1], out long rEnd))
                    {
                        merge.LogErrorLocal("[Cherry-pick] Invalid revision range format. Use START:END (e.g., 140:150).");
                        return;
                    }
                    revisionArg = $"-r {revisionInput}";
                    snapshotBefore = rStart.ToString();
                    snapshotAfter = rEnd.ToString();
                }
                else if (long.TryParse(revisionInput, out long singleRev))
                {
                    revisionArg = $"-c {singleRev}";
                    snapshotBefore = (singleRev - 1).ToString();
                    snapshotAfter = singleRev.ToString();
                }
                else
                {
                    merge.LogErrorLocal("[Cherry-pick] Invalid revision format. Use a single number (e.g., 150) or a range (140:150).");
                    return;
                }

                merge.LogInfoBlock("CHERRY-PICK SESSION START", $"Source: {sourceUrl}\nTarget: {currentUrl}\nRevision(s): {revisionInput}\nMode: {(isDryRun ? "DRY RUN" : "LIVE")}");

                string currentUuid = (await SvnRunner.RunAsync($"{SVNMerge.SshConfigOption}info --show-item repos-uuid", merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();
                string sourceUuid = (await SvnRunner.RunAsync($"{SVNMerge.SshConfigOption}info {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --show-item repos-uuid", merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();
                if (!string.Equals(currentUuid, sourceUuid, StringComparison.Ordinal))
                {
                    merge.LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                merge._hadLocalChangesBeforeMerge = await merge.HasPendingMergeChanges(token).ConfigureAwait(false);
                if (merge._hadLocalChangesBeforeMerge)
                {
                    merge.LogWarningBlock("MERGE BLOCKED", "Working copy contains uncommitted changes.\nCommit, revert or cleanup before cherry-picking.");
                    return;
                }

                await SvnRunner.RunAsync("update", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                if (!isDryRun)
                {
                    merge._snapshotManager.SetSnapshot(sourceUrl, snapshotBefore, snapshotAfter);
                    merge._snapshotManager.SaveRollbackSnapshot();
                    merge.LogInfoBlock("CHERRY-PICK SNAPSHOT", $"Created rollback point for r{snapshotBefore} → r{snapshotAfter}");
                }

                merge.LogInfo($"[Cherry-pick] Fetching file changes for r{revisionInput}...");
                string logArgs = $"{SVNMerge.SshConfigOption}log -r {revisionInput} -v --xml {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)}";
                string logXml = await SvnRunner.RunAsync(logArgs, merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);

                var previewResult = ParseCherryPickLogXml(logXml, out string commitMsg);

                if (previewResult.Files.Count > 0)
                {
                    merge.LogInfoBlock("REVISION CONTENTS", $"Commit msg: {commitMsg}\nFiles affected: {previewResult.Files.Count}");

                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        merge.RaiseDryRunCompleted(previewResult);
                    });
                }
                else
                {
                    merge.LogWarning($"[Cherry-pick] Revision r{revisionInput} seems empty or contains no file changes.");
                }

                string dryRunFlag = isDryRun ? "--dry-run " : string.Empty;
                string args = $"{SVNMerge.SshConfigOption}merge {revisionArg} {dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
                merge.LogInfo($"[Cherry-pick] Executing: svn {args}");

                string output;
                try
                {
                    output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, !isDryRun, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex.Message.Contains("E155015"))
                {
                    merge.LogWarning(isDryRun ? "[Cherry-pick] Conflicts in simulation." : "[Cherry-pick] Conflicts detected.");
                    output = ex.Message;
                }
                catch (Exception ex) when (IsAncestryError(ex))
                {
                    merge.LogWarning("[Cherry-pick] Ancestry problem – retrying with --ignore-ancestry...");
                    string retryArgs = $"{SVNMerge.SshConfigOption}merge {revisionArg} {dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    output = await SvnRunner.RunAsync(retryArgs, merge.SVNManager.WorkingDir, !isDryRun, token).ConfigureAwait(false);
                }

                await ProcessMergeResultAsync(merge, output, isDryRun, token).ConfigureAwait(false);

                if (!isDryRun)
                {
                    await merge.SVNManager.RefreshStatus().ConfigureAwait(false);
                    await merge.RefreshResolveUI().ConfigureAwait(false);
                    merge.LogSuccess("[Cherry-pick Complete]");
                }
            }
            catch (OperationCanceledException)
            {
                merge.LogWarning("[Cherry-pick] Cancelled by user.");
                await merge.SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Cherry-pick Error] {ex.Message}");
            }
            finally
            {
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCts = null;
                merge.ExitMerging();
                merge.End();
            }
        }

        private static SVNMerge.MergeFileResult ParseCherryPickLogXml(string xmlOutput, out string message)
        {
            var result = new SVNMerge.MergeFileResult();
            result.Files = new List<SVNMerge.MergeFileInfo>();
            message = "N/A";

            if (string.IsNullOrWhiteSpace(xmlOutput)) return result;

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlOutput);

                var logEntries = doc.SelectNodes("//logentry");
                if (logEntries == null) return result;

                var messages = new List<string>();

                foreach (XmlNode logEntry in logEntries)
                {
                    string msg = logEntry.SelectSingleNode("msg")?.InnerText?.Trim();
                    if (!string.IsNullOrWhiteSpace(msg)) messages.Add(msg);

                    var pathNodes = logEntry.SelectNodes(".//path");
                    if (pathNodes == null) continue;

                    foreach (XmlNode pathNode in pathNodes)
                    {
                        string action = pathNode.Attributes?["action"]?.Value ?? "M";
                        string filePath = pathNode.InnerText?.Trim() ?? "";
                        if (string.IsNullOrEmpty(filePath)) continue;

                        if (filePath.Equals("/trunk", StringComparison.OrdinalIgnoreCase))
                        {
                            filePath = ". (Root / Property Change)";
                        }
                        else if (filePath.StartsWith("/trunk/", StringComparison.OrdinalIgnoreCase))
                        {
                            filePath = filePath.Substring("/trunk/".Length);
                        }

                        char stateChar = action.Length > 0 ? char.ToUpper(action[0]) : 'M';

                        result.Files.Add(new SVNMerge.MergeFileInfo
                        {
                            Path = filePath,
                            State = stateChar
                        });
                    }
                }

                message = messages.Count > 0 ? string.Join(" | ", messages) : "No commit message provided.";

                result.RealChanges = result.Files.Count;
                result.Added = result.Files.Count(f => f.State == 'A');
                result.Updated = result.Files.Count(f => f.State == 'M' || f.State == 'R');
                result.Deleted = result.Files.Count(f => f.State == 'D');
            }
            catch (Exception)
            {
            
            }

            return result;
        }
    }
}