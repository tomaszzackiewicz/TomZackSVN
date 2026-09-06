using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    /// <summary>
    /// Thread-safe dostęp do main-only API Unity (PlayerPrefs, ścieżki Application).
    ///
    /// ZASADY PROJEKTOWE (nauczone kosztem regression-bugów):
    ///  1. Ścieżki Application.* — cache w zwykłych statycznych polach, JWNE metody
    ///     resolve per ścieżka. Żadnych generycznych helperów z 'ref' + lambda
    ///     (lambda nie widzi ref-aktualizacji → rekursja → StackOverflow).
    ///  2. PlayerPrefs — odczyt z puli: Enqueue + KRÓTKI grace (100 ms).
    ///     Dłuższe blokowanie (2 s) przy zajętej kolejce tworzyło cykl:
    ///     main czekał na managerLock → holder locka czekał na dispatchera →
    ///     backlog rósł → freeze → crash przy przełączaniu projektu.
    ///     Fallback-wartości prefsów są kosmetyczne — default lepszy niż freeze.
    ///  3. Zapis: fire-and-forget z puli, natychmiastowy na main.
    /// </summary>
    public static class SVNPrefs
    {
        private static readonly TimeSpan PrefsGrace = TimeSpan.FromMilliseconds(100);

        // ===================================================================
        //  PlayerPrefs
        // ===================================================================

        public static string GetString(string key, string defaultValue = "")
        {
            if (UnityMainThreadDispatcher.IsMainThread)
                return PlayerPrefs.GetString(key, defaultValue);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            UnityMainThreadDispatcher.Enqueue(() => tcs.TrySetResult(PlayerPrefs.GetString(key, defaultValue)));

            return tcs.Task.Wait(PrefsGrace) && tcs.Task.Status == TaskStatus.RanToCompletion
                ? tcs.Task.Result
                : defaultValue;
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            if (UnityMainThreadDispatcher.IsMainThread)
                return PlayerPrefs.GetInt(key, defaultValue);

            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            UnityMainThreadDispatcher.Enqueue(() => tcs.TrySetResult(PlayerPrefs.GetInt(key, defaultValue)));

            return tcs.Task.Wait(PrefsGrace) && tcs.Task.Status == TaskStatus.RanToCompletion
                ? tcs.Task.Result
                : defaultValue;
        }

        /// <summary>Zapis — na main thread natychmiastowy, z puli kolejkowany (fire-and-forget).</summary>
        public static void SetString(string key, string value)
        {
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                PlayerPrefs.SetString(key, value);
                PlayerPrefs.Save();
                return;
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try { PlayerPrefs.SetString(key, value); PlayerPrefs.Save(); }
                catch { }
            });
        }

        public static void DeleteKey(string key)
        {
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
                return;
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); }
                catch { }
            });
        }

        // ===================================================================
        //  Ścieżki Application — cached przy pierwszym użyciu, potem zero kosztu.
        //  JWNE resolve per ścieżka; lambda (tylko w resolverze) woła WYŁĄCZNIE
        //  surowe Application.* — nigdy te property → brak rekursji.
        // ===================================================================

        private static string _persistentDataPath;
        private static string _temporaryCachePath;
        private static readonly object PathsLock = new();

        public static string PersistentDataPath
        {
            get
            {
                var cached = Volatile.Read(ref _persistentDataPath);
                if (cached != null) return cached;

                lock (PathsLock)
                {
                    if (_persistentDataPath != null) return _persistentDataPath;

                    _persistentDataPath = UnityMainThreadDispatcher.IsMainThread
                        ? Application.persistentDataPath
                        : ResolveViaDispatcher(nameof(Application.persistentDataPath));

                    return _persistentDataPath;
                }
            }
        }

        public static string TemporaryCachePath
        {
            get
            {
                var cached = Volatile.Read(ref _temporaryCachePath);
                if (cached != null) return cached;

                lock (PathsLock)
                {
                    if (_temporaryCachePath != null) return _temporaryCachePath;

                    _temporaryCachePath = UnityMainThreadDispatcher.IsMainThread
                        ? Application.temporaryCachePath
                        : ResolveViaDispatcher(nameof(Application.temporaryCachePath));

                    return _temporaryCachePath;
                }
            }
        }

        /// <summary>
        /// Odczyt main-only property przez dispatcher (blokująco z limitem —
        /// Ścieżki resolve'ują się RAZ w życiu procesu i wyłącznie z fallbacku
        /// bootstrapowego, nigdy w cyklu z managerLock; grace 100 ms nie
        //  wystarczyłby na pierwszą klatkę). Switch po NAZWIE — jawnie, bez
        /// delegat-generyki; każda gałąź woła surowe API i NIC więcej.
        /// </summary>
        private static string ResolveViaDispatcher(string propertyName)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    string result = propertyName switch
                    {
                        nameof(Application.persistentDataPath) => Application.persistentDataPath,
                        nameof(Application.temporaryCachePath) => Application.temporaryCachePath,
                        _ => null
                    };
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            if (!tcs.Task.Wait(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException($"SVNPrefs: timeout resolving '{propertyName}' via dispatcher.");

            if (tcs.Task.Status == TaskStatus.RanToCompletion && tcs.Task.Result != null)
                return tcs.Task.Result;

            if (tcs.Task.Status == TaskStatus.Faulted)
                throw tcs.Task.Exception?.GetBaseException()
                    ?? new InvalidOperationException($"SVNPrefs: '{propertyName}' resolution failed.");

            throw new InvalidOperationException($"SVNPrefs: '{propertyName}' resolved to null.");
        }
    }
}