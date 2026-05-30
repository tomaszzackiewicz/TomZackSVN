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

            //SVNLogBridge.LogLine Path: C:\Users\[User]\AppData\LocalLow\[CompanyName]\[ProjectName]\svn_session.log
            string folderPath = Application.persistentDataPath;
            logFilePath = Path.Combine(folderPath, "svn_session.log");

            try
            {
                File.WriteAllText(logFilePath, $"<color=green>=== SVN SESSION LOG START: {DateTime.Now} ===</color>\n");
                File.AppendAllText(logFilePath, $"<color=green>OS: {SystemInfo.operatingSystem}</color>\n");
                File.AppendAllText(logFilePath, $"<color=green>Path: {Application.dataPath}</color>\n\n");

                Application.logMessageReceived += HandleUnityLog;
                initialized = true;

                SVNLogBridge.LogLine("<color=green>[SVN] Logger initialized successfully.</color>");
            }
            catch (Exception e)
            {
                SVNLogBridge.LogError($"<color=#8B0000>[SVN] Critical Logger Error:</color> <color=#8B0000>{e.Message}</color>");
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
            catch { }
        }
    }
}