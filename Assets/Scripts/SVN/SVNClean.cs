using System;
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
        private volatile bool _isDisposed;

        // === FIX (dialog → double-click): potwierdzenia jak w SVNRevert/SVNUpdate/
        // SVNBranchTag/SVNMerge. Pola per operacja; Time.unscaledTime czytane
        // WYŁĄCZNIE na main thread (publiczne metody wołane z przycisków UI,
        // przed pierwszym await). Zero dialogów — działa identycznie w edytorze
        // i player-buildzie (poprzedni fallback w buildzie auto-zwracał false
        // = "Cancelled by user" dla każdej operacji destrukcyjnej).
        private float _lastDeepRepairClickTime = -10f;
        private float _lastDiscardUnversionedClickTime = -10f;
        private float _lastHardResetClickTime = -10f;
        private float _lastRepairStructureClickTime = -10f;

        private const float ConfirmationWindow = 5f;
        private const float MinDoubleClickDelay = 0.30f;

        public SVNClean(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        // === FIX (log przez dispatcher — wołane z puli wątków):
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

        // === Entry points: confirm NA MAIN THREAD (przed pierwszym await),
        // potem operacja asynchroniczna. Zero dialogów.
        public void LightCleanup() => _ = StartAsync(LightCleanupAsync, requireConfirm: false);
        public void VacuumCleanup() => _ = StartAsync(VacuumCleanupAsync, requireConfirm: false);

        public void DeepRepair()
        {
            if (!ConfirmAction(ref _lastDeepRepairClickTime,
                    "<color=#FFAA00><b>[Deep Repair]</b></color> Conflicts will be resolved using the SERVER version (theirs-full).\n" +
                    "Local changes in conflicted files WILL BE LOST.\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(DeepRepairAsync, requireConfirm: false);
        }

        public void DiscardUnversioned()
        {
            if (!ConfirmAction(ref _lastDiscardUnversionedClickTime,
                    "<color=#FFAA00><b>[Discard Unversioned]</b></color> ALL unversioned files (?) will be <b>PERMANENTLY DELETED</b>!\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(DiscardUnversionedAsync, requireConfirm: false);
        }

        public void HardReset()
        {
            if (!ConfirmAction(ref _lastHardResetClickTime,
                    "<color=#FF4444><b>[HARD RESET]</b></color> ALL local changes AND unversioned files will be <b>PERMANENTLY DELETED</b>!\n" +
                    "Working copy will be reset to HEAD.\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(HardResetAsync, requireConfirm: false);
        }

        public void RepairStructure()
        {
            if (!ConfirmAction(ref _lastRepairStructureClickTime,
                    "<color=#FFAA00><b>[Repair Structure]</b></color> Working copy structure will be FORCED to match the repository.\n" +
                    "Local changes may be overwritten. Unversioned files will be removed.\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(RepairStructureAsync, requireConfirm: false);
        }

        // === Wzorzec double-click (spójny z SVNRevert.ConfirmAction).
        // Wołane WYŁĄCZNIE z main thread (publiczne metody = przyciski UI).
        private bool ConfirmAction(ref float lastClickTime, string warningMessage)
        {
            // Guard: jeśli wołane z puli (nie z przycisku) — odmowa (fail-safe).
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

        // === FIX (kaskada): StartAsync czeka na RunAsync; cleanup należy WYŁĄCZNIE
        // do RunAsync. Kolejność w finally: _cts=null PRZED End() (niezmiennik
        // "_cts != null ⟹ IsProcessing").
        private async Task StartAsync(Func<CancellationToken, Task> op, bool requireConfirm)
        {
            if (op == null || _isDisposed) return;

            // requireConfirm obsłużony w publicznych metodach (double-click, main thread).
            // Parametr zostaje dla kompatybilności sygnatury.

            if (_isDisposed) return;

            if (!TryStart())
            {
                LogClean("<color=yellow>Another operation is already running.</color>");
                return;
            }

            CancellationTokenSource opCts;
            lock (_ctsSync)
            {
                if (_isDisposed || _cts != null)
                {
                    End();
                    LogClean("<color=#FFAA00>Operation initialization error.</color>");
                    return;
                }

                opCts = _cts = new CancellationTokenSource();
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

                // Dispose bezpieczny: _cts zdjęte pod lockiem (Cancel nie sięgnie),
                // operacja zakończona (token nieużywany), refresh niżej własnym tokenem.
                try { cts.Dispose(); } catch { }

                PostUIFinish();

                if (!_isDisposed)
                    await RefreshStatusSafeAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            CancellationTokenSource opCts;
            lock (_ctsSync) { opCts = _cts; _cts = null; }

            if (opCts != null)
            {
                try { opCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { opCts.Dispose(); } catch { } });
            }
        }

        // ===================================================================
        //  Operacje
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
                    SVNLogBridge.LogErrorToOutput($"[SVN] Cleanup failed: {ex.Message}"));
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
            catch (Exception ex) when (
                ex.Message.IndexOf("invalid option", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ex.Message.IndexOf("unrecognized option", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                    SVNLogBridge.LogErrorToOutput("[SVN] Vacuum unsupported, fallback."));
                t.ThrowIfCancellationRequested();
                return await SvnRunner.RunAsync("cleanup", wd, false, t).ConfigureAwait(false);
            }
        }

        public async Task DeepRepairAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>[Deep Repair] Running diagnostics...</b>");

            await SvnRunner.RunAsync("cleanup", path, true, t).ConfigureAwait(false);

            LogClean("Synchronizing working copy...");
            try { await SvnRunner.RunAsync("update --force", path, true, t).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                LogClean("<color=#FFAA00>Synchronization failed. Repair aborted.</color>");
                throw;
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

            await SvnRunner.RunAsync("cleanup --remove-unversioned", path, false, t).ConfigureAwait(false);

            LogClean("<color=green>Unversioned files removed successfully.</color>");
        }

        public async Task HardResetAsync(CancellationToken t)
        {
            var path = ValidatePath();
            if (path == null) return;

            ClearLog();
            LogClean("<b>[HARD RESET]</b>");

            await SvnRunner.RunAsync("revert -R .", path, true, t).ConfigureAwait(false);
            await SvnRunner.RunAsync("cleanup --remove-unversioned --include-externals", path, true, t).ConfigureAwait(false);
            await SvnRunner.RunAsync("update --force --accept theirs-full", path, true, t).ConfigureAwait(false);

            LogClean("<color=orange>Hard Reset Complete.</color>");
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

            await SvnRunner.RunAsync("cleanup --remove-unversioned --vacuum-pristines --non-interactive", path, true, t).ConfigureAwait(false);
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
            catch { return null; }

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
            if (_isDisposed || svnManager == null) return;

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
            // celowo puste (utrzymane dla symetrii — patrz review)
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
    }
}