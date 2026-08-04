using UnityEngine;
using System.Text.RegularExpressions;
using TMPro;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public static class SVNLogBridge
    {
        private static readonly Regex RichTextRegex = new Regex(@"<[^>]*>");
        private const float DefaultNotificationDuration = 5f;
        private const int FlushThreshold = 10;
        private const int FlushDelayMs = 200;
        private const int MaxUILines = 500;

        private static readonly object _bufferLock = new();
        private static List<string> _buffer = new();
        private static bool _flushScheduled;
        private static Timer _flushTimer;

        private static string _fullLogText = "";
        private static List<string> _allLines = new();

        // Flaga zabezpieczająca przed wielokrotną rejestracją eventów
        private static bool _globalHandlingEnabled = false;

        /// <summary>
        /// Włącza globalne przechwytywanie wyjątków z Unity i wątków C#.
        /// Wywołaj to np. w metodzie Awake() lub Start() głównego menedżera.
        /// </summary>
        public static void EnableGlobalExceptionHandling()
        {
            if (_globalHandlingEnabled) return;

            Application.logMessageReceivedThreaded += HandleUnityLog;
            AppDomain.CurrentDomain.UnhandledException += HandleAppDomainException;
            TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;

            _globalHandlingEnabled = true;
            LogLine("<color=#00FF99>[SVNLogBridge] Global exception handling enabled.</color>", true, "INFO");
        }

        public static void DisableGlobalExceptionHandling()
        {
            if (!_globalHandlingEnabled) return;

            Application.logMessageReceivedThreaded -= HandleUnityLog;
            AppDomain.CurrentDomain.UnhandledException -= HandleAppDomainException;
            TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;

            _globalHandlingEnabled = false;
        }

        private static void HandleUnityLog(string logString, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
            {
                string msg = $"<color=#FF0000><b>[UNITY {type}]</b> {logString}</color>\n<color=#AAAAAA>{stackTrace}</color>";
                LogLine(msg, true, type.ToString());
            }
        }

        private static void HandleAppDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException(ex, true, "UNHANDLED_DOMAIN");
            }
        }

        private static void HandleUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogException(e.Exception, true, "UNOBSERVED_TASK");
            e.SetObserved(); // Zapobiega scrashowaniu aplikacji
        }

        public static void LogToFile(string message, string level = "INFO")
        {
            if (string.IsNullOrEmpty(message)) return;
            string cleanMessage = StripRichText(message);
            _ = Task.Run(() => SVNLogger.LogToFile(cleanMessage, level));
        }

        public static void LogLine(string message, bool append = true, string level = "INFO")
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string uiMessage = $"<color=blue>[{timestamp}]</color> {message}";
            string cleanMessage = StripRichText(message);

            _ = Task.Run(() => SVNLogger.LogToFile(cleanMessage, level));

            if (!append)
            {
                FlushImmediate(uiMessage, clear: true);
                return;
            }

            bool forceFlush = level == "ERROR" || level == "EXCEPTION" || level == "UNHANDLED_DOMAIN" || level == "UNOBSERVED_TASK";
            int count;
            lock (_bufferLock)
            {
                _buffer.Add(uiMessage);
                count = _buffer.Count;
            }

            if (forceFlush || count >= FlushThreshold)
            {
                FlushImmediate();
            }
            else if (!_flushScheduled)
            {
                ScheduleFlush();
            }
        }

        public static void LogError(string message, bool append = true)
        {
            string errorMessage = $"<color=#FF8800><b>[ERROR]</b> {message}</color>";
            LogLine(errorMessage, append, "ERROR");
        }

        /// <summary>
        /// Zmieniona i rozbudowana metoda LogException z obsługą InnerException.
        /// </summary>
        public static void LogException(Exception ex, bool append = true, string level = "EXCEPTION")
        {
            if (ex == null) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"<color=#FF0000><b>[{level}] {ex.GetType().Name}:</b> {ex.Message}</color>");

            if (ex.TargetSite != null)
            {
                sb.AppendLine($"<color=#FFAA00><b>Target:</b> {ex.TargetSite.DeclaringType?.Name}.{ex.TargetSite.Name}</color>");
            }

            sb.AppendLine($"<color=#AAAAAA>{ex.StackTrace}</color>");

            // Rekursywne wyciąganie błędów wewnętrznych
            Exception inner = ex.InnerException;
            int depth = 1;
            while (inner != null && depth <= 5) // Limit 5, aby zapobiec potencjalnym nieskończonym pętlom
            {
                sb.AppendLine($"<color=#FF5555><b>[INNER {depth}] {inner.GetType().Name}:</b> {inner.Message}</color>");
                sb.AppendLine($"<color=#AAAAAA>{inner.StackTrace}</color>");
                inner = inner.InnerException;
                depth++;
            }

            LogLine(sb.ToString(), append, level);
        }

        public static void UpdateUIField(TextMeshProUGUI uiField, string content, string logLabel = "UI", bool append = false)
        {
            if (uiField == null) return;

            string cleanContent = StripRichText(content);
            if (!string.IsNullOrEmpty(cleanContent))
                _ = Task.Run(() => SVNLogger.LogToFile(cleanContent, logLabel));

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (uiField == null) return;
                if (append)
                    uiField.text += content + "\n";
                else
                    uiField.text = content;
            });
        }

        private static void ScheduleFlush()
        {
            _flushScheduled = true;

            if (_flushTimer == null)
            {
                _flushTimer = new Timer(_ =>
                {
                    UnityMainThreadDispatcher.Enqueue(Flush);
                }, null, FlushDelayMs, Timeout.Infinite);
            }
            else
            {
                _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
            }
        }

        private static void Flush()
        {
            List<string> linesToAdd;
            lock (_bufferLock)
            {
                if (_buffer.Count == 0)
                {
                    _flushScheduled = false;
                    return;
                }
                linesToAdd = new List<string>(_buffer);
                _buffer.Clear();
                _flushScheduled = false;
            }

            foreach (var line in linesToAdd)
                _allLines.Add(line);

            while (_allLines.Count > MaxUILines)
                _allLines.RemoveAt(0);

            StringBuilder sb = new StringBuilder();
            foreach (var line in _allLines)
                sb.AppendLine(line);
            _fullLogText = sb.ToString();

            SetLogText(_fullLogText, scroll: true);
        }

        public static void FlushImmediate(string singleMessage = null, bool clear = false)
        {
            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _flushScheduled = false;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (clear)
                {
                    _allLines.Clear();
                    _buffer.Clear();
                    _allLines.Add(singleMessage);
                    _fullLogText = singleMessage + "\n";
                    SetLogText(_fullLogText, scroll: false);
                    return;
                }

                List<string> pending;
                lock (_bufferLock)
                {
                    pending = new List<string>(_buffer);
                    _buffer.Clear();
                }
                if (!string.IsNullOrEmpty(singleMessage))
                    pending.Add(singleMessage);

                foreach (var line in pending)
                    _allLines.Add(line);

                while (_allLines.Count > MaxUILines)
                    _allLines.RemoveAt(0);

                StringBuilder sb = new StringBuilder();
                foreach (var line in _allLines)
                    sb.AppendLine(line);
                _fullLogText = sb.ToString();

                SetLogText(_fullLogText, scroll: true);
            });
        }

        private static void SetLogText(string text, bool scroll)
        {
            if (SVNUI.Instance == null || SVNUI.Instance.LogText == null) return;
            SVNUI.Instance.LogText.text = text;

            if (scroll && SVNUI.Instance.LogScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                SVNUI.Instance.LogScrollRect.verticalNormalizedPosition = 0f;
            }
        }

        public static void ShowNotification(string message)
        {
            LogLine($"<color=blue>[NOTIFY]</color> {message}");
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null) return;
                SVNUI.Instance.ShowNotificationWithTimer(message, DefaultNotificationDuration);
            });
        }

        public static void LogTooltip(string message)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;
                SVNUI.Instance.TooltipText.text = $"<color=#CCCCCC>{message}</color>";
            });
        }

        public static void ClearTooltip()
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;
                SVNUI.Instance.TooltipText.text = "";
            });
        }

        public static void LogCheckoutConsole(string message)
        {
            string cleanMessage = StripRichText(message);
            if (!string.IsNullOrEmpty(cleanMessage))
                _ = Task.Run(() => SVNLogger.LogToFile(cleanMessage, "CHECKOUT"));

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.CheckoutedFilesText == null) return;
                SVNUI.Instance.CheckoutedFilesText.text = message;
            });
        }

        public static void LogRaw(string message)
        {
            FlushImmediate(message, clear: true);
        }

        private static string StripRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return RichTextRegex.Replace(input, string.Empty);
        }

        public static void Shutdown()
        {
            DisableGlobalExceptionHandling();
            FlushImmediate();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        public static void LogToOutput(string message)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.OutputText == null)
                    return;

                SVNUI.Instance.OutputText.text = message;
                Canvas.ForceUpdateCanvases();
            });
        }

        public static void LogErrorToOutput(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string formattedMessage = message.StartsWith("<color=")
                ? message
                : $"<color=#FF5555>{message}</color>";

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.OutputText == null)
                    return;

                SVNUI.Instance.OutputText.text = formattedMessage;
                Canvas.ForceUpdateCanvases();
            });
        }
    }
}