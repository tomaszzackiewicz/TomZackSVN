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
        private CancellationTokenSource _repairCTS;
        private volatile bool _isDisposed;
        private volatile bool _repairRunning;
        private volatile bool _metadataRemoved;

        public SVNRepoRepair(SVNUI svnUI, SVNManager manager) : base(svnUI, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        public async void ForceRepairWorkingCopy()
        {
            try { await ForceRepairWorkingCopyAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { HandleOperationExceptionSafe(ex); }
        }

        public async Task ForceRepairWorkingCopyAsync()
        {
            if (_isDisposed) { ShowErrorSafe("SVN Repo Repair module has already been disposed."); return; }
            if (!TryBeginRepair(out var repairCts)) { ShowErrorSafe("A repository repair operation is already running."); return; }

            bool pollingWasPausedByUs = false;
            bool checkoutStarted = false;
            bool workingCopyHealthy = false;

            try
            {
                var token = repairCts.Token;
                var path = svnUI?.CheckoutDestFolderInput?.text?.Trim();
                var url = svnUI?.CheckoutRepoUrlInput?.text?.Trim().TrimEnd('/');

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

                PostRepairStatus("<color=yellow><b>Initializing Force Repair...</b></color>");

                var keyPath = ResolveAndValidateKeyPath();
                var sshConfig = BuildSshConfigOption(keyPath);
                if (!string.IsNullOrWhiteSpace(sshConfig) && !sshConfig.StartsWith(" ")) sshConfig = " " + sshConfig;

                PostRepairStatus("<color=orange><b>Step 1/2:</b> Removing old .svn metadata...</color>\n<size=11><i>Please wait...</i></size>");

                SVNManager.Instance?.DisposeFileSystemWatcher();
                token.ThrowIfCancellationRequested();

                if (!await DeleteOldMetadataAsync(svnDir, token).ConfigureAwait(false))
                {
                    HandleMetadataDeletionFailure();
                    pollingWasPausedByUs = false;
                    return;
                }
                _metadataRemoved = true;
                token.ThrowIfCancellationRequested();

                PostRepairStatus("<color=green><b>Step 1/2:</b> Old metadata removed successfully.</color>\n<color=yellow><b>Step 2/2:</b> Rebuilding working copy...</color>\n<size=11><i>See console below for live progress.</i></size>");

                var checkoutArgs = $"checkout \"{url}\" \".\" --force --non-interactive --trust-server-cert{sshConfig}";
                checkoutStarted = true;
                SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Force Repair checkout started.");

                int svnEvents = 0;
                long lastReportedDbSize = -1;
                DateTime startTime = DateTime.Now;

                using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);

                var monitorTask = MonitorProgressAsync(svnDir, wcDbPath,
                    () => Volatile.Read(ref svnEvents),
                    () => lastReportedDbSize,
                    v => lastReportedDbSize = v,
                    () => (DateTime.Now - startTime).TotalSeconds,
                    monitorCts.Token);

                try
                {
                    await SvnRunner.RunLiveAsync(checkoutArgs, fullPath, line =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;

                        Interlocked.Increment(ref svnEvents);

                        HandleCheckoutOutput(line.Trim());
                    }, token).ConfigureAwait(false);
                }
                finally
                {
                    monitorCts.Cancel();
                    try { await monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch { }
                }

                token.ThrowIfCancellationRequested();

                if (!Directory.Exists(svnDir) || !File.Exists(wcDbPath))
                    throw new InvalidOperationException("SVN checkout finished, but the working copy database (.svn/wc.db) was not created.");

                LogRepairConsole("<color=yellow><b>Verifying working copy integrity...</b></color>\n");

                token.ThrowIfCancellationRequested();

                workingCopyHealthy = await VerifyWorkingCopyHealthyAsync(fullPath, token).ConfigureAwait(false);
                if (!workingCopyHealthy) throw new InvalidOperationException("SVN checkout completed, but the working copy failed health verification.");

                var finalDbSize = FormatSize(new FileInfo(wcDbPath).Length);
                var elapsed = DateTime.Now - startTime;
                var finalEvents = Volatile.Read(ref svnEvents);

                await SynchronizeAppStateAfterRepair(fullPath, finalDbSize, elapsed, finalEvents, token).ConfigureAwait(false);
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
            }
        }

        public void CancelRepair()
        {
            CancellationTokenSource cts;
            lock (_repairSync) { cts = _repairCTS; }
            if (cts == null) { LogRepairConsole("<color=#888888>[Force Repair] No active repair operation.</color>\n"); return; }
            try { if (!cts.IsCancellationRequested) { cts.Cancel(); LogRepairConsole("<color=yellow>[Force Repair] Cancellation requested...</color>\n"); } }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            CancellationTokenSource cts;
            lock (_repairSync) { cts = _repairCTS; }
            if (cts != null) try { cts.Cancel(); } catch (ObjectDisposedException) { }
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
                return true;
            }
        }

        private void EndRepair(CancellationTokenSource currentCts)
        {
            lock (_repairSync) { if (ReferenceEquals(_repairCTS, currentCts)) { _repairCTS = null; _repairRunning = false; } }
            try { currentCts.Dispose(); } catch (Exception ex) { SVNLogBridge.LogException(ex); }
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
        }

        private async Task MonitorProgressAsync(string svnDir, string wcDbPath,
            Func<int> getSvnEvents,
            Func<long> getLastSize, Action<long> setLastSize,
            Func<double> getElapsedSeconds, CancellationToken token)
        {
            var sb = new StringBuilder(256);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    double elapsed = Math.Max(getElapsedSeconds(), 1);
                    int events = getSvnEvents();

                    sb.Clear();
                    sb.Append("<b>[Repair Progress]</b> Rebuilding...\n");
                    sb.Append("<b>Time Elapsed:</b> ").AppendFormat("{0:F1}s", elapsed).Append('\n');

                    sb.Append("<b>SVN Events:</b> ").Append(events).Append('\n');

                    if (File.Exists(wcDbPath))
                    {
                        var size = new FileInfo(wcDbPath).Length;
                        if (size != getLastSize()) setLastSize(size);
                        sb.Append("<b>Database Size:</b> <b>").Append(FormatSize(size)).Append("</b>");
                    }
                    else if (Directory.Exists(svnDir))
                    {
                        sb.Append("<b>Database Size:</b> <i>initializing...</i>");
                    }

                    string currentText = sb.ToString();
                    PostToMainThread(() =>
                    {
                        try { SVNLogBridge.LogCheckoutConsole(currentText + "\n"); } catch { }
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                try { await Task.Delay(1000, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        private void HandleCheckoutOutput(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var n = line.Trim();

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

        private async Task SynchronizeAppStateAfterRepair(string fullPath, string finalDbSize, TimeSpan elapsed, int svnEvents, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var statusModule = svnManager?.GetModule<SVNStatus>();
            var svnBar = svnManager?.GetModule<SVNBar>();
            SVNStatus.ClearLockCache();
            if (svnManager != null) svnManager.DiskChangesDetected = true;

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
                    if (s == '?') continue;
                    if (s != ' ') modifiedFiles++;
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
                var path = line.Substring(7).Trim();
                if (string.IsNullOrWhiteSpace(path) || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                try { path = SvnRunner.NormalizeRepositoryPath(path); } catch { }
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
            var msg = "<color=#FFAA00><b>Force Repair Cancelled.</b></color>\n" + (_metadataRemoved ? "<i>SVN metadata was removed. The working copy must be repaired again.</i>" : "<i>No SVN metadata was removed.</i>");
            PostRepairStatus(msg);
            LogRepairConsole("<color=#FFAA00>[Force Repair] Cancelled by user.</color>\n");
            SVNLogBridge.LogToOutput("<color=#FFAA00>[SVN]</color> Force Repair cancelled.");
        }

        private void HandleRepairFailure(Exception ex)
        {
            var message = ex?.Message ?? "Unknown error.";
            PostRepairStatus("<color=#FF4444><b>Force Repair Failed</b></color>\n\n" + EscapeRichText(message) + "\n\n" + (_metadataRemoved ? "<color=#FFAA00><b>WARNING:</b> The old .svn metadata has already been removed. Working copy may be incomplete.</color>" : ""));
            LogRepairConsole("<color=#FF4444>[Force Repair] Failed: " + EscapeRichText(message) + "</color>\n");
            SVNLogBridge.LogErrorToOutput($"[SVN] Force repair failed: {message}");
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