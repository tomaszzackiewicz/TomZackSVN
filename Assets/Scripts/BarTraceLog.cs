using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace SVN.Core
{
    public static class BarTraceLog
    {
        private static readonly string LogPath = Path.Combine(
            Application.temporaryCachePath,
            "SVN_Bar_Trace.log");

        private static readonly object _lock = new object();
        private static bool _initialized;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                File.WriteAllText(LogPath, $"=== SVN BAR TRACE LOG Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n");
            }
            catch { }
        }

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

        public static void Clear()
        {
            try
            {
                File.WriteAllText(LogPath, $"=== LOG CLEARED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n");
            }
            catch { }
        }

        public static void OpenLog()
        {
            try
            {
                if (File.Exists(LogPath))
                    System.Diagnostics.Process.Start(LogPath);
            }
            catch { }
        }
    }
}