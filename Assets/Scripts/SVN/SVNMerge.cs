using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace SVN.Core
{
    public class SVNMerge : SVNBase
    {
        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private bool _branchesCacheValid = false;
        private string[] _cachedBranches = null;

        private string _lastMergeSource;
        private bool _lastMergeWasDryRun;
        private bool _hasRollbackPoint;
        private bool _isMerging;
        private int added = 0;
        private int updated = 0;
        private int deleted = 0;
        private int conflicted = 0;

        private string _lastMergeRevisionBefore;
        private string _lastMergeRevisionAfter;

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

                LogInfo("--------------------------------------");
                LogInfo($"Incoming (Trunk -> Branch): {missingCount}");
                LogInfo($"Outgoing (Branch -> Trunk): {localCount}");

                if (missingCount > 0 || localCount > 0)
                {
                    LogWarning("DIVERGENCE DETECTED: trunk and branch are out of sync.");
                }
                else
                {
                    LogSuccess("Fully synchronized with Trunk.");
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

            // =====================================================
            // 🔒 BLOCK DIRTY WORKING COPY
            // =====================================================

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

                string repoRoot = svnManager.GetRepoRoot()?.TrimEnd('/');

                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    LogErrorLocal("Repo Root not found.");
                    return;
                }

                // =====================================================
                // 🔒 SECURITY
                // =====================================================

                if (IsInvalidPath(sourceInput))
                {
                    LogErrorLocal("SECURITY: Invalid merge source.");
                    return;
                }

                // =====================================================
                // 🔥 BUILD SOURCE URL
                // =====================================================

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

                // =====================================================
                // 🔥 SELF MERGE BLOCK
                // =====================================================

                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Cannot merge branch into itself.");
                    return;
                }

                // =====================================================
                // 🔥 UUID SAFETY
                // =====================================================

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

                // =====================================================
                // 🔥 SNAPSHOT
                // =====================================================

                bool snapshotCreated = false;

                if (!isDryRun)
                {
                    snapshotCreated = await TryCaptureMergeSnapshot(sourceUrl);

                    if (!snapshotCreated)
                    {
                        LogInfo("[Merge] First merge detected.");
                    }
                }

                // =====================================================
                // 🔥 DETECT FIRST MERGE
                // =====================================================

                bool firstMerge = !snapshotCreated;

                // =====================================================
                // 🔥 BUILD MERGE COMMAND
                // =====================================================

                string args;

                if (sourceIsTrunk && !currentIsTrunk)
                {
                    // trunk → branch

                    if (firstMerge)
                    {
                        LogWarning("[Merge Strategy] FIRST MERGE from trunk detected.");
                        LogWarning("[Merge Strategy] Using full replay merge.");

                        args =
                            $"merge -r 0:HEAD \"{sourceUrl}\" " +
                            $"--ignore-ancestry " +
                            $"--non-interactive " +
                            $"--accept postpone";
                    }
                    else
                    {
                        args =
                            $"merge \"{sourceUrl}\" " +
                            $"--non-interactive " +
                            $"--accept postpone";
                    }
                }
                else if (!sourceIsTrunk && currentIsTrunk)
                {
                    // branch → trunk

                    args =
                        $"merge --reintegrate \"{sourceUrl}\" " +
                        $"--non-interactive " +
                        $"--accept postpone";
                }
                else
                {
                    // branch → branch

                    args =
                        $"merge \"{sourceUrl}\" " +
                        $"--ignore-ancestry " +
                        $"--non-interactive " +
                        $"--accept postpone";
                }

                // =====================================================
                // 🔥 DEBUG COMMAND
                // =====================================================

                LogInfo("====================================");
                LogInfo("[SVN MERGE COMMAND]");
                LogInfo(args);
                LogInfo("====================================");

                // =====================================================
                // 🔥 EXECUTE MERGE
                // =====================================================

                string output =
                    await SvnRunner.RunAsync(
                        args,
                        svnManager.WorkingDir,
                        isDryRun);

                // =====================================================
                // 🔥 OPTIONAL DEBUG OUTPUT
                // =====================================================

                // DEV ONLY
#if UNITY_EDITOR

                if (!string.IsNullOrWhiteSpace(output))
                {
                    LogInfo("====================================");
                    LogInfo("[RAW SVN OUTPUT]");

                    foreach (string line in output.Split(
                                 new[] { '\r', '\n' },
                                 StringSplitOptions.RemoveEmptyEntries))
                    {
                        // pomijamy spam plików
                        if (line.StartsWith("A ") ||
                            line.StartsWith("U ") ||
                            line.StartsWith("C ") ||
                            line.StartsWith("D "))
                        {
                            continue;
                        }

                        //LogInfo(line);
                    }

                    LogInfo("====================================");
                }

#endif

                // =====================================================
                // 🔥 RESULT
                // =====================================================

                ParseMergeOutput(output, isDryRun);

                // =====================================================
                // 🔥 REFRESH
                // =====================================================

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

        private void ParseMergeOutput(string output, bool isDryRun)
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

                // =====================================================
                // 🔥 MERGEINFO DETECTION
                // =====================================================
                if (line.Contains("Recording mergeinfo"))
                {
                    mergeInfoUpdated = true;
                    continue;
                }

                // =====================================================
                // 🔥 SKIPPED
                // =====================================================
                if (line.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                char state = line[0];

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

                    case 'C':
                        conflicts++;
                        break;
                }

                if (validStates.Contains(state))
                {
                    changed++;

                    // "." = tylko mergeinfo
                    if (!line.EndsWith("."))
                        realChanges++;
                }
            }

            // =====================================================
            // 🔥 DRY RUN
            // =====================================================
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

            LogInfo($"Added files      : {added}");
            LogInfo($"Updated files    : {updated}");
            LogInfo($"Deleted files    : {deleted}");

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
                    string line = raw.TrimStart();

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    char state = line[0];

                    // modified / added / deleted / conflicted
                    if (state == 'A' ||
                        state == 'M' ||
                        state == 'D' ||
                        state == 'C' ||
                        state == 'R' ||
                        state == '!')
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
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
                // =====================================================
                // 🔒 IN-FLIGHT GUARD (KLUCZOWE NA TWÓJ PROBLEM)
                // =====================================================
                if (_isFetchingBranches)
                {
                    LogInfo("[Branches] Fetch already in progress → skipping duplicate call.");
                    return _cachedBranches ?? Array.Empty<string>();
                }

                _isFetchingBranches = true;

                try
                {
                    // =====================================================
                    // 📦 CACHE FAST PATH
                    // =====================================================
                    if (!force && _branchesCacheValid && _cachedBranches != null)
                    {
                        LogInfo("[Cache] Using cached branches.");
                        return _cachedBranches;
                    }

                    // =====================================================
                    // 🌍 REPO ROOT (SAFE FALLBACK)
                    // =====================================================
                    string repoRoot = svnManager.GetRepoRoot()?.TrimEnd('/');

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

                    // =====================================================
                    // 🌿 BRANCHES FETCH
                    // =====================================================
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

                    // =====================================================
                    // 🧹 PARSING (YOUR LOGIC PRESERVED)
                    // =====================================================
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

                    // =====================================================
                    // 💾 CACHE WRITE
                    // =====================================================
                    _cachedBranches = branchList;
                    _branchesCacheValid = true;

                    LogSuccess($"Found {branchList.Length} branch(es).");

                    return branchList;
                }
                finally
                {
                    // zawsze zdejmujemy lock
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
                // =====================================================
                // 🔒 VALIDATION
                // =====================================================

                if (!_hasRollbackPoint ||
                    string.IsNullOrWhiteSpace(_lastMergeSource) ||
                    string.IsNullOrWhiteSpace(_lastMergeRevisionBefore))
                {
                    LogWarning("[Undo] No merge snapshot available (merge not tracked or overwritten).");
                    return;
                }

                if (!long.TryParse(_lastMergeRevisionBefore, out long baseRevision))
                {
                    LogErrorLocal("[Undo] Invalid base revision snapshot.");
                    return;
                }

                string wcStatus = await SvnRunner.RunAsync("status", svnManager.WorkingDir);
                if (!string.IsNullOrWhiteSpace(wcStatus))
                {
                    LogWarning("[Undo] Working copy not clean — risky revert.");
                }

                LogWarning("====================================");
                LogWarning("[UNDO MERGE]");
                LogWarning($"Source: {_lastMergeSource}");
                LogWarning($"Reverting to BASE r{baseRevision}");
                LogWarning("====================================");

                // =====================================================
                // 🔄 ROBUST REVERSE MERGE (SAFE SVN PATTERN)
                // =====================================================

                // klucz: cofamy zmiany względem BASE
                string args =
                    $"merge -r HEAD:{baseRevision} \"{_lastMergeSource}\" " +
                    $"--non-interactive --accept postpone";

                string output;

                try
                {
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                }
                catch (Exception ex)
                when (ex.Message.Contains(SvnAncestryErrorMsg))
                {
                    LogWarning("[Undo] Retry with --ignore-ancestry...");

                    string fallback =
                        $"merge -r HEAD:{baseRevision} \"{_lastMergeSource}\" " +
                        $"--ignore-ancestry --non-interactive --accept postpone";

                    output = await SvnRunner.RunAsync(fallback, svnManager.WorkingDir);
                }

                // =====================================================
                // 📊 RESULT
                // =====================================================

                ParseMergeOutput(output, false);
                await svnManager.RefreshStatus();
                await svnManager.GetModule<SVNResolve>().RefreshConflictUI();

                LogSuccess("[Undo Complete] Merge reverted safely.");

                // =====================================================
                // 🧹 RESET
                // =====================================================

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

        private (string minRev, string maxRev) ParseEligibleRevisions(string mergeinfoOutput)
        {
            if (string.IsNullOrWhiteSpace(mergeinfoOutput))
                return (null, null);

            var lines = mergeinfoOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Optymalizacja LINQ: eliminacja podwójnego parsowania (TryParse + Parse) przez Value Tuple
            var revNumbers = lines
                .Select(line => line.Trim().TrimStart('r'))
                .Select(line => (Success: long.TryParse(line, out long val), Value: val))
                .Where(t => t.Success)
                .Select(t => t.Value)
                .OrderBy(n => n)
                .ToList();

            if (revNumbers.Count == 0)
                return (null, null);

            // Zachowanie oryginalnej reguły indeksowania dolnej granicy zakresu rewizji dla komendy SVN
            const long SvnRevisionOffset = 1;
            long min = revNumbers[0] - SvnRevisionOffset;
            long max = revNumbers[revNumbers.Count - 1];

            return (min.ToString(), max.ToString());
        }

        private int CountRevisions(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return 0;

            return output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                         .Count(line => !string.IsNullOrWhiteSpace(line));
        }

        private string Normalize(string input)
        {
            return (input ?? "")
                .Trim()
                .Replace("\\", "/")
                .TrimEnd('/')
                .ToLowerInvariant();
        }

        private async Task<bool> TryCaptureMergeSnapshot(string sourceUrl)
        {
            try
            {
                string merged =
                    await SvnRunner.RunAsync(
                        $"mergeinfo \"{sourceUrl}\" . --show-revs merged",
                        svnManager.WorkingDir);

                var revisions = merged
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimStart('r'))
                    .Where(x => long.TryParse(x, out _))
                    .Select(long.Parse)
                    .OrderBy(x => x)
                    .ToList();

                // =====================================================
                // 🔥 FIRST MERGE CASE
                // =====================================================
                if (revisions.Count == 0)
                {
                    LogInfo("[Snapshot] First merge for this branch.");

                    string currentRevision =
                        await GetWorkingCopyRevision();

                    _lastMergeSource = sourceUrl;
                    _lastMergeRevisionBefore = currentRevision;
                    _lastMergeRevisionAfter = "HEAD";

                    _hasRollbackPoint = true;

                    LogInfo("====================================");
                    LogInfo("[FIRST MERGE SNAPSHOT CREATED]");
                    LogInfo($"Base Revision : r{currentRevision}");
                    LogInfo("====================================");

                    return false;
                }

                _lastMergeSource = sourceUrl;
                _lastMergeRevisionBefore = revisions.First().ToString();
                _lastMergeRevisionAfter = revisions.Last().ToString();
                _hasRollbackPoint = true;

                LogInfo("====================================");
                LogInfo("[MERGE SNAPSHOT CREATED]");
                LogInfo($"Source Revision Range : r{_lastMergeRevisionBefore} -> r{_lastMergeRevisionAfter}");
                LogInfo("====================================");

                return true;
            }
            catch (Exception ex)
            {
                LogWarning($"[Snapshot Error] {ex.Message}");

                _hasRollbackPoint = false;

                return false;
            }
        }

        private const string SvnConflictErrorCode = "E200004";
        private const string SvnAncestryErrorMsg = "ancestry";
        private const string TrunkPath = "trunk";
        private const string TrunkSuffix = "/trunk";



        private bool IsProtectedBranch(string source, string current)
        {
            string normalizedSource = Normalize(source);
            string normalizedCurrent = Normalize(current);

            bool sourceIsTrunk = normalizedSource.EndsWith(TrunkSuffix) || normalizedSource == TrunkPath;
            bool currentIsTrunk = normalizedCurrent.EndsWith(TrunkSuffix);

            if (!sourceIsTrunk && currentIsTrunk)
                return true;

            return false;
        }

        private bool IsInvalidPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return true;

            // Usuwamy "://" do sprawdzenia, żeby nie blokowało pełnych adresów URL repozytorium
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

            // 🔥 BEZ IGNORE-ANCESTRY (to psuło diagnostykę)

            string command =
$"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" " +
$"switch \"{targetUrl}\" \"{workingDir}\" " +
$"--accept theirs-full --non-interactive";

            return await SvnRunner.RunAsync(command, workingDir, true, token);
        }
    }
}