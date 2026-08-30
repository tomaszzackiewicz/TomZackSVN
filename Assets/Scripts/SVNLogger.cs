using System;
using System.IO;
using UnityEngine;

namespace SVN.Core
{
    public static class SVNLogger
    {
        private static string logFilePath;
        private static volatile bool initialized = false;
        private static readonly object FileLock = new object();

        // === FIX K2: limit rozmiaru — rotacja przy starcie sesji (zachowujemy
        // poprzednią jako .1). Bez tego AppendAllText per linia rosło w dziesiątki
        // MB na długich sesjach z checkoutami.
        private const long MaxLogSizeBytes = 8 * 1024 * 1024; // 8 MB

        public static void Initialize()
        {
            // === FIX K1: całość pod lockiem + double-check. Wcześniej check-then-act
            // z puli wątków: dwóch producentów naraz → podwójny truncate (wzajemne
            // cięcie nagłówka/logów) oraz wariant z odczytem initialized==true
            // PRZED ustawieniem logFilePath (brak bariery) → AppendAllText(null)
            // połknięte przez catch = logi znikają bezgłośnie.
            lock (FileLock)
            {
                if (initialized) return;

                try
                {
                    string folderPath = SVNPrefs.PersistentDataPath;
                    logFilePath = Path.Combine(folderPath, "svn_session.log");

                    // === FIX K2: rotacja — poprzednia sesja zostaje jako .1.
                    try
                    {
                        if (File.Exists(logFilePath))
                        {
                            string prev = logFilePath + ".1";
                            if (File.Exists(prev)) File.Delete(prev);
                            File.Move(logFilePath, prev);
                        }
                    }
                    catch { }

                    // === FIX drobiazg: czysty tekst do PLIKU (rich-text to UI).
                    File.WriteAllText(logFilePath, $"=== SVN SESSION LOG START: {DateTime.Now} ===\n");
                    File.AppendAllText(logFilePath, $"OS: {SystemInfo.operatingSystem}\n");
                    File.AppendAllText(logFilePath, $"Path: {Application.dataPath}\n\n");

                    // Subskrypcja ZANIM initialized=true — handler działa na kompletnym stanie.
                    Application.logMessageReceived += HandleUnityLog;
                    initialized = true;

                    SVNLogBridge.LogToOutput("<color=green>[SVN] Logger initialized successfully.</color>");
                }
                catch (Exception e)
                {
                    // Inicjalizacja padła — NIE ustawiamy initialized (LogToFile
                    // będzie grzecznie pomijać zamiast rzucać na null path).
                    Debug.LogWarning($"[SVNLogger] Init failed: {e.Message}");
                }
            }
        }

        // === FIX K3: odsubskrybowanie — SVNLogBridge.Shutdown woła to; bez tego
        // przy wyłączonym Domain Reload każdy Play dokładał kolejny handler
        // i każda linia Debug.Log lądowała w pliku N×.
        public static void Shutdown()
        {
            lock (FileLock)
            {
                if (!initialized) return;
                initialized = false;
                try { Application.logMessageReceived -= HandleUnityLog; } catch { }
            }
        }

        private static void HandleUnityLog(string logString, string stackTrace, LogType type)
        {
            if (!initialized) return;

            string logEntry = $"[{DateTime.Now:HH:mm:ss}] [{type}] {logString}\n";

            if (type == LogType.Error || type == LogType.Exception)
            {
                logEntry += $"ST: {stackTrace}\n";
            }

            AppendLine(logEntry);
        }

        public static void OpenLogFolder()
        {
            Application.OpenURL("file://" + SVNPrefs.PersistentDataPath);
        }

        public static void LogToFile(string message, string tag)
        {
            // === FIX K1: guard — po porażce inicjalizacji (initialized == false
            // mimo próby) pomijamy cicho, zamiast AppendAllText(null path).
            if (!initialized)
            {
                Initialize();
                if (!initialized) return;
            }

            string logEntry = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}\n";
            AppendLine(logEntry);
        }

        private static void AppendLine(string logEntry)
        {
            try
            {
                lock (FileLock)
                {
                    if (string.IsNullOrEmpty(logFilePath)) return;   // === FIX K1: defensive
                    File.AppendAllText(logFilePath, logEntry);
                }
            }
            catch { }
        }
    }
}