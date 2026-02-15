using System;
using System.IO;
using UnityEngine;

namespace SVN.Core
{
    public static class SVNLogger
    {
        private static string logFilePath;
        private static bool initialized = false;

        public static void Initialize()
        {
            if (initialized) return;

            //Log Path: C:\Users\[User]\AppData\LocalLow\[CompanyName]\[ProjectName]\svn_session.log
            string folderPath = Application.persistentDataPath;
            logFilePath = Path.Combine(folderPath, "svn_session.log");

            try
            {
                File.WriteAllText(logFilePath, $"=== SVN SESSION LOG START: {DateTime.Now} ===\n");
                File.AppendAllText(logFilePath, $"OS: {SystemInfo.operatingSystem}\n");
                File.AppendAllText(logFilePath, $"Path: {Application.dataPath}\n\n");

                Application.logMessageReceived += HandleUnityLog;
                initialized = true;

                Debug.Log($"[SVN] Logger initialized at: {logFilePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SVN] Critical Logger Error: {e.Message}");
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

            try
            {
                lock (logFilePath)
                {
                    File.AppendAllText(logFilePath, logEntry);
                }
            }
            catch { }
        }
        public static void OpenLogFolder()
        {
            Application.OpenURL("file://" + Application.persistentDataPath);
        }

        public static void LogToFile(string message, string tag)
        {
            if (!initialized) Initialize();

            string logEntry = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}\n";
            try
            {
                lock (logFilePath)
                {
                    File.AppendAllText(logFilePath, logEntry);
                }
            }
            catch { /* Ignore file access errors */ }
        }
    }
}