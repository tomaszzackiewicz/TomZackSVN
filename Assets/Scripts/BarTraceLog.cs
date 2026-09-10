using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public static class BarTraceLog
    {
        private static readonly string LogPath = Path.Combine(
            SVNPrefs.TemporaryCachePath,
            "SVN_Bar_Trace.log");

        private static readonly object _lock = new object();
        private static readonly Queue<string> _pending = new Queue<string>();

        // === FIX K2: inicjalizacja atomowo pod lockiem — dwa wątki wołające
        // Log() równolegle robiły WriteAllText dwa razy (truncate) i mogły
        // wzajemnie wycinać sobie nagłówek/linie.
        private static bool _initialized;
        private static Task _flushTask;

        public static void Init()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    File.WriteAllText(LogPath, $"=== SVN BAR TRACE LOG Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n");
                }
                catch { }
            }
        }

        /// <summary>
        /// === FIX (perf): bufor + flush co ~250 ms w tle. Wcześniej każda
        /// linia robiła synchroniczne AppendAllText — setki I/O/s na main
        /// thread = widoczne jąkanie. Teraz Log() tylko wrzuca do kolejki
        /// (O(1) pod lockiem), a zapis robi jeden background task.
        /// </summary>
        public static void Log(string source, string message)
        {
            if (!_initialized) Init();

            lock (_lock)
            {
                _pending.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}");

                if (_flushTask == null || _flushTask.IsCompleted)
                    _flushTask = Task.Run(FlushLoop);
            }
        }

        private static void FlushLoop()
        {
            while (true)
            {
                Thread.Sleep(250);

                string payload;
                lock (_lock)
                {
                    if (_pending.Count == 0) return;

                    var sb = new System.Text.StringBuilder();
                    while (_pending.Count > 0)
                        sb.Append(_pending.Dequeue()).Append('\n');

                    payload = sb.ToString();
                }

                try { File.AppendAllText(LogPath, payload); }
                catch { }
            }
        }

        /// <summary>
        /// === FIX K3: Clear pod lockiem — truncate mógł przeciąć AppendAllText
        /// w locie ("file in use" u jednej ze stron). Dodatkowo czyści pending
        /// queue, żeby kolejny flush nie wrzucił starych linii do świeżego pliku.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _pending.Clear();

                try
                {
                    File.WriteAllText(LogPath, $"=== LOG CLEARED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n");
                }
                catch { }
            }
        }

        public static void OpenLog()
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    // === FIX K1: Process.Start(string) na profilu Unity (.NET Core/
                    // Standard) ma UseShellExecute=false domyślnie → próba
                    // "uruchomienia" pliku .log rzucała Win32Exception (łapany
                    // cicho) → OpenLog nie działał. Jawne UseShellExecute=true.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = LogPath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BarTraceLog] Could not open log file: {ex.Message}. Path: {LogPath}");
            }
        }
    }
}