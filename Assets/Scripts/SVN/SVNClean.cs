using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNClean : SVNBase, IDisposable
    {
        private readonly object _ctsSync = new();
        private CancellationTokenSource _cts;

        private int _disposedFlag;

        private float _lastDeepRepairClickTime = -10f;
        private float _lastDiscardUnversionedClickTime = -10f;
        private float _lastHardResetClickTime = -10f;
        private float _lastRepairStructureClickTime = -10f;

        private string _pendingDiscardFingerprint;
        private List<string> _pendingDiscardPaths;

        private const float ConfirmationWindow = 5f;
        private const float MinDoubleClickDelay = 0.30f;

        public SVNClean(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        private void LogClean(string msg, bool append = true)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.CleanText != null)
                    SVNLogBridge.UpdateUIField(svnUI.CleanText, msg, "CLEAN", append);
                else
                    SVNLogBridge.LogLine(msg, append);
            });
        }

        private void ClearLog() => LogClean(string.Empty, false);

        public void CancelCurrentOperation()
        {
            CancellationTokenSource opCts;
            lock (_ctsSync) { opCts = _cts; }

            bool cancelled = false;
            if (opCts != null) TryCancel(opCts, ref cancelled);

            LogClean(cancelled
                ? "<color=yellow>[SVN] Cancellation requested...</color>"
                : "<color=yellow>[SVN] No active operation to cancel.</color>");
        }

        public void LightCleanup() => _ = StartAsync(LightCleanupAsync);
        public void VacuumCleanup() => _ = StartAsync(VacuumCleanupAsync);

        public void DeepRepair()
        {
            if (!ConfirmAction(ref _lastDeepRepairClickTime,
                    "<color=#FFAA00><b>[Deep Repair]</b></color> Conflicts will be resolved using the SERVER version (theirs-full).\n" +
                    "Local changes in conflicted files WILL BE LOST.\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(DeepRepairAsync);
        }

        public void DiscardUnversioned()
        {
            var (selectedPaths, nonUnversionedCount) = SnapshotDiscardSelection();
            string fingerprint = BuildSelectionFingerprint(selectedPaths);

            if (_pendingDiscardFingerprint != null &&
                !string.Equals(_pendingDiscardFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _lastDiscardUnversionedClickTime = -10f;
                _pendingDiscardFingerprint = null;
                _pendingDiscardPaths = null;
            }

            bool selective = selectedPaths.Count > 0;

            string warningMessage = selective
                ? $"<color=#FFAA00><b>[Discard Unversioned]</b></color> {selectedPaths.Count} SELECTED unversioned item(s) will be <b>PERMANENTLY DELETED</b>!" +
                  (nonUnversionedCount > 0
                      ? $"\n<i>({nonUnversionedCount} other selected item(s) will be ignored — only '?' are deleted.)</i>"
                      : "") +
                  "\nPress the button again within <b>5 seconds</b> to confirm."
                : "<color=#FFAA00><b>[Discard Unversioned]</b></color> ALL unversioned files (?) will be <b>PERMANENTLY DELETED</b>!\n" +
                  "Press the button again within <b>5 seconds</b> to confirm.";

            if (!ConfirmAction(ref _lastDiscardUnversionedClickTime, warningMessage))
            {
                _pendingDiscardFingerprint = fingerprint;
                _pendingDiscardPaths = selectedPaths;
                return;
            }

            var toDelete = _pendingDiscardPaths ?? selectedPaths;
            _pendingDiscardFingerprint = null;
            _pendingDiscardPaths = null;

            _ = StartAsync(t => toDelete.Count > 0
                ? DiscardSelectedUnversionedAsync(toDelete, t)
                : DiscardUnversionedAsync(t));
        }

        public void HardReset()
        {
            if (!ConfirmAction(ref _lastHardResetClickTime,
                    "<color=#FF4444><b>[HARD RESET]</b></color> ALL local changes AND unversioned files will be <b>PERMANENTLY DELETED</b>!\n" +
                    "Working copy will be reset to HEAD.\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(HardResetAsync);
        }

        public void RepairStructure()
        {
            if (!ConfirmAction(ref _lastRepairStructureClickTime,
                    "<color=#FFAA00><b>[Repair Structure]</b></color> Working copy structure will be FORCED to match the repository.\n" +
                    "Local changes may be overwritten. Unversioned files will be removed.\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(RepairStructureAsync);
        }

        private bool ConfirmAction(ref float lastClickTime, string warningMessage)
        {
            if (!UnityMainThreadDispatcher.IsMainThread)
            {
                SVNLogBridge.LogWarning("[SVN Clean] Confirmation must be triggered from UI (main thread). Operation denied.");
                return false;
            }

            float currentTime = Time.unscaledTime;
            float elapsed = currentTime - lastClickTime;

            if (elapsed > ConfirmationWindow || lastClickTime < 0f)
            {
                lastClickTime = currentTime;
                LogClean(warningMessage);
                return false;
            }

            if (elapsed < MinDoubleClickDelay)
            {
                lastClickTime = currentTime;
                LogClean("<color=#FFAA00><b>[SVN Clean]</b></color> Confirmation too fast — press once again.");
                return false;
            }

            lastClickTime = -10f;
            return true;
        }

        private async Task StartAsync(Func<CancellationToken, Task> op)
        {
            if (op == null || Volatile.Read(ref _disposedFlag) == 1) return;

            if (!TryStart())
            {
                LogClean("<color=yellow>Another operation is already running.</color>");
                return;
            }

            CancellationTokenSource opCts = null;
            bool initFailed = false;

            lock (_ctsSync)
            {
                if (Volatile.Read(ref _disposedFlag) == 1 || _cts != null)
                {
                    initFailed = true;
                }
                else
                {
                    opCts = _cts = new CancellationTokenSource();
                }
            }

            if (initFailed)
            {
                try { End(); } catch (Exception ex) { SVNLogBridge.LogException(ex); }
                LogClean("<color=#FFAA00>Operation initialization error.</color>");
                return;
            }

            PostUIStart();
            await RunAsync(op, opCts);
        }

        private async Task RunAsync(Func<CancellationToken, Task> op, CancellationTokenSource cts)
        {
            try
            {
                cts.Token.ThrowIfCancellationRequested();
                await op(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                LogClean("<color=yellow>Cancelled.</color>");
            }
            catch (OperationCanceledException ex)
            {
                LogClean("<color=yellow>Cancelled.</color>");
                SVNLogBridge.LogException(ex);
            }
            catch (Exception ex)
            {
                LogClean($"<color=#FFAA00>Error:</color> {ex.Message}");
                SVNLogBridge.LogException(ex);
            }
            finally
            {
                lock (_ctsSync) { if (_cts == cts) _cts = null; }

                try { End(); } catch (Exception ex) { SVNLogBridge.LogException(ex); }

                try { cts.Dispose(); } catch { }

                PostUIFinish();

                if (Volatile.Read(ref _disposedFlag) == 0)
                    await RefreshStatusSafeAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1) return;

            CancellationTokenSource opCts;
            lock (_ctsSync) { opCts = _cts; _cts = null; }

            if (opCts != null)
            {
                try { opCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { opCts.Dispose(); } catch { } });
            }
        }

        // ===================================================================
        //  Operations
        // ===================================================================

        public async Task LightCleanupAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>Releasing SVN database locks...</b>");

            var output = await CleanupAsync(path, t).ConfigureAwait(false);
            t.ThrowIfCancellationRequested();

            LogClean("<color=green>Success!</color>");
            if (!string.IsNullOrWhiteSpace(output))
                LogClean(output);
        }

        public static async Task<string> CleanupAsync(string wd, CancellationToken t = default)
        {
            try { return await SvnRunner.RunAsync("cleanup", wd, false, t).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                    SVNLogBridge.LogWarning($"[SVN] Cleanup retrying with --include-externals ({ex.Message})..."));
                t.ThrowIfCancellationRequested();
                return await SvnRunner.RunAsync("cleanup --include-externals", wd, false, t).ConfigureAwait(false);
            }
        }

        public async Task VacuumCleanupAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>Starting Deep Vacuum Cleanup...</b>");

            var output = await ExecuteVacuumCleanupAsync(path, t).ConfigureAwait(false);
            t.ThrowIfCancellationRequested();

            LogClean("<color=green>Vacuum Cleanup Successful!</color>");
            if (!string.IsNullOrWhiteSpace(output))
                LogClean(output);
        }

        public static async Task<string> ExecuteVacuumCleanupAsync(string wd, CancellationToken t = default)
        {
            try
            {
                return await SvnRunner.RunAsync("cleanup --vacuum-pristines --include-externals", wd, false, t)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsInvalidOptionError(ex.Message))
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                    SVNLogBridge.LogErrorToOutput("[SVN] Vacuum unsupported, fallback."));
                t.ThrowIfCancellationRequested();
                return await SvnRunner.RunAsync("cleanup", wd, false, t).ConfigureAwait(false);
            }
        }

        private static bool IsInvalidOptionError(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;

            return message.IndexOf("invalid option", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("unrecognized option", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("E200007", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public async Task DeepRepairAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>[Deep Repair] Running diagnostics...</b>");

            await SvnRunner.RunAsync("cleanup", path, true, t).ConfigureAwait(false);

            LogClean("Synchronizing working copy...");
            try
            {
                await SvnRunner.RunAsync("update --force", path, true, t).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Synchronization failed. Repair aborted. {ex.Message}", ex);
            }

            LogClean("Resolving conflicts (theirs-full)...");
            await SvnRunner.RunAsync("resolve --accept theirs-full -R .", path, true, t).ConfigureAwait(false);

            LogClean("<color=green>Deep Repair Finished!</color>");
        }

        public async Task DiscardUnversionedAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>Removing unversioned files...</b>");

            int remaining = await RunCleanupRemoveUnversionedUntilDoneAsync(path, t).ConfigureAwait(false);
            t.ThrowIfCancellationRequested();

            if (remaining == 0)
                LogClean("<color=green>All unversioned files removed.</color>");
            else
                LogClean($"<color=#FFAA00>Completed with {remaining} unversioned item(s) remaining — some files may be locked by another process.</color>");
        }

        private async Task DiscardSelectedUnversionedAsync(List<string> selectedRelPaths, CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();

            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in selectedRelPaths)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                if (rel == ".svn-root" || rel == ".") continue;
                string n = rel.Replace('\\', '/').Trim('/');
                if (!string.IsNullOrEmpty(n)) normalized.Add(n);
            }

            if (normalized.Count == 0)
            {
                LogClean("<color=#FFAA00>No valid unversioned items selected.</color>");
                return;
            }

            var topLevel = new List<string>(normalized.Count);
            foreach (var rel in normalized)
            {
                bool hasSelectedParent = false;
                string parent = rel;
                int slash;
                while ((slash = parent.LastIndexOf('/')) > 0)
                {
                    parent = parent.Substring(0, slash);
                    if (normalized.Contains(parent)) { hasSelectedParent = true; break; }
                }
                if (!hasSelectedParent) topLevel.Add(rel);
            }

            LogClean($"<b>Verifying {topLevel.Count} selected item(s)...</b>");

            var verifiedSafe = new List<string>(topLevel.Count);
            int newlyVersioned = 0;

            foreach (var rel in topLevel)
            {
                t.ThrowIfCancellationRequested();
                bool versioned = await IsVersionedAsync(path, rel, t).ConfigureAwait(false);

                if (versioned)
                {
                    newlyVersioned++;
                    string capturedRel = rel;
                    UnityMainThreadDispatcher.Enqueue(() =>
                        SVNLogBridge.LogWarning(
                            $"[SVNClean] Skipping '{capturedRel}' — now versioned (added between confirmation clicks)."));
                }
                else
                {
                    verifiedSafe.Add(rel);
                }
            }

            if (verifiedSafe.Count == 0)
            {
                LogClean($"<color=#FFAA00>No items deleted — all {newlyVersioned} selected path(s) are now versioned.</color>");
                return;
            }

            if (newlyVersioned > 0)
                LogClean($"<color=#FFAA00>Skipped {newlyVersioned} newly-versioned item(s).</color>");

            LogClean($"<b>Removing {verifiedSafe.Count} selected unversioned item(s)...</b>");

            string rootFull = Path.GetFullPath(path);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
                rootFull += Path.DirectorySeparatorChar;

            int deleted = 0, failed = 0, skipped = 0;

            await Task.Run(() =>
            {
                foreach (var relPath in verifiedSafe)
                {
                    t.ThrowIfCancellationRequested();

                    string nativeRel = relPath.Replace('/', Path.DirectorySeparatorChar);
                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(Path.Combine(rootFull, nativeRel));
                    }
                    catch (Exception ex)
                    {
                        string capturedRel = relPath;
                        UnityMainThreadDispatcher.Enqueue(() =>
                            SVNLogBridge.LogWarning($"[SVNClean] Invalid path '{capturedRel}': {ex.Message}"));
                        failed++;
                        continue;
                    }

                    if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        string capturedRel = relPath;
                        UnityMainThreadDispatcher.Enqueue(() =>
                            SVNLogBridge.LogWarning($"[SVNClean] Refused unsafe path: {capturedRel}"));
                        skipped++;
                        continue;
                    }

                    try
                    {
                        if (Directory.Exists(fullPath))
                        {
                            ClearReadOnlyRecursive(fullPath);
                            Directory.Delete(fullPath, true);
                            deleted++;
                        }
                        else if (File.Exists(fullPath))
                        {
                            File.SetAttributes(fullPath, FileAttributes.Normal);
                            File.Delete(fullPath);
                            deleted++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        string capturedRel = relPath;
                        UnityMainThreadDispatcher.Enqueue(() =>
                            SVNLogBridge.LogWarning($"[SVNClean] Failed to delete '{capturedRel}': {ex.Message}"));
                        failed++;
                    }
                }
            }, t).ConfigureAwait(false);

            if (failed == 0 && skipped == 0)
                LogClean($"<color=green>Deleted {deleted} item(s).</color>");
            else if (failed == 0)
                LogClean($"<color=green>Deleted {deleted} item(s).</color> <color=#FFAA00>({skipped} already gone)</color>");
            else
                LogClean($"<color=#FFAA00>Deleted {deleted}, failed {failed}, skipped {skipped} — " +
                         "some files may be locked by Unity/IDE/antivirus.</color>");
        }

        private async Task<int> RunCleanupRemoveUnversionedUntilDoneAsync(string wcRoot, CancellationToken t)
        {
            const int MaxPasses = 20;

            int previousCount = int.MaxValue;
            int lastCount = int.MaxValue;

            for (int pass = 1; pass <= MaxPasses; pass++)
            {
                t.ThrowIfCancellationRequested();

                lastCount = await CountUnversionedAsync(wcRoot, t).ConfigureAwait(false);
                if (lastCount == 0) return 0;

                LogClean($"Pass {pass}/{MaxPasses}: {lastCount} unversioned item(s) remaining — running cleanup...");

                try
                {
                    await SvnRunner.RunAsync("cleanup --remove-unversioned", wcRoot, false, t).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    SVNLogBridge.LogWarning($"[SVNClean] cleanup pass {pass} warning: {ex.Message}");
                }

                int afterCount = await CountUnversionedAsync(wcRoot, t).ConfigureAwait(false);
                if (afterCount == 0) return 0;

                if (afterCount >= lastCount)
                {
                    SVNLogBridge.LogWarning(
                        $"[SVNClean] No progress after pass {pass} (still {afterCount}). " +
                        "Some files may be locked by another process (Unity, IDE, antivirus). Aborting loop.");
                    return afterCount;
                }

                previousCount = afterCount;
            }

            return await CountUnversionedAsync(wcRoot, t).ConfigureAwait(false);
        }

        private static async Task<int> CountUnversionedAsync(string wcRoot, CancellationToken t)
        {
            string output = await SvnRunner.RunAsync("status", wcRoot, false, t).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output)) return 0;

            int count = 0;
            foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.Length >= 1 && raw[0] == '?') count++;
            }
            return count;
        }

        public async Task HardResetAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>[HARD RESET]</b>");

            await SvnRunner.RunAsync("revert -R .", path, true, t).ConfigureAwait(false);

            LogClean("Removing unversioned files...");
            int remaining = await RunCleanupRemoveUnversionedUntilDoneAsync(path, t).ConfigureAwait(false);
            if (remaining > 0)
                LogClean($"<color=#FFAA00>{remaining} unversioned item(s) could not be removed.</color>");

            await SvnRunner.RunAsync("update --force --accept theirs-full", path, true, t).ConfigureAwait(false);

            LogClean("<color=#8B0000>>Hard Reset Complete.</color>");
        }

        public async Task RepairStructureAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>[Repair Structure]</b>");

            var url = (await SvnRunner.RunAsync("info --show-item url", path, true, t).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Failed to retrieve repository URL.");

            LogClean("Removing unversioned files...");
            int remaining = await RunCleanupRemoveUnversionedUntilDoneAsync(path, t).ConfigureAwait(false);
            if (remaining > 0)
                LogClean($"<color=#FFAA00>{remaining} unversioned item(s) could not be removed.</color>");

            try
            {
                await SvnRunner.RunAsync("cleanup --vacuum-pristines --non-interactive", path, true, t).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsInvalidOptionError(ex.Message))
            {
                await SvnRunner.RunAsync("cleanup --non-interactive", path, true, t).ConfigureAwait(false);
            }

            await SvnRunner.RunAsync(new[] { "switch", url, ".", "--ignore-ancestry" }, path, true, t).ConfigureAwait(false);
            await SvnRunner.RunAsync("update --set-depth infinity --force --accept theirs-full --non-interactive", path, true, t).ConfigureAwait(false);
            await SvnRunner.RunAsync("resolve --accept theirs-full -R .", path, true, t).ConfigureAwait(false);

            LogClean("<color=green>Structure repaired successfully.</color>");
        }

        private string ValidatePath()
        {
            var p = svnManager?.WorkingDir;
            if (string.IsNullOrWhiteSpace(p))
            {
                LogClean("<color=#FFAA00>Working directory is not set.</color>", false);
                return null;
            }

            try { p = Path.GetFullPath(p); }
            catch (Exception ex)
            {
                LogClean($"<color=#FFAA00>Invalid path: {ex.Message}</color>", false);
                return null;
            }

            if (!Directory.Exists(p))
            {
                LogClean("<color=#FFAA00>Directory does not exist.</color>", false);
                return null;
            }

            var cur = p;
            while (cur != null)
            {
                if (Directory.Exists(Path.Combine(cur, ".svn")))
                    return p;
                cur = Directory.GetParent(cur)?.FullName;
            }

            LogClean("<color=#FFAA00>Not a valid SVN working copy.</color>", false);
            return null;
        }

        private async Task RefreshStatusSafeAsync()
        {
            if (Volatile.Read(ref _disposedFlag) == 1 || svnManager == null) return;

            try
            {
                await svnManager.RefreshStatus(true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Refresh failed: {ex.Message}");
            }
        }

        private void PostUIStart() => UnityMainThreadDispatcher.Enqueue(() =>
        {
            try
            {
                if (svnUI?.CleanText != null)
                {
                    SVNLogBridge.UpdateUIField(
                        svnUI.CleanText,
                        "<color=yellow>Operation in progress...</color>",
                        "CLEAN",
                        append: false);
                }
            }
            catch { }
        });

        private void PostUIFinish()
        {
           
        }

        private static void TryCancel(CancellationTokenSource cts, ref bool flag)
        {
            try
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    flag = true;
                }
            }
            catch (ObjectDisposedException) { }
        }

        // ===================================================================
        //  Selective Discard — helpers
        // ===================================================================

        private (List<string> selectedUnversioned, int nonUnversionedSelected) SnapshotDiscardSelection()
        {
            var statusModule = svnManager?.GetModule<SVNStatus>();
            if (statusModule == null)
            {
                SVNLogBridge.LogWarning(
                    "[SVNClean] SVNStatus module unavailable — falling back to global unversioned discard. " +
                    "Selective discard disabled.");
                return (new List<string>(), 0);
            }

            var currentData = statusModule.GetCurrentData();
            if (currentData == null || currentData.Count == 0)
                return (new List<string>(), 0);

            var paths = new List<string>();
            int nonUnv = 0;

            foreach (var e in currentData)
            {
                if (!e.IsChecked || string.IsNullOrEmpty(e.FullPath)) continue;
                if (e.FullPath == ".svn-root" || e.FullPath == ".") continue;

                if (e.Status == "?")
                    paths.Add(e.FullPath);
                else
                    nonUnv++;
            }

            return (paths, nonUnv);
        }

        private static string BuildSelectionFingerprint(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return "";

            var sorted = new List<string>(paths);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new System.Text.StringBuilder(sorted.Count * 32);
            foreach (var p in sorted) sb.Append(p).Append('\n');
            return sb.ToString();
        }

        private static async Task<bool> IsVersionedAsync(string root, string relPath, CancellationToken t)
        {
            try
            {
                string kind = await SvnRunner.RunAsync(
                    $"info --show-item kind \"{relPath}\"", root, false, t).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(kind)) return false;

                var k = kind.Trim();
                return k == "file" || k == "dir";
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return false;
            }
        }

        private static void ClearReadOnlyRecursive(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var attrs = File.GetAttributes(f);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(dir))
                    ClearReadOnlyRecursive(d);
            }
            catch { }
        }
    }
}