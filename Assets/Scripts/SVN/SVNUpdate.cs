// ============================================================================
//  SVNUpdate.cs — pełna, samodzielna implementacja
//
//  Zachowanie jak w dojrzałych klientach SVN (TortoiseSVN / RapidSVN):
//   * update --force --accept postpone (bezpieczny default),
//   * post-update: build-artefakty i incoming-deletes rozwiązywane auto,
//     tekstowe/property/tree konflikty w źródłach RAPORTOWANE (do SVNResolve /
//     ręcznego merge) — NIGDY nie nadpisujemy decyzji użytkownika,
//   * lokalne usunięcie + zmiana w repo → domyślnie RAPORT (decyzja usera);
//     opcjonalnie auto-restore (opt-in),
//   * incoming delete + plik na dysku w źródłach → RAPORT (nie kasujemy
//     potencjalnych lokalnych zmian); w build-artefaktach → auto,
//   * resztki unversioned pod ścieżkami usuniętymi z repo → fizycznie usuwane,
//   * puste unversioned katalogi domywane po drodze,
//   * retry na E155004 / E7200xx po svn cleanup,
//   * błędy svn NIE są wycinane z live logu (raport nie kłamie),
//   * remote-check (status -u + log -v + list --xml -R) — anulowalny przez Update.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

namespace SVN.Core
{
    public class SVNUpdate : SVNBase, IDisposable
    {
        private static readonly Regex RevisionRegex = new Regex(
            @"^Revision:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

        // ===================================================================
        //  Stan / CTS
        // ===================================================================
        private CancellationTokenSource _updateCTS;
        private CancellationTokenSource _remoteCheckCTS;
        private Task _remoteCheckTask;
        private Task _runningTask;
        private Guid _sessionId = Guid.Empty;
        private int _disposed;

        // ===================================================================
        //  Konfiguracja — pasek postępu
        // ===================================================================

        /// <summary>Wagi bajtowe = 'svn list -R' na całym repo. Wolne przy dużym repo — opt-in.</summary>
        public bool fetchByteWeights = false;

        /// <summary>Estymacja x/y = pełne 'svn status -u' przed update. Podwaja fazę sieciową — opt-in.</summary>
        public bool progressEstimation = false;

        /// <summary>Cleanup WC przed każdym update.</summary>
        public bool preUpdateCleanup = true;

        // ===================================================================
        //  Konfiguracja — SYNC I KONFLIKTY
        // ===================================================================

        /// <summary>
        /// Po udanym update: domyj dysk do stanu serwera — usuń resztki,
        /// rozwiąż bezpieczne tree-conflicty, domknij auto-restore. Domyślnie ON.
        /// </summary>
        public bool syncAfterUpdate = true;

        /// <summary>
        /// Składniki ścieżki (np. "Intermediate", "Binaries"): konflikty w tych
        /// ścieżkach są automatycznie 'theirs-full'. Ścieżki spoza listy (Source/,
        /// Content/) — ZAWSZE raportowane do ręcznego merge.
        /// </summary>
        public string[] buildArtifactPaths = { "Intermediate", "Binaries", "Saved", "DerivedDataCache" };

        /// <summary>Po sukcesie: ścieżki z linii 'D' — jeśli nadal na dysku, usuń fizycznie.</summary>
        public bool forceRemoveLeftoversAfterDelete = true;

        /// <summary>
        /// Po sukcesie: dla ścieżek z buildArtifactPaths, których nadrzędny "plugin-root"
        /// zniknął z repo — usuń całe poddrzewa. Domyślnie ON, scope ograniczony.
        /// </summary>
        public bool cleanupBuildArtifactsUnderDeletedRoots = true;

        /// <summary>
        /// 'local delete, incoming edit': plik zniknął lokalnie (user go usunął),
        /// ale w repo jest zmieniony.
        ///   true  → auto-restore (jak „Restore" w Tortoise, ale automatycznie —
        ///           nadpisuje decyzję użytkownika o usunięciu pliku),
        ///   false (default) → tylko raport; użytkownik decyduje przez SVN Resolve.
        /// </summary>
        public bool autoRestoreLocalDeleteIncomingEdit = false;

        /// <summary>Liczba prób update przy błędach przejściowych (E155004 / E7200xx).</summary>
        public int maxUpdateAttempts = 2;

        /// <summary>Dodaje '--force' do 'svn update' (nadpisuje unversioned obstruction).</summary>
        public bool forceUpdate = true;

        // ===================================================================
        //  Cache wag bajtowych
        // ===================================================================
        private static Dictionary<string, long> _sizesCache = new(StringComparer.OrdinalIgnoreCase);
        private static string _sizesCacheUrl = "";
        private static DateTime _sizesCacheTime = DateTime.MinValue;
        private static readonly TimeSpan SizesCacheTtl = TimeSpan.FromMinutes(10);

        // ===================================================================
        //  Progress UI
        // ===================================================================
        private SvnUpdateProgressUI _progress;

        // ===================================================================
        //  Raport post-update sync
        // ===================================================================
        private sealed class SvnSyncReport
        {
            public List<string> AutoResolvedIncomingDeletes = new();
            public List<string> AutoRestored = new();
            public List<string> LeftoversRemoved = new();
            public List<string> TextConflicts = new();
            public List<string> PropConflicts = new();
            public List<string> TreeConflictsManual = new();
            public List<string> Notes = new();

            public bool IsEmpty =>
                AutoResolvedIncomingDeletes.Count == 0 && AutoRestored.Count == 0 &&
                LeftoversRemoved.Count == 0 && TextConflicts.Count == 0 &&
                PropConflicts.Count == 0 && TreeConflictsManual.Count == 0 &&
                Notes.Count == 0;
        }

        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // ===================================================================
        //  Entry points
        // ===================================================================

        public void Update()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (!ValidateWorkingCopy()) return;

            bool updateBusy = _runningTask != null && !_runningTask.IsCompleted;
            var remote = _remoteCheckTask;

            if (!updateBusy && remote != null && !remote.IsCompleted)
            {
                CancelRemoteCheck();
                svnManager.OperationInfo = NewOperationInfo(UpdatingMsg("Starting update (remote check cancelled)..."));
                svnManager.WasUpdateCanceled = false;
                _sessionId = Guid.NewGuid();
                _runningTask = RunUpdateAfterRemoteCheckAsync(remote, null);
                return;
            }

            if (IsProcessing || updateBusy)
            {
                SVNLogBridge.LogToOutput("<color=#8B0000>>Update already running...</color>");
                return;
            }

            svnManager.OperationInfo = NewOperationInfo(UpdatingMsg("Starting update..."));
            svnManager.WasUpdateCanceled = false;
            _sessionId = Guid.NewGuid();
            _runningTask = ExecuteUpdateCoreAsync(svnManager.WorkingDir, null, _sessionId);
        }

        public void UpdateToRevision(string revision)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (string.IsNullOrWhiteSpace(revision)) { Update(); return; }
            if (!ValidateWorkingCopy()) return;

            bool updateBusy = _runningTask != null && !_runningTask.IsCompleted;
            var remote = _remoteCheckTask;

            if (!updateBusy && remote != null && !remote.IsCompleted)
            {
                CancelRemoteCheck();
                svnManager.OperationInfo = NewOperationInfo(
                    UpdatingMsg($"Starting update to revision {revision} (remote check cancelled)..."));
                svnManager.WasUpdateCanceled = false;
                _sessionId = Guid.NewGuid();
                _runningTask = RunUpdateAfterRemoteCheckAsync(remote, revision);
                return;
            }

            if (IsProcessing || updateBusy)
            {
                SVNLogBridge.LogLine("<color=#8B0000>>Update already running...</color>", false);
                return;
            }

            if (!int.TryParse(revision, out _))
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Invalid revision number: {revision}");
                return;
            }

