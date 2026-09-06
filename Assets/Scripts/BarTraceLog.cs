using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace SVN.Core
{
    public static class BarTraceLog
    {
        private static readonly string LogPath = Path.Combine(
            SVNPrefs.TemporaryCachePath,
            "SVN_Bar_Trace.log");

        private static readonly object _lock = new object();

        // === FIX K2: inicjalizacja atomowo pod lockiem — dwa wątki wołające
        // Log() równolegle robiły WriteAllText dwa razy (truncate) i mogły
        // wzajemnie wycinać sobie nagłówek/linie.
        private static bool _initialized;

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

        // UWAGA (wydajność, świadome): AppendAllText otwiera/zamyka plik przy
        // każdej linii. Dla debug-trace akceptowalne; przy setkach linii/s
        // warto dodać bufor z okresowym flushem.
        public static void Log(string source, string message)
        {
            if (!_initialized) Init();

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}\n";

            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogPath, line);
                }
            }
            catch { }
        }

        // === FIX K3: Clear pod lockiem — truncate mógł przeciąć AppendAllText
        // w locie ("file in use" u jednej ze stron).
        public static void Clear()
        {
            lock (_lock)
            {
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
                // === FIX: cichy catch ukrywał problem — przy braku skojarzenia .log
                // użytkownik dostaje teraz podpowiedź zamiast niczego.
                Debug.LogWarning($"[BarTraceLog] Could not open log file: {ex.Message}. Path: {LogPath}");
            }
        }
    }
}