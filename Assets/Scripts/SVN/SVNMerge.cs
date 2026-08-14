using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        // PlayerPrefs keys – zachowane jedynie dla migracji jednorazowej
        private const string PrefMergeSource = "SVN_UndoMerge_Source";
        private const string PrefMergeRevBefore = "SVN_UndoMerge_RevBefore";
        private const string PrefMergeRevAfter = "SVN_UndoMerge_RevAfter";
        private const string PrefHasRollback = "SVN_UndoMerge_HasRollback";
        private const string PrefMergeTimestamp = "SVN_UndoMerge_Timestamp";

        private string _lastMergeSource;
        private bool _hasRollbackPoint;
        private string _lastMergeRevisionBefore;
        private string _lastMergeRevisionAfter;

        private float _lastRevertToHeadClickTime = -10f;
        private float _lastForceMergeClickTime = -10f;
        private float _lastRepairMergeClickTime = -10f;

        private bool _branchesCacheValid;
        private string[] _cachedBranches;
        private int _isFetchingBranchesFlag;

        private bool _tagsCacheValid;
        private string[] _cachedTags;
        private int _isFetchingTagsFlag;

        private int _isMergingFlag;
        private string _cachedRepoRoot;
        private string _cachedWcRoot;
        private bool _obstructionsJustDeleted;

        private bool _hadLocalChangesBeforeMerge;

        private CancellationTokenSource _mergeCts;

        private static readonly HashSet<char> ValidMergeStates = new("UADGRCM");

        private static readonly Regex MergeLineRegex = new(
            @"^([AUGDRCME ])\s{2,}(\S.+)$",
            RegexOptions.Compiled);

        private static readonly Regex SkippedLineRegex = new(
            @"^Skipped\s+['""]?(.+?)['""]?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        protected override TMP_Text GetConsole() => svnUI?.MergeConsoleText;

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
            if (!string.IsNullOrEmpty(message))
                foreach (var line in message.Split('\n'))
                    LogWarning(line);
            LogWarning("====================================");
        }

        private static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            if (arg.Contains(' ') || arg.Contains('"'))
                return "\"" + arg.Replace("\"", "\\\"") + "\"";
            return arg.Replace("\"", "\\\"");
        }

        private static string SshConfigOption
        {
            get
            {
                string currentKey = SvnRunner.KeyPath;

                if (_cachedSshConfigOption != null &&
                    string.Equals(_lastCachedKeyPath, currentKey, StringComparison.OrdinalIgnoreCase))
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

        private async Task EnsureWcRootAsync(CancellationToken token = default)
        {
            if (!string.IsNullOrWhiteSpace(_cachedWcRoot)) return;
            try
            {
                string result = await SvnRunner.RunAsync(
                    "info --show-item wc-root",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                _cachedWcRoot = result?.Trim();
            }
            catch
            {
                _cachedWcRoot = svnManager?.WorkingDir;
            }
        }

        private string GetSnapshotFilePath()
        {
            string wcRoot = _cachedWcRoot;
            if (string.IsNullOrWhiteSpace(wcRoot))
                wcRoot = svnManager?.WorkingDir;

            if (string.IsNullOrWhiteSpace(wcRoot)) return null;
            return Path.Combine(wcRoot, ".svn", "merge_snapshot.json");
        }

        private void OnProjectChangedHandler(SVNProject project)
        {
            _cachedRepoRoot = null;
            _cachedWcRoot = null;

            _branchesCacheValid = false;
            _cachedBranches = null;
            _tagsCacheValid = false;
            _cachedTags = null;

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

        private async Task<string> GetRepoRootSafeAsync(CancellationToken token = default)
        {
            string root = EnsureRepoRoot();
            if (!string.IsNullOrWhiteSpace(root)) return root;

            if (svnManager != null && !string.IsNullOrWhiteSpace(svnManager.WorkingDir))
            {
                try
                {
                    string output = await SvnRunner.RunAsync(
                        "info --show-item repos-root-url",
                        svnManager.WorkingDir, false, token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        _cachedRepoRoot = output.Trim().TrimEnd('/');
                        return _cachedRepoRoot;
                    }
                }
                catch { }
            }

            return null;
        }

        private bool IsReady()
        {
            if (svnManager == null) return false;
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir)) return false;
            if (!Directory.Exists(svnManager.WorkingDir)) return false;
            if (string.IsNullOrWhiteSpace(SvnRunner.KeyPath) &&
                string.IsNullOrWhiteSpace(svnManager.CurrentKey))
                return false;
            return true;
        }

        private static string Normalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            return url.Trim().TrimEnd('/').ToLowerInvariant();
        }

        private bool ValidateSourceInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            if (input.Contains("://")) return false;
            if (input.StartsWith("/")) return false;

            if (input.Contains("..") || input.Contains("//") ||
                input.Contains("\\") || input.Contains("\0"))
                return false;

            foreach (char c in input)
            {
                if (c == ';' || c == '|' || c == '&' || c == '>' ||
                    c == '<' || c == '$' || c == '`' || c == '(' || c == ')')
                    return false;
            }

            return true;
        }

        private string ResolveSourceUrl(string input, string repoRoot)
        {
            string trimmed = input.Trim().TrimStart('/');

            if (trimmed.StartsWith("branches/", StringComparison.OrdinalIgnoreCase))
                return $"{repoRoot}/{trimmed}";

            if (trimmed.StartsWith("tags/", StringComparison.OrdinalIgnoreCase))
                return $"{repoRoot}/{trimmed}";

            if (trimmed.Equals("trunk", StringComparison.OrdinalIgnoreCase))
                return $"{repoRoot}/trunk";

            return $"{repoRoot}/branches/{trimmed}";
        }

        private bool TryEnterMerging()
        {
            if (Interlocked.CompareExchange(ref _isMergingFlag, 1, 0) != 0)
            {
                LogWarning("[Merge] Operation already in progress.");
                return false;
            }
            return true;
        }

        private void ExitMerging()
        {
            Interlocked.Exchange(ref _isMergingFlag, 0);
        }

        private async Task<string[]> GetRepoListAsync(string url, CancellationToken token = default)
        {
            try
            {
                string command = $"{SshConfigOption}list {EscapeSvnArg(url)} --non-interactive";
                string output = await SvnRunner.RunAsync(command, svnManager.WorkingDir, false, token)
                    .ConfigureAwait(false);

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
            catch (Exception ex)
            {
                LogWarning($"[SVN] Failed to list '{url}': {ex.Message}");
                return Array.Empty<string>();
            }
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

            if (!ValidateSourceInput(sourceInput))
            {
                LogErrorLocal("SECURITY: Provide only branch/tag name or internal path, not a full URL.");
                return;
            }

            _hadLocalChangesBeforeMerge = await HasPendingMergeChanges().ConfigureAwait(false);
            if (_hadLocalChangesBeforeMerge)
            {
                LogWarningBlock("MERGE BLOCKED",
                    "Working copy contains uncommitted merge changes.\n" +
                    "Commit, revert or cleanup before merging again.");
                return;
            }

            if (!TryEnterMerging()) return;
            if (!TryStart()) { ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceInput)) return;

                await EnsureWcRootAsync(token).ConfigureAwait(false);

                LogInfoBlock("MERGE SESSION START",
                    $"Source: {sourceInput}\nMode: {(isDryRun ? "DRY RUN" : "LIVE MERGE")}");

                string repoRoot = await GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("Repo Root not found.");
                    return;
                }

                string sourceUrl = ResolveSourceUrl(sourceInput, repoRoot);
                if (string.IsNullOrWhiteSpace(sourceUrl))
                {
                    LogErrorLocal("SECURITY: Invalid merge source.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir)
                    .ConfigureAwait(false);
                LogInfo($"Current URL: {currentUrl}");
                LogInfo($"Source URL : {sourceUrl}");

                bool sourceIsTrunk = Normalize(sourceUrl).EndsWith("/trunk");
                bool currentIsTrunk = Normalize(currentUrl).EndsWith("/trunk");

                if (!sourceIsTrunk && currentIsTrunk && !isDryRun)
                {
                    LogInfo("[Merge] Reintegrate detected. Checking branch synchronization with trunk...");

                    string eligibleFromTrunk = await SvnRunner.RunAsync(
                        $"{SshConfigOption}mergeinfo {EscapeSvnArg($"{repoRoot}/trunk")} {EscapeSvnArg(sourceUrl)} --show-revs eligible",
                        svnManager.WorkingDir, false, token).ConfigureAwait(false);

                    int missing = CountRevisions(eligibleFromTrunk);
                    if (missing > 0)
                    {
                        LogErrorLocal("BRANCH NOT SYNCHRONIZED WITH TRUNK.");
                        LogErrorLocal($"Branch '{sourceInput}' is missing {missing} revisions from trunk.");
                        LogErrorLocal("Please sync the branch with trunk before reintegrating.");
                        return;
                    }
                    LogSuccess("[Merge] Branch is fully synchronized with trunk. Proceeding.");
                }

                if (sourceIsTrunk && !currentIsTrunk && !isDryRun)
                {
                    LogInfo("[Merge] Sync merge detected. Checking eligible revisions...");

                    string eligible = await SvnRunner.RunAsync(
                        $"{SshConfigOption}mergeinfo {EscapeSvnArg(sourceUrl)} . --show-revs eligible",
                        svnManager.WorkingDir, false, token).ConfigureAwait(false);

                    int eligibleCount = CountRevisions(eligible);
                    if (eligibleCount == 0)
                    {
                        LogInfoBlock("Merge Blocked",
                            "Branch is already fully synchronized with Trunk.\n" +
                            "No incoming revisions to pull. Operation aborted safely.");
                        return;
                    }
                    LogSuccess($"[Merge] Found {eligibleCount} eligible revision(s).");
                }

                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Cannot merge branch into itself.");
                    return;
                }

                string currentUuid = (await SvnRunner.RunAsync(
                    $"{SshConfigOption}info --show-item repos-uuid",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();

                string sourceUuid = (await SvnRunner.RunAsync(
                    $"{SshConfigOption}info {EscapeSvnArg(sourceUrl)} --show-item repos-uuid",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false))?.Trim();

                if (!string.Equals(currentUuid, sourceUuid, StringComparison.Ordinal))
                {
                    LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                LogInfo("[Merge] Bringing working copy to a uniform revision...");
                try
                {
                    await SvnRunner.RunAsync("update", svnManager.WorkingDir, true, token)
                        .ConfigureAwait(false);
                    LogInfo("[Merge] Update completed.");
                }
                catch (Exception ex)
                {
                    LogWarning($"[Merge] Update failed (non‑fatal): {ex.Message}");
                }

                try
                {
                    string svnVersion = await SvnRunner.RunAsync(
                        "svnversion", svnManager.WorkingDir, false, token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(svnVersion) &&
                        (svnVersion.Contains(":") || svnVersion.Contains("M") || svnVersion.Contains("S")))
                    {
                        LogWarning("[Merge] Mixed-revision working copy detected.");
                        LogWarning("[Merge] It is recommended to update to a uniform revision before merging.");
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"[Merge] svnversion check skipped: {ex.Message}");
                }

                if (!isDryRun)
                {
                    var state = await TryCaptureMergeSnapshot(sourceUrl, token).ConfigureAwait(false);
                    if (state == MergeSnapshotState.Error)
                        LogWarning("[Merge] Snapshot capture failed.");
                }

                string output = await ExecuteMergeCommand(sourceUrl, isDryRun, token)
                    .ConfigureAwait(false);
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
                _hadLocalChangesBeforeMerge = false;
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

            try
            {
                await ExecuteReverseMerge(false, autoCommit, cts.Token).ConfigureAwait(false);
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
            if (_obstructionsJustDeleted)
            {
                LogErrorLocal("[Blocked] Invalid action sequence.");
                LogWarning("You just deleted tree obstructions (Soft Revert). " +
                           "You MUST click 'Revert to HEAD' or 'Commit' right now.");
                LogWarning("Do NOT run standard 'Cancel Local Merge' in this state, " +
                           "or you will corrupt SVN history!");
                return;
            }

            if (!TryStart()) return;

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;

            try
            {
                await ExecuteReverseMerge(true, false, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogWarning("[Cancel Local Merge] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
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

        private async Task ExecuteReverseMerge(bool isCancel, bool autoCommit, CancellationToken token)
        {
            string opName = isCancel ? "CANCEL LOCAL MERGE" : "UNDO LAST MERGE";
            LogInfo($"========== {opName} ==========");

            if (await HasPendingMergeChanges(token).ConfigureAwait(false))
            {
                string actionName = isCancel ? "cancelling the local merge" : "undoing the last merge";
                LogWarningBlock($"{opName} Blocked",
                    $"Uncommitted changes detected.\n" +
                    $"Commit or cancel current changes before {actionName}.");
                return;
            }

            if (!_hasRollbackPoint) LoadRollbackSnapshot();

            if (!_hasRollbackPoint ||
                string.IsNullOrWhiteSpace(_lastMergeSource) ||
                string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) ||
                string.IsNullOrWhiteSpace(_lastMergeRevisionAfter))
            {
                LogWarning($"[{opName}] No rollback point available. Perform a merge first.");
                return;
            }

            LogInfo($"[{opName}] Source : {_lastMergeSource}");
            LogInfo($"[{opName}] Range  : r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
            LogInfo($"[{opName}] Bringing working copy to a uniform revision...");

            try
            {
                await SvnRunner.RunAsync($"{SshConfigOption}update",
                    svnManager.WorkingDir, true, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarning($"[{opName}] Update failed (non‑fatal): {ex.Message}");
            }

            string range = $"{_lastMergeRevisionAfter}:{_lastMergeRevisionBefore}";

            if (_lastMergeRevisionBefore == _lastMergeRevisionAfter)
            {
                LogErrorLocal($"[{opName}] Cannot auto-undo a base-merge snapshot (identical revisions).");
                LogWarning("To undo this, manually revert the 'svn:mergeinfo' property change on the root folder.");
                return;
            }

            string args = $"{SshConfigOption}merge -r {range} {EscapeSvnArg(_lastMergeSource)} --non-interactive --accept postpone";
            LogInfo($"[{opName}] Executing: svn {args}");

            string output;
            try
            {
                output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("mixed-revision") || ex.Message.Contains("E195020"))
            {
                LogWarning($"[{opName}] Mixed-revision detected – retrying after another update...");
                await SvnRunner.RunAsync($"{SshConfigOption}update",
                    svnManager.WorkingDir, true, token).ConfigureAwait(false);
                output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.Contains("E155035") || ex.Message.Contains("Attempt to add tree conflict"))
            {
                LogErrorLocal($"[{opName}] Operation blocked by Tree Conflicts.");
                LogWarning("<color=#FFFF00>[CRITICAL SVN LIMITATION]</color>");
                LogWarning("SVN cannot undo a merge that created tree conflicts.");
                LogWarning("You must use 'Revert to HEAD' or manually resolve the tree obstructions before undoing.");
                if (isCancel) return;
                throw;
            }
            catch (Exception ex) when (IsAncestryError(ex))
            {
                LogWarning($"[{opName}] Ancestry issue – retrying with --ignore-ancestry...");
                args = $"{SshConfigOption}merge -r {range} {EscapeSvnArg(_lastMergeSource)} --ignore-ancestry --non-interactive --accept postpone";
                LogInfo($"[{opName}] Retrying with: svn {args}");
                output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);
            }

            if (autoCommit)
            {
                string msg =
                    $"Undo merge from {_lastMergeSource} (r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})";
                LogInfo($"[{opName}] Auto‑committing: {msg}");
                await SvnRunner.RunAsync(
                    $"{SshConfigOption}commit -m {EscapeSvnArg(msg)}",
                    svnManager.WorkingDir, true, token).ConfigureAwait(false);
                LogSuccess($"[{opName}] Changes committed automatically.");
            }

            ClearRollbackSnapshot();
            await svnManager.RefreshStatus().ConfigureAwait(false);
            await RefreshResolveUI().ConfigureAwait(false);

            LogSuccessBlock($"{opName} Complete",
                $"Successfully reverted merge of {_lastMergeSource} " +
                $"(r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})");
        }

        public async Task RevertToHead()
        {
            float timeSinceLastClick = Time.unscaledTime - _lastRevertToHeadClickTime;
            if (timeSinceLastClick > 5f)
            {
                _lastRevertToHeadClickTime = Time.unscaledTime;
                LogWarningBlock("Reset to HEAD",
                    "This will discard ALL local changes and update to the latest repository revision.\n" +
                    "Press the button again within 5 seconds to confirm.");
                return;
            }
            _lastRevertToHeadClickTime = -10f;

            if (!TryStart()) return;

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                LogWarning("[Reset to HEAD] Step 1/3 – Updating to HEAD...");
                await SvnRunner.RunAsync("update", svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);

                LogWarning("[Reset to HEAD] Step 2/3 – Reverting all local changes...");
                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);

                LogWarning("[Reset to HEAD] Step 3/3 – Cleaning up...");
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);

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
            catch (Exception ex)
            {
                LogErrorLocal($"[Reset Error] {ex.Message}");
            }
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

                string repoRoot = await GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    LogErrorLocal("Repo Root not found.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir)
                    .ConfigureAwait(false);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";
                LogInfo($"Target: {trunkUrl}");

                if (Normalize(currentUrl) == Normalize(trunkUrl))
                {
                    LogWarning("Already on Trunk. Comparison skipped.");
                    return;
                }

                LogInfo("Fetching revision differences...");

                string missingInBranch = await SvnRunner.RunAsync(
                    $"{SshConfigOption}mergeinfo {EscapeSvnArg(trunkUrl)} --show-revs eligible",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                string branchOnlyChanges = await SvnRunner.RunAsync(
                    $"{SshConfigOption}mergeinfo . {EscapeSvnArg(trunkUrl)} --show-revs eligible",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);

                LogInfo("--------------------------------------");
                LogInfo($"Incoming (Trunk -> Branch): {missingCount}");
                LogInfo($"Outgoing (Branch -> Trunk): {localCount}");

                if (missingCount > 0 || localCount > 0)
                {
                    LogWarning("DIVERGENCE DETECTED: trunk and branch are out of sync.");
                    if (missingCount == 0)
                        LogSuccess("No incoming changes. You only have local commits to push back.");
                }
                else
                {
                    LogSuccess("Fully synchronized with Trunk. No merge needed.");
                }
            }
            catch (OperationCanceledException)
            {
                LogWarning("[CompareWithTrunk] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Comparison Error] {ex.Message}");
            }
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
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    string rootOutput = await SvnRunner.RunAsync(
                        "info --show-item repos-root-url",
                        svnManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);
                    repoRoot = rootOutput?.Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(repoRoot))
                    {
                        LogErrorLocal("[Critical Error] Repo root missing.");
                        return Array.Empty<string>();
                    }
                }

                string branchesUrl = $"{repoRoot}/branches";
                LogInfo($"[Debug] Scanning branches at: {branchesUrl}");

                var branchList = await GetRepoListAsync(branchesUrl, CancellationToken.None)
                    .ConfigureAwait(false);

                if (branchList.Length == 0)
                {
                    LogInfo("[FetchAvailableBranches] No branches found " +
                            "(folder may be empty or not exist yet).");
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

        public async Task<string[]> FetchAvailableTags(bool force = false)
        {
            if (!IsReady())
            {
                LogInfo("[Tags] Project not ready yet — returning cached or empty.");
                return _cachedTags ?? Array.Empty<string>();
            }

            if (_isFetchingTagsFlag == 1)
            {
                LogInfo("[Tags] Fetch already in progress → returning cache.");
                return _cachedTags ?? Array.Empty<string>();
            }

            if (!force && _tagsCacheValid && _cachedTags != null)
            {
                LogInfo("[Cache] Using cached tags.");
                return _cachedTags;
            }

            if (!TryStart()) return _cachedTags ?? Array.Empty<string>();

            if (Interlocked.CompareExchange(ref _isFetchingTagsFlag, 1, 0) != 0)
            {
                End();
                return _cachedTags ?? Array.Empty<string>();
            }

            try
            {
                using var cts = new CancellationTokenSource();
                CancellationToken token = cts.Token;

                string repoRoot = await GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Tags] Repo root not found.");
                    return Array.Empty<string>();
                }

                string tagsUrl = $"{repoRoot}/tags";
                LogInfo($"[Tags] Scanning at: {tagsUrl}");

                var tagList = await GetRepoListAsync(tagsUrl, token).ConfigureAwait(false);

                if (tagList.Length == 0)
                {
                    LogInfo("[Tags] No tags found (folder may be empty or not exist yet).");
                    _cachedTags = Array.Empty<string>();
                    _tagsCacheValid = true;
                    return _cachedTags;
                }

                _cachedTags = tagList;
                _tagsCacheValid = true;
                LogSuccess($"[Tags] Found {tagList.Length} tag(s).");
                return tagList;
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Tags Error] {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                Interlocked.Exchange(ref _isFetchingTagsFlag, 0);
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
                LogInfo("[RefreshIfEmpty] Branches cache empty/invalid — fetching...");
                await FetchAvailableBranches(force: false).ConfigureAwait(false);
            }
            else
            {
                LogInfo("[RefreshIfEmpty] Branches cache valid — skipped.");
            }

            if (_cachedTags == null || !_tagsCacheValid)
            {
                LogInfo("[RefreshIfEmpty] Tags cache empty/invalid — fetching...");
                await FetchAvailableTags(force: false).ConfigureAwait(false);
            }
            else
            {
                LogInfo("[RefreshIfEmpty] Tags cache valid — skipped.");
            }
        }

        public async Task ForceMergeFromTrunk(string sourceInput = null)
        {
            float timeSinceLastClick = Time.unscaledTime - _lastForceMergeClickTime;
            if (timeSinceLastClick > 5f)
            {
                _lastForceMergeClickTime = Time.unscaledTime;
                LogWarningBlock("FORCE MERGE DANGER",
                    "This operation will DELETE svn:mergeinfo recursively!\n" +
                    "It may cause duplicate merges and history loss.\n" +
                    "Press the button again within 5 seconds to confirm.");
                return;
            }
            _lastForceMergeClickTime = -10f;

            if (!TryEnterMerging())
            {
                LogWarning("[Force Merge] Already running — request ignored.");
                return;
            }
            if (!TryStart()) { ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await EnsureWcRootAsync(token).ConfigureAwait(false);

                string repoRoot = await GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[Force Merge] Repo root not found.");
                    return;
                }

                string sourceUrl;
                if (!string.IsNullOrWhiteSpace(sourceInput))
                {
                    if (!ValidateSourceInput(sourceInput))
                    {
                        LogErrorLocal("[Force Merge] SECURITY: Invalid source input.");
                        return;
                    }
                    sourceUrl = ResolveSourceUrl(sourceInput, repoRoot);
                }
                else
                {
                    sourceUrl = $"{repoRoot}/trunk";
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir)
                    .ConfigureAwait(false);

                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Cannot merge source into itself.");
                    return;
                }

                LogInfoBlock("FORCE MERGE",
                    $"Ignoring ancestry and merging changes from {sourceUrl}.\n" +
                    "WARNING: This will delete svn:mergeinfo properties and may disrupt future merges.");

                _hadLocalChangesBeforeMerge = await HasPendingMergeChanges(token).ConfigureAwait(false);
                await TryCaptureMergeSnapshot(sourceUrl, token).ConfigureAwait(false);

                LogInfo("[Force Merge] Cleaning up stale mergeinfo properties...");
                try
                {
                    await SvnRunner.RunAsync("propdel svn:mergeinfo -R .",
                        svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    LogSuccess("[Force Merge] Stale mergeinfo cleaned.");
                }
                catch
                {
                    LogInfo("[Force Merge] No stale mergeinfo found (clean state).");
                }

                string args;
                if (_hasRollbackPoint &&
                    !string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) &&
                    !string.IsNullOrWhiteSpace(_lastMergeRevisionAfter) &&
                    _lastMergeRevisionBefore != _lastMergeRevisionAfter)
                {
                    string range = $"{_lastMergeRevisionBefore}:{_lastMergeRevisionAfter}";
                    args = $"{SshConfigOption}merge -r {range} {EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo($"[Force Merge] Range: {range}");
                }
                else
                {
                    args = $"{SshConfigOption}merge {EscapeSvnArg(sourceUrl)} --ignore-ancestry --non-interactive --accept postpone";
                    LogInfo("[Force Merge] No revision range available – merging all changes.");
                }

                LogInfo($"[Force Merge] Executing: svn {args}");
                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);
                await ParseMergeOutput(output, false, token).ConfigureAwait(false);

                await svnManager.RefreshStatus().ConfigureAwait(false);
                await RefreshResolveUI().ConfigureAwait(false);

                LogSuccess("[Force Merge Complete] Changes have been applied.");
                LogWarning("PLEASE COMMIT this merge immediately to record the history.");
                LogWarning("Without a commit, SVN may attempt to re-merge the same changes in the future.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[ForceMerge] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Force Merge Error] {ex.Message}");
            }
            finally
            {
                _hadLocalChangesBeforeMerge = false;
                _mergeCts = null;
                ExitMerging();
                End();
            }
        }

        public async Task RepairMergeHistory()
        {
            float timeSinceLastClick = Time.unscaledTime - _lastRepairMergeClickTime;
            if (timeSinceLastClick > 5f)
            {
                _lastRepairMergeClickTime = Time.unscaledTime;
                LogWarningBlock("REPAIR MERGE HISTORY",
                    "This operation will modify svn:mergeinfo metadata.\n" +
                    "No files will be changed, but repository history will be altered.\n" +
                    "Press the button again within 5 seconds to confirm.");
                return;
            }
            _lastRepairMergeClickTime = -10f;

            if (!TryEnterMerging())
            {
                LogWarning("[RepairMergeHistory] Already merging...");
                return;
            }
            if (!TryStart()) { ExitMerging(); return; }

            using var cts = new CancellationTokenSource();
            _mergeCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string repoRoot = await GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("[RepairMergeHistory] Repo root not found.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir)
                    .ConfigureAwait(false);

                if (!Normalize(currentUrl).EndsWith("/trunk"))
                {
                    LogErrorLocal("[RepairMergeHistory] This operation must be performed on trunk.");
                    LogErrorLocal("Please switch to trunk first and then run this command.");
                    return;
                }

                string sourceUrl = null;

                if (_hasRollbackPoint && !string.IsNullOrWhiteSpace(_lastMergeSource))
                {
                    sourceUrl = _lastMergeSource;
                    LogInfo($"[RepairMergeHistory] Using stored merge source: {sourceUrl}");
                }
                else
                {
                    LogWarning("[RepairMergeHistory] No stored merge source found. " +
                               "Falling back to log heuristics (less reliable)...");
                    sourceUrl = await DetermineSourceBranchFromLogAsync(repoRoot, token)
                        .ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(sourceUrl))
                {
                    LogErrorLocal(
                        "[RepairMergeHistory] Could not determine source branch.\n" +
                        "Please perform a merge first so the source is remembered,\n" +
                        "or manually select the branch and try again.");
                    return;
                }

                _hadLocalChangesBeforeMerge = await HasPendingMergeChanges(token).ConfigureAwait(false);

                LogInfo($"[RepairMergeHistory] Source branch: {sourceUrl}");
                string args =
                    $"{SshConfigOption}merge --record-only --ignore-ancestry {EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
                LogInfo($"[RepairMergeHistory] Executing: svn {args}");

                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, true, token)
                    .ConfigureAwait(false);

                if (output.Contains("Recording") ||
                    output.Contains("recorded") ||
                    string.IsNullOrWhiteSpace(output))
                {
                    LogSuccess("[RepairMergeHistory] Mergeinfo successfully recorded.");
                    LogSuccess("Please commit this change immediately.");
                    LogSuccess("After commit, standard reintegrate from branch to trunk will work correctly.");
                    await svnManager.RefreshStatus().ConfigureAwait(false);
                }
                else
                {
                    LogErrorLocal($"[RepairMergeHistory] Unexpected output: {output}");
                }
            }
            catch (OperationCanceledException)
            {
                LogWarning("[RepairMergeHistory] Cancelled by user.");
                await SafeCleanupAfterCancel().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[RepairMergeHistory Error] {ex.Message}");
            }
            finally
            {
                _hadLocalChangesBeforeMerge = false;
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

        private void SaveSnapshotToFile()
        {
            try
            {
                string path = GetSnapshotFilePath();
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
                string path = GetSnapshotFilePath();
                if (path == null || !File.Exists(path)) return;

                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<SnapshotData>(json);

                if (data == null ||
                    string.IsNullOrWhiteSpace(data.Source) ||
                    string.IsNullOrWhiteSpace(data.RevisionBefore) ||
                    string.IsNullOrWhiteSpace(data.RevisionAfter))
                    return;

                _lastMergeSource = data.Source;
                _lastMergeRevisionBefore = data.RevisionBefore;
                _lastMergeRevisionAfter = data.RevisionAfter;
                _hasRollbackPoint = true;

                LogInfo(
                    $"[Snapshot] Loaded from file: {data.Source} | " +
                    $"r{data.RevisionBefore} → r{data.RevisionAfter} | " +
                    $"Timestamp: {data.Timestamp}");
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
                string path = GetSnapshotFilePath();
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private void SaveRollbackSnapshot()
        {
            if (!_hasRollbackPoint) return;
            SaveSnapshotToFile();
            LogInfo($"[Snapshot] Saved → {_lastMergeSource} | " +
                    $"r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
        }

        private void LoadRollbackSnapshot()
        {
            if (PlayerPrefs.GetInt(PrefHasRollback, 0) == 1)
            {
                _lastMergeSource = PlayerPrefs.GetString(PrefMergeSource, "");
                _lastMergeRevisionBefore = PlayerPrefs.GetString(PrefMergeRevBefore, "");
                _lastMergeRevisionAfter = PlayerPrefs.GetString(PrefMergeRevAfter, "");
                _hasRollbackPoint = !string.IsNullOrWhiteSpace(_lastMergeSource) &&
                                    !string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) &&
                                    !string.IsNullOrWhiteSpace(_lastMergeRevisionAfter);

                if (_hasRollbackPoint)
                {
                    string ts = PlayerPrefs.GetString(PrefMergeTimestamp, "unknown");
                    LogInfo($"[Snapshot] Migrated from PlayerPrefs → {_lastMergeSource} | " +
                            $"r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter} | " +
                            $"Timestamp: {ts}");
                    SaveSnapshotToFile();
                }

                PlayerPrefs.DeleteKey(PrefMergeSource);
                PlayerPrefs.DeleteKey(PrefMergeRevBefore);
                PlayerPrefs.DeleteKey(PrefMergeRevAfter);
                PlayerPrefs.DeleteKey(PrefHasRollback);
                PlayerPrefs.DeleteKey(PrefMergeTimestamp);
                PlayerPrefs.Save();
                return;
            }

            LoadSnapshotFromFile();
        }

        private void ClearRollbackSnapshot()
        {
            _hasRollbackPoint = false;
            _lastMergeSource = null;
            _lastMergeRevisionBefore = null;
            _lastMergeRevisionAfter = null;
            DeleteSnapshotFile();
            LogInfo("[Snapshot] Cleared from memory and file.");
        }

        private enum MergeSnapshotState { Error, ExistingMerge, NoSnapshot }

        private async Task<MergeSnapshotState> TryCaptureMergeSnapshot(
            string sourceUrl, CancellationToken token)
        {
            try
            {
                string baseRevOutput = await SvnRunner.RunAsync(
                    "info --show-item revision",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                if (!long.TryParse(baseRevOutput?.Trim(), out long baseRevision))
                {
                    LogWarning("[Snapshot] Could not determine BASE revision.");
                    return MergeSnapshotState.Error;
                }

                string eligible = await SvnRunner.RunAsync(
                    $"{SshConfigOption}mergeinfo {EscapeSvnArg(sourceUrl)} . --show-revs eligible",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(eligible))
                {
                    LogInfo("[Snapshot] No eligible revisions – no rollback point created.");
                    return MergeSnapshotState.NoSnapshot;
                }

                var revisions = ParseRevisionList(eligible);
                if (revisions.Count == 0)
                {
                    LogWarning("[Snapshot] Mergeinfo exists but revisions are invalid.");
                    return MergeSnapshotState.Error;
                }

                long lastEligible = revisions[revisions.Count - 1];

                _lastMergeSource = sourceUrl;
                _lastMergeRevisionBefore = baseRevision.ToString();
                _lastMergeRevisionAfter = lastEligible.ToString();
                _hasRollbackPoint = true;
                SaveRollbackSnapshot();

                LogInfoBlock("MERGE SNAPSHOT CREATED",
                    $"BASE Revision (before) : r{_lastMergeRevisionBefore}\n" +
                    $"Last Eligible (after)  : r{_lastMergeRevisionAfter}");

                return MergeSnapshotState.ExistingMerge;
            }
            catch (Exception ex)
            {
                LogWarning($"[Snapshot Error] {ex.Message}");
                _hasRollbackPoint = false;
                return MergeSnapshotState.Error;
            }
        }

        private static List<long> ParseRevisionList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<long>();

            return raw
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.StartsWith("r", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Substring(1))
                .Select(x => long.TryParse(x, out long rev) ? rev : -1)
                .Where(x => x >= 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }

        private async Task<string> ExecuteMergeCommand(
            string sourceUrl, bool isDryRun, CancellationToken token)
        {
            string dryRunFlag = isDryRun ? "--dry-run " : string.Empty;
            string args =
                $"{SshConfigOption}merge {dryRunFlag}{EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";

            LogInfoBlock("SVN MERGE COMMAND", args);

            try
            {
                return await SvnRunner.RunAsync(args, svnManager.WorkingDir, !isDryRun, token)
                    .ConfigureAwait(false);
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
                LogWarningBlock("ANCESTRY PROBLEM DETECTED",
                    "Standard merge failed. Retrying with --ignore-ancestry.");

                string retryArgs =
                    $"{SshConfigOption}merge --ignore-ancestry {dryRunFlag}{EscapeSvnArg(sourceUrl)} --non-interactive --accept postpone";
                LogInfoBlock("SVN MERGE RETRY", retryArgs);

                try
                {
                    return await SvnRunner.RunAsync(retryArgs, svnManager.WorkingDir, !isDryRun, token)
                        .ConfigureAwait(false);
                }
                catch (Exception retryEx) when (retryEx.Message.Contains("E155015"))
                {
                    string mode = isDryRun
                        ? "simulation (ignored ancestry)"
                        : "LIVE MERGE (ignored ancestry)";
                    LogWarning($"[Merge] Conflicts produced during {mode}.");
                    return retryEx.Message;
                }
            }
        }

        private async Task ParseMergeOutput(string output, bool isDryRun, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(output) ||
                output.IndexOf("already up to date", StringComparison.OrdinalIgnoreCase) >= 0)
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
            int conflicts = 0;
            int changed = 0;
            int skipped = 0;
            int realChanges = 0;
            bool mergeInfoUpdated = false;

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string raw in lines)
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

                var skippedMatch = SkippedLineRegex.Match(line);
                if (skippedMatch.Success)
                {
                    skipped++;
                    result.SkippedPaths.Add(skippedMatch.Groups[1].Value.Trim());
                    continue;
                }

                if (line.Contains("tree conflict", StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    string conflictPath = ExtractPathFromConflictLine(line);
                    result.Files.Add(new MergeFileInfo { State = 'C', Path = conflictPath });

                    LogWarning("<color=#FF4444><b>DETECTED TREE CONFLICT!</b></color>");
                    LogWarning("<color=#FFAA00>Standard 'Cancel Local Merge' will likely FAIL " +
                               "now due to SVN limitations.</color>");
                    LogWarning("<color=#FFAA00>If this merge was a mistake, your safest option " +
                               "is 'Revert to HEAD'.</color>");
                    continue;
                }

                var match = MergeLineRegex.Match(line);
                if (!match.Success) continue;

                char state = match.Groups[1].Value[0];
                string path = match.Groups[2].Value.Trim();

                if (state == 'C')
                {
                    conflicts++;
                    result.Files.Add(new MergeFileInfo { State = 'C', Path = path });
                    continue;
                }

                if (path.Contains("conflict", StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    result.Files.Add(new MergeFileInfo { State = 'C', Path = path });
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

                    bool isMergeInfoOnly = path == "." ||
                                           (path.Length <= 2 && path.EndsWith("."));

                    if (!isMergeInfoOnly && !string.IsNullOrWhiteSpace(path))
                    {
                        realChanges++;
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
                string dryRunMsg =
                    $"Potential file changes : {realChanges}\n" +
                    $"Conflicts detected     : {conflicts}";

                if (mergeInfoUpdated)
                    dryRunMsg += "\nSVN merge history would be updated.";

                if (skipped > 0)
                    dryRunMsg += $"\nSkipped items          : {skipped}";

                if (changed > realChanges)
                    dryRunMsg += $"\n(includes {changed - realChanges} property-only changes)";

                if (conflicts > 0)
                    LogWarningBlock("DRY RUN RESULT", dryRunMsg);
                else
                    LogInfoBlock("DRY RUN RESULT", dryRunMsg);

                var handler = OnDryRunCompleted;
                if (handler != null)
                {
                    var localResult = result;
                    UnityMainThreadDispatcher.Enqueue(() => handler(localResult));
                }
            }
            else
            {
                string liveMsg =
                    $"Files changed           : {realChanges}\n" +
                    $"Conflicts               : {conflicts}";

                if (mergeInfoUpdated)
                    liveMsg += "\nSVN merge history updated.";

                if (skipped > 0)
                    liveMsg += $"\nSkipped items            : {skipped}";

                if (conflicts > 0)
                {
                    LogErrorLocal("====================================");
                    LogErrorLocal("[MERGE COMPLETED WITH CONFLICTS]");
                    LogErrorLocal(liveMsg);
                    LogErrorLocal("====================================");
                    LogWarning("Resolve conflicts in the Files panel, then commit.");
                }
                else
                {
                    LogSuccessBlock("MERGE COMPLETED", liveMsg);
                }
            }
        }

        private static string ExtractPathFromConflictLine(string line)
        {
            int quoteStart = line.IndexOf('\'');
            int quoteEnd = line.LastIndexOf('\'');
            if (quoteStart >= 0 && quoteEnd > quoteStart + 1)
                return line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);

            if (line.Length > 2) return line.Substring(2).Trim();
            return line;
        }

        private async Task<string> DetermineSourceBranchFromLogAsync(
            string repoRoot, CancellationToken token)
        {
            try
            {
                LogInfo("[RepairMergeHistory] Searching log for incomplete reintegrate...");

                string logOutput = await SvnRunner.RunAsync(
                    $"{SshConfigOption}log --stop-on-copy --xml --verbose -l 20",
                    svnManager.WorkingDir, true, token).ConfigureAwait(false);

                long targetRev = FindIncompleteReintegrateRevisionInXml(logOutput);
                if (targetRev <= 0)
                {
                    LogInfo("[RepairMergeHistory] No incomplete reintegrate found in log.");
                    return null;
                }

                LogInfo($"[RepairMergeHistory] Found candidate at r{targetRev}. " +
                        "Attempting to determine source branch from mergeinfo diff...");

                string diffOutput = await SvnRunner.RunAsync(
                    $"{SshConfigOption}diff -c {targetRev} --properties-only",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(diffOutput))
                {
                    foreach (string diffLine in diffOutput.Split(
                        new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = diffLine.Trim();

                        if (trimmed.StartsWith("+") &&
                            (trimmed.Contains("/branches/") || trimmed.Contains("/tags/")))
                        {
                            string urlPart = trimmed.TrimStart('+').Trim();
                            int colonIdx = urlPart.IndexOf(':');
                            if (colonIdx > 0)
                                urlPart = urlPart.Substring(0, colonIdx);
                            if (urlPart.StartsWith("/"))
                                urlPart = $"{repoRoot}{urlPart}";
                            LogInfo($"[RepairMergeHistory] Determined source: {urlPart}");
                            return urlPart;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"[RepairMergeHistory] Heuristic failed: {ex.Message}");
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
            catch (Exception)
            {
                // XML parse failed
            }

            return -1;
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

        private static int CountRevisions(string eligibleOutput)
        {
            if (string.IsNullOrWhiteSpace(eligibleOutput)) return 0;
            return eligibleOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(x => x.TrimStart().StartsWith("r", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> HasPendingMergeChanges(CancellationToken token = default)
        {
            try
            {
                string status = await SvnRunner.RunAsync(
                    "status --depth=infinity",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(status)) return false;

                foreach (string line in status.Split(
                    new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.TrimStart();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

                    char col1 = trimmed.Length > 0 ? trimmed[0] : ' ';
                    char col2 = trimmed.Length > 1 ? trimmed[1] : ' ';

                    if (col1 != ' ' || col2 != ' ')
                        return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private async Task RefreshResolveUI()
        {
            try
            {
                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarning($"[RefreshResolveUI] {ex.Message}");
            }
        }

        private async Task SafeCleanupAfterCancel()
        {
            try
            {
                if (_hadLocalChangesBeforeMerge)
                {
                    LogWarning("[SafeCleanup] Local changes existed before merge – " +
                               "automatic revert skipped to protect your work.");
                    LogWarning("[SafeCleanup] Run 'cleanup' manually if needed, " +
                               "then resolve the working copy state by hand.");

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, timeoutCts.Token)
                        .ConfigureAwait(false);
                    return;
                }

                LogWarning("[SafeCleanup] Reverting unfinished merge changes...");

                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                CancellationToken ct = cleanupCts.Token;

                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir, true, ct)
                    .ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, ct)
                    .ConfigureAwait(false);

                LogInfo("[SafeCleanup] Working copy restored to pre-merge state.");
            }
            catch (OperationCanceledException)
            {
                LogWarning("[SafeCleanup] Cleanup timed out – working copy may need manual attention.");
            }
            catch (Exception ex)
            {
                LogWarning($"[SafeCleanup] {ex.Message}");
            }
        }

        // ===================================================================
        //  FIX: Typy wynikowe przeniesione z powrotem do wnętrza klasy
        //  dzięki temu SVNMerge.MergeFileResult zadziała w innych plikach
        // ===================================================================

        public class MergeFileResult
        {
            public List<MergeFileInfo> Files = new List<MergeFileInfo>();
            public List<string> SkippedPaths = new List<string>();
            public int Added;
            public int Updated;
            public int Deleted;
            public int Conflicts;
            public int Skipped;
            public bool MergeInfoUpdated;
            public int RealChanges;
        }

        public class MergeFileInfo
        {
            public char State;
            public string Path;
        }
    }
}