            svnManager.OperationInfo = NewOperationInfo(
                UpdatingMsg($"Starting update to revision {revision}..."));
            svnManager.WasUpdateCanceled = false;
            _sessionId = Guid.NewGuid();
            _runningTask = ExecuteUpdateCoreAsync(svnManager.WorkingDir, revision, _sessionId);
        }

        public void CancelUpdate()
        {
            var cts = Volatile.Read(ref _updateCTS);
            if (cts == null || !svnManager.IsUpdateRunning) return;

            SVNLogBridge.LogToOutput("<color=#8B0000>><b>[SVN]</b> Cancel requested...</color>");
            svnManager.WasUpdateCanceled = true;
            svnManager.LastUpdateSucceeded = false;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            _sessionId = Guid.NewGuid();

            svnManager.OperationInfo = NewOperationInfo(new SVNOperationInfo
            {
                State = SVNOperationState.Canceled,
                Message = "Cancel requested...",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            });
        }

        // ===================================================================
        //  Update-after-remote-check chaining (update ma priorytet)
        // ===================================================================

        private async Task RunUpdateAfterRemoteCheckAsync(Task remoteCheck, string revision)
        {
            try { await remoteCheck.ConfigureAwait(false); }
            catch { /* swallowed on purpose — update ma priorytet */ }

            await ExecuteUpdateCoreAsync(svnManager.WorkingDir, revision, _sessionId).ConfigureAwait(false);
        }

        private void CancelRemoteCheck()
        {
            var cts = Volatile.Read(ref _remoteCheckCTS);
            if (cts == null) return;

            SVNLogBridge.LogToOutput(
                "<color=#8B0000>><b>[SVN]</b> Cancelling remote change check - update has priority...</color>");
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // ===================================================================
        //  Core update (retry loop + sync + report)
        // ===================================================================

        private async Task ExecuteUpdateCoreAsync(string targetPath, string targetRevision, Guid session)
        {
            if (session != _sessionId) return;

            var statusModule = svnManager.GetModule<SVNStatus>();
            statusModule?.CancelCurrentRefresh();

            var localCts = new CancellationTokenSource();
            CancellationToken token = localCts.Token;

            _updateCTS = localCts;
            svnManager.IsUpdateRunning = true;
            svnManager.LastUpdateSucceeded = false;
            IsProcessing = true;

            SVNBar svnBar = null;
            var stopwatch = Stopwatch.StartNew();
            var oldSnapshot = svnManager.CurrentSnapshot;
            string oldRevision = oldSnapshot?.Revision ?? "Unknown";
            bool svnCommandCompleted = false;

            var svnErrors = new List<string>();
            var seenErrors = new HashSet<string>(StringComparer.Ordinal);
            var deletedPaths = new List<string>();

            int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;
            int processed = 0, totalUpdates = 0;

            try
            {
                if (session != _sessionId) throw new OperationCanceledException(token);

                int waitCount = 0;
                while (statusModule != null && statusModule.IsProcessing && waitCount < 50)
                {
                    await Task.Delay(20, token);
                    waitCount++;
                }

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    svnUI?.SvnTreeView?.ClearView();
                    if (svnUI?.TreeDisplay != null && svnUI.TreeDisplay.text.Contains("Scanning"))
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                });

                if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
                {
                    SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                    return;
                }

                bool isRevisionTarget = !string.IsNullOrEmpty(targetRevision);
                string commandLabel = isRevisionTarget ? $"update to revision {targetRevision}" : "update";

                // Pre-update rewizja WC (marker interrupted ma priorytet)
                string markerReason = svnManager.GetUpdateInterruptedReason(svnManager.WorkingDir);
                string markerPreRev = svnManager.GetUpdateInterruptedPreRev(svnManager.WorkingDir);

                if (!string.IsNullOrEmpty(markerReason) && !string.IsNullOrEmpty(markerPreRev) && markerPreRev != "Unknown")
                {
                    oldRevision = markerPreRev;
                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Interrupted marker detected — using pre-update revision " +
                        $"r{markerPreRev} from marker.</color>");
                }
                else
                {
                    try
                    {
                        string infoBefore = await SvnRunner.GetInfoAsync(targetPath, token);
                        token.ThrowIfCancellationRequested();
                        string probedRev = ParseRevisionFromInfo(infoBefore);
                        if (!string.IsNullOrEmpty(probedRev) && probedRev != "Unknown")
                            oldRevision = probedRev;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogToOutput(
                            $"<color=yellow>[SVN] Could not determine current revision: {ex.Message}</color>");
                    }
                }

                if (svnManager.CurrentSnapshot != null && !string.IsNullOrEmpty(oldRevision) && oldRevision != "Unknown")
                    svnManager.CurrentSnapshot.Revision = oldRevision;

                svnManager.MarkUpdateRunning();

                svnBar = svnManager.GetModule<SVNBar>();
                string projectName = svnManager.CurrentProject?.projectName ?? Path.GetFileName(targetPath);
                svnBar?.BeginUpdate(projectName);

                svnManager.OperationInfo = NewOperationInfo(UpdatingMsg($"Running SVN {commandLabel}..."));

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                SVNLogBridge.LogToOutput("<b>[SVN]</b> Waiting for free SVN command queue...");
                await SvnRunner.WaitForSemaphoreFreeAsync(token);
                token.ThrowIfCancellationRequested();
                if (session != _sessionId) throw new OperationCanceledException(token);
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Queue free - proceeding with update.");

                if (preUpdateCleanup)
                {
                    SVNLogBridge.LogToOutput("<b>[SVN]</b> Pre-update cleanup...");
                    await SVNClean.CleanupAsync(targetPath, token);
                    SVNLogBridge.LogToOutput("<b>[SVN]</b> Cleanup completed.");
                }

                // Estymacja x/y
                var outOfDatePaths = new List<string>();
                if (progressEstimation)
                {
                    try
                    {
                        string statusOutput = await SvnRunner.RunAsync("status -u", targetPath, token: token);
                        token.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(statusOutput))
                        {
                            using var reader = new StringReader(statusOutput);
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Length > 8 && line[8] == '*')
                                {
                                    totalUpdates++;
                                    string rest = line.Substring(9).TrimStart();
                                    Match revMatch = RemoteRevPrefixRegex.Match(rest);
                                    if (revMatch.Success) rest = rest.Substring(revMatch.Length).TrimStart();
                                    string cleanPath = SvnRunner.SanitizeSvnPath(rest.TrimEnd());
                                    if (cleanPath.Length > 0) outOfDatePaths.Add(cleanPath);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogToOutput(
                            $"<color=yellow>[SVN] Progress estimation unavailable: {ex.Message}</color>");
                    }
                }

                // Wagi bajtowe
                Dictionary<string, long> incomingSizes = null;
                long totalBytes = 0;
                if (fetchByteWeights && outOfDatePaths.Count > 0)
                {
                    try
                    {
                        string listUrl = null;
                        try { listUrl = await SvnRunner.GetRepoUrlAsync(targetPath); }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        if (string.IsNullOrWhiteSpace(listUrl)) listUrl = svnManager.RepositoryUrl;

                        if (!string.IsNullOrWhiteSpace(listUrl))
                        {
                            SVNLogBridge.LogToOutput("<b>[SVN]</b> Fetching file sizes for byte-weighted progress...");
                            incomingSizes = await FetchIncomingSizesAsync(targetPath, listUrl, token);
                            token.ThrowIfCancellationRequested();
                            foreach (var p in outOfDatePaths)
                                if (incomingSizes.TryGetValue(p, out long s)) totalBytes += s;

                            if (totalBytes == 0 && incomingSizes.Count > 0 &&
                                (long)outOfDatePaths.Count * incomingSizes.Count <= 2_000_000)
                            {
                                var remapped = RemapSizesBySuffix(incomingSizes, outOfDatePaths);
                                if (remapped != null)
                                {
                                    incomingSizes = remapped;
                                    foreach (var p in outOfDatePaths)
                                        if (remapped.TryGetValue(p, out long s)) totalBytes += s;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogToOutput(
                            $"<color=yellow>[SVN] Size weights unavailable: {ex.Message}</color>");
                        incomingSizes = null;
                        totalBytes = 0;
                    }
                }

                var cmdBuilder = new StringBuilder("update");
                if (forceUpdate) cmdBuilder.Append(" --force");
                cmdBuilder.Append(" --accept postpone --non-interactive");
                if (isRevisionTarget) cmdBuilder.Append($" -r {targetRevision}");
                string svnCommand = cmdBuilder.ToString();

                // === Pętla update (z retry) ===
                string result = null;
                int attempts = Math.Max(1, maxUpdateAttempts);

                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    if (session != _sessionId) throw new OperationCanceledException(token);

                    uCount = gCount = aCount = dCount = cCount = rCount = 0;
                    processed = 0;
                    svnErrors.Clear();
                    seenErrors.Clear();
                    deletedPaths.Clear();

                    _progress?.Clear();
                    _progress = new SvnUpdateProgressUI(svnUI?.OutputText);
                    _progress.SetTotal(totalUpdates, totalBytes, incomingSizes);

                    if (attempt > 1)
                        SVNLogBridge.LogToOutput(
                            $"<color=yellow><b>[SVN]</b> Update attempt {attempt}/{attempts}...</color>");

                    SVNLogBridge.LogToOutput($"<color=blue><b>[SVN]</b> Running {commandLabel}...</color>");

                    result = await SvnRunner.RunLiveAsync(
                        svnCommand,
                        targetPath,
                        (line) => OnSvnLiveLine(line, token, session,
                            ref uCount, ref gCount, ref aCount, ref dCount, ref cCount, ref rCount,
                            ref processed, totalUpdates, svnErrors, seenErrors, deletedPaths),
                        token);

                    token.ThrowIfCancellationRequested();
                    if (session != _sessionId) throw new OperationCanceledException(token);
                    if (result == "Canceled") break;

                    if (svnErrors.Count > 0)
                    {
                        bool retryable = svnErrors.Any(IsRetryableSvnError);
                        if (retryable && attempt < attempts)
                        {
                            SVNLogBridge.LogToOutput(
                                "<color=yellow><b>[SVN]</b> Update blocked by locked/busy files — cleanup + retry. " +
                                "Close the editor/UE if open.</color>");
                            try { await SVNClean.CleanupAsync(targetPath, token); }
                            catch (OperationCanceledException) { throw; }
                            catch { }
                            await Task.Delay(500, token);
                            continue;
                        }

                        svnCommandCompleted = false;
                        string first = svnErrors[0];
                        string extra = svnErrors.Count > 1 ? $" (+{svnErrors.Count - 1} more)" : "";
                        throw new Exception($"SVN update failed: {first}{extra}");
                    }

                    svnCommandCompleted = true;
                    break;
                }

                _progress?.Finish();

                if (session != _sessionId || result == "Canceled")
                    throw new OperationCanceledException(token);

                string newRevision = targetRevision ?? "Unknown";
                try
                {
                    string infoAfter = await SvnRunner.GetInfoAsync(targetPath, token);
                    token.ThrowIfCancellationRequested();
                    newRevision = ParseRevisionFromInfo(infoAfter);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Could not determine resulting revision: {ex.Message}</color>");
                    if (newRevision == "Unknown") newRevision = targetRevision ?? "Unknown";
                }

                stopwatch.Stop();
                svnManager.LastUpdateSucceeded = true;
                svnManager.OperationInfo = NewOperationInfo(new SVNOperationInfo
                {
                    State = SVNOperationState.Success,
                    Message = $"{commandLabel} completed successfully",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                });
                SVNStatus.ClearLockCache();
                svnManager.DiskChangesDetected = true;
                svnManager.ClearUpdateInterrupted();

                // Post-update sync
                SvnSyncReport sync = null;
                if (syncAfterUpdate)
                {
                    try
                    {
                        sync = await SyncWithServerAsync(deletedPaths, targetPath, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception sex)
                    {
                        SVNLogBridge.LogToOutput(
                            $"<color=yellow>[SVN] Post-update sync failed: {sex.Message}</color>");
                    }
                }

                try
                {
                    LogUpdateReport(isRevisionTarget, targetRevision, oldRevision, newRevision,
                        stopwatch.Elapsed.TotalSeconds, uCount, aCount, dCount, gCount, rCount, cCount, sync);

                    if (!svnManager.WasUpdateCanceled && statusModule != null)
                        await statusModule.RefreshModifiedInternal();

                    if (!svnManager.WasUpdateCanceled && svnBar != null)
                    {
                        var snap = svnManager.CurrentSnapshot ?? new SVNProjectInfoSnapshot();
                        snap.Revision = newRevision;

                        string newAuthor = await GetAuthorForRevision(svnManager.WorkingDir, newRevision, token);
                        if (!string.IsNullOrEmpty(newAuthor)) snap.Author = newAuthor;

                        snap.IsValid = true;
                        svnManager.CurrentSnapshot = snap;
                        await svnBar.EndUpdate(snap);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception postEx)
                {
                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Update completed, but post-processing failed: {postEx.Message}</color>");
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                svnManager.LastUpdateSucceeded = svnCommandCompleted;

                if (!svnCommandCompleted) svnManager.MarkUpdateInterrupted("Update anulowany przez użytkownika.");
                else svnManager.ClearUpdateInterrupted();

                svnManager.OperationInfo = NewOperationInfo(new SVNOperationInfo
                {
                    State = svnCommandCompleted ? SVNOperationState.Success : SVNOperationState.Canceled,
                    Message = svnCommandCompleted
                        ? "Update completed; post-processing cancelled"
                        : "Update canceled by user",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                });

                if (svnCommandCompleted)
                {
                    SVNStatus.ClearLockCache();
                    svnManager.DiskChangesDetected = true;
                    var note = new StringBuilder();
                    note.AppendLine("\n<color=#FFAA00><b>=========================================</b></color>");
                    note.AppendLine("<color=#FFAA00><b>       UPDATE COMPLETED (post-step cancelled)</b></color>");
                    note.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                    note.AppendLine("  SVN update finished successfully.");
                    note.AppendLine("  Post-processing (refresh/report) was cancelled — press Refresh.");
                    note.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                    SVNLogBridge.LogLine(note.ToString(), false);
                }
                else
                {
                    svnManager.CurrentSnapshot = oldSnapshot;
                    svnBar?.EndUpdateFailed(oldSnapshot);
                    var cancelReport = new StringBuilder();
                    cancelReport.AppendLine("\n<color=#FFAA00><b>=========================================</b></color>");
                    cancelReport.AppendLine("<color=#FFAA00><b>          UPDATE CANCELED               </b></color>");
                    cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                    cancelReport.AppendLine($"  Process aborted after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                    cancelReport.AppendLine("  Working copy may need another Update to finish.");
                    cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                    SVNLogBridge.LogLine(cancelReport.ToString(), false);
                }
                _progress?.Clear();
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                if (ex.Message.Contains("locked") || ex.Message.Contains("E155004") || ex.Message.Contains("E7200"))
                {
                    try
                    {
                        if (localCts != null && !localCts.IsCancellationRequested)
                            await SVNClean.CleanupAsync(targetPath, localCts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                }

                svnManager.LastUpdateSucceeded = svnCommandCompleted;
                if (!svnCommandCompleted) svnManager.MarkUpdateInterrupted($"Błąd update'u: {ex.Message}");
                else svnManager.ClearUpdateInterrupted();

                svnManager.OperationInfo = NewOperationInfo(new SVNOperationInfo
                {
                    State = SVNOperationState.Failed,
                    Message = ex.Message,
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                });

                var failureReport = new StringBuilder();
                failureReport.AppendLine("\n<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine("<color=#B22222><b>            UPDATE FAILED                </b></color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine($"  Execution crashed after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                failureReport.AppendLine($"  Error message: <color=#E6E6E6>{ex.Message}</color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                SVNLogBridge.LogLine(failureReport.ToString(), false);

                _progress?.Clear();
            }
            finally
            {
                try
                {
                    if (svnBar != null && svnBar.CurrentState == BarState.Updating)
                    {
                        if (svnCommandCompleted) await svnBar.EndUpdate(oldSnapshot).ConfigureAwait(false);
                        else svnBar.EndUpdateFailed(oldSnapshot);
                    }
                }
                catch { }

                svnManager.IsUpdateRunning = false;
                IsProcessing = false;

                if (ReferenceEquals(Volatile.Read(ref _updateCTS), localCts)) _updateCTS = null;
                _ = Task.Delay(1000).ContinueWith(_ => { try { localCts.Dispose(); } catch { } });
                _runningTask = null;
            }
        }

        // ===================================================================
        //  Live line handler
        // ===================================================================

        private void OnSvnLiveLine(
            string line, CancellationToken token, Guid session,
            ref int uCount, ref int gCount, ref int aCount, ref int dCount, ref int cCount, ref int rCount,
            ref int processed, int totalUpdates,
            List<string> svnErrors, HashSet<string> seenErrors, List<string> deletedPaths)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && trimmed.All(c => c == '@' || c == '*' || c == ' ')) return;
            if (trimmed.StartsWith("*****") || trimmed.StartsWith("@@@@@")) return;

            // Błędy — zbieramy i wyświetlamy na czerwono (NIE wycinamy)
            if (trimmed.StartsWith("[SVN ERROR]", StringComparison.Ordinal))
            {
                string err = trimmed.Replace("[SVN ERROR]", "").Trim();
                if (err.Length > 0 && seenErrors.Add(err))
                {
                    svnErrors.Add(err);
                    SVNLogBridge.LogToOutput($"<color=#B22222><b>[SVN ERROR]</b> {err}</color>");
                }
                return;
            }
            if (trimmed.StartsWith("svn: ", StringComparison.Ordinal))
            {
                string err = trimmed.Substring(5).Trim();
                if (err.Length > 0 && seenErrors.Add(err))
                {
                    svnErrors.Add(err);
                    SVNLogBridge.LogToOutput($"<color=#B22222><b>[SVN ERROR]</b> {err}</color>");
                }
                return;
            }
            if (trimmed.StartsWith("client_loop", StringComparison.Ordinal))
            {
                if (seenErrors.Add(trimmed))
                {
                    svnErrors.Add(trimmed);
                    SVNLogBridge.LogToOutput($"<color=#B22222><b>[SVN ERROR]</b> {trimmed}</color>");
                }
                return;
            }

            if (token.IsCancellationRequested) return;
            if (session != _sessionId) return;

            if (trimmed.StartsWith("Updating", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("At revision", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Checked out revision", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Transmitting", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Fetching", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("External", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Updated to revision", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Restored", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Summary of conflicts", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Text conflicts:", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Property conflicts:", StringComparison.Ordinal)) return;
            if (trimmed.StartsWith("Tree conflicts:", StringComparison.Ordinal)) return;

            if (trimmed.StartsWith("Skipped", StringComparison.Ordinal))
            {
                SVNLogBridge.LogToOutput($"<color=yellow><b>[SVN]</b> {trimmed}</color>");
                return;
            }

            processed++;

            string displayLine;
            string itemPath = null;
            char contentStatus = trimmed.Length > 0 ? trimmed[0] : ' ';
            char propStatus = trimmed.Length > 1 ? trimmed[1] : ' ';
            char activeStatus = contentStatus != ' ' ? contentStatus : propStatus;

            if (trimmed.Length > 2 && "UAGDCR".Contains(activeStatus) && trimmed[1] == ' ')
            {
                char status = activeStatus;
                string path = SvnRunner.SanitizeSvnPath(trimmed.Substring(2).TrimStart());
                itemPath = path;

                switch (status)
                {
                    case 'U': uCount++; break;
                    case 'A': aCount++; break;
                    case 'D': dCount++; deletedPaths.Add(path); break;
                    case 'C': cCount++; break;
                    case 'G': gCount++; break;
                    case 'R': rCount++; break;
                }

                string prefix = status switch
                {
                    'U' => "= Updated",
                    'A' => "+ Added",
                    'D' => "- Deleted",
                    'C' => "x Conflict",
                    'G' => "~ Merged",
                    'R' => "~ Replaced",
                    _ => $"  {status}"
                };
                displayLine = $"{prefix}: {path}";
            }
            else displayLine = trimmed;

            string progressStr;
            if (totalUpdates > 0)
            {
                int shown = Math.Min(processed, totalUpdates);
                if (processed > totalUpdates)
                    progressStr = $" ({totalUpdates}/{totalUpdates}, +{processed - totalUpdates} extra)";
                else progressStr = $" ({shown}/{totalUpdates})";
            }
            else progressStr = "";

            bool isConflictLine = trimmed.Length > 2 && trimmed[1] == ' ' && activeStatus == 'C';
            _progress?.OnItem(itemPath, displayLine, progressStr, isConflictLine);
        }

        // ===================================================================
        //  Post-update sync
        // ===================================================================

        private async Task<SvnSyncReport> SyncWithServerAsync(
            List<string> deletedPaths, string workingDir, CancellationToken token)
        {
            var report = new SvnSyncReport();
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir)) return report;

            string wcUrl = null;
            try { wcUrl = await SvnRunner.GetRepoUrlAsync(workingDir); }
            catch (OperationCanceledException) { throw; }
            catch { }

            // 1. Skan konfliktów
            var (treeConflicts, textConflicts, propConflicts) = await ScanConflictsAsync(workingDir, token);

            // 2. Tree-conflicty — respektujemy decyzje użytkownika
            foreach (var rel in treeConflicts)
            {
                token.ThrowIfCancellationRequested();
                await ResolveTreeConflictAsync(rel, workingDir, wcUrl, token, report);
            }

            // 3. Text/prop-conflicty — build-artefakty auto, reszta raport
            foreach (var rel in textConflicts)
            {
                token.ThrowIfCancellationRequested();
                if (IsBuildArtifactPath(rel))
                {
                    if (await TryResolveTheirsFullAsync(rel, workingDir, token))
                        report.AutoResolvedIncomingDeletes.Add(rel + " (text conflict, build artifact)");
                    else report.TextConflicts.Add(rel);
                }
                else report.TextConflicts.Add(rel);
            }

            foreach (var rel in propConflicts)
            {
                token.ThrowIfCancellationRequested();
                if (IsBuildArtifactPath(rel))
                {
                    if (await TryResolveTheirsFullAsync(rel, workingDir, token))
                        report.AutoResolvedIncomingDeletes.Add(rel + " (prop conflict, build artifact)");
                    else report.PropConflicts.Add(rel);
                }
                else report.PropConflicts.Add(rel);
            }

            // 4. Force-delete leftovers z linii 'D'
            if (forceRemoveLeftoversAfterDelete && deletedPaths != null && deletedPaths.Count > 0)
            {
                var distinct = deletedPaths
                    .Select(p => (p ?? "").Replace('\\', '/').Trim())
                    .Where(p => p.Length > 0 && !p.StartsWith(".."))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(p => p.Length)
                    .ToList();

                foreach (var rel in distinct)
                {
                    token.ThrowIfCancellationRequested();
                    string fsPath = Path.Combine(workingDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fsPath) && !Directory.Exists(fsPath)) continue;

                    if (await ForceDeleteFilesystemEntryAsync(fsPath, token))
                    {
                        report.LeftoversRemoved.Add(rel);
                        await TryRemoveEmptyUnversionedDirsAsync(
                            workingDir, Path.GetDirectoryName(fsPath) ?? workingDir, token);
                    }
                    else
                    {
                        report.TreeConflictsManual.Add(
                            $"{rel} (nie dało się usunąć — plik zajęty, zamknij edytor)");
                    }
                }
            }

            // 5. Cleanup build-artefaktów pod usuniętymi root'ami (plugin case)
            if (cleanupBuildArtifactsUnderDeletedRoots && deletedPaths != null && deletedPaths.Count > 0)
                await CleanupBuildArtifactsUnderDeletedRootsAsync(workingDir, deletedPaths, wcUrl, token, report);

            // 6. Log podsumowania
            if (!report.IsEmpty)
            {
                if (report.AutoResolvedIncomingDeletes.Count > 0)
                    SVNLogBridge.LogToOutput(
                        $"<b>[SVN]</b> Sync: auto-resolved {report.AutoResolvedIncomingDeletes.Count} " +
                        "(incoming delete / build artifact).");
                if (report.AutoRestored.Count > 0)
                    SVNLogBridge.LogToOutput(
                        $"<b>[SVN]</b> Sync: restored {report.AutoRestored.Count} local delete(s).");
                if (report.LeftoversRemoved.Count > 0)
                    SVNLogBridge.LogToOutput(
                        $"<b>[SVN]</b> Sync: removed {report.LeftoversRemoved.Count} leftover path(s) from disk.");
                if (report.TextConflicts.Count > 0)
                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Sync: {report.TextConflicts.Count} text conflict(s) need manual merge.</color>");
                if (report.PropConflicts.Count > 0)
                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Sync: {report.PropConflicts.Count} property conflict(s) need manual resolve.</color>");
                if (report.TreeConflictsManual.Count > 0)
                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Sync: {report.TreeConflictsManual.Count} item(s) need manual attention.</color>");
            }

            return report;
        }

        // ===================================================================
        //  Konflikt — skan
        // ===================================================================

        private async Task<(List<string> tree, List<string> text, List<string> prop)> ScanConflictsAsync(
            string workingDir, CancellationToken token)
        {
            var tree = new List<string>();
            var text = new List<string>();
            var prop = new List<string>();

            string statusOut;
            try
            {
                statusOut = await SvnRunner.RunAsync("status --non-interactive", workingDir, token: token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch { return (tree, text, prop); }

            if (string.IsNullOrWhiteSpace(statusOut)) return (tree, text, prop);

            using var reader = new StringReader(statusOut);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 8) continue;
                string rel = SvnRunner.SanitizeSvnPath(line.Substring(7).Trim());
                if (rel.Length == 0 || rel.StartsWith("..")) continue;

                if (line[6] == 'C') { if (!tree.Contains(rel)) tree.Add(rel); }
                if (line[0] == 'C') { if (!text.Contains(rel)) text.Add(rel); }
                if (line[1] == 'C') { if (!prop.Contains(rel)) prop.Add(rel); }
            }

            return (tree, text, prop);
        }

        // ===================================================================
        //  Konflikt — rozwiązywanie (RESPEKTUJE DECYZJE UŻYTKOWNIKA)
        // ===================================================================

        private async Task ResolveTreeConflictAsync(
            string rel, string workingDir, string wcUrl, CancellationToken token, SvnSyncReport report)
        {
            bool? repoHas = await RepoPathExistsAsync(wcUrl, rel, workingDir, token);
            string fsPath = Path.Combine(workingDir, rel.Replace('/', Path.DirectorySeparatorChar));
            bool onDisk = File.Exists(fsPath) || Directory.Exists(fsPath);
            bool isBuildArtifact = IsBuildArtifactPath(rel);

            if (repoHas == null)
            {
                report.TreeConflictsManual.Add($"{rel} (network error — cannot verify repo)");
                return;
            }

            // ============================================================
            //  Serwer MA plik
            // ============================================================
            if (repoHas == true)
            {
                if (onDisk)
                {
                    // Plik istnieje po obu stronach
                    if (isBuildArtifact)
                    {
                        if (await TryResolveTheirsFullAsync(rel, workingDir, token))
                            report.AutoResolvedIncomingDeletes.Add(rel + " (tree conflict, build artifact → theirs)");
                        else
                            report.TreeConflictsManual.Add($"{rel} (tree conflict, build artifact, resolve failed)");
                        return;
                    }
                    // Source: user ma coś swojego — nie ruszamy
                    report.TreeConflictsManual.Add(
                        $"{rel} (tree conflict — file exists on both sides, manual merge)");
                    return;
                }

                // Local delete, incoming edit
                if (!autoRestoreLocalDeleteIncomingEdit)
                {
                    report.TreeConflictsManual.Add(
                        $"{rel} (local delete, incoming edit — manual decision; " +
                        "use SVN Resolve to restore or keep delete)");
                    return;
                }

                if (await TryResolveTheirsFullAsync(rel, workingDir, token))
                    report.AutoRestored.Add(rel);
                else
                    report.TreeConflictsManual.Add($"{rel} (restore failed)");
                return;
            }

            // ============================================================
            //  Serwer NIE MA pliku (incoming delete)
            // ============================================================
            if (onDisk)
            {
                // Plik istnieje lokalnie, ale repo go nie ma.
                // Może mieć lokalne modyfikacje użytkownika → NIE ruszamy (poza build-artefaktami).
                if (isBuildArtifact)
                {
                    if (await TryResolveTheirsFullAsync(rel, workingDir, token))
                    {
                        if (File.Exists(fsPath) || Directory.Exists(fsPath))
                            await ForceDeleteFilesystemEntryAsync(fsPath, token);
                        await TryRemoveEmptyUnversionedDirsAsync(
                            workingDir, Path.GetDirectoryName(fsPath) ?? workingDir, token);
                        report.AutoResolvedIncomingDeletes.Add(rel + " (incoming delete, build artifact)");
                    }
                    else
                    {
                        report.TreeConflictsManual.Add(
                            $"{rel} (incoming delete, build artifact, resolve failed)");
                    }
                    return;
                }

                // Source: plik na dysku + usunięty z repo = możliwe lokalne zmiany.
                // Decyzja NALEŻY do użytkownika — nigdy nie kasujemy automatycznie.
                report.TreeConflictsManual.Add(
                    $"{rel} (incoming delete, file exists locally — manual decision; " +
                    "use SVN Resolve: theirs-full = accept delete, working = keep your version)");
                return;
            }

            // onDisk == false → plik już nie ma lokalnie, repo też nie ma → domknij konflikt
            if (await TryResolveTheirsFullAsync(rel, workingDir, token))
                report.AutoResolvedIncomingDeletes.Add(rel);
            else
                report.TreeConflictsManual.Add($"{rel} (incoming delete, resolve failed)");
        }

        private async Task<bool> TryResolveTheirsFullAsync(string rel, string workingDir, CancellationToken token)
        {
            try
            {
                await SvnRunner.RunAsync(
                    $"resolve --accept theirs-full --non-interactive \"{rel}\"",
                    workingDir, token: token);
                token.ThrowIfCancellationRequested();
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                try
                {
                    await SvnRunner.RunAsync(
                        $"resolve --accept theirs-full --recursive --non-interactive \"{rel}\"",
                        workingDir, token: token);
                    token.ThrowIfCancellationRequested();
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Resolve theirs-full '{rel}' failed: {ex.Message}</color>");
                    return false;
                }
            }
        }

        // ===================================================================
        //  Cleanup build-artefaktów pod usuniętymi plugin-root'ami
        // ===================================================================

        private async Task CleanupBuildArtifactsUnderDeletedRootsAsync(
            string workingDir, List<string> deletedPaths, string wcUrl,
            CancellationToken token, SvnSyncReport report)
        {
            if (buildArtifactPaths == null || buildArtifactPaths.Length == 0) return;

            var rootsToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var del in deletedPaths)
            {
                var parts = del.Replace('\\', '/').Trim('/').Split('/');
                if (parts.Length >= 2) rootsToCheck.Add(parts[0] + "/" + parts[1]);
            }

            foreach (var root in rootsToCheck)
            {
                token.ThrowIfCancellationRequested();

                var rootAbs = Path.Combine(workingDir, root.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(rootAbs)) continue;

                bool? repoHas;
                try { repoHas = await RepoPathExistsAsync(wcUrl, root, workingDir, token); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                if (repoHas != false) continue;

                var removed = new List<string>();
                foreach (var pattern in buildArtifactPaths)
                {
                    foreach (var sub in EnumerateMatchingDirs(rootAbs, pattern))
                    {
                        token.ThrowIfCancellationRequested();
                        if (await ForceDeleteFilesystemEntryAsync(sub, token))
                            removed.Add(Path.GetRelativePath(workingDir, sub).Replace('\\', '/'));
                    }
                }

                foreach (var r in removed) report.LeftoversRemoved.Add(r);
                await TryRemoveEmptyUnversionedDirsAsync(workingDir, rootAbs, token);
            }
        }

        private static IEnumerable<string> EnumerateMatchingDirs(string rootAbs, string pattern)
        {
            string direct = Path.Combine(rootAbs, pattern);
            if (Directory.Exists(direct)) yield return direct;

            List<string> nested;
            try
            {
                nested = Directory.EnumerateDirectories(rootAbs, "*", SearchOption.AllDirectories).ToList();
            }
            catch
            {
                nested = new List<string>();
            }

            foreach (var dir in nested)
            {
                string name = null;
                try { name = Path.GetFileName(dir); }
                catch { }

                if (name != null && string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))
                    yield return dir;
            }
        }

        // ===================================================================
        //  Helpers — repo query
        // ===================================================================

        private async Task<bool?> RepoPathExistsAsync(
            string wcUrl, string relPath, string workingDir, CancellationToken token)
        {
            string rel = (relPath ?? "").Replace('\\', '/').Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(wcUrl) || rel.Length == 0) return null;
            if (rel == ".." || rel.StartsWith("../") || rel.Contains("../")) return null;

            string url = wcUrl.TrimEnd('/') + "/" + rel;

            string output;
            try
            {
                output = await SvnRunner.RunAsync(
                    $"list --xml --depth empty --non-interactive \"{url}\"",
                    workingDir, token: token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // E160013 / E170000 / not found → ścieżka nie istnieje w repo
                if (ex.Message.IndexOf("E160013", StringComparison.Ordinal) >= 0 ||
                    ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ex.Message.IndexOf("non-existent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ex.Message.IndexOf("path not found", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;

                SVNLogBridge.LogToOutput(
                    $"<color=yellow>[SVN] repo-check '{rel}' inconclusive: {ex.Message}</color>");
                return null;
            }

            // sukces 'svn list' → ścieżka istnieje
            return !string.IsNullOrWhiteSpace(output);
        }

        // ===================================================================
        //  Helpers — build artifacts / disk cleanup
        // ===================================================================

        private bool IsBuildArtifactPath(string relPath)
        {
            if (string.IsNullOrEmpty(relPath) || buildArtifactPaths == null) return false;

            var parts = relPath.Replace('\\', '/').Split('/');
            foreach (var part in parts)
            {
                foreach (var artifact in buildArtifactPaths)
                {
                    if (string.Equals(part, artifact, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private async Task<bool> ForceDeleteFilesystemEntryAsync(string fsPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(fsPath)) return false;

            try
            {
                if (Directory.Exists(fsPath))
                {
                    try { ClearReadOnlyRecursive(fsPath); } catch { }

                    for (int i = 0; i < 3; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            Directory.Delete(fsPath, recursive: true);
                            return true;
                        }
                        catch (IOException)
                        {
                            await Task.Delay(200, token);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            try { ClearReadOnlyRecursive(fsPath); } catch { }
                            await Task.Delay(200, token);
                        }
                    }

                    try { Directory.Delete(fsPath, recursive: true); return true; }
                    catch { return false; }
                }

                if (File.Exists(fsPath))
                {
                    try
                    {
                        var attrs = File.GetAttributes(fsPath);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(fsPath, attrs & ~FileAttributes.ReadOnly);
                    }
                    catch { }

                    for (int i = 0; i < 3; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        try { File.Delete(fsPath); return true; }
                        catch (IOException) { await Task.Delay(200, token); }
                        catch (UnauthorizedAccessException) { await Task.Delay(200, token); }
                    }
                }

                return !File.Exists(fsPath) && !Directory.Exists(fsPath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput(
                    $"<color=yellow>[SVN] Delete failed '{fsPath}': {ex.Message}</color>");
                return false;
            }
        }

        private static void ClearReadOnlyRecursive(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                catch { }
            }
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(dir);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(dir, attrs & ~FileAttributes.ReadOnly);
                }
                catch { }
            }
            try
            {
                var attrs = File.GetAttributes(root);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(root, attrs & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        private async Task TryRemoveEmptyUnversionedDirsAsync(
            string workingDir, string startDir, CancellationToken token)
        {
            if (string.IsNullOrEmpty(workingDir) || string.IsNullOrEmpty(startDir)) return;

            string wcRoot = Path.GetFullPath(workingDir).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = Path.GetFullPath(startDir);

            if (!current.StartsWith(wcRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(current, wcRoot, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                while (!string.IsNullOrEmpty(current) &&
                       !string.Equals(current, wcRoot, StringComparison.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();

                    if (!Directory.Exists(current)) break;
                    if (Directory.Exists(Path.Combine(current, ".svn"))) break;

                    bool anyFiles = Directory.EnumerateFileSystemEntries(current).Any();
                    if (anyFiles) break;

                    try { Directory.Delete(current, recursive: false); }
                    catch { break; }

                    current = Path.GetDirectoryName(current);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        // ===================================================================
        //  Retryable errors
        // ===================================================================

        private static bool IsRetryableSvnError(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            return err.IndexOf("E155004", StringComparison.Ordinal) >= 0
                || err.IndexOf("E7200", StringComparison.Ordinal) >= 0
                || err.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("cleanup", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ===================================================================
        //  Final report
        // ===================================================================

        private void LogUpdateReport(
            bool isRevisionTarget, string targetRevision,
            string oldRevision, string newRevision,
            double durationSeconds,
            int uCount, int aCount, int dCount, int gCount, int rCount, int cCount,
            SvnSyncReport sync)
        {
            var report = new StringBuilder();
            report.AppendLine("\n<color=blue><b>=========================================</b></color>");
            report.AppendLine(isRevisionTarget
                ? $"<color=blue><b>     UPDATE TO REVISION {targetRevision} REPORT    </b></color>"
                : "<color=blue><b>          SVN UPDATE REPORT             </b></color>");
            report.AppendLine("<color=blue><b>=========================================</b></color>");
            report.AppendLine(oldRevision == newRevision || oldRevision == "Unknown"
                ? $"  Revision:   <b>{newRevision}</b> (No incoming changes)"
                : $"  Revision:   <b>{oldRevision}</b> -> <b>{newRevision}</b>");
            report.AppendLine($"  Duration:   <b>{durationSeconds:F2}s</b>\n");

            bool hasChanges = uCount > 0 || aCount > 0 || dCount > 0 || cCount > 0 || gCount > 0 || rCount > 0;
            if (!hasChanges)
            {
                report.AppendLine(isRevisionTarget
                    ? "  <color=green>Working copy was already at this revision.</color>"
                    : "  <color=green>Working copy was already fully up-to-date.</color>");
            }
            else
            {
                report.AppendLine("  <b>[File Modifications]</b>");
                if (uCount > 0) report.AppendLine($"    Updated:   <b>{uCount}</b>");
                if (aCount > 0) report.AppendLine($"    Added:     <b>{aCount}</b>");
                if (dCount > 0) report.AppendLine($"    Deleted:   <b><color=#B22222>{dCount}</color></b>");
                if (gCount > 0) report.AppendLine($"    Merged:    <b>{gCount}</b>");
                if (rCount > 0) report.AppendLine($"    Replaced:  <b>{rCount}</b>");

                if (cCount > 0)
                {
                    report.AppendLine("\n  <color=#FFAA00><b>CRITICAL WARNING: CONFLICTS DETECTED</b></color>");
                    report.AppendLine($"    Conflicts: <b><color=#FFAA00>{cCount}</color></b>");
                    report.AppendLine("    Please resolve conflicts in working copy before compiling.");
                }
            }

            if (sync != null && !sync.IsEmpty)
            {
                report.AppendLine("\n  <b>[Post-update sync]</b>");
                if (sync.AutoResolvedIncomingDeletes.Count > 0)
                    report.AppendLine($"    Auto-resolved:  <b>{sync.AutoResolvedIncomingDeletes.Count}</b>");
                if (sync.AutoRestored.Count > 0)
                    report.AppendLine($"    Auto-restored:  <b>{sync.AutoRestored.Count}</b>");
                if (sync.LeftoversRemoved.Count > 0)
                    report.AppendLine($"    Leftovers removed: <b>{sync.LeftoversRemoved.Count}</b>");
                if (sync.TextConflicts.Count > 0)
                    report.AppendLine($"    <color=#FFAA00>Text conflicts (manual): <b>{sync.TextConflicts.Count}</b></color>");
                if (sync.PropConflicts.Count > 0)
                    report.AppendLine($"    <color=#FFAA00>Prop conflicts (manual): <b>{sync.PropConflicts.Count}</b></color>");
                if (sync.TreeConflictsManual.Count > 0)
                    report.AppendLine($"    <color=#FFAA00>Tree conflicts (manual): <b>{sync.TreeConflictsManual.Count}</b></color>");
            }

            report.AppendLine("<color=yellow><b>=========================================</b></color>");
            SVNLogBridge.LogLine(report.ToString(), false);
        }

        // ===================================================================
        //  Utilities — revision / author
        // ===================================================================

        private async Task<string> GetAuthorForRevision(string targetPath, string revision, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(revision) || revision == "Unknown")
                return string.Empty;

            try
            {
                token.ThrowIfCancellationRequested();
                string logOutput = await SvnRunner.RunAsync($"log -r {revision} -l 1", targetPath, token: token);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(logOutput)) return string.Empty;

                using var reader = new StringReader(logOutput);
                reader.ReadLine();
                string revisionLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(revisionLine)) return string.Empty;

                string[] parts = revisionLine.Split('|');
                if (parts.Length >= 2) return parts[1].Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput(
                    $"<color=yellow>[SVN] Could not determine revision author: {ex.Message}</color>");
            }
            return string.Empty;
        }

        public string ParseRevisionFromInfo(string infoOutput)
        {
            if (string.IsNullOrWhiteSpace(infoOutput)) return "Unknown";
            var match = RevisionRegex.Match(infoOutput);
            return match.Success ? match.Groups[1].Value : "Unknown";
        }

        private SVNOperationInfo NewOperationInfo(SVNOperationInfo info) => info;

        private SVNOperationInfo UpdatingMsg(string message) => new SVNOperationInfo
        {
            State = SVNOperationState.Updating,
            Message = message,
            Duration = 0,
            Repo = svnManager.RepositoryUrl
        };

        // ===================================================================
        //  Working copy validation
        // ===================================================================

        private bool ValidateWorkingCopy()
        {
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir) ||
                !Directory.Exists(svnManager.WorkingDir))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return false;
            }

            if (!SVNAssetLocator.IsWorkingCopy(svnManager.WorkingDir))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Not a valid SVN working copy (missing .svn).");
                return false;
            }

            return true;
        }

        // ===================================================================
        //  Progress — wagi bajtowe
        // ===================================================================

        private async Task<Dictionary<string, long>> FetchIncomingSizesAsync(
            string workingDir, string repoUrl, CancellationToken token)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(repoUrl)) return sizes;

            string url = repoUrl.Trim().TrimEnd('/');

            if (_sizesCache.Count > 0 &&
                string.Equals(url, _sizesCacheUrl, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - _sizesCacheTime < SizesCacheTtl)
            {
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Byte weights from cache.");
                return new Dictionary<string, long>(_sizesCache, StringComparer.OrdinalIgnoreCase);
            }

            string xml = await SvnRunner.RunAsync($"list --xml -R \"{url}\"", workingDir, token: token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(xml)) return sizes;

            try
            {
                int start = xml.IndexOf('<');
                int end = xml.LastIndexOf('>');
                if (start < 0 || end <= start) return sizes;

                var doc = new XmlDocument();
                doc.LoadXml(xml.Substring(start, end - start + 1));

                XmlNodeList lists = doc.SelectNodes("//list");
                if (lists == null) return sizes;

                foreach (XmlNode listNode in lists)
                {
                    string listPath = (listNode.Attributes?["path"]?.Value ?? "")
                        .Trim().Replace('\\', '/').TrimEnd('/');

                    int idx = listPath.IndexOf(url, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) listPath = listPath.Substring(idx + url.Length);
                    listPath = listPath.Trim('/');

                    foreach (XmlNode entry in listNode.SelectNodes("entry"))
                    {
                        if (!string.Equals(entry.Attributes?["kind"]?.Value, "file",
                                StringComparison.OrdinalIgnoreCase)) continue;

                        string name = entry.SelectSingleNode("name")?.InnerText;
                        XmlNode sizeNode = entry.SelectSingleNode("size");
                        if (string.IsNullOrEmpty(name) || sizeNode == null) continue;
                        if (!long.TryParse(sizeNode.InnerText, out long size)) continue;

                        string rel = string.IsNullOrEmpty(listPath) ? name : listPath + "/" + name;
                        rel = rel.Replace('\\', '/').TrimStart('/');
                        if (rel.Length > 0) sizes[rel] = size;
                    }
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Size weights parse failed: {ex.Message}</color>");
                return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }

            _sizesCache = sizes;
            _sizesCacheUrl = url;
            _sizesCacheTime = DateTime.UtcNow;
            return sizes;
        }

        private static Dictionary<string, long> RemapSizesBySuffix(
            Dictionary<string, long> sizes, List<string> referencePaths)
        {
            if (sizes == null || sizes.Count == 0 || referencePaths == null || referencePaths.Count == 0)
                return null;

            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in referencePaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string bestKey = null;
                long bestSize = 0;

                foreach (var kv in sizes)
                {
                    if (string.Equals(kv.Key, p, StringComparison.OrdinalIgnoreCase))
                    { bestKey = kv.Key; bestSize = kv.Value; break; }

                    if (kv.Key.Length > p.Length + 1 &&
                        kv.Key.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase) &&
                        (bestKey == null || kv.Key.Length < bestKey.Length))
                    { bestKey = kv.Key; bestSize = kv.Value; }
                }

                if (bestKey != null) result[p] = bestSize;
            }
            return result.Count > 0 ? result : null;
        }

        // ===================================================================
        //  Remote modifications check
        // ===================================================================

        public async void CheckRemoteModificationsButton() => await ShowRemoteUpdatesInline();

        private static readonly Regex StatusAgainstRevRegex =
            new Regex(@"Status against revision:\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex RemoteRevPrefixRegex = new Regex(@"^(\d+)\s+");
        private static readonly Regex LogChangedPathRegex = new Regex(@"^\s*([MARD])\s+(\S.*?)\s*$");
        private static readonly Regex SvnDateRegex = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2})");
        private static readonly Regex UrlInfoRegex =
            new Regex(@"^URL:\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex RepoRootInfoRegex =
            new Regex(@"^Repository Root:\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

        private sealed class RemoteItem
        {
            public string Path;
            public long RemoteRevision;
        }

        private sealed class CommitPath
        {
            public char Action;
            public string RepoPath;
            public string RelativePath;
        }

        private sealed class CommitItem
        {
            public RemoteItem Item;
            public char Action;
        }

        private sealed class CommitInfo
        {
            public long Revision;
            public string Author = "";
            public string Date = "";
            public string Message = "";
            public List<CommitPath> Paths = new List<CommitPath>();
            public List<CommitItem> Matched = new List<CommitItem>();
            public long Bytes;
        }

        private sealed class RemoteChangeReport
        {
            public string Root = "";
            public string Url = "";
            public string RepoPrefix;
            public long LocalRevision;
            public long HeadRevision;
            public List<RemoteItem> Items = new List<RemoteItem>();
            public List<string> Conflicts = new List<string>();
            public List<CommitInfo> Commits = new List<CommitInfo>();
            public List<RemoteItem> Unmatched = new List<RemoteItem>();
            public Dictionary<string, long> Sizes;
            public string LogError = "";
            public string SizeError = "";

            public long TotalBytes()
            {
                if (Sizes == null) return 0;
                long total = 0;
                foreach (var item in Items)
                    if (Sizes.TryGetValue(item.Path, out long s)) total += s;
                return total;
            }

            public List<CommitInfo> DisplayCommits()
                => Commits.Where(c => c.Matched.Count > 0).OrderByDescending(c => c.Revision).ToList();
        }

        public async Task ShowRemoteUpdatesInline()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (_remoteCheckTask != null && !_remoteCheckTask.IsCompleted) return;

            if (IsProcessing || (_runningTask != null && !_runningTask.IsCompleted))
            {
                SVNLogBridge.LogToOutput("<color=#8B0000>>Another SVN operation is running...</color>");
                return;
            }

            string root = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return;
            }

            var cts = new CancellationTokenSource();
            _remoteCheckCTS = cts;

            Task core = RemoteCheckCoreAsync(root, cts);
            _remoteCheckTask = core;
            await core.ConfigureAwait(false);
        }

        private async Task RemoteCheckCoreAsync(string root, CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;
            IsProcessing = true;

            try
            {
                SVNLogBridge.LogLine("<i>Checking remote changes...</i>");
                string output = await SvnRunner.RunAsync("status -u", root, token: token);
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(output))
                {
                    SVNLogBridge.LogLine("<color=green>No remote changes found.</color>");
                    return;
                }

                var report = ParseStatusOutput(output, root);

                if (report.Conflicts.Count > 0)
                {
                    SVNLogBridge.LogLine(
                        $"<color=#FF4444><b>WARNING: {report.Conflicts.Count} local conflict(s) detected!</b></color>");
                    SVNLogBridge.LogLine("<color=#FF4444>Resolve before updating or merge will fail.</color>");
                    foreach (var c in report.Conflicts)
                        SVNLogBridge.LogLine($"<color=#FF4444>  • {c}</color>");
                    SVNLogBridge.LogLine("");
                }

                if (report.Items.Count == 0)
                {
                    SVNLogBridge.LogLine("<color=green>Your working copy is up to date.</color>");
                    return;
                }

                await EnrichWithCommitInfoAsync(report, token);

                string tempFile = null;
                try { tempFile = await WriteRemoteChangesToTempFileAsync(report, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN] Could not write report file: {ex.Message}");
                }

                SVNLogBridge.LogLine(
                    $"<b>Summary:</b> Found <color=#FFAA00>{report.Items.Count}</color> items to update.");
                LogRemoteChangeSummary(report);

                if (tempFile != null)
                {
                    OpenInEditor(tempFile);
                    SVNLogBridge.LogLine("<color=yellow>Full list opened in external text editor.</color>");
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=yellow>Remote update check canceled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Remote check error: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                if (ReferenceEquals(Volatile.Read(ref _remoteCheckCTS), cts))
                    _remoteCheckCTS = null;
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }
        }

        private RemoteChangeReport ParseStatusOutput(string output, string root)
        {
            var report = new RemoteChangeReport { Root = root };

            Match head = StatusAgainstRevRegex.Match(output);
            if (head.Success && long.TryParse(head.Groups[1].Value, out long headRev))
                report.HeadRevision = headRev;

            using var reader = new StringReader(output);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;

                if (line.Length > 8 && line[8] == '*')
                {
                    string rest = line.Substring(9).TrimStart();
                    long remoteRev = 0;
                    Match revMatch = RemoteRevPrefixRegex.Match(rest);
                    if (revMatch.Success && long.TryParse(revMatch.Groups[1].Value, out long rr))
                    {
                        remoteRev = rr;
                        rest = rest.Substring(revMatch.Length).TrimStart();
                    }

                    string cleanPath = SvnRunner.SanitizeSvnPath(rest.TrimEnd());
                    if (cleanPath.Length > 0)
                        report.Items.Add(new RemoteItem { Path = cleanPath, RemoteRevision = remoteRev });
                }

                if (line.Length > 1 && (line[0] == 'C' || line[1] == 'C'))
                {
                    string rawPath = line.Length > 8 ? line.Substring(8).Trim() : line.Trim();
                    string cleanPath = SvnRunner.SanitizeSvnPath(SvnRunner.CleanSvnPath(rawPath));
                    if (cleanPath.Length > 0 && !report.Conflicts.Contains(cleanPath))
                        report.Conflicts.Add(cleanPath);
                }
            }
            return report;
        }

        private async Task EnrichWithCommitInfoAsync(RemoteChangeReport report, CancellationToken token)
        {
            try
            {
                string info = await SvnRunner.GetInfoAsync(report.Root, token);
                token.ThrowIfCancellationRequested();

                report.LocalRevision = ParseRevisionLong(ParseRevisionFromInfo(info));

                var urlMatch = UrlInfoRegex.Match(info);
                var rootMatch = RepoRootInfoRegex.Match(info);
                if (urlMatch.Success)
                {
                    report.Url = urlMatch.Groups[1].Value.Trim().TrimEnd('/');
                    if (rootMatch.Success)
                    {
                        string repoRoot = rootMatch.Groups[1].Value.Trim().TrimEnd('/');
                        report.RepoPrefix = report.Url.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                            ? report.Url.Substring(repoRoot.Length)
                            : null;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { report.LogError = $"svn info failed: {ex.Message}"; }

            long rangeStart = ComputeLogRangeStart(report);
            if (rangeStart > 0 && report.HeadRevision > 0 && rangeStart <= report.HeadRevision)
            {
                try
                {
                    string logOutput = await SvnRunner.RunAsync(
                        $"log -v -r {rangeStart}:{report.HeadRevision}", report.Root, token: token);
                    report.Commits = ParseSvnLog(logOutput, report.RepoPrefix);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { report.LogError = ex.Message; }
            }
            else if (report.HeadRevision == 0 && report.LogError.Length == 0)
                report.LogError = "could not determine repository HEAD revision";

            if (report.Commits.Count == 0 && report.LogError.Length == 0)
                report.LogError = rangeStart > 0 ? "no log entries returned" : "could not determine revision range";

            if (!string.IsNullOrEmpty(report.Url))
            {
                try { report.Sizes = await FetchPathSizesAsync(report, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { report.SizeError = ex.Message; }
            }

            MatchItemsToCommits(report);
            ComputeCommitSizes(report);
        }

        private static long ComputeLogRangeStart(RemoteChangeReport report)
        {
            long minRemote = 0;
            foreach (var it in report.Items)
                if (it.RemoteRevision > 0 && (minRemote == 0 || it.RemoteRevision < minRemote))
                    minRemote = it.RemoteRevision;

            if (report.LocalRevision > 0 && minRemote > 0)
                return Math.Min(report.LocalRevision + 1, minRemote);
            if (report.LocalRevision > 0) return report.LocalRevision + 1;
            return minRemote;
        }

        private static void MatchItemsToCommits(RemoteChangeReport report)
        {
            if (report.Commits.Count == 0)
            {
                report.Unmatched = new List<RemoteItem>(report.Items);
                return;
            }

            var byPath = new Dictionary<string, RemoteItem>(report.Items.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var item in report.Items)
                if (!byPath.ContainsKey(item.Path)) byPath[item.Path] = item;

            var matched = new HashSet<RemoteItem>();
            foreach (var commit in report.Commits)
            {
                foreach (var cp in commit.Paths)
                {
                    RemoteItem item = null;
                    if (cp.RelativePath != null) byPath.TryGetValue(cp.RelativePath, out item);

                    if (item == null && report.RepoPrefix == null)
                    {
                        foreach (var kv in byPath)
                        {
                            if (cp.RepoPath.Length > kv.Key.Length &&
                                cp.RepoPath.EndsWith("/" + kv.Key, StringComparison.OrdinalIgnoreCase))
                            { item = kv.Value; break; }
                        }
                    }

                    if (item != null)
                    {
                        matched.Add(item);
                        bool already = false;
                        foreach (var ci in commit.Matched)
                            if (ReferenceEquals(ci.Item, item)) { already = true; break; }
                        if (!already) commit.Matched.Add(new CommitItem { Item = item, Action = cp.Action });
                    }
                }
            }

            report.Unmatched = report.Items.Where(i => !matched.Contains(i)).ToList();
        }

        private static void ComputeCommitSizes(RemoteChangeReport report)
        {
            if (report.Sizes == null) return;
            foreach (var commit in report.Commits)
            {
                long bytes = 0;
                foreach (var ci in commit.Matched)
                    if (report.Sizes.TryGetValue(ci.Item.Path, out long s)) bytes += s;
                commit.Bytes = bytes;
            }
        }

        private static List<CommitInfo> ParseSvnLog(string logOutput, string repoPrefix)
        {
            var commits = new List<CommitInfo>();
            if (string.IsNullOrWhiteSpace(logOutput)) return commits;

            CommitInfo current = null;
            var message = new List<string>();
            bool inChangedPaths = false;

            using var reader = new StringReader(logOutput);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length >= 20 && IsDashLine(line))
                {
                    if (current != null)
                    {
                        current.Message = string.Join(" ", message).Trim();
                        commits.Add(current);
                        current = null;
                    }
                    inChangedPaths = false;
                    message = new List<string>();
                    continue;
                }

                if (current == null)
                {
                    var header = ParseLogHeader(line);
                    if (header != null)
                    { current = header; message = new List<string>(); inChangedPaths = false; }
                    continue;
                }

                if (line.Trim() == "Changed paths:") { inChangedPaths = true; continue; }

                if (inChangedPaths)
                {
                    Match m = LogChangedPathRegex.Match(line);
                    if (m.Success)
                    {
                        string repoPath = m.Groups[2].Value.Replace('\\', '/').Trim();
                        int fromIdx = repoPath.IndexOf(" (from ", StringComparison.Ordinal);
                        if (fromIdx > 0) repoPath = repoPath.Substring(0, fromIdx);

                        current.Paths.Add(new CommitPath
                        {
                            Action = char.ToUpperInvariant(m.Groups[1].Value[0]),
                            RepoPath = repoPath,
                            RelativePath = MapRepoPathToRelative(repoPath, repoPrefix)
                        });
                        continue;
                    }
                    inChangedPaths = false;
                }

                message.Add(line);
            }

            if (current != null)
            {
                current.Message = string.Join(" ", message).Trim();
                commits.Add(current);
            }
            return commits;
        }

        private static CommitInfo ParseLogHeader(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 2 || trimmed[0] != 'r' || !char.IsDigit(trimmed[1])) return null;

            string[] parts = trimmed.Split('|');
            if (parts.Length < 4) return null;
            if (!long.TryParse(parts[0].Trim().TrimStart('r'), out long rev)) return null;

            return new CommitInfo
            {
                Revision = rev,
                Author = parts[1].Trim(),
                Date = FormatSvnDate(parts[2].Trim())
            };
        }

        private static string FormatSvnDate(string svnDate)
        {
            Match m = SvnDateRegex.Match(svnDate);
            return m.Success ? m.Groups[1].Value : svnDate;
        }

        private static string MapRepoPathToRelative(string repoPath, string repoPrefix)
        {
            if (string.IsNullOrEmpty(repoPath)) return null;
            if (string.IsNullOrEmpty(repoPrefix)) return repoPath.TrimStart('/');

            string prefix = repoPrefix.TrimEnd('/');
            if (repoPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return repoPath.Substring(prefix.Length + 1);
            if (string.Equals(repoPath, prefix, StringComparison.OrdinalIgnoreCase)) return "";
            return null;
        }

        private async Task<Dictionary<string, long>> FetchPathSizesAsync(
            RemoteChangeReport report, CancellationToken token)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(report.Url)) return sizes;

            string url = report.Url.TrimEnd('/');
            string xml = await SvnRunner.RunAsync($"list --xml -R \"{url}\"", report.Root, token: token);
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(xml)) throw new Exception("empty 'svn list' output");

            int start = xml.IndexOf('<');
            int end = xml.LastIndexOf('>');
            if (start < 0 || end <= start) throw new Exception("'svn list' returned no XML data");

            var doc = new XmlDocument();
            doc.LoadXml(xml.Substring(start, end - start + 1));

            XmlNodeList lists = doc.SelectNodes("//list");
            if (lists == null) return sizes;

            foreach (XmlNode listNode in lists)
            {
                string listRel = StripToRelative(listNode.Attributes?["path"]?.Value, report);

                foreach (XmlNode entry in listNode.SelectNodes("entry"))
                {
                    if (!string.Equals(entry.Attributes?["kind"]?.Value, "file",
                            StringComparison.OrdinalIgnoreCase)) continue;

                    string name = entry.SelectSingleNode("name")?.InnerText;
                    XmlNode sizeNode = entry.SelectSingleNode("size");
                    if (string.IsNullOrEmpty(name) || sizeNode == null) continue;
                    if (!long.TryParse(sizeNode.InnerText, out long size)) continue;

                    string rel = string.IsNullOrEmpty(listRel) ? name : listRel + "/" + name;
                    rel = rel.Replace('\\', '/').TrimStart('/');
                    if (rel.Length > 0) sizes[rel] = size;
                }
            }
            return sizes;
        }

        private static string StripToRelative(string listPath, RemoteChangeReport report)
        {
            string p = (listPath ?? "").Trim().Replace('\\', '/').TrimEnd('/');
            if (p.Length == 0) return "";

            if (!string.IsNullOrEmpty(report.Url))
            {
                string url = report.Url.TrimEnd('/');
                int idx = p.IndexOf(url, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) p = p.Substring(idx + url.Length);
            }

            if (report.RepoPrefix != null && p.Length > 0)
            {
                string prefix = report.RepoPrefix.TrimEnd('/');
                string probe = p.StartsWith("/", StringComparison.Ordinal) ? p : "/" + p;

                if (probe.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                    p = probe.Substring(prefix.Length + 1);
                else if (string.Equals(probe, prefix, StringComparison.OrdinalIgnoreCase))
                    p = "";
            }

            return p.TrimStart('/');
        }

        private void LogRemoteChangeSummary(RemoteChangeReport report)
        {
            var display = report.DisplayCommits();

            if (display.Count > 0)
            {
                string range = (report.LocalRevision > 0 && report.HeadRevision > 0)
                    ? $" (r{report.LocalRevision} -> r{report.HeadRevision})"
                    : "";
                SVNLogBridge.LogLine($"<b>Incoming commits ({display.Count}){range}, newest first:</b>");

                bool showBytes = report.Sizes != null;
                foreach (var c in display)
                {
                    string bytes = showBytes ? $" | ~{FormatSize(c.Bytes)}" : "";
                    string msg = c.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    string msgPart = msg.Length > 0 ? $" - \"{msg}\"" : "";
                    SVNLogBridge.LogLine(
                        $"  <color=white>r{c.Revision}</color> | {c.Author} | {c.Date} | " +
                        $"{c.Matched.Count} item(s){bytes}{msgPart}");
                }
            }
            else if (report.LogError.Length > 0)
            {
                SVNLogBridge.LogLine($"<color=#8B0000>>Commit details unavailable: {report.LogError}</color>");
            }

            if (report.Unmatched.Count > 0)
            {
                string where = report.LocalRevision > 0
                    ? $"older than r{report.LocalRevision + 1}" : "older revisions";
                SVNLogBridge.LogLine(
                    $"<color=#8B0000>>{report.Unmatched.Count} item(s) changed in {where} (mixed-revision working copy).</color>");
            }

            if (report.Sizes != null)
            {
                SVNLogBridge.LogLine(
                    $"<b>Estimated download:</b> {report.Items.Count} items, ~{FormatSize(report.TotalBytes())} " +
                    "(full file sizes; real transfer is usually smaller)");
            }
            else if (report.SizeError.Length > 0)
            {
                SVNLogBridge.LogLine($"<color=yellow>Size estimate unavailable: {report.SizeError}</color>");
            }
        }

        private async Task<string> WriteRemoteChangesToTempFileAsync(
            RemoteChangeReport report, CancellationToken token)
        {
            CleanupOldTempFiles("svn_remote_changes_*.txt");

            string tempFilePath = Path.Combine(
                Path.GetTempPath(), $"svn_remote_changes_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();

            string thin = new string('-', 78);
            string thick = new string('=', 78);
            bool haveSizes = report.Sizes != null;
            var display = report.DisplayCommits();

            sb.AppendLine(thick);
            sb.AppendLine(" SVN REMOTE CHANGES REPORT");
            sb.AppendLine(thick);
            sb.AppendLine($" Root:                   {report.Root}");
            if (!string.IsNullOrEmpty(report.Url))
                sb.AppendLine($" Repository URL:         {report.Url}");
            sb.AppendLine($" Working copy revision:  {(report.LocalRevision > 0 ? "r" + report.LocalRevision : "unknown")}");
            sb.AppendLine($" Repository HEAD:        {(report.HeadRevision > 0 ? "r" + report.HeadRevision : "unknown")}");
            sb.AppendLine($" Items to update:        {report.Items.Count}");
            if (haveSizes)
                sb.AppendLine($" Estimated download:     ~{FormatSize(report.TotalBytes())}  " +
                              "(full file sizes; actual transfer is usually smaller)");
            sb.AppendLine($" Generated:              {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(thick);

            if (report.LogError.Length > 0)
                sb.AppendLine($" NOTE: commit details unavailable ({report.LogError}).");
            if (report.SizeError.Length > 0)
                sb.AppendLine($" NOTE: size estimates unavailable ({report.SizeError}).");

            if (report.Conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($" LOCAL CONFLICTS ({report.Conflicts.Count}) - resolve before updating!");
                sb.AppendLine(thin);
                foreach (var c in report.Conflicts) sb.AppendLine($"  {c}");
            }

            if (display.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($" INCOMING COMMITS ({display.Count}), newest first");
                sb.AppendLine(thin);

                foreach (var c in display)
                {
                    sb.AppendLine(
                        $"[r{c.Revision}]  {c.Author}  |  {c.Date}  |  {c.Matched.Count} item(s)" +
                        (haveSizes ? $"  |  ~{FormatSize(c.Bytes)}" : ""));
                    if (c.Message.Length > 0) sb.AppendLine($"  Message: {c.Message}");
                    sb.AppendLine("  Files:");
                    foreach (var ci in c.Matched)
                    {
                        string size = (haveSizes && report.Sizes.TryGetValue(ci.Item.Path, out long s))
                            ? FormatSize(s).PadLeft(10) : new string(' ', 10);
                        string rev = ci.Item.RemoteRevision > 0 ? $"r{ci.Item.RemoteRevision} " : "";
                        sb.AppendLine($"    [{ci.Action}] {ci.Item.Path}  {rev}{size}");
                    }
                    sb.AppendLine();
                }
            }

            if (display.Count > 0)
            {
                sb.AppendLine(" CHANGES BY AUTHOR");
                sb.AppendLine(thin);
                foreach (var g in display.GroupBy(c => c.Author)
                    .Select(g => new
                    {
                        Author = g.Key,
                        Commits = g.Count(),
                        Items = g.Sum(c => c.Matched.Count),
                        Bytes = g.Sum(c => c.Bytes)
                    })
                    .OrderByDescending(x => x.Items))
                {
                    string bytes = haveSizes ? $", ~{FormatSize(g.Bytes)}" : "";
                    sb.AppendLine($"  {g.Author,-24} {g.Commits} commit(s), {g.Items} item(s){bytes}");
                }
                sb.AppendLine();
            }

            if (report.Unmatched.Count > 0)
            {
                string where = report.LocalRevision > 0
                    ? $"older than r{report.LocalRevision + 1}" : "older revisions";
                sb.AppendLine($" ITEMS CHANGED IN {where.ToUpperInvariant()} (mixed-revision working copy)");
                sb.AppendLine(thin);
                foreach (var item in report.Unmatched)
                {
                    string rev = item.RemoteRevision > 0 ? $" (r{item.RemoteRevision})" : "";
                    sb.AppendLine($"  {item.Path}{rev}");
                }
                sb.AppendLine();
            }

            sb.AppendLine($" FULL LIST ({report.Items.Count} items)");
            sb.AppendLine(thin);
            foreach (var item in report.Items)
            {
                string rev = item.RemoteRevision > 0 ? $" (r{item.RemoteRevision})" : "";
                string size = (haveSizes && report.Sizes.TryGetValue(item.Path, out long s))
                    ? $"  {FormatSize(s),10}" : "";
                sb.AppendLine($"  {item.Path}{rev}{size}");
            }

            sb.AppendLine();
            sb.AppendLine(thick);
            sb.AppendLine(" END OF REPORT");
            sb.AppendLine(thick);

            await File.WriteAllTextAsync(
                tempFilePath, sb.ToString(), new UTF8Encoding(false), token).ConfigureAwait(false);
            return tempFilePath;
        }

        // ===================================================================
        //  Small helpers
        // ===================================================================

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("F1", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB";
            return (mb / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        }

        private static bool IsDashLine(string line)
        {
            foreach (char c in line) if (c != '-') return false;
            return true;
        }

        private static long ParseRevisionLong(string revision)
            => long.TryParse(revision, out long v) ? v : 0;

        private void OpenInEditor(string filePath)
        {
            try
            {
                string editorPath = svnManager?.MergeToolPath;
                if (!string.IsNullOrWhiteSpace(editorPath) && File.Exists(editorPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = editorPath,
                        Arguments = $"\"{filePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Could not open editor: {ex.Message}");
            }
        }

        private static void CleanupOldTempFiles(string pattern)
        {
            try
            {
                string temp = Path.GetTempPath();
                foreach (var file in Directory.GetFiles(temp, pattern))
                {
                    if (!File.Exists(file)) continue;
                    var fi = new FileInfo(file);
                    if (fi.CreationTime < DateTime.Now.AddHours(-24))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        // ===================================================================
        //  Dirty-state
        // ===================================================================

        public async Task<bool> HasLocalModificationsAsync(string workingDir, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir)) return false;
            try
            {
                return await svnManager.HasLocalModificationsAsync(workingDir, includeUnversioned: true, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Error checking local modifications: {ex.Message}");
                return true;
            }
        }

        // ===================================================================
        //  Dispose
        // ===================================================================

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _progress?.Clear();
            CancelUpdate();
            CancelRemoteCheck();

            var cts = Interlocked.Exchange(ref _updateCTS, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }

            var rcts = Interlocked.Exchange(ref _remoteCheckCTS, null);
            if (rcts != null)
            {
                try { rcts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { rcts.Dispose(); } catch { } });
            }
        }
    }
}