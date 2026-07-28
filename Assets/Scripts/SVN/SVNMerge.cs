using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMerge : SVNBase, IDisposable
    {
        public event Action<MergeFileResult> OnDryRunCompleted;

        private static string _cachedSshConfigOption;
        private static string _lastCachedKeyPath;

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
        private string _cachedRepoRoot;
        private bool _obstructionsJustDeleted;

        private CancellationTokenSource _mergeCts;

        private static readonly HashSet<char> ValidMergeStates = new("UADGRCM");

        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            LoadRollbackSnapshot();
            manager.OnProjectChanged += OnProjectChangedHandler;
        }

        public void Dispose()
        {
            if (svnManager != null)
                svnManager.OnProjectChanged -= OnProjectChangedHandler;
            _mergeCts?.Cancel();
            _mergeCts?.Dispose();
            _mergeCts = null;
        }

        #region SSH Helper

        #region SSH Helper

        private static string SshConfigOption
        {
            get
            {
                string currentKey = SvnRunner.KeyPath;

                if (_cachedSshConfigOption != null && string.Equals(_lastCachedKeyPath, currentKey, StringComparison.OrdinalIgnoreCase))
                {
                    return _cachedSshConfigOption;
                }

                string sshArgs = "-o BatchMode=yes -o StrictHostKeyChecking=no";
                if (!string.IsNullOrEmpty(currentKey))
                    sshArgs = $"-i '{currentKey}' {sshArgs}";

                _cachedSshConfigOption = $"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" ";
                _lastCachedKeyPath = currentKey;

                return _cachedSshConfigOption;
            }
        }

        #endregion

        #endregion

        private void OnProjectChangedHandler(SVNProject project)
        {
            _cachedRepoRoot = null;
            _branchesCacheValid = false;
            _cachedBranches = null;
            _obstructionsJustDeleted = false;
            ClearRollbackSnapshot();
        }

        private string EnsureRepoRoot()
        {
            if (!string.IsNullOrWhiteSpace(_cachedRepoRoot)) return _cachedRepoRoot;
            if (svnManager == null || string.IsNullOrWhiteSpace(svnManager.WorkingDir))
                return null;

            try
            {
                _cachedRepoRoot = svnManager.GetRepoRoot()?.Trim().TrimEnd('/');
            }
            catch (Exception ex)
            {
                LogWarning($"[SVNMerge] GetRepoRoot failed: {ex.Message}");
            }
            return _cachedRepoRoot;
        }

        private bool IsReady()
        {
            if (svnManager == null) return false;
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir)) return false;
            if (!Directory.Exists(svnManager.WorkingDir)) return false;
            if (string.IsNullOrWhiteSpace(SvnRunner.KeyPath) && string.IsNullOrWhiteSpace(svnManager.CurrentKey))
                return false;
            return true;
        }

        private async Task<string[]> GetRepoListAsync(string url, CancellationToken token = default)
        {
            try
            {
                string command = $"{SshConfigOption}list \"{url}\" --non-interactive";
                string output = await SvnRunner.RunAsync(command, svnManager.WorkingDir, false, token);

                if (string.IsNullOrWhiteSpace(output))
                    return Array.Empty<string>();

                return output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimEnd('/'))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => !x.StartsWith("*"))
                    .Where(x => x.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) < 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch { return Array.Empty<string>(); }
        }

        public void CancelMerge()
        {
            _mergeCts?.Cancel();
            LogWarning("[Merge] Cancel requested by user.");
        }

        public async Task ExecuteMerge(string sourceInput, bool isDryRun)
        {
            if (svnManager == null || svnUI == null)
            {
                LogErrorLocal("[Error] SVN Manager or UI not initialized.");
                return;
            }

            if (await HasPendingMergeChanges())
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

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
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

                string currentUuid = (await SvnRunner.RunAsync($"{SshConfigOption}info --show-item repos-uuid", svnManager.WorkingDir, false, token))?.Trim();
                string sourceUuid = (await SvnRunner.RunAsync($"{SshConfigOption}info \"{sourceUrl}\" --show-item repos-uuid", svnManager.WorkingDir, false, token))?.Trim();

                if (!string.Equals(currentUuid, sourceUuid, StringComparison.Ordinal))
                {
                    LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                LogInfo("[Merge] Bringing working copy to a uniform revision...");
                try
                {
                    await SvnRunner.RunAsync("update", svnManager.WorkingDir, true, token);
                    LogInfo("[Merge] Update completed.");
                }
                catch (Exception ex)
                {
                    LogWarning($"[Merge] Update failed (non‑fatal): {ex.Message}");
                }

                if (!isDryRun)
                {
                    var state = await TryCaptureMergeSnapshot(sourceUrl, token);
                    if (state == MergeSnapshotState.Error)
                        LogWarning("[Merge] Snapshot capture failed.");
                }

                string output = await ExecuteMergeCommand(sourceUrl, isDryRun, token);
                await ParseMergeOutput(output, isDryRun, token);

                if (!isDryRun)
                {
                    await svnManager.RefreshStatus();
                    await RefreshResolveUI();
                    LogSuccess("[Merge Complete]");
                }
            }
            catch (OperationCanceledException)
            {
                LogWarning("[Merge] Cancelled by user.");
                await SafeCleanupAfterCancel();
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
                if (await HasPendingMergeChanges(token))
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

                try { await SvnRunner.RunAsync($"{SshConfigOption}update", svnManager.WorkingDir, true, token); }
                catch (Exception ex) { LogWarning($"[Undo] Update failed (non‑fatal): {ex.Message}"); }

                string range = $"{_lastMergeRevisionAfter}:{_lastMergeRevisionBefore}";

                if (_lastMergeRevisionBefore == _lastMergeRevisionAfter)
                {
                    LogErrorLocal("[Undo] Cannot auto-undo a base-merge snapshot (identical revisions).");
                    LogWarning("To undo this, manually revert the 'svn:mergeinfo' property change on the root folder.");
                    return;
                }

                string args = $"{SshConfigOption}merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --non-interactive --accept postpone";
                LogInfo($"[Undo] Executing: svn {args}");

                string output;
                try
                {
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);
                }
                catch (Exception ex) when (ex.Message.Contains("mixed-revision") || ex.Message.Contains("E195020"))
                {
                    LogWarning("[Undo] Mixed-revision detected – retrying after another update...");
                    await SvnRunner.RunAsync($"{SshConfigOption}update", svnManager.WorkingDir, true, token);
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);
                }
                catch (Exception ex) when (ex.Message.Contains("E155035") || ex.Message.Contains("Attempt to add tree conflict"))
                {
                    LogErrorLocal("[Undo] Operation blocked by Tree Conflicts.");
                    LogWarning("<color=#FFFF00>[CRITICAL SVN LIMITATION]</color>");
                    LogWarning("SVN cannot undo a merge that created tree conflicts.");
                    LogWarning("You must use 'Revert to HEAD' or manually resolve the tree obstructions before undoing.");
                    throw;
                }
                catch (Exception ex) when (IsAncestryError(ex))
                {
                    LogWarning("[Undo] Ancestry issue – retrying with --ignore-ancestry...");
                    args = $"{SshConfigOption}merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo($"[Undo] Retrying with: svn {args}");
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);
                }

                if (autoCommit)
                {
                    string msg = $"Undo merge from {_lastMergeSource} (r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})";
                    LogInfo($"[Undo] Auto‑committing: {msg}");
                    await SvnRunner.RunAsync($"{SshConfigOption}commit -m \"{msg}\"", svnManager.WorkingDir, true, token);
                    LogSuccess("[Undo] Changes committed automatically.");
                }

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus();
                await RefreshResolveUI();

                LogSuccessBlock("Undo Complete", $"Successfully reverted merge of {_lastMergeSource} (r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[Undo] Cancelled by user.");
                await SafeCleanupAfterCancel();
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
            if (_obstructionsJustDeleted)
            {
                LogErrorLocal("[Blocked] Invalid action sequence.");
                LogWarning("You just deleted tree obstructions (Soft Revert). You MUST click 'Revert to HEAD' or 'Commit' right now.");
                LogWarning("Do NOT run standard 'Cancel Local Merge' in this state, or you will corrupt SVN history!");
                return;
            }

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
                string args = $"{SshConfigOption}merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --non-interactive --accept postpone";
                LogInfo($"[Cancel Local Merge] Executing: svn {args}");

                string output;
                try
                {
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);
                }
                catch (Exception ex) when (ex.Message.Contains("mixed-revision") || ex.Message.Contains("E195020"))
                {
                    LogWarning("[Cancel Local Merge] Mixed-revision detected – retrying after another update...");
                    await SvnRunner.RunAsync($"{SshConfigOption}update", svnManager.WorkingDir, true, token);
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);
                }
                catch (Exception ex) when (ex.Message.Contains("E155035") || ex.Message.Contains("Attempt to add tree conflict"))
                {
                    LogErrorLocal("[Cancel Local Merge] Operation blocked by Tree Conflicts.");
                    LogWarning("<color=#FFFF00>[CRITICAL SVN LIMITATION]</color>");
                    LogWarning("SVN cannot reverse a merge that created tree conflicts.");
                    LogWarning("You must use 'Revert to HEAD' or manually resolve the tree obstructions before undoing.");
                    return;
                }
                catch (Exception ex) when (IsAncestryError(ex))
                {
                    LogWarning("[Cancel Local Merge] Ancestry issue – retrying with --ignore-ancestry...");
                    args = $"{SshConfigOption}merge -r {range} \"{EscapeSvnArg(_lastMergeSource)}\" --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo($"[Cancel Local Merge] Retrying with: svn {args}");
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);
                }

                if (string.IsNullOrWhiteSpace(output) || output.Contains("No changes"))
                {
                    LogInfo("[Cancel Local Merge] No changes to revert.");
                }
                else
                {
                    int reverted = CountLinesMatching(output, @"^[A-Z]\s");
                    LogSuccess($"[Cancel Local Merge] Successfully reverted {reverted} files.");
                }

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus();
                await RefreshResolveUI();

                LogSuccess("[Cancel Local Merge Complete] Merge changes have been reverted.");
                LogInfo("Files with status 'R' are locally scheduled for replacement.");
                LogInfo("To clear 'R', commit the undo (or use RevertToHead to discard everything).");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[Cancel Local Merge] Cancelled by user.");
                await SafeCleanupAfterCancel();
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Cancel Local Merge Error] {ex.Message}");
            }
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
                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir, true, token);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token);

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus();
                await RefreshResolveUI();

                LogSuccess("[Reset Complete] Working copy is now at HEAD.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[RevertToHead] Cancelled by user.");
                await SafeCleanupAfterCancel();
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

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";
                LogInfo($"Target: {trunkUrl}");

                if (Normalize(currentUrl) == Normalize(trunkUrl))
                {
                    LogWarning("Already on Trunk. Comparison skipped.");
                    return;
                }

                LogInfo("Fetching revision differences...");
                string missingCmd = $"{SshConfigOption}mergeinfo \"{trunkUrl}\" --show-revs eligible";
                string missingInBranch = await SvnRunner.RunAsync(missingCmd, svnManager.WorkingDir, false, token);
                string localCmd = $"{SshConfigOption}mergeinfo . \"{trunkUrl}\" --show-revs eligible";
                string branchOnlyChanges = await SvnRunner.RunAsync(localCmd, svnManager.WorkingDir, false, token);

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
                await SafeCleanupAfterCancel();
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
            if (!IsReady())
            {
                LogInfo("[Branches] Project not ready yet — returning cached or empty.");
                return _cachedBranches ?? Array.Empty<string>();
            }

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
                await svnManager.CancelBackgroundTasksAsync();

                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    string rootOutput = await SvnRunner.RunAsync("info --show-item repos-root-url", svnManager.WorkingDir, false, CancellationToken.None);
                    repoRoot = rootOutput?.Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(repoRoot))
                    {
                        LogErrorLocal("[Critical Error] Repo root missing.");
                        return Array.Empty<string>();
                    }
                }

                string branchesUrl = $"{repoRoot}/branches";
                LogInfo($"[Debug] Scanning branches at: {branchesUrl}");

                var branchList = await GetRepoListAsync(branchesUrl, CancellationToken.None);

                if (branchList.Length == 0)
                {
                    LogInfo("[FetchAvailableBranches] No branches found (folder may be empty or not exist yet).");
                    _cachedBranches = Array.Empty<string>();
                    _branchesCacheValid = true;
                    return _cachedBranches;
                }

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

        public async Task RefreshIfEmpty()
        {
            if (!IsReady())
            {
                LogInfo("[RefreshIfEmpty] Not ready — skipped.");
                return;
            }

            if (_cachedBranches == null || !_branchesCacheValid)
            {
                LogInfo("[RefreshIfEmpty] Cache empty/invalid — fetching...");
                await FetchAvailableBranchesAsync(force: false);
            }
            else
            {
                LogInfo("[RefreshIfEmpty] Cache valid — skipped.");
            }
        }

        private async Task FetchAvailableBranchesAsync(bool force)
        {
            await FetchAvailableBranches(force);
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
                if (string.IsNullOrWhiteSpace(repoRoot)) { LogErrorLocal("[Force Merge] Repo root not found."); return; }

                string sourceUrl = $"{repoRoot}/trunk";
                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Already on trunk. Cannot merge trunk into itself.");
                    return;
                }

                LogInfo("[Force Merge] Cleaning up stale mergeinfo properties...");
                try
                {
                    await SvnRunner.RunAsync("propdel svn:mergeinfo -R .", svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    LogSuccess("[Force Merge] Stale mergeinfo cleaned.");
                }
                catch
                {
                    LogInfo("[Force Merge] No stale mergeinfo found (clean state).");
                }

                await TryCaptureMergeSnapshot(sourceUrl, token);

                string args;
                if (_hasRollbackPoint && !string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) &&
                    !string.IsNullOrWhiteSpace(_lastMergeRevisionAfter) &&
                    _lastMergeRevisionBefore != _lastMergeRevisionAfter)
                {
                    string range = $"{_lastMergeRevisionBefore}:{_lastMergeRevisionAfter}";
                    args = $"{SshConfigOption}merge -r {range} \"{sourceUrl}\" --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo($"[Force Merge] Range: {range}");
                }
                else
                {
                    args = $"{SshConfigOption}merge \"{sourceUrl}\" --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo("[Force Merge] No revision range available – merging all trunk changes.");
                }

                LogInfo($"[Force Merge] Executing: svn {args}");
                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);
                await ParseMergeOutput(output, false, token);

                await svnManager.RefreshStatus();
                await RefreshResolveUI();

                LogSuccess("[Force Merge Complete] Trunk changes have been applied.");
                LogWarning("PLEASE COMMIT this merge immediately to record the history.");
                LogWarning("Without a commit, SVN may attempt to re-merge the same changes in the future.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[ForceMerge] Cancelled by user.");
                await SafeCleanupAfterCancel();
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
                await svnManager.CancelBackgroundTasksAsync();

                string repoRoot = GetRepoRootSafe();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[RepairReintegrateHistory] Repo root not found.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
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
                string logOutput = await SvnRunner.RunAsync($"{SshConfigOption}log --stop-on-copy --xml --verbose -l 20", svnManager.WorkingDir, true, token);

                long targetRev = await FindIncompleteReintegrateRevisionAsync(logOutput, token);
                if (targetRev <= 0)
                {
                    LogSuccess("[RepairReintegrateHistory] No incomplete reintegrate commit found. History may already be clean.");
                    return;
                }

                LogInfo($"[RepairReintegrateHistory] Found possible incomplete reintegrate at r{targetRev}");

                string sourceUrl = await DetermineSourceBranchAsync(repoRoot, targetRev, token);
                if (string.IsNullOrEmpty(sourceUrl))
                {
                    LogErrorLocal("[RepairReintegrateHistory] Could not determine source branch. Please select the branch in the Merge panel and try again.");
                    return;
                }

                LogInfo($"[RepairReintegrateHistory] Source branch: {sourceUrl}");
                string args = $"{SshConfigOption}merge --record-only --ignore-ancestry \"{sourceUrl}\" --non-interactive --accept postpone";
                LogInfo($"[RepairReintegrateHistory] Executing: svn {args}");
                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token);

                if (output.Contains("Recording") || output.Contains("recorded") || string.IsNullOrWhiteSpace(output))
                {
                    LogSuccess("[RepairReintegrateHistory] Mergeinfo successfully recorded.");
                    LogSuccess("Please commit this change immediately.");
                    LogSuccess("After commit, standard reintegrate from branch to trunk will work correctly.");
                    await svnManager.RefreshStatus();
                }
                else
                {
                    LogErrorLocal($"[RepairReintegrateHistory] Unexpected output: {output}");
                }
            }
            catch (OperationCanceledException)
            {
                LogWarning("[RepairMergeHistory] Cancelled by user.");
                await SafeCleanupAfterCancel();
            }
            catch (Exception ex) { LogErrorLocal($"[RepairReintegrateHistory Error] {ex.Message}"); }
            finally
            {
                _mergeCts = null;
                ExitMerging();
                End();
            }
        }

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

        private void LoadSnapshotFromFile()
        {
            try
            {
                string path = SnapshotFilePath;
                if (path == null || !File.Exists(path)) return;

                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<SnapshotData>(json);

                if (data == null || string.IsNullOrWhiteSpace(data.Source)
                    || string.IsNullOrWhiteSpace(data.RevisionBefore)
                    || string.IsNullOrWhiteSpace(data.RevisionAfter))
                    return;

                _lastMergeSource = data.Source;
                _lastMergeRevisionBefore = data.RevisionBefore;
                _lastMergeRevisionAfter = data.RevisionAfter;
                _hasRollbackPoint = true;

                LogInfo($"[Snapshot] Loaded from file: {data.Source} | r{data.RevisionBefore} → r{data.RevisionAfter} | Timestamp: {data.Timestamp}");
            }
            catch (Exception ex)
            {
                LogWarning($"[Snapshot] File load failed: {ex.Message}");
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

            LoadSnapshotFromFile();

            if (_hasRollbackPoint)
            {
                PlayerPrefs.SetString(PrefMergeSource, _lastMergeSource);
                PlayerPrefs.SetString(PrefMergeRevBefore, _lastMergeRevisionBefore);
                PlayerPrefs.SetString(PrefMergeRevAfter, _lastMergeRevisionAfter);
                PlayerPrefs.SetInt(PrefHasRollback, 1);
                PlayerPrefs.Save();
            }
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
                    $"{SshConfigOption}mergeinfo \"{sourceUrl}\" . --show-revs eligible",
                    svnManager.WorkingDir, false, token);

                if (string.IsNullOrWhiteSpace(eligible))
                {
                    LogInfo("[Snapshot] No merge history found – creating first‑merge snapshot.");
                    string currentRevision = await GetWorkingCopyRevision(token);

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

        private async Task<string> ExecuteMergeCommand(string sourceUrl, bool isDryRun, CancellationToken token)
        {
            string dryRunFlag = isDryRun ? "--dry-run " : string.Empty;
            string args = $"{SshConfigOption}merge {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";

            LogInfoBlock("SVN MERGE COMMAND", args);

            try
            {
                return await SvnRunner.RunAsync(args, svnManager.WorkingDir, !isDryRun, token);
            }
            catch (Exception ex) when (ex.Message.Contains("E155015"))
            {
                if (isDryRun)
                    LogWarning("[Merge] Conflicts produced during simulation. No files were changed on disk.");
                else
                    LogWarning("[Merge] Conflicts produced during LIVE MERGE. Working copy updated.");

                return ex.Message;
            }
            catch (Exception ex) when (IsAncestryError(ex))
            {
                LogWarningBlock("ANCESTRY PROBLEM DETECTED", "Standard merge failed. Retrying with --ignore-ancestry.");

                string retryArgs = $"{SshConfigOption}merge --ignore-ancestry {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";
                LogInfoBlock("SVN MERGE RETRY", retryArgs);

                try
                {
                    return await SvnRunner.RunAsync(retryArgs, svnManager.WorkingDir, !isDryRun, token);
                }
                catch (Exception retryEx) when (retryEx.Message.Contains("E155015"))
                {
                    string mode = isDryRun ? "simulation (ignored ancestry)" : "LIVE MERGE (ignored ancestry)";
                    LogWarning($"[Merge] Conflicts produced during {mode}.");
                    return retryEx.Message;
                }
            }
        }

        private async Task ParseMergeOutput(string output, bool isDryRun, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(output) || output.IndexOf("already up to date", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LogSuccess("Everything is already up to date.");
                if (isDryRun)
                {
                    var handler = OnDryRunCompleted;
                    if (handler != null)
                    {
                        var emptyResult = new MergeFileResult();
                        UnityMainThreadDispatcher.Enqueue(() => handler(emptyResult));
                    }
                }
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

                    if (line.Contains("tree conflict", StringComparison.OrdinalIgnoreCase))
                    {
                        LogWarning("<color=#FF4444><b>DETECTED TREE CONFLICT!</b></color>");
                        LogWarning("<color=#FFAA00>Standard 'Cancel Local Merge' will likely FAIL now due to SVN limitations.</color>");
                        LogWarning("<color=#FFAA00>If this merge was a mistake, your safest option is 'Revert to HEAD'.</color>");
                    }

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
                string dryRunMsg = $"Potential file changes : {realChanges}\nConflicts detected     : {conflicts}" +
                    (mergeInfoUpdated ? "\nSVN merge history would be updated." : "") +
                    (skipped > 0 ? $"\nSkipped items          : {skipped}" : "") +
                    (realChanges == 0 && conflicts == 0 ? "\nNo incoming file changes detected." : "");

                if (conflicts > 0)
                {
                    dryRunMsg += "\n\n<color=#FFD700><b>NOTE:</b> These are SIMULATED conflicts.</color>";
                    dryRunMsg += "\n<color=#FFD700>Click 'Confirm Merge' to apply them to disk.</color>";
                    dryRunMsg += "\n<color=#FFD700>Then use the Resolve panel to fix them.</color>";
                }

                LogInfoBlock("DRY RUN RESULT", dryRunMsg);

                var dryRunHandler = OnDryRunCompleted;
                if (dryRunHandler != null)
                {
                    var capturedResult = result;
                    UnityMainThreadDispatcher.Enqueue(() => dryRunHandler(capturedResult));
                }
                return;
            }

            LogSuccessBlock("MERGE COMPLETED SUCCESSFULLY", null);

            var realStats = await GetRealDiffStats(token);

            LogInfo($"Total change entries : {changed}");
            LogInfo($"Added files      : {realStats.added}");
            LogInfo($"Updated files    : {realStats.updated}");
            LogInfo($"Deleted files    : {realStats.deleted}");

            if (mergeInfoUpdated) LogInfo("Merge history updated.");
            if (conflicts > 0) LogErrorLocal($"Conflicts detected : {conflicts}");
            if (skipped > 0) LogWarning($"Skipped items : {skipped}");
            if (realChanges == 0 && conflicts == 0) LogSuccess("Merge executed but no real file changes were applied.");

            LogSuccess("Review changes before commit.");

            var liveHandler = OnDryRunCompleted;
            if (liveHandler != null && result.Files.Count > 0)
            {
                var capturedResult = result;
                UnityMainThreadDispatcher.Enqueue(() => liveHandler(capturedResult));
            }
        }

        private async Task<long> FindIncompleteReintegrateRevisionAsync(string logOutput, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(logOutput)) return -1;

            try
            {
                using var stringReader = new StringReader(logOutput);
                using var reader = XmlReader.Create(stringReader);
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
                                string propMods = reader.GetAttribute("prop-mods") ?? "false";
                                string action = reader.GetAttribute("action") ?? "";

                                string value = await reader.ReadElementContentAsStringAsync();

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
            string logEntry = await SvnRunner.RunAsync($"log -r {targetRev} --xml --verbose", svnManager.WorkingDir, true, token);
            if (string.IsNullOrWhiteSpace(logEntry)) return null;

            try
            {
                using var stringReader = new StringReader(logEntry);
                using var reader = XmlReader.Create(stringReader);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "path")
                    {
                        string action = reader.GetAttribute("action") ?? "";
                        string copyFrom = reader.GetAttribute("copyfrom-path") ?? "";

                        string value = await reader.ReadElementContentAsStringAsync();

                        if ((action == "A" || action == "M") && value.StartsWith("/branches/") &&
                            (!string.IsNullOrEmpty(copyFrom) || value.Contains("(from ")))
                        {
                            if (!string.IsNullOrEmpty(copyFrom) && copyFrom.Contains("/branches/"))
                                return $"{repoRoot}{copyFrom}";

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

        public static async Task<SvnStats> GetStatsAsync(string workingDir, CancellationToken token = default)
        {
            string output = await SvnRunner.RunAsync("status", workingDir, false, token);
            var stats = new SvnStats();
            foreach (string line in (output ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
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
                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir, true, cleanupCts.Token);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, cleanupCts.Token);
                LogWarning("[Merge] Working copy cleaned up.");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Merge] Cleanup after cancel failed: {ex.Message}");
                LogWarning("[Merge] You may need to run 'svn revert -R .' and 'svn cleanup' manually.");
            }
        }

        private async Task<(int added, int updated, int deleted)> GetRealDiffStats(CancellationToken token = default)
        {
            try
            {
                string output = await SvnRunner.RunAsync("diff --summarize", svnManager.WorkingDir, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return (0, 0, 0);

                int a = 0, u = 0, d = 0;
                foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.TrimStart();
                    if (line.Length == 0) continue;

                    string path = line.Length > 2 ? line.Substring(2).Trim() : "";

                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.EndsWith(".mine", StringComparison.OrdinalIgnoreCase)) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(path, @"\.r\d+$")) continue;

                    switch (line[0])
                    {
                        case 'A': a++; break;
                        case 'M': u++; break;
                        case 'D': d++; break;
                    }
                }
                return (a, u, d);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return (0, 0, 0);
            }
        }

        private async Task<bool> HasPendingMergeChanges(CancellationToken token = default)
        {
            try
            {
                string status = await SvnRunner.RunAsync("status", svnManager.WorkingDir, false, token);
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
                string rev = await SvnRunner.RunAsync("info --show-item revision", svnManager.WorkingDir, false, token);
                return rev?.Trim() ?? "unknown";
            }
            catch { return "unknown"; }
        }

        private async Task RefreshResolveUI()
        {
            var resolve = svnManager?.GetModule<SVNResolve>();
            if (resolve != null) await resolve.RefreshConflictUI();
        }

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
    }
}