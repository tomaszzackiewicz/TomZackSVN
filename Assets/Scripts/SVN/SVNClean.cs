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

        // === FIX: atomowa flaga (było: volatile bool check-then-set — benign
        // race przy podwójnym Dispose; wzorzec Interlocked jak w reszcie modułów).
        private int _disposedFlag;   // 0/1

        // === FIX (dialog → double-click): potwierdzenia jak w SVNRevert/SVNUpdate/
        // SVNBranchTag/SVNMerge. Pola per operacja; Time.unscaledTime czytane
        // WYŁĄCZNIE na main thread (publiczne metody wołane z przycisków UI,
        // przed pierwszym await). Zero dialogów — działa identycznie w edytorze
        // i player-buildzie.
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
            if (!ConfirmAction(ref _lastDiscardUnversionedClickTime,
                    "<color=#FFAA00><b>[Discard Unversioned]</b></color> ALL unversioned files (?) will be <b>PERMANENTLY DELETED</b>!\n" +
                    "Press the button again within <b>5 seconds</b> to confirm."))
                return;

            _ = StartAsync(DiscardUnversionedAsync);
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

        // === Wzorzec double-click (spójny z SVNRevert.ConfirmAction).
        // Wołane WYŁĄCZNIE z main thread — jawnie odmawia z puli (fail-safe).
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

        // === FIX (kaskada): StartAsync czeka na RunAsync; cleanup należy WYŁĄCZNIE
        // do RunAsync. Kolejność w finally: _cts=null PRZED End() (niezmiennik
        // "_cts != null ⟹ IsProcessing").
        //
        // === FIX (End pod lockiem): wyjście z _ctsSync PRZED wywołaniem End().
        // End() w SVNBase dziś nie dotyka _ctsSync, ale to kontrakt niejawny —
        // każda przyszła zmiana w SVNBase (np. dodanie logu wołającego callback)
        // mogłaby wywołać deadlock. Bezpieczniej: trzymać lock tylko na czas
        // mutacji _cts.
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

            // === End() POZA lockiem — patrz komentarz wyżej.
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

                // Dispose bezpieczny: _cts zdjęte pod lockiem (Cancel nie sięgnie),
                // operacja zakończona (token nieużywany), refresh niżej własnym guardem.
                try { cts.Dispose(); } catch { }

                PostUIFinish();

                if (Volatile.Read(ref _disposedFlag) == 0)
                    await RefreshStatusSafeAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            // === FIX: atomowo (Interlocked) — check-then-set na volatile bool
            // miał benign race przy podwójnym Dispose
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
                // === FIX: WARNING zamiast ERROR — to próba, nie porażka; sukces
                // retry zaloguje "Success!". Wcześniej user widział sekwencję
                // "Cleanup failed..." → "Success!" w jednej operacji.
                // (Retry po KAŻDYM błędzie jest naciągany — sensowny głównie
                // dla błędów externals; zostaje, z komunikatem nie mylącym.)
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

        /// <summary>
        /// Vacuum z fallbackiem dla klientów SVN &lt; 1.10 (brak --vacuum-pristines).
        ///
        /// === FIX (locale): dopasowanie po 'invalid option' / 'unrecognized option'
        /// łapie tylko angielskie komunikaty SVN. Na polskim systemie SVN zwraca
        /// "nieprawidłowa opcja" — fallback nie odpalał, user dostawał twardy błąd.
        /// Dodano kod błędu SVN (E200007), który jest niezależny od locale.
        ///
        /// (UWAGA: fallback 'invalid option' jest TYLKO tu — HardReset/RepairStructure
        /// używają --remove-unversioned/--vacuum-pristines bez fallbacku; na kliencie
        /// &lt; 1.10 padną twardym błędem, co łapie RunAsync jako "Error". Świadome.)
        /// </summary>
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

        /// <summary>
        /// Detekcja błędu "nieznana opcja" — odporny na locale.
        /// - angielski:  "invalid option" / "unrecognized option"
        /// - kod SVN:    E200007 (niezależny od języka)
        /// </summary>
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
                // === FIX (podwójny log): komunikat wrzucamy DO wyjątku, żeby
                // RunAsync (catch Exception) zalogował go raz — bez duplikatu
                // "Synchronization failed." + "Error: svn: ...".
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

            // === FIX: było ciche return null (pozostałe ścieżki walidacji
            // logują — niespójność)
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
            // celowo puste (utrzymane dla symetrii — status końcowy logują operacje)
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