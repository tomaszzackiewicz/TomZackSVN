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

        // cap listy undo (-c -N per rewizja) — limit długości linii komendy
        private const int MaxUndoListRevisions = 1000;

        public static async Task ExecuteMergeAsync(SVNMerge merge, string sourceInput, bool isDryRun)
        {
            if (merge == null) return;
            if (merge.SVNManager == null || merge.SVNUI == null)
            {
                merge.LogErrorLocal("[Error] SVN Manager or UI not initialized.");
                return;
            }

            if (!merge.TryStart())
            {
                merge.LogWarning("[Merge] Another operation is already in progress.");
                return;
            }

            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;
            bool snapshotSessionStarted = false;   // lifecycle: czy sesja doszła do etapu snapshotu

            try
            {
                if (!SvnMergeUrlResolver.ValidateSourceInput(sourceInput))
                {
                    merge.LogErrorLocal("SECURITY: Provide only branch/tag name or internal path, not a full URL.");
                    return;
                }

                merge._hadLocalChangesBeforeMerge = await merge.HasPendingMergeChanges(token).ConfigureAwait(false);
                if (merge._hadLocalChangesBeforeMerge)
                {
                    merge.LogWarningBlock("MERGE BLOCKED", "Working copy contains uncommitted changes.\nCommit, revert or cleanup before merging again.");
                    return;
                }

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

                    string eligible = await RunWithHeartbeatAsync(merge,
                        $"mergeinfo {SvnMergeUrlResolver.EscapeSvnArg($"{repoRoot}/trunk")} {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --show-revs eligible",
                        merge.SVNManager.WorkingDir, false, token, "Checking Reintegrate Sync").ConfigureAwait(false);

                    // === FIX (fail-closed): awaria mergeinfo wyglądała jak "zsynchronizowany"
                    if (IsSvnErrorOutput(eligible))
                    {
                        merge.LogErrorLocal("[Merge] Reintegrate sync check FAILED — aborting (fail-closed). Check connection.");
                        return;
                    }

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

                    string eligible = await RunWithHeartbeatAsync(merge,
                        $"mergeinfo {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} . --show-revs eligible",
                        merge.SVNManager.WorkingDir, false, token, "Checking Sync Merge").ConfigureAwait(false);

                    // === FIX (fail-closed): jw.
                    if (IsSvnErrorOutput(eligible))
                    {
                        merge.LogErrorLocal("[Merge] Sync check FAILED — aborting (fail-closed). Check connection.");
                        return;
                    }

                    if (CountRevisions(eligible) == 0)
                    {
                        merge.LogInfoBlock("Merge Blocked", "Branch is already fully synchronized with Trunk.");
                        return;
                    }
                    merge.LogSuccess("[Merge] Found eligible revisions.");
                }

                if (SVNMerge.Normalize(sourceUrl) == SVNMerge.Normalize(currentUrl))
                {
                    merge.LogErrorLocal("Cannot merge branch into itself.");
                    return;
                }

                string currentUuid = (await SvnRunner.RunAsync("info --show-item repos-uuid", merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();
                string sourceUuid = (await SvnRunner.RunAsync($"info {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --show-item repos-uuid", merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();
                if (!string.Equals(currentUuid, sourceUuid, StringComparison.Ordinal))
                {
                    merge.LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                if (!isDryRun)
                {
                    // === FIX (lifecycle A): unieważnia punkt undo POPRZEDNIEJ sesji
                    merge._snapshotManager.ClearRollbackSnapshot();
                    snapshotSessionStarted = true;

                    // === FIX (fail-closed + switch): currentUrl trafia do snapshotu
                    if (!await TryCaptureMergeSnapshotAsync(merge, sourceUrl, currentUrl, token).ConfigureAwait(false))
                    {
                        merge.LogErrorLocal("[Merge] Aborted — could not determine merge range (mergeinfo failed).");
                        return;
                    }
                }

                await RunWithHeartbeatAsync(merge, "update", merge.SVNManager.WorkingDir, true, token, "Updating Working Copy").ConfigureAwait(false);

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
                // === FIX (lifecycle C): revert cofnął merge — snapshot wisi
                if (snapshotSessionStarted) merge._snapshotManager.ClearRollbackSnapshot();
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Merge Error] {ex.Message}");
                // === FIX (lifecycle B): merge nie doszedł do skutku — snapshot wisi.
                // Trade-off świadomy: wyjątek PO udanym merge też skasuje punkt —
                // strata undo < odwracanie niezmierzganych rewizji.
                if (snapshotSessionStarted) merge._snapshotManager.ClearRollbackSnapshot();
            }
            finally
            {
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCommandStarted = false;
                merge._mergeCts = null;
                merge.End();
            }
        }

        public static async Task UndoLastMergeAsync(SVNMerge merge, bool autoCommit)
        {
            if (merge == null) return;

            // === FIX P1: pojedynczy guard, try natychmiast po nim.
            if (!merge.TryStart())
            {
                merge.LogWarning("[Undo] Another operation is already in progress.");
                return;
            }

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
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCommandStarted = false;
                merge._mergeCts = null;
                merge.End();
            }
        }

        public static async Task CancelLocalMergeAsync(SVNMerge merge)
        {
            if (merge == null) return;

            if (merge._obstructionsJustDeleted)
            {
                merge.LogErrorLocal("[Blocked] Invalid action sequence.");
                merge.LogWarning("You just deleted tree obstructions (Soft Revert). You MUST click 'Revert to HEAD' or 'Commit' right now.");
                merge.LogWarning("Do NOT run standard 'Cancel Local Merge' in this state, or you will corrupt SVN history!");
                return;
            }

            // === FIX P1: poprzednio tylko TryStart (bez merge-flaga) — mógł biec
            // równolegle do ExecuteMerge. Teraz jeden wspólny guard chroni wszystko.
            if (!merge.TryStart())
            {
                merge.LogWarning("[Cancel Local Merge] Another operation is already in progress.");
                return;
            }

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
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCommandStarted = false;
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

            // === FIX: LoadFromFile — pool-safe (PlayerPrefs tylko z main/konstruktora)
            merge._snapshotManager.LoadFromFile();

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
            string revisionList = merge._snapshotManager.LastMergeRevisionList;   // null dla starych snapshotów

            merge.LogInfo($"[{opName}] Source : {source}");
            merge.LogInfo($"[{opName}] Range  : r{revBefore} → r{revAfter}");
            if (!string.IsNullOrWhiteSpace(revisionList))
                merge.LogInfo($"[{opName}] Reversing revisions: {revisionList}");

            // === FIX (switch): switch gałęzi NIE czyści pliku snapshotu (per-WC, z
            // rozsądku — przeżywa restarty projektu). Bez weryfikacji Undo odwracałoby
            // zakres mergowany na INNEJ gałęzi. Stare snapshoty (bez URL) → check
            // pomijany. Fail-closed: awaria GetRepoUrlAsync → mismatch → abort.
            if (!string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeWorkingCopyUrl))
            {
                string currentWcUrl = await SvnRunner.GetRepoUrlAsync(merge.SVNManager.WorkingDir).ConfigureAwait(false);
                if (SVNMerge.Normalize(currentWcUrl) != SVNMerge.Normalize(merge._snapshotManager.LastMergeWorkingCopyUrl))
                {
                    merge.LogErrorLocal($"[{opName}] Rollback point belongs to a DIFFERENT branch — aborting.");
                    merge.LogWarning($"Snapshot branch : {merge._snapshotManager.LastMergeWorkingCopyUrl}");
                    merge.LogWarning($"Current branch  : {currentWcUrl ?? "(unknown)"}");
                    merge.LogWarning("Switch back to the original branch first, or use 'Revert to HEAD' to reset.");
                    return;
                }
            }

            merge.LogInfo($"[{opName}] Bringing working copy to a uniform revision...");

            try
            {
                await SvnRunner.RunAsync("update", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                merge.LogWarning($"[{opName}] Update failed (non-fatal): {ex.Message}");
            }

            // === FIX (dziury + off-by-one): undo z LISTY (-c -N odwraca dokładnie
            // wskazane rewizje i koryguje mergeinfo per-rewizja); zakres tylko jako
            // fallback dla starych snapshotów bez listy.
            string baseArgs;
            if (!string.IsNullOrWhiteSpace(revisionList))
            {
                string reverseList = string.Join(",", revisionList
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => $"-{x.Trim()}"));

                baseArgs = $"merge -c {reverseList} {SvnMergeUrlResolver.EscapeSvnArg(source)} --non-interactive --accept postpone";
            }
            else
            {
                if (revBefore == revAfter)
                {
                    merge.LogErrorLocal($"[{opName}] Cannot auto-undo a legacy snapshot (identical revisions).");
                    merge.LogWarning("Perform a fresh merge (new snapshot format) or revert the svn:mergeinfo property manually.");
                    return;
                }

                baseArgs = $"merge -r {revAfter}:{revBefore} {SvnMergeUrlResolver.EscapeSvnArg(source)} --non-interactive --accept postpone";
            }

            // === FIX P2 (zachowany): flaga przed komendą — cancel w trakcie undo
            // revertuje częściowe zmiany undo do BASE.
            merge._mergeCommandStarted = true;

            string args = baseArgs;
            merge.LogInfo($"[{opName}] Executing: svn {args}");

            string output;
            try
            {
                output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("mixed-revision") || ex.Message.Contains("E195020"))
            {
                merge.LogWarning($"[{opName}] Mixed-revision detected – retrying after another update...");
                await SvnRunner.RunAsync("update", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
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
                args = $"{baseArgs} --ignore-ancestry";
                merge.LogInfo($"[{opName}] Retrying with: svn {args}");
                output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
            }

            if (autoCommit)
            {
                string msg = $"Undo merge from {source} (r{revBefore}→r{revAfter})";
                merge.LogInfo($"[{opName}] Auto-committing: {msg}");
                await SvnRunner.RunAsync($"commit -m {SvnMergeUrlResolver.EscapeSvnArg(msg)} --no-unlock", merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);
                merge.LogSuccess($"[{opName}] Changes committed automatically.");
            }

            merge._snapshotManager.ClearRollbackSnapshot();
            await merge.SVNManager.RefreshStatus().ConfigureAwait(false);
            await merge.RefreshResolveUI().ConfigureAwait(false);
            merge.LogSuccessBlock($"{opName} Complete", $"Successfully reverted merge of {source}");
        }

        public static async Task RevertToHeadAsync(SVNMerge merge)
        {
            if (merge == null) return;

            float timeSinceLastClick = Time.unscaledTime - merge._lastRevertToHeadClickTime;
            if (timeSinceLastClick > 5f)
            {
                merge._lastRevertToHeadClickTime = Time.unscaledTime;
                merge.LogWarningBlock("Reset to HEAD", "This will discard ALL local changes and update to the latest repository revision.\nPress the button again within 5 seconds to confirm.");
                return;
            }
            merge._lastRevertToHeadClickTime = -10f;

            if (!merge.TryStart())
            {
                merge.LogWarning("[Reset to HEAD] Another operation is already in progress.");
                return;
            }

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
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCommandStarted = false;
                merge._mergeCts = null;
                merge.End();
            }
        }

        public static async Task CompareWithTrunkAsync(SVNMerge merge)
        {
            if (merge == null) return;

            if (!merge.TryStart())
            {
                merge.LogWarning("[Compare] Another operation is already in progress.");
                return;
            }

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
                    $"mergeinfo {SvnMergeUrlResolver.EscapeSvnArg(trunkUrl)} --show-revs eligible",
                    merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);
                string branchOnlyChanges = await SvnRunner.RunAsync(
                    $"mergeinfo . {SvnMergeUrlResolver.EscapeSvnArg(trunkUrl)} --show-revs eligible",
                    merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);

                // === FIX (fail-closed raportu): awaria mergeinfo raportowała
                // "Fully synchronized" — mylące zamiast błędu
                if (IsSvnErrorOutput(missingInBranch) || IsSvnErrorOutput(branchOnlyChanges))
                {
                    merge.LogErrorLocal("[Comparison] mergeinfo failed — comparison unavailable.");
                    return;
                }

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
            if (merge == null) return;

            // Potwierdzenie double-click PRZED guardem (czyste UI, żaden stan nie zajęty).
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

            if (!merge.TryStart())
            {
                merge.LogWarning("[Force Merge] Another operation is already in progress.");
                return;
            }

            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;
            bool snapshotSessionStarted = false;   // lifecycle

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

                // === FIX (warn-not-block — celowo BEZ blokady): force = escape hatch;
                // SVN sam obroni się konfliktami, a flaga _hadLocalChanges chroni
                // usera przed revert -R przy cancel. Ale user musi wiedzieć, że cancel
                // NIE zrobi auto-revert na brudnej kopii.
                if (merge._hadLocalChangesBeforeMerge)
                    merge.LogWarning("[Force Merge] Working copy is NOT clean – force merge will proceed, but cancel will NOT auto-revert. Commit or revert first if unsure.");

                // === FIX (lifecycle A): unieważnia punkt poprzedniej sesji
                merge._snapshotManager.ClearRollbackSnapshot();
                snapshotSessionStarted = true;

                // === FIX (fail-closed + switch): currentUrl → snapshot
                if (!await TryCaptureMergeSnapshotAsync(merge, sourceUrl, currentUrl, token).ConfigureAwait(false))
                {
                    merge.LogErrorLocal("[Force Merge] Aborted — could not determine merge range (mergeinfo failed).");
                    return;
                }

                string args;
                if (merge._snapshotManager.HasRollbackPoint &&
                    !string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeRevisionBefore) &&
                    !string.IsNullOrWhiteSpace(merge._snapshotManager.LastMergeRevisionAfter) &&
                    merge._snapshotManager.LastMergeRevisionBefore != merge._snapshotManager.LastMergeRevisionAfter)
                {
                    // snapshot = (peg, last) → -r peg:last = dokładnie first..last
                    string range = $"{merge._snapshotManager.LastMergeRevisionBefore}:{merge._snapshotManager.LastMergeRevisionAfter}";
                    args = $"merge -r {range} {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    merge.LogInfo($"[Force Merge] Range: {range}");
                }
                else
                {
                    args = $"merge {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    merge.LogInfo("[Force Merge] No revision range available – merging all changes.");
                }

                merge.LogInfo($"[Force Merge] Executing: svn {args}");

                merge._mergeCommandStarted = true;
                string output = await RunWithHeartbeatAsync(merge, args, merge.SVNManager.WorkingDir, true, token, "Force Merge").ConfigureAwait(false);

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
                // === FIX (lifecycle C)
                if (snapshotSessionStarted) merge._snapshotManager.ClearRollbackSnapshot();
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Force Merge Error] {ex.Message}");
                // === FIX (lifecycle B)
                if (snapshotSessionStarted) merge._snapshotManager.ClearRollbackSnapshot();
            }
            finally
            {
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCommandStarted = false;
                merge._mergeCts = null;
                merge.End();
            }
        }

        public static async Task RepairMergeHistoryAsync(SVNMerge merge)
        {
            if (merge == null) return;

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

            if (!merge.TryStart())
            {
                merge.LogWarning("[RepairMergeHistory] Another operation is already in progress.");
                return;
            }

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

                // === FIX (warn-not-block — celowo BEZ blokady): repair istnieje po to,
                // żeby naprawiać bałagan w mergeinfo — a taki bałagan SAM rejestruje się
                // jako pending change (' M' w kolumnie 2). Blokada odmówiłaby naprawy
                // dokładnie tego stanu, po którym ta operacja istnieje.
                if (merge._hadLocalChangesBeforeMerge)
                    merge.LogWarning("[RepairMergeHistory] Working copy has pending changes – the mergeinfo record will mix with them on commit. Commit selectively if unsure.");

                merge.LogInfo($"[RepairMergeHistory] Source branch: {sourceUrl}");
                string args = $"merge --record-only --ignore-ancestry {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
                merge.LogInfo($"[RepairMergeHistory] Executing: svn {args}");

                merge._mergeCommandStarted = true;
                string output = await SvnRunner.RunAsync(args, merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                // === FIX: null-check pierwszy (było: output.Contains przed IsNullOrWhiteSpace → NRE przy null)
                if (string.IsNullOrWhiteSpace(output) || output.Contains("Recording") || output.Contains("recorded"))
                {
                    merge.LogSuccess("[RepairMergeHistory] Mergeinfo successfully recorded.");
                    // === FIX: LogWarning zamiast LogSuccess — "commitnij teraz" nie może ginąć między sukcesami
                    merge.LogWarning("Please commit this change immediately.");
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
                merge._mergeCommandStarted = false;
                merge._mergeCts = null;
                merge.End();
            }
        }

        // === FIX: zwraca false, gdy mergeinfo padł — caller przerywa (fail-closed).
        // workingCopyUrl trafia do snapshotu — Undo weryfikuje, czy nadal jesteśmy
        // na tej samej gałęzi (switch w obrębie projektu NIE czyści pliku snapshotu,
        // bo jest per-WC — bez tego Undo odwracałoby zakres na INNEJ gałęzi).
        private static async Task<bool> TryCaptureMergeSnapshotAsync(SVNMerge merge, string sourceUrl, string workingCopyUrl, CancellationToken token)
        {
            string eligible = await SvnRunner.RunAsync(
                $"mergeinfo {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} . --show-revs eligible",
                merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

            if (IsSvnErrorOutput(eligible))
            {
                merge.LogErrorLocal($"[Snapshot] Mergeinfo failed: {eligible.Trim()}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(eligible))
            {
                merge.LogInfo("[Snapshot] No eligible revisions – no rollback point created.");
                return true;
            }

            var revisions = ParseRevisionList(eligible);
            if (revisions.Count == 0)
            {
                merge.LogWarning("[Snapshot] Mergeinfo exists but revisions are invalid.");
                return true;
            }

            // === FIX (off-by-one): svn merge -r N:M aplikuje zmiany N+1..M — zakres
            // [first..last] wymaga pegu (first-1).
            long firstPeg = revisions.First() - 1;
            long lastEligible = revisions.Last();

            // === FIX (dziury): pełna lista — undo przez -c -N.
            string revisionList = string.Join(",", revisions);
            bool contiguous = (lastEligible - firstPeg) == revisions.Count;

            merge._snapshotManager.SetSnapshot(sourceUrl, firstPeg.ToString(), lastEligible.ToString(), revisionList, workingCopyUrl);
            merge._snapshotManager.SaveRollbackSnapshot();
            merge.LogInfoBlock("MERGE SNAPSHOT CREATED",
                $"Eligible: r{firstPeg + 1} → r{lastEligible}{(contiguous ? "" : " (non-contiguous)")}\n(Undo will reverse exactly these revisions)");
            return true;
        }

        private static async Task<string> ExecuteMergeCommandAsync(SVNMerge merge, string sourceUrl, bool isDryRun, CancellationToken token)
        {
            // === FIX P2: flaga "merge wystartował" TYLKO dla live — dry-run niczego
            // nie modyfikuje, cancel w dry-run nie może prowadzić do revert -R.
            if (!isDryRun) merge._mergeCommandStarted = true;

            string dryRunFlag = isDryRun ? "--dry-run " : string.Empty;
            string args = $"merge {dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
            merge.LogInfoBlock("SVN MERGE COMMAND", args);

            string context = isDryRun ? "Dry-Run Merge" : "Live Merge";

            try
            {
                return await RunWithHeartbeatAsync(merge, args, merge.SVNManager.WorkingDir, !isDryRun, token, context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("E155015"))
            {
                merge.LogWarning(isDryRun ? "[Merge] Conflicts in simulation." : "[Merge] Conflicts in live merge.");
                return ex.Message;
            }
            catch (Exception ex) when (IsAncestryError(ex))
            {
                merge.LogWarningBlock("ANCESTRY PROBLEM DETECTED", "Retrying with --ignore-ancestry.");
                string retryArgs = $"merge --ignore-ancestry {dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";

                return await RunWithHeartbeatAsync(merge, retryArgs, merge.SVNManager.WorkingDir, !isDryRun, token, $"{context} (Retry)").ConfigureAwait(false);
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

        public static async Task CherryPickMergeAsync(SVNMerge merge, string sourceInput, string revisionInput, bool isDryRun)
        {
            if (merge == null) return;
            if (merge.SVNManager == null || merge.SVNUI == null)
            {
                merge.LogErrorLocal("[Error] SVN Manager or UI not initialized.");
                return;
            }

            if (!merge.TryStart())
            {
                merge.LogWarning("[Cherry-pick] Another operation is already in progress.");
                return;
            }

            using var cts = new CancellationTokenSource();
            merge._mergeCts = cts;
            CancellationToken token = cts.Token;
            bool snapshotSessionStarted = false;   // lifecycle; false dla dry-run (nie kasuje punktu LIVE merge)

            try
            {
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

                var revisionItems = SvnRevisionRangeParser.Parse(revisionInput);
                if (revisionItems.Count == 0)
                {
                    merge.LogErrorLocal("[Cherry-pick] No valid revisions specified.");
                    return;
                }

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

                // === FIX (preview): prefiks ścieżki źródła — wyliczony, nie hardcoded
                string sourcePathPrefix = ComputeSourcePathPrefix(sourceUrl, repoRoot);

                string revisionDescription = string.Join(", ", revisionItems.Select(item =>
                    item.IsRange ? $"{item.Start}:{item.End}" : item.Start.ToString()));

                merge.LogInfoBlock("CHERRY-PICK SESSION START",
                    $"Source: {sourceUrl}\nTarget: {currentUrl}\nRevisions: {revisionDescription}\nMode: {(isDryRun ? "DRY RUN" : "LIVE")}");

                string currentUuid = (await SvnRunner.RunAsync(
                    "info --show-item repos-uuid",
                    merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();

                string sourceUuid = (await SvnRunner.RunAsync(
                    $"info {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --show-item repos-uuid",
                    merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();

                if (!string.Equals(currentUuid, sourceUuid, StringComparison.Ordinal))
                {
                    merge.LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                merge._hadLocalChangesBeforeMerge = await merge.HasPendingMergeChanges(token).ConfigureAwait(false);
                if (merge._hadLocalChangesBeforeMerge)
                {
                    merge.LogWarningBlock("MERGE BLOCKED",
                        "Working copy contains uncommitted changes.\nCommit, revert or cleanup before cherry-picking.");
                    return;
                }

                await RunWithHeartbeatAsync(merge, "update", merge.SVNManager.WorkingDir,
                    true, token, "Cherry-Pick Update").ConfigureAwait(false);

                // === FIX (semantyka): item = ZBIÓR rewizji Start..End → -r (Start-1):End
                var revisionArgs = new System.Text.StringBuilder();
                foreach (var item in revisionItems)
                {
                    if (item.IsRange)
                        revisionArgs.Append($"-r {item.Start - 1}:{item.End} ");
                    else
                        revisionArgs.Append($"-c {item.Start} ");
                }

                if (!isDryRun)
                {
                    // === FIX (lifecycle A): unieważnia punkt poprzedniej sesji
                    merge._snapshotManager.ClearRollbackSnapshot();
                    snapshotSessionStarted = true;

                    if (revisionItems.Count == 1)
                    {
                        var single = revisionItems[0];
                        string snapBefore = (single.Start - 1).ToString();
                        string snapAfter = single.End.ToString();
                        merge._snapshotManager.SetSnapshot(sourceUrl, snapBefore, snapAfter, null, currentUrl);
                        merge._snapshotManager.SaveRollbackSnapshot();
                        merge.LogInfoBlock("CHERRY-PICK SNAPSHOT", $"Created rollback point for {revisionDescription}.");
                    }
                    else
                    {
                        long totalRevisions = revisionItems.Sum(i => i.End - i.Start + 1);
                        if (totalRevisions <= MaxUndoListRevisions)
                        {
                            var allRevisions = new List<long>();
                            foreach (var item in revisionItems)
                                for (long r = item.Start; r <= item.End; r++)
                                    allRevisions.Add(r);

                            merge._snapshotManager.SetSnapshot(
                                sourceUrl,
                                (revisionItems.Min(i => i.Start) - 1).ToString(),
                                revisionItems.Max(i => i.End).ToString(),
                                string.Join(",", allRevisions),
                                currentUrl);
                            merge._snapshotManager.SaveRollbackSnapshot();
                            merge.LogInfoBlock("CHERRY-PICK SNAPSHOT", $"Created rollback point for {revisionDescription} (union list).");
                        }
                        else
                        {
                            // === FIX (pkt 3 analizy): LogWarningBlock zamiast jednolinijkowego
                            // LogWarning — to decision-critical info, musi być widoczne
                            merge.LogWarningBlock("CHERRY-PICK SNAPSHOT UNAVAILABLE",
                                $"Revision set too large ({totalRevisions} > {MaxUndoListRevisions}) for an automatic rollback point.\n" +
                                "No automatic Undo will be available for this cherry-pick.\n" +
                                "If you need to cancel its effects, use 'Revert to HEAD' to reset the working copy.");
                        }
                    }
                }

                merge.LogInfo($"[Cherry-pick] Fetching file changes for revisions: {revisionDescription}");
                string logArgs = $"log -r {revisionDescription.Replace(" ", "")} -v --xml {SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive";
                string logXml = await SvnRunner.RunAsync(logArgs, merge.SVNManager.WorkingDir, false, token).ConfigureAwait(false);

                // === FIX (preview): prefiks źródła przekazany do parsera
                var previewResult = ParseCherryPickLogXml(logXml, sourcePathPrefix, out string commitMsg);
                if (previewResult.Files.Count > 0)
                {
                    merge.LogInfoBlock("REVISION CONTENTS", $"Commit msg: {commitMsg}\nFiles affected: {previewResult.Files.Count}");
                    UnityMainThreadDispatcher.Enqueue(() => merge.RaiseDryRunCompleted(previewResult));
                }
                else
                {
                    merge.LogWarning($"[Cherry-pick] Revisions {revisionDescription} seem empty or contain no file changes.");
                }

                string dryRunFlag = isDryRun ? "--dry-run " : string.Empty;
                string args = $"merge {revisionArgs}{dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
                merge.LogInfo($"[Cherry-pick] Executing: svn {args}");

                if (!isDryRun) merge._mergeCommandStarted = true;

                string output;
                try
                {
                    output = await RunWithHeartbeatAsync(merge, args, merge.SVNManager.WorkingDir,
                        !isDryRun, token, "Cherry-Pick Merge").ConfigureAwait(false);
                }
                catch (Exception ex) when (ex.Message.Contains("E155015"))
                {
                    merge.LogWarning(isDryRun ? "[Cherry-pick] Conflicts in simulation." : "[Cherry-pick] Conflicts detected.");
                    output = ex.Message;
                }
                catch (Exception ex) when (IsAncestryError(ex))
                {
                    merge.LogWarning("[Cherry-pick] Ancestry problem – retrying with --ignore-ancestry...");
                    string retryArgs = $"merge {revisionArgs}{dryRunFlag}{SvnMergeUrlResolver.EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    output = await RunWithHeartbeatAsync(merge, retryArgs, merge.SVNManager.WorkingDir,
                        !isDryRun, token, "Cherry-Pick Merge (Retry)").ConfigureAwait(false);
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
                if (snapshotSessionStarted) merge._snapshotManager.ClearRollbackSnapshot();
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Cherry-pick Error] {ex.Message}");
                if (snapshotSessionStarted) merge._snapshotManager.ClearRollbackSnapshot();
            }
            finally
            {
                merge._hadLocalChangesBeforeMerge = false;
                merge._mergeCommandStarted = false;
                merge._mergeCts = null;
                merge.End();
            }
        }

        /// <summary>
        /// Parsowanie XML z 'svn log -v --xml' dla cherry-picka.
        ///
        /// === FIX (redundancja): usunięte 'result.Files = new List<...>()' — pole
        /// jest już zainicjalizowane w konstruktorze klasy MergeFileResult.
        ///
        /// === FIX (preview): obcinanie prefiksu WYLICZONEGO ze źródła, nie
        /// hardcoded "/trunk/". Cherry-pick z brancha pokazywał pełne ścieżki
        /// "/branches/x/Assets/..." zamiast relatywnych. Dotyczy wyłącznie
        /// preview UI — merge operuje na URL-ach i nie czyta nic z tego parsera.
        /// </summary>
        private static SVNMerge.MergeFileResult ParseCherryPickLogXml(
            string xmlOutput, string sourcePathPrefix, out string message)
        {
            var result = new SVNMerge.MergeFileResult();
            message = "N/A";

            if (string.IsNullOrWhiteSpace(xmlOutput)) return result;

            string prefix = (sourcePathPrefix ?? "").Trim().TrimEnd('/');
            bool hasPrefix = prefix.Length > 0;

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

                        // === FIX: prefiks źródła (dla trunka — "/trunk" — zachowanie
                        // identyczne z dotychczasowym; dla brancha — jego własny prefiks)
                        if (hasPrefix)
                        {
                            if (filePath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                filePath = ". (Root / Property Change)";
                            }
                            else if (filePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                            {
                                filePath = filePath.Substring(prefix.Length + 1);
                            }
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

        #region Private Helpers - Heartbeat & Parsing

        // === NOWA: błąd mergeinfo potrafi wrócić jako tekst "svn: E..." zamiast
        // wyjątku (semantyka flagi w RunAsync nie została rozstrzygnięta) — bez tego
        // check wyglądał jak pusty eligible = "zsynchronizowany" (fail-open).
        private static bool IsSvnErrorOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return false;   // puste = legalny "brak eligible"
            string trimmed = output.TrimStart();
            return trimmed.StartsWith("svn: E", StringComparison.Ordinal) ||
                   trimmed.StartsWith("svn: W", StringComparison.Ordinal);
        }

        private static async Task<string> RunWithHeartbeatAsync(SVNMerge merge, string command, string workingDir, bool logErrors, CancellationToken token, string context)
        {
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var heartbeatTask = RunHeartbeatLoopAsync(merge, heartbeatCts.Token, context);

            try
            {
                return await SvnRunner.RunAsync(command, workingDir, logErrors, token).ConfigureAwait(false);
            }
            finally
            {
                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch { }
            }
        }

        private static async Task RunHeartbeatLoopAsync(SVNMerge merge, CancellationToken token, string context)
        {
            int seconds = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(15000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                seconds += 15;
                merge.LogInfo($"[{context}] Still working... ({seconds}s elapsed)");
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

        /// Repo-relatywna ścieżka ŹRÓDŁA ("/trunk", "/branches/foo") — do preview
        /// cherry-picka. sourceUrl pochodzi zawsze z ResolveSourceUrl
        /// (repoRoot + /trunk|/branches/...|/tags/...), więc prefiks jest bezpieczny.
        /// Zwraca null, gdy sourceUrl nie leży pod repoRoot — wtedy preview nie obcina.
        private static string ComputeSourcePathPrefix(string sourceUrl, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl)) return null;

            string url = sourceUrl.Trim().TrimEnd('/');
            string root = (repoRoot ?? "").Trim().TrimEnd('/');

            if (root.Length > 0 &&
                url.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                return url.Substring(root.Length);   // "/trunk" | "/branches/foo" | "/tags/x"
            }

            return null;
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

                // === FIX P3 (domknięty): BEZ SshConfigOption — SSH zapewnia SvnRunner
                // (env SVN_SSH per proces). Prefiks konkurował z env (własna analiza P3).
                string logOutput = await SvnRunner.RunAsync(
                    "log --stop-on-copy --xml --verbose -l 20",
                    merge.SVNManager.WorkingDir, true, token).ConfigureAwait(false);

                long targetRev = FindIncompleteReintegrateRevisionInXml(logOutput);
                if (targetRev <= 0)
                {
                    merge.LogInfo("[RepairMergeHistory] No incomplete reintegrate found in log.");
                    return null;
                }

                merge.LogInfo($"[RepairMergeHistory] Found candidate at r{targetRev}. Attempting to determine source branch from mergeinfo diff...");
                string diffOutput = await SvnRunner.RunAsync(
                    $"diff -c {targetRev} --properties-only",
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

        #endregion
    }
}