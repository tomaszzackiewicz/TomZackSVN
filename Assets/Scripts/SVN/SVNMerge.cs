using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMerge : SVNBase
    {
        public event Action<MergeFileResult> OnDryRunCompleted;

        private const string PrefMergeSource = "SVN_UndoMerge_Source";
        private const string PrefMergeRevBefore = "SVN_UndoMerge_RevBefore";
        private const string PrefMergeRevAfter = "SVN_UndoMerge_RevAfter";
        private const string PrefHasRollback = "SVN_UndoMerge_HasRollback";
        private const string PrefMergeTimestamp = "SVN_UndoMerge_Timestamp";

        private string _lastMergeSource;
        private bool _hasRollbackPoint;
        private string _lastMergeRevisionBefore;
        private string _lastMergeRevisionAfter;
        private int _lastIncomingCount = -1;

        private float _lastRevertToHeadClickTime = -10f;

        private bool _branchesCacheValid;
        private string[] _cachedBranches;
        private int _isFetchingBranchesFlag;
        private int _isMergingFlag;

        private CancellationTokenSource _mergeCts;

        private static readonly HashSet<char> ValidMergeStates = new("UADGRCM");

        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            LoadRollbackSnapshot();
        }

        #region Public API

        public void CancelMerge()
        {
            _mergeCts?.Cancel();
            LogWarning("[Merge] Cancel requested by user.");
        }

        public async Task ExecuteMerge(string sourceInput, bool isDryRun)
        {
            if (await HasPendingMergeChanges().ConfigureAwait(false))
            {
                LogWarningBlock("MERGE BLOCKED", "Working copy contains uncommitted merge changes.\nCommit, revert or cleanup before merging again.");
                return;
            }

            if (!TryEnterMerging()) return;
            if (!TryStart()) { ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceInput))
                    return;

                LogInfoBlock("MERGE SESSION START", $"Source: {sourceInput}\nMode: {(isDryRun ? "DRY RUN" : "LIVE MERGE")}");

                string repoRoot = GetRepoRootSafe();
                if (string.IsNullOrWhiteSpace(repoRoot)) { LogErrorLocal("Repo Root not found."); return; }

                if (IsInvalidPath(sourceInput))
                {
                    LogErrorLocal("SECURITY: Invalid merge source.");
                    return;
                }

                string cleanedInput = sourceInput.Trim();
                string sourceUrl = cleanedInput.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/branches/{EscapeSvnArg(cleanedInput)}";

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
                LogInfo($"Current URL: {currentUrl}");
                LogInfo($"Source URL : {sourceUrl}");

                bool sourceIsTrunk = Normalize(sourceUrl).EndsWith("/trunk");
                bool currentIsTrunk = Normalize(currentUrl).EndsWith("/trunk");

                if (sourceIsTrunk && !currentIsTrunk && _lastIncomingCount == 0)
                {
                    LogInfoBlock("Merge Blocked", "Branch is already fully synchronized with Trunk.\nNo incoming revisions to pull. Operation aborted safely.");
                    return;
                }

                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Cannot merge branch into itself.");
                    return;
                }

                string currentUuid = (await SvnRunner.RunAsync("info --show-item repos-uuid", svnManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();
                string sourceUuid = (await SvnRunner.RunAsync($"info \"{sourceUrl}\" --show-item repos-uuid", svnManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();

                if (!string.Equals(currentUuid, sourceUuid, StringComparison.Ordinal))
                {
                    LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                LogInfo("[Merge] Bringing working copy to a uniform revision...");
                try
                {
                    await SvnRunner.RunAsync("update", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    LogInfo("[Merge] Update completed.");
                }
                catch (Exception ex)
                {
                    LogWarning($"[Merge] Update failed (non‑fatal): {ex.Message}");
                }

                if (!isDryRun)
                {
                    var state = await TryCaptureMergeSnapshot(sourceUrl, token).ConfigureAwait(false);
                    if (state == MergeSnapshotState.Error)
                        LogWarning("[Merge] Snapshot capture failed.");
                }

                string output = await ExecuteMergeCommand(sourceUrl, isDryRun, token).ConfigureAwait(false);
                await ParseMergeOutput(output, isDryRun, token).ConfigureAwait(false);

                if (!isDryRun)
                {
                    await svnManager.RefreshStatus().ConfigureAwait(false);
                    await RefreshResolveUI().ConfigureAwait(false);
                    LogSuccess("[Merge Complete]");
                }
            }
            catch (OperationCanceledException)
            {
                LogWarning("[Merge] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Merge Error] {ex.Message}");
            }
            finally
            {
                _mergeCts = null;
                ExitMerging();
                End();
            }
        }

        public async Task UndoLastMerge(bool autoCommit = false)
        {
            if (!TryStart()) return;

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                LogInfo("========== UNDO LAST MERGE ==========");
                if (await HasPendingMergeChanges(token).ConfigureAwait(false))
                {
                    LogWarningBlock("Undo Blocked", "Uncommitted changes detected.\nCommit or cancel current changes before undoing the last merge.");
                    return;
                }

                if (!_hasRollbackPoint) LoadRollbackSnapshot();

                if (!_hasRollbackPoint || string.IsNullOrWhiteSpace(_lastMergeSource) ||
                    string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) || string.IsNullOrWhiteSpace(_lastMergeRevisionAfter))
                {
                    LogWarning("[Undo] No rollback point available. Perform a merge first.");
                    return;
                }

                LogInfo($"[Undo] Source : {_lastMergeSource}");
                LogInfo($"[Undo] Range  : r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
                LogInfo("[Undo] Bringing working copy to a uniform revision...");

                try { await SvnRunner.RunAsync("update", svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (Exception ex) { LogWarning($"[Undo] Update failed (non‑fatal): {ex.Message}"); }

                string range = $"{_lastMergeRevisionAfter}:{_lastMergeRevisionBefore}";
                string args = $"merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --non-interactive --accept postpone";
                LogInfo($"[Undo] Executing: svn {args}");

                string output;
                try { output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex.Message.Contains("mixed-revision") || ex.Message.Contains("E195020"))
                {
                    LogWarning("[Undo] Mixed-revision detected – retrying after another update...");
                    await SvnRunner.RunAsync("update", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsAncestryError(ex))
                {
                    LogWarning("[Undo] Ancestry issue – retrying with --ignore-ancestry...");
                    args = $"merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --ignore-ancestry --non-interactive --accept postpone";
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }

                if (autoCommit)
                {
                    string msg = $"Undo merge from {_lastMergeSource} (r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})";
                    LogInfo($"[Undo] Auto‑committing: {msg}");
                    await SvnRunner.RunAsync($"commit -m \"{msg}\"", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    LogSuccess("[Undo] Changes committed automatically.");
                }

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshResolveUI().ConfigureAwait(false);

                LogSuccessBlock("Undo Complete", $"Successfully reverted merge of {_lastMergeSource} (r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[Undo] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorLocal("[Undo Error] " + ex.Message);
            }
            finally
            {
                _mergeCts = null;
                End();
            }
        }

        public async Task CancelLocalMerge()
        {
            if (!TryStart()) return;

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                if (!_hasRollbackPoint || string.IsNullOrWhiteSpace(_lastMergeSource) ||
                    string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) || string.IsNullOrWhiteSpace(_lastMergeRevisionAfter))
                {
                    LogWarning("[Cancel Local Merge] No merge snapshot available. Perform a merge first.");
                    return;
                }

                LogInfoBlock("CANCEL LOCAL MERGE",
                    $"Source: {_lastMergeSource}\nRevisions: r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");

                string range = $"{_lastMergeRevisionAfter}:{_lastMergeRevisionBefore}";
                string args = $"merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --non-interactive --accept postpone";

                string output;
                try { output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (Exception ex) when (IsAncestryError(ex))
                {
                    LogWarning("[CancelLocalMerge] Ancestry issue – retrying with --ignore-ancestry...");
                    args = $"merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --ignore-ancestry --non-interactive --accept postpone";
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(output) || output.Contains("No changes"))
                {
                    LogInfo("[CancelLocalMerge] No changes to revert.");
                }
                else
                {
                    int reverted = CountLinesMatching(output, @"^[A-Z]\s");
                    LogSuccess($"[CancelLocalMerge] Successfully reverted {reverted} files.");
                }

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshResolveUI().ConfigureAwait(false);

                LogSuccess("[Cancel Local Merge Complete] Merge changes have been reverted.");
                LogInfo("Files with status 'R' are locally scheduled for replacement.");
                LogInfo("To clear 'R', commit the undo (or use RevertToHead to discard everything).");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[CancelLocalMerge] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex) { LogErrorLocal($"[CancelLocalMerge Error] {ex.Message}"); }
            finally
            {
                _mergeCts = null;
                End();
            }
        }

        public async Task RevertToHead()
        {
            float timeSinceLastClick = Time.time - _lastRevertToHeadClickTime;
            if (timeSinceLastClick > 5f)
            {
                _lastRevertToHeadClickTime = Time.time;
                LogWarningBlock("Reset to HEAD", "This will discard ALL local changes!\nPress the button again within 5 seconds to confirm.");
                return;
            }
            _lastRevertToHeadClickTime = -10f;

            if (!TryStart()) return;

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                LogWarning("[Reset to HEAD] Reverting all local changes...");
                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token).ConfigureAwait(false);

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshResolveUI().ConfigureAwait(false);

                LogSuccess("[Reset Complete] Working copy is now at HEAD.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[RevertToHead] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex) { LogErrorLocal($"[Reset Error] {ex.Message}"); }
            finally
            {
                _mergeCts = null;
                End();
            }
        }

        public async Task CompareWithTrunk()
        {
            if (!TryStart()) return;

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                LogInfoBlock("Comparison", "Starting analysis against Trunk...");

                string repoRoot = GetRepoRootSafe();
                if (string.IsNullOrEmpty(repoRoot)) { LogErrorLocal("Repo Root not found."); return; }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";
                LogInfo($"Target: {trunkUrl}");

                if (Normalize(currentUrl) == Normalize(trunkUrl))
                {
                    LogWarning("Already on Trunk. Comparison skipped.");
                    return;
                }

                LogInfo("Fetching revision differences...");
                string missingCmd = $"mergeinfo \"{trunkUrl}\" --show-revs eligible";
                string missingInBranch = await SvnRunner.RunAsync(missingCmd, svnManager.WorkingDir, false, token).ConfigureAwait(false);
                string localCmd = $"mergeinfo . \"{trunkUrl}\" --show-revs eligible";
                string branchOnlyChanges = await SvnRunner.RunAsync(localCmd, svnManager.WorkingDir, false, token).ConfigureAwait(false);

                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);
                _lastIncomingCount = missingCount;

                LogInfo("--------------------------------------");
                LogInfo($"Incoming (Trunk -> Branch): {missingCount}");
                LogInfo($"Outgoing (Branch -> Trunk): {localCount}");

                if (missingCount > 0 || localCount > 0)
                {
                    LogWarning("DIVERGENCE DETECTED: trunk and branch are out of sync.");
                    if (missingCount == 0) LogSuccess("No incoming changes. You only have local commits to push back.");
                }
                else LogSuccess("Fully synchronized with Trunk. No merge needed.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[CompareWithTrunk] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex) { LogErrorLocal($"[Comparison Error] {ex.Message}"); }
            finally
            {
                _mergeCts = null;
                End();
            }
        }

        public async Task<string[]> FetchAvailableBranches(bool force = false)
        {
            if (_isFetchingBranchesFlag == 1)
            {
                LogInfo("[Branches] Fetch already in progress → returning cache.");
                return _cachedBranches ?? Array.Empty<string>();
            }

            if (!force && _branchesCacheValid && _cachedBranches != null)
            {
                LogInfo("[Cache] Using cached branches.");
                return _cachedBranches;
            }

            if (!TryStart()) return _cachedBranches ?? Array.Empty<string>();

            if (Interlocked.CompareExchange(ref _isFetchingBranchesFlag, 1, 0) != 0)
            {
                End();
                return _cachedBranches ?? Array.Empty<string>();
            }

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string repoRoot = GetRepoRootSafe();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    string rootOutput = await SvnRunner.RunAsync("info --show-item repos-root-url", svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);
                    repoRoot = rootOutput?.Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(repoRoot))
                    {
                        LogErrorLocal("[Critical Error] Repo root missing.");
                        return Array.Empty<string>();
                    }
                }

                string branchesUrl = $"{repoRoot}/branches";
                LogInfo($"[Debug] Scanning branches at: {branchesUrl}");

                string rawOutput = await SvnRunner.RunAsync($"list \"{branchesUrl}\" --non-interactive", svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawOutput))
                {
                    LogWarning("Branches folder is empty.");
                    _cachedBranches = Array.Empty<string>();
                    _branchesCacheValid = true;
                    return _cachedBranches;
                }

                var branchList = rawOutput
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimEnd('/'))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => !x.StartsWith("*"))
                    .Where(x => x.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) < 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray();

                _cachedBranches = branchList;
                _branchesCacheValid = true;
                LogSuccess($"Found {branchList.Length} branch(es).");
                return branchList;
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Critical Error] Scan failed: {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                Interlocked.Exchange(ref _isFetchingBranchesFlag, 0);
                End();
            }
        }

        public async Task ForceMergeFromTrunk()
        {
            if (!TryEnterMerging()) { LogWarning("[Force Merge] Already running — request ignored."); return; }
            if (!TryStart()) { ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                LogInfoBlock("FORCE MERGE FROM TRUNK",
                    "Ignoring ancestry and merging trunk changes into current branch.");

                string repoRoot = GetRepoRootSafe();
                if (string.IsNullOrWhiteSpace(repoRoot)) { LogErrorLocal("Repo Root not found."); return; }

                string sourceUrl = $"{repoRoot}/trunk";
                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Already on trunk. Cannot merge trunk into itself.");
                    return;
                }

                await TryCaptureMergeSnapshot(sourceUrl, token).ConfigureAwait(false);

                string args;
                if (_hasRollbackPoint && !string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) &&
                    !string.IsNullOrWhiteSpace(_lastMergeRevisionAfter) &&
                    _lastMergeRevisionBefore != _lastMergeRevisionAfter)
                {
                    string range = $"{_lastMergeRevisionBefore}:{_lastMergeRevisionAfter}";
                    args = $"merge -r {range} \"{sourceUrl}\" --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo($"[Force Merge] Range: {range}");
                }
                else
                {
                    args = $"merge \"{sourceUrl}\" --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo("[Force Merge] No revision range available – merging all trunk changes.");
                }

                LogInfo($"[Force Merge] Executing: svn {args}");
                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token).ConfigureAwait(false);
                await ParseMergeOutput(output, false, token).ConfigureAwait(false);

                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshResolveUI().ConfigureAwait(false);

                LogSuccess("[Force Merge Complete] Trunk changes have been applied.");
                LogWarning("PLEASE COMMIT this merge immediately to record the history.");
                LogWarning("Without a commit, SVN may attempt to re-merge the same changes in the future.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[ForceMerge] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex) { LogErrorLocal($"[Force Merge Error] {ex.Message}"); }
            finally
            {
                _mergeCts = null;
                ExitMerging();
                End();
            }
        }

        public async Task RepairMergeHistory()
        {
            if (!TryEnterMerging()) { LogWarning("[RepairReintegrateHistory] Already merging..."); return; }
            if (!TryStart()) { ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string repoRoot = GetRepoRootSafe();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[RepairReintegrateHistory] Repo root not found.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
                bool isTrunk = Normalize(currentUrl).EndsWith("/trunk");
                if (!isTrunk)
                {
                    LogErrorLocal("[RepairReintegrateHistory] This operation must be performed on trunk.");
                    LogErrorLocal("Please switch to trunk first and then run this command.");
                    return;
                }

                LogInfoBlock("REPAIR REINTEGRATE HISTORY",
                    "This will find the incomplete reintegrate commit and record it as fully merged.\nNo files will be changed – only svn:mergeinfo metadata.");

                LogInfo("[RepairReintegrateHistory] Searching for incomplete reintegrate commit...");
                string logOutput = await SvnRunner.RunAsync("log --stop-on-copy --xml --verbose -l 20", svnManager.WorkingDir, true, token).ConfigureAwait(false);

                long targetRev = await FindIncompleteReintegrateRevisionAsync(logOutput, token).ConfigureAwait(false);
                if (targetRev <= 0)
                {
                    LogSuccess("[RepairReintegrateHistory] No incomplete reintegrate commit found. History may already be clean.");
                    return;
                }

                LogInfo($"[RepairReintegrateHistory] Found possible incomplete reintegrate at r{targetRev}");

                string sourceUrl = await DetermineSourceBranchAsync(repoRoot, targetRev, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sourceUrl))
                {
                    LogErrorLocal("[RepairReintegrateHistory] Could not determine source branch. Please select the branch in the Merge panel and try again.");
                    return;
                }

                LogInfo($"[RepairReintegrateHistory] Source branch: {sourceUrl}");
                string args = $"merge --record-only --ignore-ancestry \"{sourceUrl}\" --non-interactive --accept postpone";
                LogInfo($"[RepairReintegrateHistory] Executing: svn {args}");
                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token).ConfigureAwait(false);

                if (output.Contains("Recording") || output.Contains("recorded") || string.IsNullOrWhiteSpace(output))
                {
                    LogSuccess("[RepairReintegrateHistory] Mergeinfo successfully recorded.");
                    LogSuccess("Please commit this change immediately.");
                    LogSuccess("After commit, standard reintegrate from branch to trunk will work correctly.");
                    await svnManager.RefreshStatus().ConfigureAwait(false);
                }
                else
                {
                    LogErrorLocal($"[RepairReintegrateHistory] Unexpected output: {output}");
                }
            }
            catch (OperationCanceledException)
            {
                LogWarning("[RepairMergeHistory] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex) { LogErrorLocal($"[RepairReintegrateHistory Error] {ex.Message}"); }
            finally
            {
                _mergeCts = null;
                ExitMerging();
                End();
            }
        }

        #endregion

        #region Snapshot & Rollback

        [Serializable]
        private class SnapshotData
        {
            public string Source;
            public string RevisionBefore;
            public string RevisionAfter;
            public string Timestamp;
        }

        private string SnapshotFilePath
        {
            get
            {
                string wd = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(wd)) return null;
                return Path.Combine(wd, ".svn", "merge_snapshot.json");
            }
        }

        private void SaveSnapshotToFile()
        {
            try
            {
                string path = SnapshotFilePath;
                if (path == null) return;

                var data = new SnapshotData
                {
                    Source = _lastMergeSource,
                    RevisionBefore = _lastMergeRevisionBefore,
                    RevisionAfter = _lastMergeRevisionAfter,
                    Timestamp = DateTime.Now.ToString("o")
                };

                File.WriteAllText(path, JsonUtility.ToJson(data, true));
                LogInfo($"[Snapshot] Saved to file: {path}");
            }
            catch (Exception ex)
            {
                LogWarning($"[Snapshot] File save failed: {ex.Message}");
            }
        }

        private bool LoadSnapshotFromFile()
        {
            try
            {
                string path = SnapshotFilePath;
                if (path == null || !File.Exists(path)) return false;

                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<SnapshotData>(json);

                if (data == null || string.IsNullOrWhiteSpace(data.Source)
                    || string.IsNullOrWhiteSpace(data.RevisionBefore)
                    || string.IsNullOrWhiteSpace(data.RevisionAfter))
                    return false;

                _lastMergeSource = data.Source;
                _lastMergeRevisionBefore = data.RevisionBefore;
                _lastMergeRevisionAfter = data.RevisionAfter;
                _hasRollbackPoint = true;

                LogInfo($"[Snapshot] Loaded from file: {data.Source} | r{data.RevisionBefore} → r{data.RevisionAfter} | Timestamp: {data.Timestamp}");
                return true;
            }
            catch (Exception ex)
            {
                LogWarning($"[Snapshot] File load failed: {ex.Message}");
                return false;
            }
        }

        private void DeleteSnapshotFile()
        {
            try
            {
                string path = SnapshotFilePath;
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private void SaveRollbackSnapshot()
        {
            if (!_hasRollbackPoint) return;

            PlayerPrefs.SetString(PrefMergeSource, _lastMergeSource ?? "");
            PlayerPrefs.SetString(PrefMergeRevBefore, _lastMergeRevisionBefore ?? "");
            PlayerPrefs.SetString(PrefMergeRevAfter, _lastMergeRevisionAfter ?? "");
            PlayerPrefs.SetInt(PrefHasRollback, 1);
            PlayerPrefs.SetString(PrefMergeTimestamp, DateTime.Now.ToString("o"));
            PlayerPrefs.Save();

            SaveSnapshotToFile();

            LogInfo($"[Snapshot] Saved → {_lastMergeSource} | r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
        }

        private void LoadRollbackSnapshot()
        {
            if (PlayerPrefs.GetInt(PrefHasRollback, 0) == 1)
            {
                _lastMergeSource = PlayerPrefs.GetString(PrefMergeSource, "");
                _lastMergeRevisionBefore = PlayerPrefs.GetString(PrefMergeRevBefore, "");
                _lastMergeRevisionAfter = PlayerPrefs.GetString(PrefMergeRevAfter, "");
                _hasRollbackPoint = !string.IsNullOrWhiteSpace(_lastMergeSource)
                                    && !string.IsNullOrWhiteSpace(_lastMergeRevisionBefore)
                                    && !string.IsNullOrWhiteSpace(_lastMergeRevisionAfter);

                if (_hasRollbackPoint)
                {
                    string ts = PlayerPrefs.GetString(PrefMergeTimestamp, "unknown");
                    LogInfo($"[Snapshot] Loaded from PlayerPrefs → {_lastMergeSource} | r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter} | Timestamp: {ts}");
                    return;
                }
            }

            if (LoadSnapshotFromFile())
            {
                PlayerPrefs.SetString(PrefMergeSource, _lastMergeSource);
                PlayerPrefs.SetString(PrefMergeRevBefore, _lastMergeRevisionBefore);
                PlayerPrefs.SetString(PrefMergeRevAfter, _lastMergeRevisionAfter);
                PlayerPrefs.SetInt(PrefHasRollback, 1);
                PlayerPrefs.Save();
                return;
            }

            _hasRollbackPoint = false;
            LogInfo("[Snapshot] No valid rollback snapshot found.");
        }

        private void ClearRollbackSnapshot()
        {
            _hasRollbackPoint = false;
            _lastMergeSource = null;
            _lastMergeRevisionBefore = null;
            _lastMergeRevisionAfter = null;

            PlayerPrefs.DeleteKey(PrefMergeSource);
            PlayerPrefs.DeleteKey(PrefMergeRevBefore);
            PlayerPrefs.DeleteKey(PrefMergeRevAfter);
            PlayerPrefs.DeleteKey(PrefHasRollback);
            PlayerPrefs.DeleteKey(PrefMergeTimestamp);
            PlayerPrefs.Save();

            DeleteSnapshotFile();

            LogInfo("[Snapshot] Cleared from memory, PlayerPrefs and file.");
        }

        private enum MergeSnapshotState { Error, FirstMerge, ExistingMerge }

        private async Task<MergeSnapshotState> TryCaptureMergeSnapshot(string sourceUrl, CancellationToken token)
        {
            try
            {
                string eligible = await SvnRunner.RunAsync(
                    $"mergeinfo \"{sourceUrl}\" . --show-revs eligible",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(eligible))
                {
                    LogInfo("[Snapshot] No merge history found – creating first‑merge snapshot.");
                    string currentRevision = await GetWorkingCopyRevision(token).ConfigureAwait(false);

                    _lastMergeSource = sourceUrl;
                    _lastMergeRevisionBefore = currentRevision;
                    _lastMergeRevisionAfter = currentRevision;
                    _hasRollbackPoint = true;
                    SaveRollbackSnapshot();

                    LogInfoBlock("FIRST MERGE SNAPSHOT CREATED", $"Base Revision : r{currentRevision}");
                    return MergeSnapshotState.FirstMerge;
                }

                var revisions = eligible
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("r"))
                    .Select(x => x.TrimStart('r'))
                    .Select(x => (ok: long.TryParse(x, out long rev), rev))
                    .Where(x => x.ok)
                    .Select(x => x.rev)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                if (revisions.Count == 0)
                {
                    LogWarning("[Snapshot] Mergeinfo exists but revisions are invalid.");
                    return MergeSnapshotState.Error;
                }

                _lastMergeSource = sourceUrl;
                _lastMergeRevisionBefore = (revisions.First() - 1).ToString();
                _lastMergeRevisionAfter = revisions.Last().ToString();
                _hasRollbackPoint = true;
                SaveRollbackSnapshot();

                LogInfoBlock("MERGE SNAPSHOT CREATED", $"Source Revision Range : r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
                return MergeSnapshotState.ExistingMerge;
            }
            catch (Exception ex)
            {
                LogWarning($"[Snapshot Error] {ex.Message}");
                _hasRollbackPoint = false;
                return MergeSnapshotState.Error;
            }
        }

        #endregion

        #region Merge Execution

        private async Task<string> ExecuteMergeCommand(string sourceUrl, bool isDryRun, CancellationToken token)
        {
            string dryRunFlag = isDryRun ? "--dry-run " : string.Empty;
            string args = $"merge {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";

            LogInfoBlock("SVN MERGE COMMAND", args);

            try
            {
                return await SvnRunner.RunAsync(args, svnManager.WorkingDir, !isDryRun, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsAncestryError(ex))
            {
                LogWarningBlock("ANCESTRY PROBLEM DETECTED", "Standard merge failed. Retrying with --ignore-ancestry.");

                args = $"merge --ignore-ancestry {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";

                LogInfoBlock("SVN MERGE RETRY", args);

                return await SvnRunner.RunAsync(args, svnManager.WorkingDir, !isDryRun, token).ConfigureAwait(false);
            }
        }

        private async Task ParseMergeOutput(string output, bool isDryRun, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(output) || output.IndexOf("already up to date", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LogSuccess("Everything is already up to date.");
                if (isDryRun) OnDryRunCompleted?.Invoke(new MergeFileResult());
                return;
            }

            var result = new MergeFileResult();
            int conflicts = 0, changed = 0, skipped = 0, realChanges = 0;
            bool mergeInfoUpdated = false;

            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();

                string line = raw.TrimStart();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains("Recording mergeinfo", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("recorded mergeinfo", StringComparison.OrdinalIgnoreCase))
                {
                    mergeInfoUpdated = true;
                    continue;
                }

                if (line.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

                char state = line[0];
                bool isConflictLine = state == 'C' ||
                    line.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("tree conflict", StringComparison.OrdinalIgnoreCase);

                if (isConflictLine)
                {
                    conflicts++;
                    string conflictPath = line.Length > 2 ? line.Substring(2).Trim() : line;
                    result.Files.Add(new MergeFileInfo { State = 'C', Path = conflictPath });
                    continue;
                }

                switch (state)
                {
                    case 'A': result.Added++; break;
                    case 'U': case 'G': result.Updated++; break;
                    case 'D': result.Deleted++; break;
                }

                if (ValidMergeStates.Contains(state))
                {
                    changed++;
                    bool isMergeInfoOnly = line.Trim() == "." || line.EndsWith(" .") || (line.EndsWith(".") && line.Length <= 3);
                    if (!isMergeInfoOnly && line.Length > 2 && line[1] == ' ' && (line[2] == ' ' || line[2] == '\t'))
                    {
                        realChanges++;
                        string path = line.Substring(2).Trim();
                        if (!string.IsNullOrWhiteSpace(path))
                            result.Files.Add(new MergeFileInfo { State = state, Path = path });
                    }
                }
            }

            result.Conflicts = conflicts;
            result.Skipped = skipped;
            result.MergeInfoUpdated = mergeInfoUpdated;
            result.RealChanges = realChanges;

            if (isDryRun)
            {
                LogInfoBlock("DRY RUN RESULT",
                    $"Potential file changes : {realChanges}\nConflicts detected     : {conflicts}" +
                    (mergeInfoUpdated ? "\nSVN merge history would be updated." : "") +
                    (skipped > 0 ? $"\nSkipped items          : {skipped}" : "") +
                    (realChanges == 0 && conflicts == 0 ? "\nNo incoming file changes detected." : ""));

                OnDryRunCompleted?.Invoke(result);
                return;
            }

            LogSuccessBlock("MERGE COMPLETED SUCCESSFULLY", null);

            var realStats = await GetRealDiffStats().ConfigureAwait(false);
            LogInfo($"Total change entries : {changed}");
            LogInfo($"Added files      : {realStats.added}");
            LogInfo($"Updated files    : {realStats.updated}");
            LogInfo($"Deleted files    : {realStats.deleted}");

            if (mergeInfoUpdated) LogInfo("Merge history updated.");
            if (conflicts > 0) LogErrorLocal($"Conflicts detected : {conflicts}");
            if (skipped > 0) LogWarning($"Skipped items : {skipped}");
            if (realChanges == 0 && conflicts == 0) LogSuccess("Merge executed but no real file changes were applied.");

            LogSuccess("Review changes before commit.");

            if (result.Files.Count > 0) OnDryRunCompleted?.Invoke(result);
        }

        #endregion

        #region Repair Helpers

        private async Task<long> FindIncompleteReintegrateRevisionAsync(string logOutput, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(logOutput)) return -1;

            try
            {
                using var reader = XmlReader.Create(new StringReader(logOutput));
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "logentry")
                    {
                        string revStr = reader.GetAttribute("revision");
                        if (!long.TryParse(revStr, out long rev)) continue;

                        bool hasTrunkPropMod = false;
                        while (reader.Read())
                        {
                            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "logentry") break;
                            if (reader.NodeType == XmlNodeType.Element && reader.Name == "path")
                            {
                                string value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                string propMods = reader.GetAttribute("prop-mods") ?? "false";
                                string action = reader.GetAttribute("action") ?? "";

                                if ((value == "/trunk" || value == "/trunk/") && propMods == "true" && action == "M")
                                {
                                    hasTrunkPropMod = true;
                                }
                            }
                        }

                        if (hasTrunkPropMod) return rev;
                    }
                }
            }
            catch { /* fallback */ }

            return -1;
        }

        private async Task<string> DetermineSourceBranchAsync(string repoRoot, long targetRev, CancellationToken token)
        {
            string logEntry = await SvnRunner.RunAsync($"log -r {targetRev} --xml --verbose", svnManager.WorkingDir, true, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(logEntry)) return null;

            try
            {
                using var reader = XmlReader.Create(new StringReader(logEntry));
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "path")
                    {
                        string action = reader.GetAttribute("action") ?? "";
                        string copyFrom = reader.GetAttribute("copyfrom-path") ?? "";
                        string value = reader.ReadElementContentAsString();

                        if ((action == "A" || action == "M") && value.StartsWith("/branches/") &&
                            (!string.IsNullOrEmpty(copyFrom) || value.Contains("(from ")))
                        {
                            if (!string.IsNullOrEmpty(copyFrom) && copyFrom.Contains("/branches/"))
                                return $"{repoRoot}{copyFrom}";

                            // Parse "(from /branches/...)" syntax
                            int fromIdx = value.IndexOf("(from ", StringComparison.Ordinal);
                            if (fromIdx >= 0)
                            {
                                string fromPart = value.Substring(fromIdx + 6).TrimEnd(')');
                                fromPart = fromPart.Split(':')[0].Trim();
                                if (fromPart.StartsWith("/")) return $"{repoRoot}{fromPart}";
                            }
                        }
                    }
                }
            }
            catch { /* fallback */ }

            string manualBranch = svnUI?.MergeSourceInput?.text?.Trim();
            if (!string.IsNullOrEmpty(manualBranch) && !manualBranch.Equals("trunk", StringComparison.OrdinalIgnoreCase))
            {
                LogInfo($"[RepairReintegrateHistory] Using manually selected branch: {manualBranch}");
                return $"{repoRoot}/branches/{EscapeSvnArg(manualBranch)}";
            }

            return null;
        }

        #endregion

        #region Static / Utility

        public static async Task<SvnStats> GetStatsAsync(string workingDir, CancellationToken token = default)
        {
            string output = await SvnRunner.RunAsync("status", workingDir, false, token).ConfigureAwait(false);
            var stats = new SvnStats();
            foreach (string line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 1) continue;
                switch (line[0])
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

        private async Task SafeCleanupAfterCancel()
        {
            try
            {
                LogWarning("[Merge] Reverting unfinished merge changes...");
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir, true, cleanupCts.Token).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, cleanupCts.Token).ConfigureAwait(false);
                LogWarning("[Merge] Working copy cleaned up.");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Merge] Cleanup after cancel failed: {ex.Message}");
                LogWarning("[Merge] You may need to run 'svn revert -R .' and 'svn cleanup' manually.");
            }
        }

        private async Task<(int added, int updated, int deleted)> GetRealDiffStats()
        {
            try
            {
                string output = await SvnRunner.RunAsync("diff --summarize", svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return (0, 0, 0);
                int a = 0, u = 0, d = 0;
                foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.TrimStart();
                    if (line.Length == 0) continue;
                    switch (line[0]) { case 'A': a++; break; case 'M': u++; break; case 'D': d++; break; }
                }
                return (a, u, d);
            }
            catch { return (0, 0, 0); }
        }

        private async Task<bool> HasPendingMergeChanges(CancellationToken token = default)
        {
            try
            {
                string status = await SvnRunner.RunAsync("status", svnManager.WorkingDir, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(status)) return false;
                return status.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd())
                    .Any(line => line.Length > 0 && "AMDCRG!".Contains(line[0]));
            }
            catch (Exception ex) { LogWarning($"[Merge Check Failed] {ex.Message}"); return true; }
        }

        private async Task<string> GetWorkingCopyRevision(CancellationToken token = default)
        {
            try
            {
                string rev = await SvnRunner.RunAsync("info --show-item revision", svnManager.WorkingDir, false, token).ConfigureAwait(false);
                return rev?.Trim() ?? "unknown";
            }
            catch { return "unknown"; }
        }

        private async Task RefreshResolveUI()
        {
            var resolve = svnManager?.GetModule<SVNResolve>();
            if (resolve != null) await resolve.RefreshConflictUI().ConfigureAwait(false);
        }

        #endregion

        #region Helpers

        private bool TryEnterMerging() => Interlocked.CompareExchange(ref _isMergingFlag, 1, 0) == 0;
        private void ExitMerging() => Interlocked.Exchange(ref _isMergingFlag, 0);

        private string GetRepoRootSafe()
        {
            string root = svnManager?.GetRepoRoot()?.Trim().TrimEnd('/');
            return root;
        }

        private static string Normalize(string input) => (input ?? "").Trim().Replace("\\", "/").TrimEnd('/').ToLowerInvariant();

        private static bool IsInvalidPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return true;
            string sanitized = input.Replace("://", "");
            return sanitized.Contains("..") || sanitized.Contains("//") || sanitized.Contains("\\") || sanitized.Contains("\0");
        }

        private static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            return arg.Replace("\"", "\\\"");
        }

        private static int CountRevisions(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return 0;
            int count = 0;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 1 && trimmed[0] == 'r' && long.TryParse(trimmed.AsSpan(1), out _))
                    count++;
            }
            return count;
        }

        private static int CountLinesMatching(string output, string pattern)
        {
            if (string.IsNullOrWhiteSpace(output)) return 0;
            int count = 0;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line.TrimStart(), pattern))
                    count++;
            }
            return count;
        }

        private static bool IsAncestryError(Exception ex)
        {
            if (ex == null) return false;
            string msg = ex.Message ?? string.Empty;
            return msg.Contains("ancestry", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("reintegrate", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E195016", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E195012", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("E195014", StringComparison.OrdinalIgnoreCase);
        }

        private void LogInfoBlock(string title, string message)
        {
            LogInfo("====================================");
            LogInfo($"[{title}]");
            if (!string.IsNullOrEmpty(message))
                foreach (var line in message.Split('\n'))
                    LogInfo(line);
            LogInfo("====================================");
        }

        private void LogSuccessBlock(string title, string message)
        {
            LogSuccess("====================================");
            LogSuccess($"[{title}]");
            if (!string.IsNullOrEmpty(message))
                foreach (var line in message.Split('\n'))
                    LogSuccess(line);
            LogSuccess("====================================");
        }

        private void LogWarningBlock(string title, string message)
        {
            LogWarning("====================================");
            LogWarning($"[{title}]");
            foreach (var line in message.Split('\n'))
                LogWarning(line);
            LogWarning("====================================");
        }

        protected override TMP_Text GetConsole() => svnUI?.MergeConsoleText;

        #endregion

        #region Result Classes

        public class MergeFileResult
        {
            public readonly List<MergeFileInfo> Files = new();
            public int Conflicts;
            public int Skipped;
            public bool MergeInfoUpdated;
            public int Added;
            public int Updated;
            public int Deleted;
            public int RealChanges;
        }

        public class MergeFileInfo
        {
            public string Path;
            public char State;
        }

        #endregion
    }
}