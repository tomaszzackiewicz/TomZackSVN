using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNClean : SVNBase, IDisposable
    {
        private readonly object _ctsSync = new();
        private CancellationTokenSource _cts;
        private CancellationTokenSource _confirmationCts;
        private volatile bool _isDisposed;

        public SVNClean(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        // === FIX K2: pole UI wyłącznie przez dispatcher — LogClean wołany jest z
        // thread poolu (operacje po ConfigureAwait(false), catch w RunAsync).
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
            CancellationTokenSource opCts, confCts;
            lock (_ctsSync) { opCts = _cts; confCts = _confirmationCts; }

            bool cancelled = false;
            if (opCts != null) TryCancel(opCts, ref cancelled);
            if (confCts != null) TryCancel(confCts, ref cancelled);

            LogClean(cancelled
                ? "<color=yellow>[SVN] Cancellation requested...</color>"
                : "<color=yellow>[SVN] No active operation to cancel.</color>");
        }

        public void LightCleanup() => _ = StartAsync(LightCleanupAsync, false);
        public void VacuumCleanup() => _ = StartAsync(VacuumCleanupAsync, false);
        public void DeepRepair() => _ = StartAsync(DeepRepairAsync, true);
        public void DiscardUnversioned() => _ = StartAsync(DiscardUnversionedAsync, true);
        public void HardReset() => _ = StartAsync(HardResetAsync, true);
        public void RepairStructure() => _ = StartAsync(RepairStructureAsync, true);

        // === FIX K1: przebudowane. Wcześniej '_ = RunAsync(...)' (fire-and-forget)
        // + teardown w finally StartAsync → finally wykonywał się natychmiast po
        // pierwszym await OPERACJI: nullował _cts (Cancel martwy), zdjmował
        // IsProcessing (kolejne operacje startowały równolegle) i DISPO NOWAŁ CTS,
        // którego token używała trwająca operacja (ODE w środku SvnRunner).
        // Teraz: StartAsync czeka na RunAsync; cleanup należy WYŁĄCZNIE do RunAsync.
        private async Task StartAsync(Func<CancellationToken, Task> op, bool confirm)
        {
            if (op == null || _isDisposed) return;

            if (confirm)
            {
                CancellationTokenSource confCts;
                lock (_ctsSync)
                {
                    if (_confirmationCts != null)
                    {
                        LogClean("<color=yellow>Awaiting confirmation...</color>");
                        return;
                    }
                    if (_isDisposed) return;
                    confCts = _confirmationCts = new CancellationTokenSource();
                }

                bool confirmed = false;
                try
                {
                    confirmed = await RequireConfirmationAsync(GetTitle(op), GetMessage(op), confCts.Token);
                    confCts.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    LogClean("<color=yellow>Cancelled.</color>");
                    return;
                }
                catch (Exception ex)
                {
                    LogClean($"<color=#FFAA00>Confirmation error: {ex.Message}</color>");
                    return;
                }
                finally
                {
                    lock (_ctsSync) { if (_confirmationCts == confCts) _confirmationCts = null; }
                    _ = Task.Delay(1000).ContinueWith(_ => { try { confCts.Dispose(); } catch { } });
                }

                if (!confirmed)
                {
                    LogClean("<color=yellow>Cancelled by user.</color>");
                    return;
                }
            }
            else
            {
                lock (_ctsSync)
                {
                    if (_confirmationCts != null)
                    {
                        LogClean("<color=yellow>Another operation is awaiting confirmation.</color>");
                        return;
                    }
                }
            }

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
                    // Obrona: niezmiennik "_cts != null ⟹ IsProcessing" naruszony.
                    End();
                    LogClean("<color=#FFAA00>Operation initialization error.</color>");
                    return;
                }

                opCts = _cts = new CancellationTokenSource();
            }

            PostUIStart();

            // Własność opCts przechodzi na RunAsync — on (i tylko on) sprząta.
            await RunAsync(op, opCts).ConfigureAwait(false);
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
                LogClean($"<color=#FFAA00>Error: {ex.Message}</color>");
                SVNLogBridge.LogException(ex);
            }
            finally
            {
                // === FIX K1 (kolejność): _cts = null PRZED End() — dzięki temu
                // niezmiennik "_cts != null ⟹ IsProcessing" trwa przez cały cykl
                // i obronna ścieżka w StartAsync prawie nigdy nie odpala.
                lock (_ctsSync) { if (_cts == cts) _cts = null; }

                try { End(); } catch (Exception ex) { SVNLogBridge.LogException(ex); }

                // Dispose BEZPIECZNY: _cts zdjęte pod lockiem (Cancel go nie dostanie),
                // operacja zakończona (token nieużywany), refresh niżej własnym tokenem.
                try { cts.Dispose(); } catch { }

                if (!_isDisposed)
                    await RefreshStatusSafeAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            CancellationTokenSource opCts, confCts;
            lock (_ctsSync) { opCts = _cts; confCts = _confirmationCts; _cts = null; _confirmationCts = null; }

            // === FIX: delayed dispose (wcześniej cancel-only — CTS-y przeciekały).
            if (opCts != null)
            {
                try { opCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { opCts.Dispose(); } catch { } });
            }
            if (confCts != null)
            {
                try { confCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { confCts.Dispose(); } catch { } });
            }
        }

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

            LogClean("Resolving conflicts...");
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

        private async Task<bool> RequireConfirmationAsync(string title, string msg, CancellationToken t)
        {
            t.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource<bool>();
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try
                {
#if UNITY_EDITOR
                    bool result = UnityEditor.EditorUtility.DisplayDialog(title, msg, "Yes", "No");
                    tcs.TrySetResult(result);
#else
                    // === FIX K3: w buildzie Player brak dialogu modalnego — stary
                    // fallback auto-klikał "TAK", czyli HARD RESET ("ALL LOCAL
                    // CHANGES... PERMANENTLY DELETED!") wykonywał się BEZ pytania.
                    // Safe default = odmowa (użytkownik może użyć operacji
                    // niedestrukcyjnych albo uruchomić w edytorze).
                    SVNLogBridge.LogErrorToOutput(
                        "[SVN Clean] Confirmation dialog is editor-only — destructive operation denied in player build.");
                    tcs.TrySetResult(false);
#endif
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            using (t.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task.ConfigureAwait(false);
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

        private string GetTitle(Func<CancellationToken, Task> op) => op switch
        {
            var x when x == DeepRepairAsync => "Deep Repair",
            var x when x == HardResetAsync => "HARD RESET",
            var x when x == DiscardUnversionedAsync => "Discard Unversioned",
            var x when x == RepairStructureAsync => "Repair Structure",
            _ => "Confirmation"
        };

        private string GetMessage(Func<CancellationToken, Task> op) => op switch
        {
            var x when x == DeepRepairAsync => "Conflicts will be resolved using the server version. Continue?",
            var x when x == HardResetAsync => "ALL LOCAL CHANGES AND UNVERSIONED FILES WILL BE PERMANENTLY DELETED!",
            var x when x == DiscardUnversionedAsync => "Unversioned files will be permanently deleted.",
            var x when x == RepairStructureAsync => "Working copy structure will be forced, local changes might be overwritten.",
            _ => "Are you sure?"
        };

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