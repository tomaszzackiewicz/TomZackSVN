using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRepoRepair : SVNBase, IDisposable
    {
        private readonly object _repairSync = new();

        private int _disposedFlag;

        private CancellationTokenSource _repairCTS;
        private volatile bool _repairRunning;
        private volatile bool _metadataRemoved;

        // === PROGRESS BAR: indeterminate → %, gdy prefetch rozmiarów wróci.
        private SvnUpdateProgressUI _progressUI;

        // === DEBUG: licznik linii checkoutu zalogowanych do konsoli.
        private int _checkoutDebugLinesLogged = 0;

        // === PRE-REPAIR SNAPSHOT: flaga czy w tej sesji repair snapshot się udał.
        private bool _preRepairSnapshotCreated;

        // === STEP SUMMARY: akumulowane linie kroków 1/3, 2/3, 3/3.
        // Powód: PostRepairStatus podmienia cały tekst — Step 1/3 znikał
        // zanim user zdążył go przeczytać. Akumulujemy i pokazujemy razem.
        private readonly StringBuilder _stepSummary = new();

        private bool _isDisposed
        {
            get => Volatile.Read(ref _disposedFlag) == 1;
            set => Volatile.Write(ref _disposedFlag, value ? 1 : 0);
        }

        // === Wynik kroku snapshotu — używany do wyświetlenia uczciwego statusu.
        private enum SnapshotPreRepairOutcome
        {
            Created,   // snapshot zapisany
            Skipped,   // brak warunków (brak .svn, brak modułu, brak working dir)
            Failed     // próbowano, ale coś poszło nie tak
        }

        public SVNRepoRepair(SVNUI svnUI, SVNManager manager) : base(svnUI, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        public async void ForceRepairWorkingCopy()
        {
            string path = svnUI?.CheckoutDestFolderInput?.text?.Trim();
            string url = svnUI?.CheckoutRepoUrlInput?.text?.Trim().TrimEnd('/');

            try { await ForceRepairWorkingCopyAsync(path, url).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { HandleOperationExceptionSafe(ex); }
        }

        public async Task ForceRepairWorkingCopyAsync(string path, string url)
        {
            if (_isDisposed) { ShowErrorSafe("SVN Repo Repair module has already been disposed."); return; }
            if (!TryBeginRepair(out var repairCts)) { ShowErrorSafe("A repository repair operation is already running."); return; }

            bool pollingWasPausedByUs = false;
            bool checkoutStarted = false;
            bool workingCopyHealthy = false;

            try
            {
                var token = repairCts.Token;

                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url))
                { ShowErrorSafe("Repository URL and destination path cannot be empty for force repair."); return; }
                token.ThrowIfCancellationRequested();
                if (!IsValidSvnUrl(url)) { ShowErrorSafe("Invalid SVN URL."); return; }
                if (!TryValidatePath(path, out var fullPath)) return;
                if (!Directory.Exists(fullPath)) { ShowErrorSafe($"Destination directory does not exist:\n{fullPath}"); return; }
                if (!IsSafeRepairDirectory(fullPath)) { ShowErrorSafe("The selected destination directory is not safe for Force Repair."); return; }

                var svnDir = Path.Combine(fullPath, ".svn");
                var wcDbPath = Path.Combine(svnDir, "wc.db");
                token.ThrowIfCancellationRequested();

                SetPollingState(isPaused: true, cancelCurrent: true);
                pollingWasPausedByUs = true;
                SetProcessingState(true);

                _checkoutDebugLinesLogged = 0;
                _preRepairSnapshotCreated = false;

                lock (_stepSummary) _stepSummary.Clear();
                PostStep("<color=yellow><b>Initializing Force Repair...</b></color>");

                var keyPath = ResolveAndValidateKeyPath();
                if (string.IsNullOrWhiteSpace(keyPath) && url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase))
                { ShowErrorSafe("SSH repository requires a valid private key."); return; }

                // PREFETCH rozmiarów (równolegle z krokiem 1 i 2).
                var prefetchTask = PrefetchRemoteSizesAsync(url, token);

                // ============================================================
                //  STEP 1/3 — PRE-REPAIR SNAPSHOT
                // ============================================================
                PostStep("<color=yellow><b>Step 1/3:</b> Creating pre-repair safety snapshot...</color>");
                PostStep("<size=11><i>Capturing local changes before rebuild (silent).</i></size>");

                var snapOutcome = await TryCreatePreRepairSnapshotAsync(token).ConfigureAwait(false);
                _preRepairSnapshotCreated = (snapOutcome == SnapshotPreRepairOutcome.Created);

                switch (snapOutcome)
                {
                    case SnapshotPreRepairOutcome.Created:
                        PostStep("<color=green><b>Step 1/3:</b> Safety snapshot created.</color>");
                        break;
                    case SnapshotPreRepairOutcome.Skipped:
                        PostStep("<color=#FFAA00><b>Step 1/3:</b> Safety snapshot skipped (no .svn or module unavailable).</color>");
                        break;
                    default:
                        PostStep("<color=#FF8800><b>Step 1/3:</b> Safety snapshot failed — see log above.</color>");
                        break;
                }

                token.ThrowIfCancellationRequested();

                // ============================================================
                //  STEP 2/3 — usuń stare .svn
                // ============================================================
                PostStep("<color=#8B0000>><b>Step 2/3:</b> Removing old .svn metadata...</color>");

                SVNManager.Instance?.DisposeFileSystemWatcher();
                token.ThrowIfCancellationRequested();

                if (!await DeleteOldMetadataAsync(svnDir, token).ConfigureAwait(false))
                {
                    PostStep("<color=#FF4444><b>Step 2/3:</b> Failed to remove .svn.</color>");
                    HandleMetadataDeletionFailure();
                    pollingWasPausedByUs = false;
                    return;
                }
                _metadataRemoved = true;
                PostStep("<color=green><b>Step 2/3:</b> Old metadata removed successfully.</color>");
                token.ThrowIfCancellationRequested();

                // ============================================================
                //  STEP 3/3 — checkout
                // ============================================================
                PostStep("<color=yellow><b>Step 3/3:</b> Rebuilding working copy...</color>");
                PostStep("<size=11><i>See console below for live progress.</i></size>");

                var progressTarget = svnUI?.CheckoutedFilesText;
                if (progressTarget != null)
                {
                    _progressUI = new SvnUpdateProgressUI(progressTarget);

                    var progressRef = _progressUI;
                    _ = prefetchTask.ContinueWith(t =>
                    {
                        if (t.IsCanceled || t.IsFaulted) return;
                        if (token.IsCancellationRequested) return;
                        if (!ReferenceEquals(_progressUI, progressRef)) return;

                        var result = t.Result;
                        if (result.totalFiles > 0 && result.totalBytes > 0)
                        {
                            progressRef.SetTotal(result.totalFiles, result.totalBytes, result.sizes);
                            SVNLogBridge.LogLine(
                                "<color=#4FC3F7>[SVN Repair]</color> Progress bar switched to byte-weighted % mode.");
                        }
                    }, TaskScheduler.Default);
                }

                var checkoutArgs = $"checkout \"{url}\" \".\" --force --non-interactive --trust-server-cert";
                checkoutStarted = true;
                SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Force Repair checkout started.");

                int svnEvents = 0;
                DateTime startTime = DateTime.Now;

                await SvnRunner.RunLiveAsync(checkoutArgs, fullPath, chunk =>
                {
                    if (string.IsNullOrWhiteSpace(chunk)) return;

                    var lines = chunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line)) continue;

                        Interlocked.Increment(ref svnEvents);
                        HandleCheckoutOutput(line);
                    }
                }, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                if (!Directory.Exists(svnDir) || !File.Exists(wcDbPath))
                    throw new InvalidOperationException("SVN checkout finished, but the working copy database (.svn/wc.db) was not created.");

                _progressUI?.Finish();

                PostStep("<color=yellow><b>Verifying working copy integrity...</b></color>");
                token.ThrowIfCancellationRequested();

                workingCopyHealthy = await VerifyWorkingCopyHealthyAsync(fullPath, token).ConfigureAwait(false);
                if (!workingCopyHealthy) throw new InvalidOperationException("SVN checkout completed, but the working copy failed health verification.");

                var finalDbSize = FormatSize(new FileInfo(wcDbPath).Length);
                var elapsed = DateTime.Now - startTime;
                var finalEvents = Volatile.Read(ref svnEvents);

                await SynchronizeAppStateAfterRepair(fullPath, url, keyPath, finalDbSize, elapsed, finalEvents, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                ResetAndResumePolling();
                pollingWasPausedByUs = false;
            }
            catch (OperationCanceledException)
            {
                HandleRepairCancellation(checkoutStarted);
                if (!_metadataRemoved || workingCopyHealthy || await TryVerifyWorkingCopyHealthyWithoutCancellationAsync().ConfigureAwait(false))
                { ResetAndResumePolling(); pollingWasPausedByUs = false; }
            }
            catch (Exception ex)
            {
                HandleRepairFailure(ex);
                if (!_metadataRemoved) { ResetAndResumePolling(); pollingWasPausedByUs = false; }
            }
            finally
            {
                SetProcessingState(false);
                if (pollingWasPausedByUs)
                    LogRepairConsole("<color=#FFAA00>[Force Repair] SVN polling remains paused because the working copy could not be verified.</color>\n");
                EndRepair(repairCts);

                _progressUI = null;
            }
        }

        // ===================================================================
        //  STEP SUMMARY HELPERS — akumulacja kroków żeby Step 1/3 nie znikał.
        // ===================================================================
        private void PostStep(string line)
        {
            string snapshot;
            lock (_stepSummary)
            {
                _stepSummary.AppendLine(line);
                snapshot = _stepSummary.ToString();
            }
            PostRepairStatus(snapshot);
        }

        // ===================================================================
        //  STEP 1/3 — PRE-REPAIR SNAPSHOT
        // ===================================================================
        private async Task<SnapshotPreRepairOutcome> TryCreatePreRepairSnapshotAsync(CancellationToken repairToken)
        {
            var snapshotModule = svnManager?.GetModule<SVNSnapshot>();
            if (snapshotModule == null)
            {
                SVNLogBridge.LogToOutput(
                    "<color=#FFAA00>[SVN Repair]</color> Snapshot module not available — skipping step 1.");
                LogRepairConsole(
                    "<color=#FFAA00>[SVN Repair]</color> Snapshot module unavailable — Step 1 skipped, repair continues.\n");
                return SnapshotPreRepairOutcome.Skipped;
            }

            if (string.IsNullOrWhiteSpace(svnManager?.WorkingDir))
            {
                SVNLogBridge.LogToOutput(
                    "<color=#FFAA00>[SVN Repair]</color> Working dir not set — skipping step 1.");
                LogRepairConsole(
                    "<color=#FFAA00>[SVN Repair]</color> Working dir not set — Step 1 skipped, repair continues.\n");
                return SnapshotPreRepairOutcome.Skipped;
            }

            string wcDbPath = Path.Combine(svnManager.WorkingDir, ".svn", "wc.db");
            if (!File.Exists(wcDbPath))
            {
                SVNLogBridge.LogToOutput(
                    "<color=#FFAA00>[SVN Repair]</color> Working copy has no .svn — no local changes to snapshot. " +
                    "Step 1 skipped, repair continues.");
                LogRepairConsole(
                    "<color=#FFAA00>[SVN Repair]</color> Working copy has no .svn (broken state) — nothing to snapshot.\n");
                return SnapshotPreRepairOutcome.Skipped;
            }

            LogRepairConsole("<color=yellow>[SVN Repair]</color> Creating pre-repair safety snapshot (silent)...\n");

            bool ok;
            try
            {
                ok = await snapshotModule.CreateSnapshotForRepairAsync("Pre-Repair").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogToOutput("<color=#FFAA00>[SVN Repair]</color> Pre-repair snapshot cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN Repair] Pre-repair snapshot failed: {ex.Message}");
                LogRepairConsole($"<color=#FF8800>[SVN Repair]</color> Snapshot failed: {EscapeRichText(ex.Message)}\n");
                return SnapshotPreRepairOutcome.Failed;
            }

            if (ok)
            {
                SVNLogBridge.LogToOutput(
                    "<color=green>[SVN Repair]</color> Pre-repair snapshot created. " +
                    "<b>After repair, use the 'Restore Snapshot' button to recover your local modifications.</b>");
                LogRepairConsole(
                    "<color=green>[SVN Repair]</color> Safety snapshot created.\n" +
                    "<color=#FFCC00><b>After repair, use the 'Restore Snapshot' button to recover your local modifications.</b></color>\n");
                return SnapshotPreRepairOutcome.Created;
            }

            SVNLogBridge.LogToOutput(
                "<color=#FFAA00>[SVN Repair]</color> Pre-repair snapshot returned false — proceeding without safety net.");
            LogRepairConsole("<color=#FFAA00>[SVN Repair]</color> Snapshot not created (see log above).\n");
            return SnapshotPreRepairOutcome.Failed;
        }

        public void CancelRepair()
        {
            CancellationTokenSource cts;
            lock (_repairSync) { cts = _repairCTS; }
            if (cts == null) { LogRepairConsole("<color=yellow>[Force Repair] No active repair operation.</color>\n"); return; }
            try { if (!cts.IsCancellationRequested) { cts.Cancel(); LogRepairConsole("<color=yellow>[Force Repair] Cancellation requested...</color>\n"); } }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1) return;

            try { _progressUI?.Clear(); } catch { }
            _progressUI = null;

            CancellationTokenSource cts;
            lock (_repairSync)
            {
                cts = _repairCTS;
                _repairCTS = null;
            }

            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }

                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    try { cts.Dispose(); } catch { }
                });
            }
        }

        private bool TryBeginRepair(out CancellationTokenSource cts)
        {
            lock (_repairSync)
            {
                cts = null;
                if (_isDisposed || _repairRunning || _repairCTS != null) return false;
                cts = _repairCTS = new CancellationTokenSource();
                _repairRunning = true;
                _metadataRemoved = false;
                _preRepairSnapshotCreated = false;
                return true;
            }
        }

        private void EndRepair(CancellationTokenSource currentCts)
        {
            lock (_repairSync) { if (ReferenceEquals(_repairCTS, currentCts)) { _repairCTS = null; _repairRunning = false; } }
            _ = Task.Delay(1000).ContinueWith(_ => { try { currentCts.Dispose(); } catch { } });
        }

        private async Task<(int totalFiles, long totalBytes, Dictionary<string, long> sizes)>
            PrefetchRemoteSizesAsync(string url, CancellationToken token)
        {
            try
            {
                string runDir = !string.IsNullOrWhiteSpace(svnManager?.WorkingDir)
                    ? svnManager.WorkingDir
                    : Path.GetTempPath();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var output = await SvnRunner.RunAsync(
                    $"list -R --xml \"{SVNShared.EscapeSvnArg(url)}\"",
                    runDir,
                    retryOnLock: false,
                    token: token).ConfigureAwait(false);
                sw.Stop();

                if (string.IsNullOrWhiteSpace(output))
                    return (0, 0, null);

                int xmlStart = output.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
                if (xmlStart < 0) xmlStart = output.IndexOf("<lists", StringComparison.OrdinalIgnoreCase);
                if (xmlStart < 0) xmlStart = output.IndexOf("<list", StringComparison.OrdinalIgnoreCase);
                if (xmlStart > 0) output = output.Substring(xmlStart);

                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                long totalBytes = 0;
                int fileCount = 0;

                var doc = XDocument.Parse(output);
                foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "entry"))
                {
                    token.ThrowIfCancellationRequested();

                    string kind = (string)entry.Attribute("kind");
                    if (kind != "file") continue;

                    var nameEl = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "name");
                    if (nameEl == null) continue;

                    string path = (nameEl.Value ?? "").Replace('\\', '/').Trim();
                    if (string.IsNullOrEmpty(path)) continue;

                    long size = 0;
                    var sizeEl = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "size");
                    if (sizeEl != null && long.TryParse(sizeEl.Value, out var s)) size = s;

                    sizes[path] = size;
                    totalBytes += size;
                    fileCount++;
                }

                SVNLogBridge.LogLine(
                    $"<color=#4FC3F7>[SVN Repair]</color> Size prefetch done in {sw.Elapsed.TotalSeconds:F1}s: " +
                    $"{fileCount} file(s), ~{FormatSize(totalBytes)}.");
                return (fileCount, totalBytes, sizes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SVNLogBridge.LogWarning($"[SVN Repair] Size prefetch failed (indeterminate mode): {ex.Message}");
                return (0, 0, null);
            }
        }

        private async Task<bool> DeleteOldMetadataAsync(string svnDir, CancellationToken token)
        {
            if (!Directory.Exists(svnDir)) return true;
            try
            {
                await Task.Run(() => DeleteDirectorySecured(svnDir, token), token).ConfigureAwait(false);
                return !Directory.Exists(svnDir);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] Failed to delete .svn: {ex.Message}"); return false; }
        }

        private static void DeleteDirectorySecured(string targetDir, CancellationToken token)
        {
            if (!Directory.Exists(targetDir)) return;
            var dirs = new Stack<string>(); dirs.Push(targetDir);
            while (dirs.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = dirs.Pop();
                foreach (var f in Directory.GetFiles(current))
                {
                    token.ThrowIfCancellationRequested();
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    File.Delete(f);
                }
                var children = Directory.GetDirectories(current);
                if (children.Length > 0)
                {
                    dirs.Push(current);
                    foreach (var c in children) dirs.Push(c);
                    continue;
                }
                try { File.SetAttributes(current, FileAttributes.Normal); } catch { }
                Directory.Delete(current, false);
            }
        }

        private void HandleMetadataDeletionFailure()
        {
            var msg = "Cannot remove the old .svn folder. It is locked by another process (e.g., TortoiseSVN, IDE, Antivirus).\n\n<b>ACTION REQUIRED:</b>\n1. Close other programs that might be using this folder.\n2. Manually delete the hidden <b>.svn</b> folder from your working directory.\n3. Click <b>Force Repair</b> again to continue.";
            PostRepairStatus("<color=#FF4444><b>DELETION FAILED</b></color>\n\n" + msg);
            LogRepairConsole("<color=#FF4444><b>[Repair]</b> Failed to delete .svn. Manual deletion required.</color>\n");

            _progressUI?.Clear();
        }

        private void HandleCheckoutOutput(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var n = line.Trim();

            if (n.Length > 0 && n[0] == '\uFEFF')
                n = n.Substring(1).TrimStart();
            if (string.IsNullOrEmpty(n)) return;

            int dbg = Interlocked.Increment(ref _checkoutDebugLinesLogged);
            if (dbg <= 5)
            {
                int previewLen = Math.Min(60, n.Length);
                SVNLogBridge.LogLine(
                    $"<color={SVNColors.Dim}>[Repair Debug] checkout line #{dbg}: " +
                    $"'{n.Substring(0, previewLen)}' (len={n.Length})</color>");
            }

            if (_progressUI != null && n.Length >= 3)
            {
                char c = n[0];
                char c1 = n[1];

                bool isStatus =
                    (char.IsLetter(c) || c == '~' || c == '!') &&
                    (c1 == ' ' || c1 == '\t' || char.IsWhiteSpace(c1));

                if (isStatus)
                {
                    string path = n.Substring(1).TrimStart();
                    if (!string.IsNullOrEmpty(path))
                    {
                        path = path.Replace('\\', '/').Trim('"', '\'');
                        bool isConflict = (c == 'C');
                        _progressUI.OnItem(path, path, "", isConflict);
                    }
                }
            }

            if (n.StartsWith("Checked out revision", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Updated to revision", StringComparison.OrdinalIgnoreCase))
            {
                LogRepairConsole($"<color=green>{EscapeRichText(n)}</color>\n");
            }
        }

        private async Task<bool> VerifyWorkingCopyHealthyAsync(string fullPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var output = await SvnRunner.RunAsync("info --show-item revision", fullPath, false, token).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(output) && long.TryParse(output.Trim(), out _);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] Health check failed: {ex.Message}"); return false; }
        }

        private async Task<bool> TryVerifyWorkingCopyHealthyWithoutCancellationAsync()
        {
            try
            {
                var path = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
                if (!File.Exists(Path.Combine(path, ".svn", "wc.db"))) return false;
                return !string.IsNullOrWhiteSpace(await SvnRunner.RunAsync("info --show-item revision", path, false, CancellationToken.None).ConfigureAwait(false));
            }
            catch { return false; }
        }

        private async Task SynchronizeAppStateAfterRepair(string fullPath, string repairedUrl, string repairedKey,
            string finalDbSize, TimeSpan elapsed, int svnEvents, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var statusModule = svnManager?.GetModule<SVNStatus>();
            var svnBar = svnManager?.GetModule<SVNBar>();
            SVNStatus.ClearLockCache();
            if (svnManager != null) svnManager.DiskChangesDetected = true;

            string newWorkingDir = fullPath.Replace('\\', '/').TrimEnd('/');

            if (svnManager != null)
            {
                string currentNorm = (svnManager.WorkingDir ?? "").Replace('\\', '/').TrimEnd('/');
                if (!string.Equals(newWorkingDir, currentNorm, StringComparison.OrdinalIgnoreCase))
                {
                    SVNLogBridge.LogToOutput($"<color=yellow>[SVN Repair]</color> Working directory switched to repaired copy: {newWorkingDir}</color>");
                    svnManager.WorkingDir = newWorkingDir;
                }
                if (!string.IsNullOrWhiteSpace(repairedUrl))
                    svnManager.RepositoryUrl = repairedUrl;
                if (!string.IsNullOrWhiteSpace(repairedKey))
                {
                    svnManager.CurrentKey = repairedKey;
                    SvnRunner.KeyPath = repairedKey;
                }

                try
                {
                    ProjectSettings.AddOrUpdateProject(fullPath, (p, created) =>
                    {
                        if (created)
                        {
                            string auto = SVNProjectNaming.BuildDisplayName(repairedUrl, fullPath);
                            if (!string.IsNullOrWhiteSpace(auto))
                                p.projectName = auto;
                        }

                        if (!string.IsNullOrWhiteSpace(repairedUrl))
                            p.repoUrl = repairedUrl;
                        if (!string.IsNullOrWhiteSpace(repairedKey))
                            p.privateKeyPath = repairedKey;

                        p.lastOpened = DateTime.UtcNow;
                    });

                    var refreshed = ProjectSettings.LoadProjects().Find(p =>
                        string.Equals(
                            (p.workingDir ?? "").Replace('\\', '/').TrimEnd('/'),
                            newWorkingDir,
                            StringComparison.OrdinalIgnoreCase));

                    if (refreshed != null)
                        svnManager.ReplaceCurrentProject(refreshed);
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN Repair] Project registration failed: {ex.Message}");
                }

                string urlSnapshot = repairedUrl;
                string dirSnapshot = newWorkingDir;
                string keySnapshot = repairedKey;
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        svnManager.SynchronizeConnectionSettings(
                            SVNManager.SettingsSource.None,
                            url: urlSnapshot,
                            workingDirValue: dirSnapshot,
                            key: keySnapshot);
                    }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogErrorToOutput($"[SVN Repair] UI sync failed: {ex.Message}");
                    }
                });
            }

            string newRevision = "Unknown", newAuthor = "", commitDate = "", statusOutput = "";
            bool healthy = false;

            try
            {
                var xml = await SvnRunner.RunAsync("info --xml", fullPath, false, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(xml))
                {
                    var doc = XDocument.Parse(xml);
                    var revAttr = doc.Descendants("entry").FirstOrDefault()?.Attribute("revision");
                    if (revAttr != null && !string.IsNullOrWhiteSpace(revAttr.Value))
                    {
                        newRevision = revAttr.Value;
                        healthy = true;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] svn info failed: {ex.Message}"); }

            token.ThrowIfCancellationRequested();
            if (!healthy) throw new InvalidOperationException("Working copy verification failed after repair.");

            try { statusOutput = await SvnRunner.RunAsync("status --no-ignore", fullPath, false, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] status failed: {ex.Message}"); }

            int modifiedFiles = 0;
            if (!string.IsNullOrWhiteSpace(statusOutput))
            {
                using var sr = new StringReader(statusOutput);
                string sl;
                while ((sl = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(sl) || sl.Length < 8) continue;
                    char s = sl[0];
                    if (s == '?' || s == 'I' || s == 'X' || s == ' ') continue;
                    modifiedFiles++;
                }
            }

            if (newRevision != "Unknown")
            {
                try
                {
                    var logXml = await SvnRunner.RunAsync($"log -r {newRevision} -l 1 --xml", fullPath, false, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(logXml))
                    {
                        var logDoc = XDocument.Parse(logXml);
                        var entry = logDoc.Descendants("logentry").FirstOrDefault();
                        newAuthor = entry?.Element("author")?.Value?.Trim() ?? "";
                        var dateEl = entry?.Element("date");
                        if (dateEl != null) commitDate = dateEl.Value.Trim();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] log failed: {ex.Message}"); }
            }
            token.ThrowIfCancellationRequested();

            if (statusModule != null)
            {
                try { statusModule.ClearSVNTreeView(); statusModule.ClearCurrentData(); await statusModule.RefreshModifiedInternal().ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] status module refresh failed: {ex.Message}"); }
            }
            token.ThrowIfCancellationRequested();

            if (svnBar != null)
            {
                try
                {
                    var proj = svnManager?.CurrentProject;
                    var snap = await svnBar.BuildSnapshotAsync(proj, fullPath).ConfigureAwait(false);
                    if (snap != null)
                    {
                        snap.Revision = newRevision;
                        if (!string.IsNullOrWhiteSpace(newAuthor)) { snap.Author = newAuthor; snap.CurrentUser = newAuthor; }
                        svnManager.CurrentSnapshot = snap;
                    }
                    token.ThrowIfCancellationRequested();
                    PostToMainThread(() => { try { _ = svnBar.ShowProjectInfo(proj, fullPath, forceOutdatedCheck: true, isRefreshing: false); } catch (Exception ex) { SVNLogBridge.LogException(ex); } });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] bar update failed: {ex.Message}"); }
            }

            var report = new StringBuilder()
                .AppendLine("<color=green><b>=========================================</b></color>")
                .AppendLine("<color=green><b>     FORCE REPAIR COMPLETED!</b></color>")
                .AppendLine("<color=green><b>=========================================</b></color>")
                .AppendLine($"Duration: <b>{elapsed.TotalSeconds:F1}s</b>")
                .AppendLine($"SVN Events: <b>{svnEvents}</b>")
                .AppendLine($"Database size: <b>{finalDbSize}</b>")
                .AppendLine($"Revision: <b>{EscapeRichText(newRevision)}</b>");

            if (!string.IsNullOrWhiteSpace(newAuthor)) report.AppendLine($"Author: <b>{EscapeRichText(newAuthor)}</b>");
            if (!string.IsNullOrWhiteSpace(commitDate)) report.AppendLine($"Commit Date: <b>{EscapeRichText(commitDate)}</b>");
            if (modifiedFiles > 0) report.AppendLine($"Locally modified: <b>{modifiedFiles}</b>");

            if (!string.IsNullOrWhiteSpace(statusOutput))
            {
                var formatted = FormatCheckoutStatusLines(statusOutput);
                if (!string.IsNullOrWhiteSpace(formatted)) report.AppendLine().AppendLine("<b>[Working Copy Status]</b>").AppendLine(formatted);
            }

            if (_preRepairSnapshotCreated)
            {
                report.AppendLine();
                report.AppendLine("<color=#FFCC00><b>=========================================</b></color>");
                report.AppendLine("<color=#FFCC00><b>  !!! IMPORTANT — SAFETY SNAPSHOT READY !!!</b></color>");
                report.AppendLine("<color=#FFCC00><b>=========================================</b></color>");
                report.AppendLine();
                report.AppendLine("<color=#FFCC00><b>Your local changes were captured BEFORE the rebuild.</b></color>");
                report.AppendLine("<color=#FFCC00><b>To get them back, you MUST click the</b></color>");
                report.AppendLine("<color=#FFCC00><b>'RESTORE SNAPSHOT' button in the Snapshot panel.</b></color>");
                report.AppendLine();
                report.AppendLine("<color=#FFCC00>Repair does NOT restore them automatically.</color>");
                report.AppendLine("<color=#FFCC00>If you don't need them, click 'Delete Snapshot' instead.</color>");
                report.AppendLine("<color=#FFCC00><b>=========================================</b></color>");

                PostToMainThread(() =>
                {
                    try
                    {
                        svnUI?.ShowNotificationWithTimer(
                            "Repair done. Your local changes are in the Snapshot — " +
                            "click 'Restore Snapshot' to recover them.",
                            10f);
                    }
                    catch { }
                });
            }

            report.AppendLine("<color=green><b>=========================================</b></color>");

            PostRepairStatus(report.ToString());

            LogRepairConsole("<color=green><b>[Repair Progress]</b> Force repair completed successfully.</color>\n");

            SVNLogBridge.LogToOutput("<color=green>[SVN]</color> Force repair finished successfully.");
            SVNManager.Instance?.ProjectSelectionPanel?.RefreshList();
        }

        private string FormatCheckoutStatusLines(string rawStatus)
        {
            if (string.IsNullOrWhiteSpace(rawStatus)) return "";
            var sb = new StringBuilder();
            using var reader = new StringReader(rawStatus);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length < 8) continue;
                char status = line[0];
                if (status == 'I' || status == 'X') continue;
                var path = line.Substring(7).Trim();
                if (string.IsNullOrWhiteSpace(path) || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                try { path = SvnRunner.SanitizeSvnPath(path); } catch { }
                var color = status switch { 'A' => "#00FF41", 'D' => "#FF4444", 'M' => "#FFD700", '!' => "#FF00FF", '?' => "yellow", 'C' => "#FF8800", 'R' => "#00FFFF", _ => "#E6E6E6" };
                sb.AppendLine($"<color={color}>[{status}]</color> {EscapeRichText(path)}");
            }
            return sb.ToString();
        }

        private bool IsSafeRepairDirectory(string fullPath)
        {
            try
            {
                var norm = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Path.GetPathRoot(norm)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(root) || string.Equals(norm, root, StringComparison.OrdinalIgnoreCase)) return false;
                return !IsDangerousSystemPath(norm);
            }
            catch { return false; }
        }

        private static bool IsDangerousSystemPath(string normalizedPath)
        {
            string[] folders = { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            foreach (var folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                var protectedFull = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedPath, protectedFull, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith(protectedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void SetPollingState(bool isPaused, bool cancelCurrent)
        {
            try
            {
                var svc = SVNManager.Instance?.GetComponent<SVNPollingService>();
                if (svc == null) return;
                svc.IsPaused = isPaused;
                if (cancelCurrent) svc.CancelCurrentCheck();
            }
            catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] Polling change failed: {ex.Message}"); }
        }

        private void ResetAndResumePolling()
        {
            try
            {
                var svc = SVNManager.Instance?.GetComponent<SVNPollingService>();
                if (svc == null) return;
                svc.ResetRevisionTracking();
                svc.IsPaused = false;
            }
            catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN Repair] Polling resume failed: {ex.Message}"); }
        }

        private void PostRepairStatus(string msg) => PostToMainThread(() => { if (_isDisposed || svnUI?.CheckoutStatusInfoText == null) return; SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, msg, "SVN"); try { Canvas.ForceUpdateCanvases(); } catch { } });

        private void LogRepairConsole(string msg) => PostToMainThread(() => { try { SVNLogBridge.LogCheckoutConsole(msg); } catch (Exception ex) { SVNLogBridge.LogException(ex); } });

        private void ShowErrorSafe(string msg) => PostToMainThread(() => ShowError(msg));
        private void SetProcessingState(bool v) => IsProcessing = v;

        private void HandleRepairCancellation(bool checkoutStarted)
        {
            var checkoutNote = checkoutStarted
                ? "<i>The rebuild was interrupted — the working copy must be repaired again.</i>"
                : "<i>No SVN metadata was removed.</i>";
            var msg = "<color=#FFAA00><b>Force Repair Cancelled.</b></color>\n" + checkoutNote;
            PostRepairStatus(msg);
            LogRepairConsole("<color=#FFAA00>[Force Repair] Cancelled by user.</color>\n");
            SVNLogBridge.LogToOutput("<color=#FFAA00>[SVN]</color> Force repair cancelled.");

            if (checkoutStarted)
                _progressUI?.FinishIncomplete("CANCELLED");
            else
                _progressUI?.Clear();
        }

        private void HandleRepairFailure(Exception ex)
        {
            var message = ex?.Message ?? "Unknown error.";
            PostRepairStatus("<color=#FF4444><b>Force Repair Failed</b></color>\n\n" + EscapeRichText(message) + "\n\n" + (_metadataRemoved ? "<color=#FFAA00><b>WARNING:</b> The old .svn metadata has already been removed. Working copy may be incomplete.</color>" : ""));
            LogRepairConsole("<color=#FF4444>[Force Repair] Failed: " + EscapeRichText(message) + "</color>\n");
            SVNLogBridge.LogErrorToOutput($"[SVN] Force repair failed: {message}");

            _progressUI?.Clear();
        }

        private void HandleOperationExceptionSafe(Exception ex)
        {
            if (ex is OperationCanceledException) return;
            try { HandleRepairFailure(ex); } catch { }
        }

        private static string EscapeRichText(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}