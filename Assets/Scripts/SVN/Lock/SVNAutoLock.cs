using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    /// <summary>
    /// MODUŁ: AutoLock — automatyczne lockowanie zapisanych plików.
    ///
    /// === PERSISTENCJA per project: stan ON/OFF zapamiętywany w PlayerPrefs
    /// pod kluczem pochodnym od ścieżki working copy. Po załadowaniu projektu
    /// moduł SAM odczytuje ustawienie i się uzbraja — user włącza raz i nigdy
    /// więcej o tym nie myśli. Podpinamy się pod istniejący event
    /// SVNManager.OnProjectChanged (zero zmian w managerze).
    /// </summary>
    public class SVNAutoLock : SVNBase
    {
        // ================== KONFIGURACJA ==================
        private const int FlagCheckMs = 500;             // check flagi dysku
        private const int FastTriggerCooldownMs = 5000;  // min. odstęp polli
        private const int FallbackIntervalMs = 15000;    // pełny przegląd co 15 s
        private const int SvnTimeoutMs = 60000;          // twardy limit komendy svn
        private const int BusyWaitMaxMs = 15000;         // max czekania na wolny SVN
        private const int LogThrottleMs = 60000;         // ten sam log max raz/min

        private int _enabledFlag;
        private int _loopRunning;
        private CancellationTokenSource _loopCts;

        private DateTime _lastPollUtc = DateTime.MinValue;
        private DateTime _lastOtherLockLogUtc = DateTime.MinValue;
        private DateTime _lastErrorLogUtc = DateTime.MinValue;

        public SVNAutoLock(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager)
        {
            // === PERSISTENCJA: po każdym załadowaniu projektu sprawdzamy
            // zapamiętane ustawienie i ewentualnie uzbrajamy pętlę.
            if (svnManager != null)
                svnManager.OnProjectChanged += OnProjectChangedHandler;
        }

        private void OnProjectChangedHandler(SVNProject project)
        {
            if (project == null || string.IsNullOrEmpty(project.workingDir)) return;

            string dir = project.workingDir;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                // === FIX (stale-handler): jeśli w międzyczasie przełączono projekt
                // ponownie, ten callback dotyczy STAREGO projektu — ignorujemy całkiem
                // (nie aplikujemy stanu, nie logujemy, nie persistujemy pod zły klucz).
                string current = svnManager?.WorkingDir?.Replace('\\', '/').TrimEnd('/');
                string target = dir.Replace('\\', '/').TrimEnd('/');
                if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                    return;

                // === FIX (mylące stare komunikaty): panel logów Locków czyszczony
                // przy zmianie projektu — komunikaty nie "przeciekają" między projektami.
                if (svnUI?.LockDisplayArea != null)
                    SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, "", append: false);

                bool persisted = LoadPersisted(dir);
                if (Enabled != persisted)
                    Enabled = persisted;   // setter: start/stop pętli + zapis

                // Jawny stan dla NOWEGO projektu (również gdy OFF — bez ciszy):
                if (persisted)
                    LogToLockPanel("<color=#00E5FF>[AutoLock]</color> Enabled (remembered for this project).");
                else
                    LogToLockPanel("<color=#00E5FF>[AutoLock]</color> OFF for this project.");
            });
        }

        public bool Enabled
        {
            get => Interlocked.CompareExchange(ref _enabledFlag, 0, 0) == 1;
            set
            {
                // Jedyna rzecz na wątku UI: flip flagi + start/stop pętli w tle.
                Interlocked.Exchange(ref _enabledFlag, value ? 1 : 0);
                if (value) StartLoop();
                else _loopCts?.Cancel();

                Persist(value);   // === zapamiętanie per project
            }
        }

        // ================== PERSISTENCJA ==================

        // Klucz = "SVN_AutoLock_" + ścieżka working copy → każdy projekt ma
        // własne, niezależne ustawienie.
        private static string PrefsKey(string workingDir) =>
            "SVN_AutoLock_" + (workingDir ?? "").Replace('\\', '/').TrimEnd('/').ToUpperInvariant();

        private static bool LoadPersisted(string workingDir)
        {
            try
            {
                if (string.IsNullOrEmpty(workingDir)) return false;
                return PlayerPrefs.GetInt(PrefsKey(workingDir), 0) == 1;
            }
            catch { return false; }
        }

        private void Persist(bool value)
        {
            try
            {
                string dir = svnManager?.WorkingDir;
                if (string.IsNullOrEmpty(dir)) return;

                if (UnityMainThreadDispatcher.IsMainThread)
                {
                    PlayerPrefs.SetInt(PrefsKey(dir), value ? 1 : 0);
                    PlayerPrefs.Save();
                }
                else
                {
                    string snapshot = dir;
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        PlayerPrefs.SetInt(PrefsKey(snapshot), value ? 1 : 0);
                        PlayerPrefs.Save();
                    });
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError("[AutoLock] Persist failed: " + ex.Message);
            }
        }

        // ================== PĘTLA (bez zmian względem działającej wersji) ==================

        private void StartLoop()
        {
            if (Interlocked.Exchange(ref _loopRunning, 1) == 1) return;

            _loopCts?.Cancel();
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;

            _ = Task.Run(() => LoopAsync(token));
        }

        private async Task LoopAsync(CancellationToken token)
        {
            DateTime lastFallbackUtc = DateTime.UtcNow;

            try
            {
                while (Enabled && !token.IsCancellationRequested)
                {
                    bool diskActivity = svnManager.DiskChangesDetected;
                    bool fallbackDue = (DateTime.UtcNow - lastFallbackUtc).TotalMilliseconds >= FallbackIntervalMs;

                    if ((diskActivity || fallbackDue) && CooldownPassed())
                    {
                        bool ran = await TryPollAsync(token).ConfigureAwait(false);
                        if (ran)
                            lastFallbackUtc = DateTime.UtcNow;
                    }

                    try { await Task.Delay(FlagCheckMs, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _loopRunning, 0);
                if (Enabled) StartLoop();
            }
        }

        private bool CooldownPassed() =>
            (DateTime.UtcNow - _lastPollUtc).TotalMilliseconds >= FastTriggerCooldownMs;

        private async Task<bool> TryPollAsync(CancellationToken token)
        {
            int waited = 0;
            while ((svnManager.IsProcessing || svnManager.IsUpdateRunning) && waited < BusyWaitMaxMs)
            {
                try { await Task.Delay(500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
                waited += 500;
            }

            if (svnManager.IsProcessing || svnManager.IsUpdateRunning)
                return false;

            _lastPollUtc = DateTime.UtcNow;

            try
            {
                await PollOnceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }
            catch (TimeoutException)
            {
                LogThrottled(ref _lastErrorLogUtc,
                    "<color=#8B0000>>[AutoLock] SVN did not respond in time — will retry automatically. (Use 'Lock All Modified' if needed.)</color>");
            }
            catch (Exception ex)
            {
                LogThrottled(ref _lastErrorLogUtc, $"<color=#8B0000>>[AutoLock] {ex.Message}</color>");
            }

            return true;
        }

        // ================== POLL (= rdzeń "Lock All Modified", bez zmian) ==================

        private async Task PollOnceAsync(CancellationToken token)
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            var statusDict = await RunWithTimeoutAsync(
                () => SvnRunner.GetFullStatusDictionaryAsync(root), token).ConfigureAwait(false);
            if (statusDict == null) return;

            var modified = statusDict
                .Where(x => x.Value.status == "M")
                .Select(x => x.Key)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (modified.Count == 0) return;

            string me = (svnManager.CurrentUserName ?? "").Trim();

            var toLock = new List<string>();
            int foreign = 0;

            foreach (var p in modified)
            {
                if (svnManager.LockCache.Locks.TryGetValue(NormalizePath(p), out var info))
                {
                    string owner = (info.Owner ?? "").Trim();
                    if (owner.Equals(me, StringComparison.OrdinalIgnoreCase))
                        continue;          // już mój — OK
                    foreign++;              // cudzy — nie kradniemy po cichu
                    continue;
                }
                toLock.Add(p);
            }

            if (toLock.Count == 0)
            {
                if (foreign > 0)
                    LogThrottled(ref _lastOtherLockLogUtc,
                        $"<color=#8B0000>>[AutoLock] {foreign} modified file(s) are locked by someone else. Use Steal in the Locks panel if you really need them.</color>");
                return;
            }

            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_lock_{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(targetsFile, toLock, new UTF8Encoding(false), token).ConfigureAwait(false);
            try
            {
                await RunWithTimeoutAsync(
                    () => SvnRunner.RunAsync($"lock --targets \"{targetsFile}\"", root), token).ConfigureAwait(false);

                LogToLockPanel($"<color=green>[AutoLock] Locked {toLock.Count} file(s): " +
                               $"{string.Join(", ", toLock.Select(Path.GetFileName))}</color>");

                SVNStatus.ClearLockCache();
                _ = svnManager.GetModule<SVNLock>()?.RefreshLockCacheAsync(true);
                PostToMainThread(() => svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly());
            }
            finally
            {
                try { if (File.Exists(targetsFile)) File.Delete(targetsFile); } catch { }
            }
        }

        // ================== POMOCNICZE ==================

        private static async Task<T> RunWithTimeoutAsync<T>(Func<Task<T>> op, CancellationToken token)
        {
            var task = op();
            var winner = await Task.WhenAny(task, Task.Delay(SvnTimeoutMs, token)).ConfigureAwait(false);
            if (winner != task)
                throw new TimeoutException();
            return await task.ConfigureAwait(false);
        }

        private void LogThrottled(ref DateTime lastUtc, string message)
        {
            if ((DateTime.UtcNow - lastUtc).TotalMilliseconds < LogThrottleMs) return;
            lastUtc = DateTime.UtcNow;
            LogToLockPanel(message);
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            string root = svnManager.WorkingDir?.Replace("\\", "/").TrimEnd('/') ?? "";
            path = path.Replace("\\", "/");

            if (!string.IsNullOrEmpty(root) &&
                path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(root.Length + 1);
            }
            return path.TrimStart('/');
        }

        private void LogToLockPanel(string message, bool append = true)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.LockDisplayArea != null)
                    SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, message, append: append);
                else
                    SVNLogBridge.LogLine(message, append);
            });
        }
    }
}