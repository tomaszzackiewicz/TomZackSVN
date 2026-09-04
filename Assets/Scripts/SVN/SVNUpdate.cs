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
        private static readonly Regex RevisionRegex = new Regex(@"^Revision:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

        private CancellationTokenSource _updateCTS;
        private CancellationTokenSource _remoteCheckCTS;   // === NEW: anulowanie remote-check przez Update
        private Task _remoteCheckTask;                     // === NEW
        private Task _runningTask;
        private Guid _sessionId = Guid.Empty;
        private int _disposed;

        // === PROGRESS UI: dwie linie odświeżane in-place w polu plików
        // (pasek+% / aktualny plik na niebiesko + (done/remaining))
        private SvnUpdateProgressUI _progress;

        public SVNUpdate(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // ===================================================================
        //  Entry points
        // ===================================================================

        public void Update()
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir) || !Directory.Exists(svnManager.WorkingDir))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return;
            }
            if (!SVNAssetLocator.IsWorkingCopy(svnManager.WorkingDir))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Not a valid SVN working copy (missing .svn).");
                return;
            }

            // === NEW: remote-check w toku -> anuluj go, update ma priorytet
            // (bez limitów remote-check trzyma kolejkę SVN i blokowałby update).
            bool updateBusy = _runningTask != null && !_runningTask.IsCompleted;
            var remote = _remoteCheckTask;

            if (!updateBusy && remote != null && !remote.IsCompleted)
            {
                CancelRemoteCheck();

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Updating,
                    Message = "Starting update (remote check cancelled)...",
                    Duration = 0,
                    Repo = svnManager.RepositoryUrl
                };
                svnManager.WasUpdateCanceled = false;
                _sessionId = Guid.NewGuid();

                _runningTask = RunUpdateAfterRemoteCheckAsync(remote, null);
                return;
            }

            if (IsProcessing || updateBusy)
            {
                SVNLogBridge.LogToOutput("<color=orange>Update already running...</color>");
                return;
            }

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Updating,
                Message = "Starting update...",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };
            svnManager.WasUpdateCanceled = false;
            _sessionId = Guid.NewGuid();

            _runningTask = ExecuteUpdateCoreAsync(svnManager.WorkingDir, null, _sessionId);
        }

        public void UpdateToRevision(string revision)
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            if (string.IsNullOrWhiteSpace(revision))
            {
                Update();
                return;
            }

            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir) || !Directory.Exists(svnManager.WorkingDir))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return;
            }
            if (!SVNAssetLocator.IsWorkingCopy(svnManager.WorkingDir))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Not a valid SVN working copy (missing .svn).");
                return;
            }

            // === NEW: remote-check w toku -> anuluj go, update ma priorytet
            bool updateBusy = _runningTask != null && !_runningTask.IsCompleted;
            var remote = _remoteCheckTask;

            if (!updateBusy && remote != null && !remote.IsCompleted)
            {
                CancelRemoteCheck();

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Updating,
                    Message = $"Starting update to revision {revision} (remote check cancelled)...",
                    Duration = 0,
                    Repo = svnManager.RepositoryUrl
                };
                svnManager.WasUpdateCanceled = false;
                _sessionId = Guid.NewGuid();

                _runningTask = RunUpdateAfterRemoteCheckAsync(remote, revision);
                return;
            }

            if (IsProcessing || updateBusy)
            {
                SVNLogBridge.LogLine("<color=orange>Update already running...</color>", false);
                return;
            }

            if (!int.TryParse(revision, out _))
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Invalid revision number: {revision}");
                return;
            }

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Updating,
                Message = $"Starting update to revision {revision}...",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };

            svnManager.WasUpdateCanceled = false;
            _sessionId = Guid.NewGuid();
            _runningTask = ExecuteUpdateCoreAsync(svnManager.WorkingDir, revision, _sessionId);
        }

        // ===================================================================
        //  Update-after-remote-check chaining (update ma priorytet)
        // ===================================================================

        private async Task RunUpdateAfterRemoteCheckAsync(Task remoteCheck, string revision)
        {
            // Poczekaj aż zdychająca komenda remote-check zwolni kolejkę SVN.
            // Błędy są już obsłużone wewnątrz remote-check — tu tylko czekamy.
            try { await remoteCheck.ConfigureAwait(false); }
            catch { /* ignored on purpose */ }

            // Sesja czytana NA TERAZ — jeśli w międzyczasie ktoś wywołał CancelUpdate
            // (nowy _sessionId), update po prostu się nie wystartuje (guard sesji).
            await ExecuteUpdateCoreAsync(svnManager.WorkingDir, revision, _sessionId).ConfigureAwait(false);
        }

        private void CancelRemoteCheck()
        {
            var cts = Volatile.Read(ref _remoteCheckCTS);
            if (cts == null) return;

            SVNLogBridge.LogToOutput("<color=orange><b>[SVN]</b> Cancelling remote change check - update has priority...</color>");
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // ===================================================================
        //  Core update logic
        // ===================================================================

        // ===================================================================
        //  Core update logic
        // ===================================================================

        // ===================================================================
        //  Core update logic
        // ===================================================================

        private async Task ExecuteUpdateCoreAsync(string targetPath, string targetRevision, Guid session)
        {
            if (session != _sessionId) return;

            var statusModule = svnManager.GetModule<SVNStatus>();
            statusModule?.CancelCurrentRefresh();

            var localCts = new CancellationTokenSource();
            CancellationToken token = localCts.Token;

            // === FIX K2: CTS + flagi PRZED pętlą czekania (Cancel działa od 1. ms).
            _updateCTS = localCts;
            svnManager.IsUpdateRunning = true;
            svnManager.LastUpdateSucceeded = false;
            IsProcessing = true;

            SVNBar svnBar = null;
            var stopwatch = Stopwatch.StartNew();
            var oldSnapshot = svnManager.CurrentSnapshot;
            string oldRevision = oldSnapshot?.Revision ?? "Unknown";

            // === FIX K3: czy komenda svn realnie zakończyła (cancel po niej ≠ porażka).
            bool svnCommandCompleted = false;

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
                    {
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                    }
                });

                if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
                {
                    SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                    return;
                }

                bool isRevisionTarget = !string.IsNullOrEmpty(targetRevision);
                string commandLabel = isRevisionTarget ? $"update to revision {targetRevision}" : "update";

                if (oldRevision == "Unknown")
                {
                    try
                    {
                        string infoBefore = await SvnRunner.GetInfoAsync(targetPath, token);
                        token.ThrowIfCancellationRequested();
                        oldRevision = ParseRevisionFromInfo(infoBefore);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Could not determine current revision: {ex.Message}</color>");
                    }
                }

                svnBar = svnManager.GetModule<SVNBar>();
                string projectName = svnManager.CurrentProject?.projectName ?? Path.GetFileName(targetPath);
                svnBar?.BeginUpdate(projectName);

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Updating,
                    Message = $"Running SVN {commandLabel}...",
                    Duration = 0,
                    Repo = svnManager.RepositoryUrl
                };

                int uCount = 0, gCount = 0, aCount = 0, dCount = 0, cCount = 0, rCount = 0;
                int processed = 0;
                int totalUpdates = 0;

                // === NEW (diagnostyka): widać wprost, kiedy update czeka na kolejkę SVN.
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Waiting for free SVN command queue...");
                await SvnRunner.WaitForSemaphoreFreeAsync(token);
                token.ThrowIfCancellationRequested();
                if (session != _sessionId) throw new OperationCanceledException(token);
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Queue free - proceeding with update.");

                SVNLogBridge.LogToOutput("<b>[SVN]</b> Pre-update cleanup...");
                await SVNClean.CleanupAsync(targetPath, token);
                SVNLogBridge.LogToOutput("<b>[SVN]</b> Cleanup completed.");

                // === Progress estimation (pliki + ścieżki): 'svn status -u' — pozycje '*'.
                // ŚCIEŻKI są potrzebne jako klucze do wag bajtowych (svn list).
                var outOfDatePaths = new List<string>();
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

                                // po '*': remote revision + ścieżka (jak w ParseStatusOutput)
                                string rest = line.Substring(9).TrimStart();
                                Match revMatch = RemoteRevPrefixRegex.Match(rest);
                                if (revMatch.Success)
                                    rest = rest.Substring(revMatch.Length).TrimStart();

                                string cleanPath = SvnRunner.NormalizeRepositoryPath(rest.TrimEnd());
                                if (cleanPath.Length > 0)
                                    outOfDatePaths.Add(cleanPath);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Progress estimation unavailable: {ex.Message}</color>");
                }

                // === PROGRESS (wagi bajtowe): rozmiary plików z HEAD przez svn list -R.
                // Porażka → totalBytes = 0 → tracker działa per-plikowo (fallback).
                // UWAGA: przy update -r N rozmiary z HEAD są przybliżeniem.
                Dictionary<string, long> incomingSizes = null;
                long totalBytes = 0;
                if (outOfDatePaths.Count > 0 && !string.IsNullOrWhiteSpace(svnManager.RepositoryUrl))
                {
                    try
                    {
                        SVNLogBridge.LogToOutput("<b>[SVN]</b> Fetching file sizes for byte-weighted progress...");
                        incomingSizes = await FetchIncomingSizesAsync(targetPath, svnManager.RepositoryUrl, token);
                        token.ThrowIfCancellationRequested();

                        foreach (var p in outOfDatePaths)
                            if (incomingSizes.TryGetValue(p, out long s))
                                totalBytes += s;

                        SVNLogBridge.LogToOutput(
                            totalBytes > 0
                                ? $"<b>[SVN]</b> Byte weights ready: {FormatSize(totalBytes)} across {outOfDatePaths.Count} item(s)."
                                : "<color=yellow>[SVN] No byte weights matched — using file-count progress.</color>");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Size weights unavailable — file-count progress: {ex.Message}</color>");
                        incomingSizes = null;
                        totalBytes = 0;
                    }
                }

                string svnCommand = isRevisionTarget
                    ? $"update --accept postpone -r {targetRevision}"
                    : "update --accept postpone";

                // === PROGRESS UI: pasek ważony BAJTAMI (linia 1) + pliki (linia 2),
                // podmieniane in-place w OutputText.
                _progress = new SvnUpdateProgressUI(svnUI?.OutputText);
                _progress.SetTotal(totalUpdates, totalBytes, incomingSizes);

                SVNLogBridge.LogToOutput($"<color=blue><b>[SVN]</b> Running {commandLabel}...</color>");

                string result = await SvnRunner.RunLiveAsync(
                    svnCommand,
                    targetPath,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        string trimmed = line.Trim();
                        if (trimmed.Length > 0 && trimmed.All(c => c == '@' || c == '*' || c == ' ')) return;
                        if (trimmed.StartsWith("*****") || trimmed.StartsWith("@@@@@")) return;

                        string cleanLine = trimmed.Replace("[SVN ERROR]", "").Trim();
                        if (cleanLine.Length > 0 && cleanLine.All(c => c == '@' || c == '*')) return;

                        if (token.IsCancellationRequested) return;
                        if (session != _sessionId) return;

                        // === FIX (progress counter): liczone TYLKO linie reprezentujące plik.
                        if (trimmed.StartsWith("Updating", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("At revision", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("Checked out revision", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("Transmitting", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("Fetching", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("External", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("Updated to revision", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("Restored", StringComparison.Ordinal)) return;
                        if (trimmed.StartsWith("Summary of conflicts", StringComparison.Ordinal)) return;

                        processed++;

                        string displayLine;
                        string itemPath = null;   // do wagi bajtowej (klucz = znormalizowana ścieżka)
                        char contentStatus = trimmed.Length > 0 ? trimmed[0] : ' ';
                        char propStatus = trimmed.Length > 1 ? trimmed[1] : ' ';
                        char activeStatus = contentStatus != ' ' ? contentStatus : propStatus;

                        if (trimmed.Length > 2 && "UAGDCR".Contains(activeStatus) && trimmed[1] == ' ')
                        {
                            char status = activeStatus;
                            string path = SvnRunner.NormalizeRepositoryPath(trimmed.Substring(2).TrimStart());
                            itemPath = path;   // <<< waga bajtowa: ta sama normalizacja co status -u

                            switch (status)
                            {
                                case 'U': uCount++; break;
                                case 'G': gCount++; break;
                                case 'A': aCount++; break;
                                case 'D': dCount++; break;
                                case 'C': cCount++; break;
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
                        else
                        {
                            displayLine = trimmed;
                        }

                        // === ORYGINALNY format (x/y) plików — linia 2, bez zmian
                        string progressStr;
                        if (totalUpdates > 0)
                        {
                            int shown = Math.Min(processed, totalUpdates);
                            if (processed > totalUpdates)
                                progressStr = $" ({totalUpdates}/{totalUpdates}, +{processed - totalUpdates} extra)";
                            else
                                progressStr = $" ({shown}/{totalUpdates})";
                        }
                        else
                        {
                            progressStr = "";
                        }

                        // === PROGRESS UI: OnItem z ścieżką (waga bajtowa paska)
                        bool isConflictLine = trimmed.Length > 2 && trimmed[1] == ' ' && activeStatus == 'C';
                        _progress?.OnItem(itemPath, displayLine, progressStr, isConflictLine);
                    },
                    token
                );

                token.ThrowIfCancellationRequested();
                svnCommandCompleted = true;
                _progress?.Finish();      // === sukces: snap do 100% (pliki i bajty)

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
                    SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Could not determine resulting revision: {ex.Message}</color>");
                    if (newRevision == "Unknown") newRevision = targetRevision ?? "Unknown";
                }

                stopwatch.Stop();
                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Success,
                    Message = $"{commandLabel} completed successfully",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };
                svnManager.LastUpdateSucceeded = true;
                SVNStatus.ClearLockCache();
                svnManager.DiskChangesDetected = true;

                // === FIX K1: post-sukces w wewnętrznym try.
                try
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
                    report.AppendLine($"  Duration:   <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>\n");

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
                            await svnManager.GetModule<SVNResolve>()?.RefreshConflictUI();
                        }
                    }
                    report.AppendLine("<color=yellow><b>=========================================</b></color>");
                    SVNLogBridge.LogLine(report.ToString(), false);

                    if (!svnManager.WasUpdateCanceled && statusModule != null)
                        await statusModule.RefreshModifiedInternal();

                    if (!svnManager.WasUpdateCanceled && svnBar != null)
                    {
                        var snap = svnManager.CurrentSnapshot ?? new SVNProjectInfoSnapshot();
                        snap.Revision = newRevision;

                        string newAuthor = await GetAuthorForRevision(svnManager.WorkingDir, newRevision, token);
                        if (!string.IsNullOrEmpty(newAuthor))
                        {
                            snap.Author = newAuthor;
                        }

                        snap.IsValid = true;
                        svnManager.CurrentSnapshot = snap;

                        await svnBar.EndUpdate(snap);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception postEx)
                {
                    SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Update completed, but post-processing failed: {postEx.Message}</color>");
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                svnManager.LastUpdateSucceeded = svnCommandCompleted;

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = svnCommandCompleted ? SVNOperationState.Success : SVNOperationState.Canceled,
                    Message = svnCommandCompleted
                        ? "Update completed; post-processing cancelled"
                        : "Update canceled by user",
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

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
                    cancelReport.AppendLine("<color=#FFAA00><b>          UPDATE INTERRUPTED             </b></color>");
                    cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                    cancelReport.AppendLine($"  Process aborted after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                    cancelReport.AppendLine("  Working copy state might be incomplete.");
                    cancelReport.AppendLine("<color=#FFAA00><b>=========================================</b></color>");
                    SVNLogBridge.LogLine(cancelReport.ToString(), false);
                }

                _progress?.Clear();   // === cancel: dwie linie postępu znikają
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                if (ex.Message.Contains("locked") || ex.Message.Contains("E155004"))
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

                svnManager.OperationInfo = new SVNOperationInfo
                {
                    State = SVNOperationState.Failed,
                    Message = ex.Message,
                    Duration = stopwatch.Elapsed.TotalSeconds,
                    Repo = svnManager.RepositoryUrl
                };

                var failureReport = new StringBuilder();
                failureReport.AppendLine("\n<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine("<color=#B22222><b>            UPDATE FAILED                </b></color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                failureReport.AppendLine($"  Execution crashed after <b>{stopwatch.Elapsed.TotalSeconds:F2}s</b>.");
                failureReport.AppendLine($"  Error message: <color=#E6E6E6>{ex.Message}</color>");
                failureReport.AppendLine("<color=#B22222><b>=========================================</b></color>");
                SVNLogBridge.LogLine(failureReport.ToString(), false);

                _progress?.Clear();   // === błąd: jw.
            }
            finally
            {
                svnManager.IsUpdateRunning = false;
                IsProcessing = false;

                if (ReferenceEquals(Volatile.Read(ref _updateCTS), localCts))
                    _updateCTS = null;

                _ = Task.Delay(1000).ContinueWith(_ => { try { localCts.Dispose(); } catch { } });
                _runningTask = null;
            }
        }

        /// <summary>
        /// Rozmiary plików z HEAD (wagi bajtowe paska postępu) — svn list --xml -R
        /// na URL projektu. Klucze = ścieżki względne projektu (ten sam format co
        /// status -u i wyjście svn update). Porażka = pusty słownik (fallback na
        /// liczenie per plik w trackerze).
        /// </summary>
        private async Task<Dictionary<string, long>> FetchIncomingSizesAsync(
            string workingDir, string repoUrl, CancellationToken token)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(repoUrl)) return sizes;

            string url = repoUrl.Trim().TrimEnd('/');
            string xml = await SvnRunner.RunAsync($"list --xml -R \"{url}\"", workingDir, token: token);
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(xml)) return sizes;

            try
            {
                // odporność na ewentualne śmieci przed/za XML (jak w FetchPathSizesAsync)
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

                    // list path może być pełnym URL-em — zdejmij prefiks
                    int idx = listPath.IndexOf(url, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) listPath = listPath.Substring(idx + url.Length);
                    listPath = listPath.Trim('/');

                    foreach (XmlNode entry in listNode.SelectNodes("entry"))
                    {
                        if (!string.Equals(entry.Attributes?["kind"]?.Value, "file",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        string name = entry.SelectSingleNode("name")?.InnerText;
                        XmlNode sizeNode = entry.SelectSingleNode("size");
                        if (string.IsNullOrEmpty(name) || sizeNode == null) continue;
                        if (!long.TryParse(sizeNode.InnerText, out long size)) continue;

                        string rel = string.IsNullOrEmpty(listPath) ? name : listPath + "/" + name;
                        rel = rel.Replace('\\', '/').TrimStart('/');
                        if (rel.Length > 0)
                            sizes[rel] = size;
                    }
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Size weights parse failed: {ex.Message}</color>");
                return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }

            return sizes;
        }

        // ===================================================================
        //  Cancel
        // ===================================================================

        public void CancelUpdate()
        {
            var cts = Volatile.Read(ref _updateCTS);
            if (cts == null || !svnManager.IsUpdateRunning) return;

            SVNLogBridge.LogToOutput("<color=orange><b>[SVN]</b> Cancel requested...</color>");

            svnManager.WasUpdateCanceled = true;
            svnManager.LastUpdateSucceeded = false;

            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }

            _sessionId = Guid.NewGuid();

            svnManager.OperationInfo = new SVNOperationInfo
            {
                State = SVNOperationState.Canceled,
                Message = "Cancel requested...",
                Duration = 0,
                Repo = svnManager.RepositoryUrl
            };
        }

        // ===================================================================
        //  Utilities
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
                if (parts.Length >= 2)
                    return parts[1].Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color=yellow>[SVN] Could not determine revision author: {ex.Message}</color>");
            }
            return string.Empty;
        }

        public string ParseRevisionFromInfo(string infoOutput)
        {
            if (string.IsNullOrWhiteSpace(infoOutput)) return "Unknown";

            var match = RevisionRegex.Match(infoOutput);
            return match.Success ? match.Groups[1].Value : "Unknown";
        }

        // ===================================================================
        //  Remote modifications check
        //  (no timeouts, no limits — everything SVN returns is shown in full)
        //  Update() anuluje trwający remote-check (update ma priorytet).
        // ===================================================================

        public async void CheckRemoteModificationsButton() => await ShowRemoteUpdatesInline();

        // --- parsing regexes ---
        private static readonly Regex StatusAgainstRevRegex = new Regex(@"Status against revision:\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex RemoteRevPrefixRegex = new Regex(@"^(\d+)\s+");
        private static readonly Regex LogChangedPathRegex = new Regex(@"^\s*([MARD])\s+(\S.*?)\s*$");
        private static readonly Regex SvnDateRegex = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2})");
        private static readonly Regex UrlInfoRegex = new Regex(@"^URL:\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex RepoRootInfoRegex = new Regex(@"^Repository Root:\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

        // --- data holders ---
        private sealed class RemoteItem
        {
            public string Path;             // WC-relative, normalized
            public long RemoteRevision;     // repo revision the item is at (0 = unknown)
        }

        private sealed class CommitPath
        {
            public char Action;             // M / A / D / R
            public string RepoPath;         // '/trunk/Project/Assets/x.cs'
            public string RelativePath;     // 'Assets/x.cs' (null = outside the WC subtree)
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
            public List<CommitItem> Matched = new List<CommitItem>();   // out-of-date items touched by this commit
            public long Bytes;                                          // sum of matched file sizes (when sizes known)
        }

        private sealed class RemoteChangeReport
        {
            public string Root = "";
            public string Url = "";
            public string RepoPrefix;                   // '/trunk/Project' ('' = WC is repo root, null = unknown)
            public long LocalRevision;                  // 0 = unknown
            public long HeadRevision;                   // 0 = unknown
            public List<RemoteItem> Items = new List<RemoteItem>();
            public List<string> Conflicts = new List<string>();
            public List<CommitInfo> Commits = new List<CommitInfo>();
            public List<RemoteItem> Unmatched = new List<RemoteItem>(); // older / mixed-revision items
            public Dictionary<string, long> Sizes;      // path -> bytes (null = unavailable)
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

        // --- wrapper: walidacja + CTS + task (może być anulowany przez Update) ---
        public async Task ShowRemoteUpdatesInline()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (_remoteCheckTask != null && !_remoteCheckTask.IsCompleted) return;   // już działa

            if (IsProcessing || (_runningTask != null && !_runningTask.IsCompleted))
            {
                SVNLogBridge.LogToOutput("<color=orange>Another SVN operation is running...</color>");
                return;
            }

            string root = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogErrorToOutput("[SVN] Working directory does not exist.");
                return;
            }

            // NO timeout, NO limits — commands run until they finish and everything is reported.
            var cts = new CancellationTokenSource();
            _remoteCheckCTS = cts;

            Task core = RemoteCheckCoreAsync(root, cts);
            _remoteCheckTask = core;
            await core.ConfigureAwait(false);
        }

        // --- core: cała logika sprawdzenia (anulowalna tokenem) ---
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
                    SVNLogBridge.LogLine($"<color=#FF4444><b>WARNING: {report.Conflicts.Count} local conflict(s) detected!</b></color>");
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

                // === authors / dates / messages / size estimates for the incoming changes
                await EnrichWithCommitInfoAsync(report, token);

                string tempFile = null;
                try { tempFile = await WriteRemoteChangesToTempFileAsync(report, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] Could not write report file: {ex.Message}"); }

                SVNLogBridge.LogLine($"<b>Summary:</b> Found <color=#FFAA00>{report.Items.Count}</color> items to update.");
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

        // ===================================================================
        //  Remote report building / parsing
        // ===================================================================

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

                // out-of-date item: '*' in column 9, followed by its remote revision and path
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

                    string cleanPath = SvnRunner.NormalizeRepositoryPath(rest.TrimEnd());
                    if (cleanPath.Length > 0)
                        report.Items.Add(new RemoteItem { Path = cleanPath, RemoteRevision = remoteRev });
                }

                // local conflicts
                if (line.Length > 1 && (line[0] == 'C' || line[1] == 'C'))
                {
                    string rawPath = line.Length > 8 ? line.Substring(8).Trim() : line.Trim();
                    string cleanPath = SvnRunner.NormalizeRepositoryPath(SvnRunner.CleanSvnPath(rawPath));
                    if (cleanPath.Length > 0 && !report.Conflicts.Contains(cleanPath))
                        report.Conflicts.Add(cleanPath);
                }
            }
            return report;
        }

        private async Task EnrichWithCommitInfoAsync(RemoteChangeReport report, CancellationToken token)
        {
            // --- (1) local revision + repository URL mapping (svn info)
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
                            ? report.Url.Substring(repoRoot.Length)   // '/trunk/Project' or ''
                            : null;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.LogError = $"svn info failed: {ex.Message}";
            }

            // --- (2) FULL commit log for the incoming range (svn log -v, no revision-count limit)
            long rangeStart = ComputeLogRangeStart(report);
            if (rangeStart > 0 && report.HeadRevision > 0 && rangeStart <= report.HeadRevision)
            {
                try
                {
                    string logOutput = await SvnRunner.RunAsync(
                        $"log -v -r {rangeStart}:{report.HeadRevision}",
                        report.Root, token: token);

                    report.Commits = ParseSvnLog(logOutput, report.RepoPrefix);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.LogError = ex.Message;
                }
            }
            else if (report.HeadRevision == 0 && report.LogError.Length == 0)
            {
                report.LogError = "could not determine repository HEAD revision";
            }

            if (report.Commits.Count == 0 && report.LogError.Length == 0)
                report.LogError = rangeStart > 0 ? "no log entries returned" : "could not determine revision range";

            // --- (3) byte-size estimates (svn list --xml -R, always fetched)
            if (!string.IsNullOrEmpty(report.Url))
            {
                try
                {
                    report.Sizes = await FetchPathSizesAsync(report, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.SizeError = ex.Message;
                }
            }

            // --- (4) link out-of-date items to commits + per-commit byte sums
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
                return Math.Min(report.LocalRevision + 1, minRemote);  // mixed-rev: cover older commits too
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
                    if (cp.RelativePath != null)
                        byPath.TryGetValue(cp.RelativePath, out item);

                    if (item == null && report.RepoPrefix == null)
                    {
                        // fallback only when URL mapping is unknown: suffix match
                        foreach (var kv in byPath)
                        {
                            if (cp.RepoPath.Length > kv.Key.Length &&
                                cp.RepoPath.EndsWith("/" + kv.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                item = kv.Value;
                                break;
                            }
                        }
                    }

                    if (item != null)
                    {
                        matched.Add(item);
                        bool already = false;
                        foreach (var ci in commit.Matched)
                            if (ReferenceEquals(ci.Item, item)) { already = true; break; }
                        if (!already)
                            commit.Matched.Add(new CommitItem { Item = item, Action = cp.Action });
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
                // entry separator: a full line of dashes (svn uses 72)
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
                    {
                        current = header;
                        message = new List<string>();
                        inChangedPaths = false;
                    }
                    continue;
                }

                if (line.Trim() == "Changed paths:")
                {
                    inChangedPaths = true;
                    continue;
                }

                if (inChangedPaths)
                {
                    Match m = LogChangedPathRegex.Match(line);
                    if (m.Success)
                    {
                        string repoPath = m.Groups[2].Value.Replace('\\', '/').Trim();

                        // copy/move source: 'A /x (from /y:123)'
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
                    inChangedPaths = false; // block ended -> message starts on this line
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
            // 'r1523 | jkowalski | 2025-06-12 14:32:05 +0200 (Thu, 12 Jun 2025) | 1 line'
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

            if (string.IsNullOrEmpty(repoPrefix))
                return repoPath.TrimStart('/');   // unknown mapping or WC == repo root

            string prefix = repoPrefix.TrimEnd('/');
            if (repoPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return repoPath.Substring(prefix.Length + 1);
            if (string.Equals(repoPath, prefix, StringComparison.OrdinalIgnoreCase))
                return "";
            return null; // outside of the working-copy subtree
        }

        private async Task<Dictionary<string, long>> FetchPathSizesAsync(RemoteChangeReport report, CancellationToken token)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(report.Url)) return sizes;

            string url = report.Url.TrimEnd('/');
            string xml = await SvnRunner.RunAsync($"list --xml -R \"{url}\"", report.Root, token: token);
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(xml))
                throw new Exception("empty 'svn list' output");

            int start = xml.IndexOf('<');
            int end = xml.LastIndexOf('>');
            if (start < 0 || end <= start)
                throw new Exception("'svn list' returned no XML data");

            var doc = new XmlDocument();
            doc.LoadXml(xml.Substring(start, end - start + 1));

            XmlNodeList lists = doc.SelectNodes("//list");
            if (lists == null) return sizes;

            foreach (XmlNode listNode in lists)
            {
                string listRel = StripToRelative(listNode.Attributes?["path"]?.Value, report);

                foreach (XmlNode entry in listNode.SelectNodes("entry"))
                {
                    if (!string.Equals(entry.Attributes?["kind"]?.Value, "file", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = entry.SelectSingleNode("name")?.InnerText;
                    XmlNode sizeNode = entry.SelectSingleNode("size");
                    if (string.IsNullOrEmpty(name) || sizeNode == null) continue;
                    if (!long.TryParse(sizeNode.InnerText, out long size)) continue;

                    string rel = string.IsNullOrEmpty(listRel) ? name : listRel + "/" + name;
                    rel = rel.Replace('\\', '/').TrimStart('/');
                    if (rel.Length > 0)
                        sizes[rel] = size;
                }
            }
            return sizes;
        }

        // list paths may arrive as full URL, repo-absolute ('/trunk/Project/Assets') or repo-relative
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

        // ===================================================================
        //  Console summary + report file (everything in full, no caps)
        // ===================================================================

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
                    SVNLogBridge.LogLine($"  <color=white>r{c.Revision}</color> | {c.Author} | {c.Date} | {c.Matched.Count} item(s){bytes}{msgPart}");
                }
            }
            else if (report.LogError.Length > 0)
            {
                SVNLogBridge.LogLine($"<color=orange>Commit details unavailable: {report.LogError}</color>");
            }

            if (report.Unmatched.Count > 0)
            {
                string where = report.LocalRevision > 0 ? $"older than r{report.LocalRevision + 1}" : "older revisions";
                SVNLogBridge.LogLine($"<color=orange>{report.Unmatched.Count} item(s) changed in {where} (mixed-revision working copy).</color>");
            }

            if (report.Sizes != null)
            {
                SVNLogBridge.LogLine($"<b>Estimated download:</b> {report.Items.Count} items, ~{FormatSize(report.TotalBytes())} (full file sizes; real transfer is usually smaller)");
            }
            else if (report.SizeError.Length > 0)
            {
                SVNLogBridge.LogLine($"<color=yellow>Size estimate unavailable: {report.SizeError}</color>");
            }
        }

        private async Task<string> WriteRemoteChangesToTempFileAsync(RemoteChangeReport report, CancellationToken token)
        {
            CleanupOldTempFiles("svn_remote_changes_*.txt");

            string tempFilePath = Path.Combine(Path.GetTempPath(), $"svn_remote_changes_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();

            string thin = new string('-', 78);
            string thick = new string('=', 78);
            bool haveSizes = report.Sizes != null;
            var display = report.DisplayCommits();

            // ---------- header ----------
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
                sb.AppendLine($" Estimated download:     ~{FormatSize(report.TotalBytes())}  (full file sizes; actual transfer is usually smaller)");
            sb.AppendLine($" Generated:              {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(thick);

            if (report.LogError.Length > 0) sb.AppendLine($" NOTE: commit details unavailable ({report.LogError}).");
            if (report.SizeError.Length > 0) sb.AppendLine($" NOTE: size estimates unavailable ({report.SizeError}).");

            // ---------- conflicts ----------
            if (report.Conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($" LOCAL CONFLICTS ({report.Conflicts.Count}) - resolve before updating!");
                sb.AppendLine(thin);
                foreach (var c in report.Conflicts)
                    sb.AppendLine($"  {c}");
            }

            // ---------- commits (all of them) ----------
            if (display.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($" INCOMING COMMITS ({display.Count}), newest first");
                sb.AppendLine(thin);

                foreach (var c in display)
                {
                    sb.AppendLine($"[r{c.Revision}]  {c.Author}  |  {c.Date}  |  {c.Matched.Count} item(s)" +
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

            // ---------- per author ----------
            if (display.Count > 0)
            {
                sb.AppendLine(" CHANGES BY AUTHOR");
                sb.AppendLine(thin);
                foreach (var g in display.GroupBy(c => c.Author)
                                  .Select(g => new { Author = g.Key, Commits = g.Count(), Items = g.Sum(c => c.Matched.Count), Bytes = g.Sum(c => c.Bytes) })
                                  .OrderByDescending(x => x.Items))
                {
                    string bytes = haveSizes ? $", ~{FormatSize(g.Bytes)}" : "";
                    sb.AppendLine($"  {g.Author,-24} {g.Commits} commit(s), {g.Items} item(s){bytes}");
                }
                sb.AppendLine();
            }

            // ---------- mixed-revision leftovers ----------
            if (report.Unmatched.Count > 0)
            {
                string where = report.LocalRevision > 0 ? $"older than r{report.LocalRevision + 1}" : "older revisions";
                sb.AppendLine($" ITEMS CHANGED IN {where.ToUpperInvariant()} (mixed-revision working copy)");
                sb.AppendLine(thin);
                foreach (var item in report.Unmatched)
                {
                    string rev = item.RemoteRevision > 0 ? $" (r{item.RemoteRevision})" : "";
                    sb.AppendLine($"  {item.Path}{rev}");
                }
                sb.AppendLine();
            }

            // ---------- full flat list ----------
            sb.AppendLine($" FULL LIST ({report.Items.Count} items)");
            sb.AppendLine(thin);
            foreach (var item in report.Items)
            {
                string rev = item.RemoteRevision > 0 ? $" (r{item.RemoteRevision})" : "";
                string size = (haveSizes && report.Sizes.TryGetValue(item.Path, out long s)) ? $"  {FormatSize(s),10}" : "";
                sb.AppendLine($"  {item.Path}{rev}{size}");
            }

            sb.AppendLine();
            sb.AppendLine(thick);
            sb.AppendLine(" END OF REPORT");
            sb.AppendLine(thick);

            await File.WriteAllTextAsync(tempFilePath, sb.ToString(), new UTF8Encoding(false), token).ConfigureAwait(false);
            return tempFilePath;
        }

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
            foreach (char c in line)
                if (c != '-') return false;
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
                    if (File.Exists(file))
                    {
                        var fi = new FileInfo(file);
                        if (fi.CreationTime < DateTime.Now.AddHours(-24))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        // ===================================================================
        //  Dirty-state (delegates to manager — S5)
        // ===================================================================

        public async Task<bool> HasLocalModificationsAsync(string workingDir, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
                return false;

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

            // === PROGRESS UI: schowaj linie postępu (panel może być wciąż aktywny)
            _progress?.Clear();

            CancelUpdate();
            CancelRemoteCheck();   // === NEW: zatrzymaj też trwający remote-check

            var cts = Interlocked.Exchange(ref _updateCTS, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }
        }
    }
}