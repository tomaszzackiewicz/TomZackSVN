using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMerge : SVNBase
    {
        // Stałe dla PlayerPrefs
        private const string PrefMergeSource = "SVN_UndoMerge_Source";
        private const string PrefMergeRevBefore = "SVN_UndoMerge_RevBefore";
        private const string PrefMergeRevAfter = "SVN_UndoMerge_RevAfter";
        private const string PrefHasRollback = "SVN_UndoMerge_HasRollback";
        private const string PrefMergeTimestamp = "SVN_UndoMerge_Timestamp";

        // Pola dla mechanizmu undo / snapshot
        private string _lastMergeSource;
        private bool _hasRollbackPoint;
        private string _lastMergeRevisionBefore;
        private string _lastMergeRevisionAfter;
        private int _lastIncomingCount = -1;

        // Timery dla double‑click
        private float _lastRevertToHeadClickTime = -10f;
        private float _lastCancelMergeClickTime = -10f;

        private bool _branchesCacheValid = false;
        private string[] _cachedBranches = null;
        private bool _isFetchingBranches;
        private bool _isMerging;

        private const string SvnAncestryErrorMsg = "ancestry";

        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            LoadRollbackSnapshot();
        }

        // ============================================================
        // SNAPSHOT – POMOCNICZA KLASA DO SERIALIZACJI
        // ============================================================
        [Serializable]
        private class SnapshotData
        {
            public string Source;
            public string RevisionBefore;
            public string RevisionAfter;
            public string Timestamp;
        }

        // ============================================================
        // ŚCIEŻKA DO PLIKU SNAPSHOTU
        // ============================================================
        private string SnapshotFilePath =>
            Path.Combine(svnManager.WorkingDir, ".svn", "merge_snapshot.json");

        // ============================================================
        // ZAPIS / ODCZYT / USUWANIE SNAPSHOTU (PLIK)
        // ============================================================
        private void SaveSnapshotToFile()
        {
            try
            {
                var data = new SnapshotData
                {
                    Source = _lastMergeSource,
                    RevisionBefore = _lastMergeRevisionBefore,
                    RevisionAfter = _lastMergeRevisionAfter,
                    Timestamp = DateTime.Now.ToString("o")
                };

                File.WriteAllText(SnapshotFilePath, JsonUtility.ToJson(data, true));
                LogInfo($"[Snapshot] Saved to file: {SnapshotFilePath}");
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
                if (!File.Exists(SnapshotFilePath))
                    return false;

                string json = File.ReadAllText(SnapshotFilePath);
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
                if (File.Exists(SnapshotFilePath))
                    File.Delete(SnapshotFilePath);
            }
            catch { }
        }

        // ============================================================
        // UNIWERSALNY ZAPIS / ODCZYT / CZYSZCZENIE SNAPSHOTU
        // ============================================================
        private void SaveRollbackSnapshot()
        {
            if (!_hasRollbackPoint) return;

            // PlayerPrefs
            PlayerPrefs.SetString(PrefMergeSource, _lastMergeSource ?? "");
            PlayerPrefs.SetString(PrefMergeRevBefore, _lastMergeRevisionBefore ?? "");
            PlayerPrefs.SetString(PrefMergeRevAfter, _lastMergeRevisionAfter ?? "");
            PlayerPrefs.SetInt(PrefHasRollback, 1);
            PlayerPrefs.SetString(PrefMergeTimestamp, DateTime.Now.ToString("o"));
            PlayerPrefs.Save();

            // Plik
            SaveSnapshotToFile();

            LogInfo($"[Snapshot] Saved → {_lastMergeSource} | r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
        }

        private void LoadRollbackSnapshot()
        {
            // 1. PlayerPrefs
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

            // 2. Plik
            if (LoadSnapshotFromFile())
            {
                // Synchronizuj PlayerPrefs
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

        // ============================================================
        // MERGE I JEGO ODMIANY
        // ============================================================
        private enum MergeSnapshotState { Error, FirstMerge, ExistingMerge }

        private async Task<MergeSnapshotState> TryCaptureMergeSnapshot(string sourceUrl)
        {
            try
            {
                string eligible = await SvnRunner.RunAsync(
                    $"mergeinfo \"{sourceUrl}\" . --show-revs eligible",
                    svnManager.WorkingDir);

                if (string.IsNullOrWhiteSpace(eligible))
                {
                    LogInfo("[Snapshot] No merge history found – creating first‑merge snapshot.");
                    string currentRevision = await GetWorkingCopyRevision();

                    _lastMergeSource = sourceUrl;
                    _lastMergeRevisionBefore = currentRevision;
                    _lastMergeRevisionAfter = currentRevision;
                    _hasRollbackPoint = true;
                    SaveRollbackSnapshot();

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

                LogInfo("====================================");
                LogInfo("[MERGE SNAPSHOT CREATED]");
                LogInfo($"Source Revision Range : r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
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

        // ============================================================
        // EXECUTE MERGE
        // ============================================================
        public async Task ExecuteMerge(string sourceInput, bool isDryRun)
        {
            if (await HasPendingMergeChanges())
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
                string sourceUrl = cleanedInput.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? $"{repoRoot}/trunk"
                    : $"{repoRoot}/branches/{cleanedInput}";

                string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir);

                LogInfo($"Current URL: {currentUrl}");
                LogInfo($"Source URL : {sourceUrl}");

                bool sourceIsTrunk = Normalize(sourceUrl).EndsWith("/trunk");
                bool currentIsTrunk = Normalize(currentUrl).EndsWith("/trunk");

                if (sourceIsTrunk && !currentIsTrunk && _lastIncomingCount == 0)
                {
                    LogInfo("====================================");
                    LogSuccess("[Merge Blocked] Branch is already fully synchronized with Trunk.");
                    LogSuccess("No incoming revisions to pull. Operation aborted safely.");
                    LogInfo("====================================");
                    return;
                }

                if (Normalize(sourceUrl) == Normalize(currentUrl))
                {
                    LogErrorLocal("Cannot merge branch into itself.");
                    return;
                }

                string currentUuid = await SvnRunner.RunAsync("info --show-item repos-uuid", svnManager.WorkingDir);
                string sourceUuid = await SvnRunner.RunAsync($"info \"{sourceUrl}\" --show-item repos-uuid", svnManager.WorkingDir);
                if (currentUuid.Trim() != sourceUuid.Trim())
                {
                    LogErrorLocal("Repository UUID mismatch.");
                    return;
                }

                // 🔥 WYMUSZONY UPDATE PRZED MERGE – unikamy E195020
                LogInfo("[Merge] Bringing working copy to a uniform revision...");
                try
                {
                    await SvnRunner.RunAsync("update", svnManager.WorkingDir);
                    LogInfo("[Merge] Update completed.");
                }
                catch (Exception ex)
                {
                    LogWarning($"[Merge] Update failed (non‑fatal): {ex.Message}");
                }

                if (!isDryRun)
                {
                    var state = await TryCaptureMergeSnapshot(sourceUrl);
                    if (state == MergeSnapshotState.Error)
                        LogWarning("[Merge] Snapshot capture failed.");
                }

                string dryRunFlag = isDryRun ? "--dry-run " : "";
                string args = $"merge {dryRunFlag}\"{sourceUrl}\" --non-interactive --accept postpone";

                LogInfo("====================================");
                LogInfo("[SVN MERGE COMMAND]");
                LogInfo(args);
                LogInfo("====================================");

                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, !isDryRun);

                await ParseMergeOutput(output, isDryRun);

                if (!isDryRun)
                {
                    await svnManager.RefreshStatus();
                    await svnManager.GetModule<SVNResolve>().RefreshConflictUI();
                    LogSuccess("[Merge Complete]");
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

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

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
                    case 'A': added++; break;
                    case 'U':
                    case 'G': updated++; break;
                    case 'D': deleted++; break;
                }

                if (validStates.Contains(state))
                {
                    changed++;
                    bool isMergeInfoOnly =
                        line.Trim() == "." ||
                        line.EndsWith(" .") ||
                        (line.EndsWith(".") && line.Length <= 3);
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
                if (mergeInfoUpdated) LogInfo("SVN merge history would be updated.");
                if (skipped > 0) LogWarning($"Skipped items          : {skipped}");
                if (realChanges == 0 && conflicts == 0) LogSuccess("No incoming file changes detected.");
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

            if (mergeInfoUpdated) LogInfo("Merge history updated.");
            if (conflicts > 0) LogErrorLocal($"Conflicts detected : {conflicts}");
            if (skipped > 0) LogWarning($"Skipped items : {skipped}");
            if (realChanges == 0 && conflicts == 0) LogSuccess("Merge executed but no real file changes were applied.");

            LogSuccess("Review changes before commit.");
            LogSuccess("====================================");
        }

        // ============================================================
        // UNDO LAST MERGE (cofa zatwierdzony merge)
        // ============================================================
        public async Task UndoLastMerge(bool autoCommit = false)
        {
            if (!TryStart()) return;

            try
            {
                LogInfo("========== UNDO LAST MERGE ==========");

                if (await HasPendingMergeChanges())
                {
                    LogWarning("[Undo Blocked] Uncommitted changes detected.");
                    LogWarning("Commit or cancel current changes before undoing the last merge.");
                    LogInfo("====================================");
                    return;
                }

                if (!_hasRollbackPoint)
                {
                    LogInfo("[Undo] No snapshot in memory, trying PlayerPrefs...");
                    LoadRollbackSnapshot();
                }

                if (!_hasRollbackPoint
                    || string.IsNullOrWhiteSpace(_lastMergeSource)
                    || string.IsNullOrWhiteSpace(_lastMergeRevisionBefore)
                    || string.IsNullOrWhiteSpace(_lastMergeRevisionAfter))
                {
                    LogWarning("[Undo] No rollback point available. Perform a merge first.");
                    LogInfo("====================================");
                    return;
                }

                LogInfo($"[Undo] Source : {_lastMergeSource}");
                LogInfo($"[Undo] Range  : r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");

                // Ujednolicamy rewizję kopii roboczej
                LogInfo("[Undo] Bringing working copy to a uniform revision...");
                try
                {
                    await SvnRunner.RunAsync("update", svnManager.WorkingDir);
                }
                catch (Exception ex)
                {
                    LogWarning($"[Undo] Update failed (non‑fatal): {ex.Message}");
                }

                string range = $"{_lastMergeRevisionAfter}:{_lastMergeRevisionBefore}";
                string args = $"merge -r {range} \"{_lastMergeSource}\" --non-interactive --accept postpone";
                LogInfo($"[Undo] Executing: svn {args}");

                string output;
                try
                {
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                }
                catch (Exception ex) when (ex.Message.Contains("mixed-revision") || ex.Message.Contains("E195020"))
                {
                    LogWarning("[Undo] Mixed-revision detected – retrying after another update...");
                    await SvnRunner.RunAsync("update", svnManager.WorkingDir);
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                }
                catch (Exception ex) when (ex.Message.Contains(SvnAncestryErrorMsg))
                {
                    LogWarning("[Undo] Ancestry issue – retrying with --ignore-ancestry...");
                    args = $"merge -r {range} \"{_lastMergeSource}\" --ignore-ancestry --non-interactive --accept postpone";
                    output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);
                }

                if (autoCommit)
                {
                    string msg = $"Undo merge from {_lastMergeSource} (r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})";
                    LogInfo($"[Undo] Auto‑committing: {msg}");
                    await SvnRunner.RunAsync($"commit -m \"{msg}\"", svnManager.WorkingDir);
                    LogSuccess("[Undo] Changes committed automatically.");
                }

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus();
                await svnManager.GetModule<SVNResolve>().RefreshConflictUI();

                LogSuccess("[Undo Complete] Last merge reverted safely.");
                LogInfo($"Successfully reverted merge of {_lastMergeSource} (r{_lastMergeRevisionBefore}→r{_lastMergeRevisionAfter})");
                LogInfo("====================================");
            }
            catch (Exception ex)
            {
                LogErrorLocal("[Undo Error] " + ex.Message);
                LogInfo("====================================");
            }
            finally
            {
                End();
            }
        }

        // ============================================================
        // CANCEL LOCAL MERGE (cofa niezapisany merge)
        // ============================================================
        public async Task CancelLocalMerge()
        {
            if (!TryStart()) return;

            try
            {
                if (!_hasRollbackPoint
                    || string.IsNullOrWhiteSpace(_lastMergeSource)
                    || string.IsNullOrWhiteSpace(_lastMergeRevisionBefore)
                    || string.IsNullOrWhiteSpace(_lastMergeRevisionAfter))
                {
                    LogWarning("[Cancel Local Merge] No merge snapshot available. Perform a merge first.");
                    return;
                }

                LogInfo("====================================");
                LogInfo("[CANCEL LOCAL MERGE]");
                LogInfo($"Source: {_lastMergeSource}");
                LogInfo($"Revisions: r{_lastMergeRevisionBefore} → r{_lastMergeRevisionAfter}");
                LogInfo("====================================");

                string range = $"{_lastMergeRevisionAfter}:{_lastMergeRevisionBefore}";
                string args = $"merge -r {range} \"{_lastMergeSource}\" --non-interactive --accept postpone";

                string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir);

                if (string.IsNullOrWhiteSpace(output) || output.Contains("No changes"))
                {
                    LogInfo("[CancelLocalMerge] No changes to revert.");
                }
                else
                {
                    int reverted = Regex.Matches(output, @"^[A-Z]\s", RegexOptions.Multiline).Count;
                    LogSuccess($"[CancelLocalMerge] Successfully reverted {reverted} files.");
                }

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus();
                await svnManager.GetModule<SVNResolve>().RefreshConflictUI();

                LogSuccess("[Cancel Local Merge Complete] Merge changes have been reverted.");
                LogInfo("Files with status 'R' are locally scheduled for replacement.");
                LogInfo("To clear 'R', commit the undo (or use RevertToHead to discard everything).");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[CancelLocalMerge Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        // ============================================================
        // REVERT TO HEAD (odrzuca wszystkie lokalne zmiany)
        // ============================================================
        public async Task RevertToHead()
        {
            float timeSinceLastClick = Time.time - _lastRevertToHeadClickTime;

            if (timeSinceLastClick > 5f)
            {
                _lastRevertToHeadClickTime = Time.time;
                LogWarning("====================================");
                LogWarning("[Reset to HEAD] This will discard ALL local changes!");
                LogWarning("Press the button again within 5 seconds to confirm.");
                LogWarning("====================================");
                return;
            }

            _lastRevertToHeadClickTime = -10f;

            if (!TryStart()) return;

            try
            {
                LogWarning("[Reset to HEAD] Reverting all local changes...");

                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir);

                ClearRollbackSnapshot();
                await svnManager.RefreshStatus();
                await svnManager.GetModule<SVNResolve>().RefreshConflictUI();

                LogSuccess("[Reset Complete] Working copy is now at HEAD.");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Reset Error] {ex.Message}");
            }
            finally
            {
                End();
            }
        }

        // ============================================================
        // POMOCNICZE
        // ============================================================
        private string Normalize(string input) =>
            (input ?? "").Trim().Replace("\\", "/").TrimEnd('/').ToLowerInvariant();

        private bool IsInvalidPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return true;
            string sanitized = input.Replace("://", "");
            return sanitized.Contains("..") || sanitized.Contains("//") || sanitized.Contains("\\") || sanitized.Contains("\0");
        }

        private async Task<string> GetWorkingCopyRevision()
        {
            try { return (await SvnRunner.RunAsync("info --show-item revision", svnManager.WorkingDir))?.Trim(); }
            catch { return "unknown"; }
        }

        private async Task<bool> HasPendingMergeChanges()
        {
            try
            {
                string status = await SvnRunner.RunAsync("status", svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(status)) return false;
                return status.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd())
                    .Where(line => line.Length > 0)
                    .Any(line => "AMDCRG!".Contains(line[0]));
            }
            catch (Exception ex)
            {
                LogWarning($"[Merge Check Failed] {ex.Message}");
                return true;
            }
        }

        private async Task<(int added, int updated, int deleted)> GetRealDiffStats()
        {
            try
            {
                string output = await SvnRunner.RunAsync("diff --summarize", svnManager.WorkingDir);
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

        private int CountRevisions(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return 0;
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Count(x => x.StartsWith("r") && long.TryParse(x.TrimStart('r'), out _));
        }

        protected override TMP_Text GetConsole() => svnUI?.MergeConsoleText;

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
    }
}