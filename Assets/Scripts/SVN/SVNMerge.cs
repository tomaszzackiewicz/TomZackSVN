using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace SVN.Core
{
    public class SVNMerge : SVNBase
    {
        private const string SvnAncestryErrorMsg = "ancestry";

        private bool _branchesCacheValid = false;
        private string[] _cachedBranches = null;

        private string _lastMergeSource;
        private bool _lastMergeWasDryRun;
        private bool _hasRollbackPoint;
        private bool _isMerging;
        private int _lastIncomingCount = -1; // -1 no branch comparison

        private string _lastMergeRevisionBefore;
        private string _lastMergeRevisionAfter;

        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private enum MergeSnapshotState
        {
            Error,
            FirstMerge,
            ExistingMerge
        }

        private async Task<MergeSnapshotState> TryCaptureMergeSnapshot(string sourceUrl)
        {
            try
            {
                string eligible =
     await SvnRunner.RunAsync(
         $"mergeinfo \"{sourceUrl}\" . --show-revs eligible",
         svnManager.WorkingDir);

                if (string.IsNullOrWhiteSpace(eligible))
                {
                    LogInfo("[Snapshot] No merge history found.");

                    string currentRevision =
                        await GetWorkingCopyRevision();

                    _lastMergeSource = sourceUrl;
                    _lastMergeRevisionBefore = currentRevision;
                    _lastMergeRevisionAfter = currentRevision;

                    _hasRollbackPoint = true;

                    LogInfo("====================================");
                    LogInfo("[FIRST MERGE SNAPSHOT CREATED]");
                    LogInfo($"Base Revision : r{currentRevision}");
                    LogInfo("====================================");

                    return MergeSnapshotState.FirstMerge;
                }

                var revisions = eligible
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("r"))
                    .Select(x => x.TrimStart('r'))
                    .Select(x =>
                    {
                        bool ok = long.TryParse(x, out long rev);
                        return (ok, rev);
                    })
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

                LogInfo("====================================");
                LogInfo("[MERGE SNAPSHOT CREATED]");
                LogInfo($"Source Revision Range : r{_lastMergeRevisionBefore} -> r{_lastMergeRevisionAfter}");
                LogInfo("====================================");

                return MergeSnapshotState.ExistingMerge;
            }
            catch (Exception ex)
            {
                LogWarning($"[Snapshot Error] {ex.Message}");

                _hasRollbackPoint = false;

                return MergeSnapshotState.Error;
            }
        }

        public async Task CompareWithTrunk()
        {
            if (!TryStart()) return;

            try
            {
                LogInfo("====================================");
                LogInfo("[Comparison] Starting analysis against Trunk...");

                string repoRoot = svnManager.GetRepoRoot();
                if (string.IsNullOrEmpty(repoRoot))
                {
                    LogErrorLocal("Repo Root not found.");
                    return;
                }

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);
                string trunkUrl = $"{repoRoot.TrimEnd('/')}/trunk";

                LogInfo($"Target: {trunkUrl}");

                if (Normalize(currentUrl) == Normalize(trunkUrl))
                {
                    LogWarning("Already on Trunk. Comparison skipped.");
                    return;
                }

                LogInfo("Fetching revision differences...");

                string missingCmd = $"mergeinfo \"{trunkUrl}\" --show-revs eligible";
                string missingInBranch = await SvnRunner.RunAsync(missingCmd, svnManager.WorkingDir);

                string localCmd = $"mergeinfo . \"{trunkUrl}\" --show-revs eligible";
                string branchOnlyChanges = await SvnRunner.RunAsync(localCmd, svnManager.WorkingDir);

                int missingCount = CountRevisions(missingInBranch);
                int localCount = CountRevisions(branchOnlyChanges);

                _lastIncomingCount = missingCount;

                LogInfo("--------------------------------------");
                LogInfo($"Incoming (Trunk -> Branch): {missingCount}");
                LogInfo($"Outgoing (Branch -> Trunk): {localCount}");

                if (missingCount > 0 || localCount > 0)
                {
                    LogWarning("DIVERGENCE DETECTED: trunk and branch are out of sync.");
                    if (missingCount == 0)
                    {
                        LogSuccess("No incoming changes. You only have local commits to push back.");
                    }
                }
                else
                {
                    LogSuccess("Fully synchronized with Trunk. No merge needed.");
                }
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Comparison Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        public async Task ExecuteMerge(string sourceInput, bool isDryRun)
        {
            bool hasPendingChanges = await HasPendingMergeChanges();

            if (hasPendingChanges)
            {
                LogWarning("====================================");
                LogWarning("[MERGE BLOCKED]");
                LogWarning("Working copy contains uncommitted merge changes.");
                LogWarning("Commit, revert or cleanup before merging again.");
                LogWarning("====================================");

                return;
            }

            if (sourceInput.Equals("trunk", StringComparison.OrdinalIgnoreCase) && _lastIncomingCount == 0)
            {
                LogInfo("====================================");
                LogSuccess("[Merge Blocked] Branch is already fully synchronized with Trunk.");
                LogSuccess("No incoming revisions to pull. Operation aborted safely.");
                LogInfo("====================================");
                return;
            }

            if (_isMerging)
            {
                LogWarning("[Merge] Already running — request ignored.");
                return;
            }

            if (string.IsNullOrWhiteSpace(sourceInput))
                return;

            if (!TryStart())
                return;

            _isMerging = true;

            try
            {
                LogInfo("====================================");
                LogInfo("[MERGE SESSION START]");
                LogInfo($"Source: {sourceInput}");
                LogInfo($"Mode: {(isDryRun ? "DRY RUN" : "LIVE MERGE")}");
                LogInfo("====================================");

                string repoRoot = svnManager.GetRepoRoot()?.Trim().TrimEnd('/');

                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("Repo Root not found.");
                    return;
                }

                if (IsInvalidPath(sourceInput))
                {
                    LogErrorLocal("SECURITY: Invalid merge source.");
                    return;
                }

                string cleanedInput = sourceInput.Trim();

                string sourceUrl =
                    cleanedInput.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                        ? $"{repoRoot}/trunk"
                        : $"{repoRoot}/branches/{cleanedInput}";

                string currentUrl =
                    await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);

                LogInfo($"Current URL: {currentUrl}");
                LogInfo($"Source URL : {sourceUrl}");

                bool sourceIsTrunk =
                    Normalize(sourceUrl).EndsWith("/trunk");

                bool currentIsTrunk =
                    Normalize(currentUrl).EndsWith("/trunk");

                if (sourceIsTrunk && !currentIsTrunk)
                {
                    LogInfo("[Direction] trunk → branch");
                }
                else if (!sourceIsTrunk && currentIsTrunk)
                {
                    LogInfo("[Direction] branch → trunk");
                }
                else
                {
                    LogInfo("[Direction] branch → branch");
                }

                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Cannot merge branch into itself.");
                    return;
                }

                string currentUuid =
                    await SvnRunner.RunAsync(
                        "info --show-item repos-uuid",
                        svnManager.WorkingDir);

                string sourceUuid =
                    await SvnRunner.RunAsync(
                        $"info \"{sourceUrl}\" --show-item repos-uuid",
                        svnManager.WorkingDir);

                if (currentUuid.Trim() != sourceUuid.Trim())
                {
                    LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                MergeSnapshotState snapshotState = MergeSnapshotState.Error;

                if (!isDryRun)
                {
                    snapshotState =
                        await TryCaptureMergeSnapshot(sourceUrl);

                    if (snapshotState == MergeSnapshotState.Error)
                    {
                        LogWarning("[Merge] Snapshot capture failed.");
                    }
                }

                bool firstMerge =
                    snapshotState == MergeSnapshotState.FirstMerge;

                string dryRunFlag = isDryRun ? "--dry-run " : "";
                string args;

                if (sourceIsTrunk && !currentIsTrunk)
                {
                    // trunk → branch
                    if (firstMerge)
                    {
                        LogInfo("[Merge Strategy] FIRST MERGE from trunk. Using SVN automatic tracking.");
                    }
                    else
                    {
                        LogInfo("[Merge Strategy] Subsequent catch-up merge. Merging missing revisions.");
                    }

                    args = $"merge {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";
                }
                else if (!sourceIsTrunk && currentIsTrunk)
                {
                    LogInfo("[Merge Strategy] Merging branch feature-set back into Trunk.");

                    args = $"merge {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";
                }
                else
                {
                    LogInfo("[Merge Strategy] Inter-branch merge.");

                    args = $"merge {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";
                }

                LogInfo("====================================");
                LogInfo("[SVN MERGE COMMAND]");
                LogInfo(args);
                LogInfo("====================================");

                string output =
                    await SvnRunner.RunAsync(
                        args,
                        svnManager.WorkingDir,
                        isDryRun);
#if UNITY_EDITOR

                if (!string.IsNullOrWhiteSpace(output))
                {
                    LogInfo("====================================");
                    LogInfo("[RAW SVN OUTPUT]");

                    foreach (string line in output.Split(
                                 new[] { '\r', '\n' },
                                 StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("A ") ||
                            line.StartsWith("U ") ||
                            line.StartsWith("C ") ||
                            line.StartsWith("D "))
                        {
                            continue;
                        }

                    }

                    LogInfo("====================================");
                }

#endif

                await ParseMergeOutput(output, isDryRun);

                if (!isDryRun)
                {
                    await svnManager.RefreshStatus();
                    await svnManager.GetModule<SVNResolve>().RefreshConflictUI();

                    LogSuccess("[Merge Complete]");
                    LogWarning("Review changes before commit.");
                }
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Merge Error] {ex.Message}");
            }
            finally
            {
                _isMerging = false;
                End();
            }
        }

        private async Task ParseMergeOutput(string output, bool isDryRun)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                LogSuccess("Everything is already up to date.");
                return;
            }

            if (output.IndexOf("already up to date", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LogSuccess("Everything is already up to date.");
                return;
            }

            var lines = output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            int conflicts = 0;
            int changed = 0;
            int skipped = 0;
            int realChanges = 0;

            int added = 0;
            int updated = 0;
            int deleted = 0;

            bool mergeInfoUpdated = false;

            const string validStates = "UADGRCM";

            foreach (string raw in lines)
            {
                string line = raw.TrimStart();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.Contains("Recording mergeinfo", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("recorded mergeinfo", StringComparison.OrdinalIgnoreCase))
                {
                    mergeInfoUpdated = true;
                    continue;
                }

                if (line.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                char state = line[0];

                bool isConflictLine =
                    state == 'C' ||
                    line.StartsWith("C") ||
                    line.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("tree conflict", StringComparison.OrdinalIgnoreCase);

                if (isConflictLine)
                {
                    conflicts++;
                    continue;
                }

                switch (state)
                {
                    case 'A':
                        added++;
                        break;

                    case 'U':
                    case 'G':
                        updated++;
                        break;

                    case 'D':
                        deleted++;
                        break;
                }

                if (validStates.Contains(state))
                {
                    changed++;

                    bool isMergeInfoOnly =
                        line.Trim() == "." ||
                        line.EndsWith(" .") ||
                        line.EndsWith(".") && line.Length <= 3;

                    if (!isMergeInfoOnly)
                        realChanges++;
                }
            }

            if (isDryRun)
            {
                LogInfo("====================================");
                LogInfo("[DRY RUN RESULT]");
                LogInfo("====================================");

                LogInfo($"Potential file changes : {realChanges}");
                LogInfo($"Conflicts detected     : {conflicts}");

                if (mergeInfoUpdated)
                    LogInfo("SVN merge history would be updated.");

                if (skipped > 0)
                    LogWarning($"Skipped items          : {skipped}");

                if (realChanges == 0 && conflicts == 0)
                    LogSuccess("No incoming file changes detected.");

                return;
            }

            LogSuccess("====================================");
            LogSuccess("MERGE COMPLETED SUCCESSFULLY");
            LogSuccess("====================================");

            var realStats = await GetRealDiffStats();

            LogInfo($"Total change entries : {changed}");
            LogInfo($"Added files      : {realStats.added}");
            LogInfo($"Updated files    : {realStats.updated}");
            LogInfo($"Deleted files    : {realStats.deleted}");

            if (mergeInfoUpdated)
            {
                LogInfo("Merge history updated.");
            }

            if (conflicts > 0)
            {
                LogErrorLocal($"Conflicts detected : {conflicts}");
            }

            if (skipped > 0)
            {
                LogWarning($"Skipped items : {skipped}");
            }

            if (realChanges == 0 && conflicts == 0)
            {
                LogSuccess("Merge executed but no real file changes were applied.");
            }

            LogWarning("Review changes before commit.");
        }

        private async Task<bool> HasPendingMergeChanges()
        {
            try
            {
                string status =
                    await SvnRunner.RunAsync(
                        "status",
                        svnManager.WorkingDir);

                if (string.IsNullOrWhiteSpace(status))
                    return false;

                var lines = status.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

                foreach (string raw in lines)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    string line = raw.TrimEnd();

                    if (line.Length == 0)
                        continue;

                    char state = line[0];

                    switch (state)
                    {
                        case 'A':
                        case 'M':
                        case 'D':
                        case 'C':
                        case 'R':
                        case 'G':
                        case '!':
                            return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                LogWarning($"[Merge Check Failed] {ex.Message}");

                return true;
            }
        }

        private bool _isFetchingBranches;

        public async Task<string[]> FetchAvailableBranches(bool force = false)
        {
            if (!TryStart())
                return Array.Empty<string>();

            try
            {
                if (_isFetchingBranches)
                {
                    LogInfo("[Branches] Fetch already in progress → skipping duplicate call.");
                    return _cachedBranches ?? Array.Empty<string>();
                }

                _isFetchingBranches = true;

                try
                {
                    if (!force && _branchesCacheValid && _cachedBranches != null)
                    {
                        LogInfo("[Cache] Using cached branches.");
                        return _cachedBranches;
                    }

                    await svnManager.CancelBackgroundTasksAsync();

                    string repoRoot = svnManager.GetRepoRoot()?.Trim().TrimEnd('/');

                    if (string.IsNullOrWhiteSpace(repoRoot))
                    {
                        string rootOutput =
                            await SvnRunner.RunAsync(
                                "info --show-item repos-root-url",
                                svnManager.WorkingDir);

                        repoRoot = rootOutput?.Trim().TrimEnd('/');

                        if (string.IsNullOrWhiteSpace(repoRoot))
                        {
                            LogErrorLocal("[Critical Error] Repo root missing.");
                            return Array.Empty<string>();
                        }
                    }

                    string branchesUrl = $"{repoRoot}/branches";

                    LogInfo("[Debug] Scanning branches at:");
                    LogInfo(branchesUrl);

                    string rawOutput =
                        await SvnRunner.RunAsync(
                            $"list \"{branchesUrl}\" --non-interactive",
                            svnManager.WorkingDir);

                    if (string.IsNullOrWhiteSpace(rawOutput))
                    {
                        LogWarning("Branches folder is empty.");

                        _cachedBranches = Array.Empty<string>();
                        _branchesCacheValid = true;

                        return _cachedBranches;
                    }

                    var branchList = rawOutput
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Select(x => x.TrimEnd('/'))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Where(x => !x.StartsWith("*", StringComparison.Ordinal))
                        .Where(x => x.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) < 0)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();

                    _cachedBranches = branchList;
                    _branchesCacheValid = true;

                    LogSuccess($"Found {branchList.Length} branch(es).");

                    return branchList;
                }
                finally
                {
                    _isFetchingBranches = false;
                }
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Critical Error] Scan failed: {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                End();
            }
        }

        public async Task CancelLocalMerge()
        {
            if (!TryStart())
                return;

            try
            {
                LogWarning("====================================");
                LogWarning("[ROLLBACK] Reverting local merge changes...");
                LogWarning("====================================");

                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir);
                await SvnRunner.RunAsync("status", svnManager.WorkingDir);

                _hasRollbackPoint = false;
                _lastMergeSource = null;
                _lastMergeRevisionBefore = null;
                _lastMergeRevisionAfter = null;

                await svnManager.RefreshStatus();
                await svnManager.GetModule<SVNResolve>().RefreshConflictUI();

                LogSuccess("[Rollback Complete]");
                LogSuccess("Local workspace cleaned.");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Rollback Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        public async Task UndoLastMerge()
        {
            if (!TryStart())
                return;

            try
            {

                bool hasPendingChanges = await HasPendingMergeChanges();

                if (hasPendingChanges)
                {
                    LogWarning("====================================");
                    LogWarning("[Undo Blocked] Uncommitted changes detected (dirty working copy).");
                    LogWarning("• Use 'Cancel Local Merge' to discard local merge state.");
                    LogWarning("• 'Undo Last Merge' only works for already applied/committed merges.");
                    LogWarning("Commit or cancel current changes before using this function.");
                    LogWarning("====================================");
                    return;
                }

                if (!_hasRollbackPoint ||
                    string.IsNullOrWhiteSpace(_lastMergeSource) ||
                    string.IsNullOrWhiteSpace(_lastMergeRevisionBefore) ||
                    string.IsNullOrWhiteSpace(_lastMergeRevisionAfter))
                {
                    LogWarning("[Undo] No rollback point available (merge not tracked or session lost).");
                    return;
                }

                if (!long.TryParse(_lastMergeRevisionBefore, out long baseRevision))
                {
                    LogErrorLocal("[Undo] Invalid base revision in snapshot.");
                    return;
                }

                if (!long.TryParse(_lastMergeRevisionAfter, out long lastRevision))
                {
                    LogErrorLocal("[Undo] Invalid last revision in snapshot.");
                    return;
                }

                string wcStatus = await SvnRunner.RunAsync("status", svnManager.WorkingDir);
                if (!string.IsNullOrWhiteSpace(wcStatus))
                {
                    LogWarning("[Undo] Working copy not clean — proceeding is risky.");
                }

                LogWarning("====================================");
                LogWarning("[UNDO MERGE]");
                LogWarning($"Source: {_lastMergeSource}");
                LogWarning($"Range: r{baseRevision} -> r{lastRevision}");
                LogWarning("====================================");

                string range = $"{lastRevision}:{baseRevision}";

                string args =
                    $"merge -r {range} \"{_lastMergeSource}\" " +
                    $"--non-interactive --accept postpone";

                string output;

                try
                {
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                }
                catch (Exception ex) when (ex.Message.Contains(SvnAncestryErrorMsg))
                {
                    LogWarning("[Undo] Retry with --ignore-ancestry...");

                    string fallback =
                        $"merge -r {range} \"{_lastMergeSource}\" " +
                        $"--ignore-ancestry --non-interactive --accept postpone";

                    output = await SvnRunner.RunAsync(fallback, svnManager.WorkingDir);
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogWarning("[Undo] SVN returned empty output — possible no effective changes.");
                }

                await ParseMergeOutput(output, false);
                await svnManager.RefreshStatus();
                await svnManager.GetModule<SVNResolve>().RefreshConflictUI();

                LogSuccess("[Undo Complete] Merge reverted safely.");

                _hasRollbackPoint = false;
                _lastMergeSource = null;
                _lastMergeRevisionBefore = null;
                _lastMergeRevisionAfter = null;
            }
            catch (Exception ex)
            {
                LogErrorLocal("[Undo Error] " + ex.Message);
            }
            finally
            {
                End();
            }
        }

        private int CountRevisions(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return 0;

            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Count(x =>
                    x.StartsWith("r") &&
                    long.TryParse(x.TrimStart('r'), out _));
        }

        private async Task<(int added, int updated, int deleted)> GetRealDiffStats()
        {
            try
            {
                string output =
                    await SvnRunner.RunAsync(
                        "diff --summarize",
                        svnManager.WorkingDir);

                if (string.IsNullOrWhiteSpace(output))
                    return (0, 0, 0);

                int added = 0;
                int updated = 0;
                int deleted = 0;

                var lines = output.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

                foreach (var raw in lines)
                {
                    string line = raw.TrimStart();

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    char state = line[0];

                    switch (state)
                    {
                        case 'A':
                            added++;
                            break;

                        case 'M':
                            updated++;
                            break;

                        case 'D':
                            deleted++;
                            break;
                    }
                }

                return (added, updated, deleted);
            }
            catch
            {
                return (0, 0, 0);
            }
        }

        private string Normalize(string input)
        {
            return (input ?? "")
                .Trim()
                .Replace("\\", "/")
                .TrimEnd('/')
                .ToLowerInvariant();
        }

        private bool IsInvalidPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return true;

            string sanitizedInput = input.Replace("://", "");

            return sanitizedInput.Contains("..") ||
                   sanitizedInput.Contains("//") ||
                   sanitizedInput.Contains("\\") ||
                   sanitizedInput.Contains("\0");
        }

        private async Task<string> GetWorkingCopyRevision()
        {
            try
            {
                string output = await SvnRunner.RunAsync("info --show-item revision", svnManager.WorkingDir);
                return output?.Trim();
            }
            catch
            {
                return "unknown";
            }
        }

        public void InvalidateBranchCache()
        {
            _branchesCacheValid = false;
            _cachedBranches = null;

            LogInfo("[Cache] Branch cache invalidated.");
        }

        protected override TMPro.TMP_Text GetConsole()
        {
            return svnUI?.MergeConsoleText;
        }

        public async Task<string> SwitchAsync(
    string workingDir,
    string targetUrl,
    CancellationToken token = default)
        {
            string currentKey = SvnRunner.KeyPath;

            string sshArgs = "-o BatchMode=yes -o StrictHostKeyChecking=no";

            if (!string.IsNullOrEmpty(currentKey))
                sshArgs = $"-i \"{currentKey}\" {sshArgs}";

            string command =
$"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" " +
$"switch \"{targetUrl}\" \"{workingDir}\" " +
$"--accept theirs-full --non-interactive";

            return await SvnRunner.RunAsync(command, workingDir, true, token);
        }
    }
}