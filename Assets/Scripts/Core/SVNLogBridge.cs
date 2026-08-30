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
        private static readonly Regex RichTextRegex = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        private const float DefaultNotificationDuration = 5f;
        private const int FlushThreshold = 10;
        private const int FlushDelayMs = 200;
        private const int MaxUILines = 500;

        private static readonly object _bufferLock = new();
        private static List<string> _buffer = new();
        private static bool _flushScheduled;
        private static Timer _flushTimer;
        private static readonly object _timerLock = new();

        // === FIX K1/Ś1: WŁASNOŚĆ MAIN THREAD ONLY (dokumentowane). Mutowane
        // wyłącznie w kodzie wykonywanym przez UnityMainThreadDispatcher —
        // Flush/FlushImmediate/TraceBar/ClearConsole (wszystkie w Enqueue).
        private static readonly StringBuilder _fullLogBuilder = new StringBuilder(16 * 1024);
        private static readonly Queue<string> _allLines = new Queue<string>(MaxUILines + 8);

        private static bool _globalHandlingEnabled = false;

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
                string msg = $"<color=#FF0000><b>[UNITY {type}]</b> {logString}</color>\n<color=88CCFF>{stackTrace}</color>";
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
            e.SetObserved();
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

            // === FIX drobiazg: porównanie OrdinalIgnoreCase — literówka
            // "UNobserved_TASK" sprawiała, że unobserved task exceptions nigdy
            // nie force-flushowały.
            bool forceFlush = level.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("EXCEPTION", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("UNHANDLED_DOMAIN", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("UNOBSERVED_TASK", StringComparison.OrdinalIgnoreCase);

            int count;
            lock (_bufferLock)
            {
                if (!string.IsNullOrWhiteSpace(uiMessage.TrimEnd('\n', '\r')))
                {
                    _buffer.Add(uiMessage);
                }
                count = _buffer.Count;
            }

            if (forceFlush || count >= FlushThreshold)
            {
                FlushImmediate();
            }
            else
            {
                ScheduleFlush();
            }
        }

        public static void LogError(string message, bool append = true)
        {
            string errorMessage = $"<color=#FF8800><b>[ERROR]</b> {message}</color>";
            LogLine(errorMessage, append, "ERROR");
        }

        public static void LogException(Exception ex, bool append = true, string level = "EXCEPTION")
        {
            if (ex == null) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"<color=#FF0000><b>[{level}] {ex.GetType().Name}:</b> {ex.Message}</color>");

            if (ex.TargetSite != null)
            {
                sb.AppendLine($"<color=#FFAA00><b>Target:</b> {ex.TargetSite.DeclaringType?.Name}.{ex.TargetSite.Name}</color>");
            }

            sb.AppendLine($"<color=#88CCFF>{ex.StackTrace}</color>");

            Exception inner = ex.InnerException;
            int depth = 1;
            while (inner != null && depth <= 5)
            {
                sb.AppendLine($"<color=#FF9900><b>[INNER {depth}] {inner.GetType().Name}:</b> {inner.Message}</color>");
                sb.AppendLine($"<color=#88CCFF>{inner.StackTrace}</color>");
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

                string trimmedContent = content.TrimEnd('\n', '\r');

                if (append)
                {
                    string currentText = uiField.text.TrimEnd('\n', '\r');
                    uiField.text = string.IsNullOrEmpty(currentText)
                        ? trimmedContent + "\n"
                        : currentText + "\n" + trimmedContent + "\n";
                }
                else
                {
                    uiField.text = trimmedContent + "\n";
                }
            });
        }

        // === FIX K1: ScheduleFlush pod lockiem — dwa wątki jednocześnie mogły
        // utworzyć DWA Timery (jeden zombie-strzelał flushami w pustkę).
        private static void ScheduleFlush()
        {
            lock (_timerLock)
            {
                if (_flushScheduled) return;   // === FIX: idempotentne (wcześniej
                                               // ustawiało flagę NA ZEWNĄTRZ checka)
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
        }

        // Wykonywane WYŁĄCZNIE na main thread (z dispatchera).
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

            AppendToLog(linesToAdd);
            SetLogText(BuildCurrentText(), scroll: true);
        }

        public static void FlushImmediate(string singleMessage = null, bool clear = false)
        {
            lock (_timerLock)
            {
                _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _flushScheduled = false;
            }

            string single = singleMessage;   // snapshot do closure
            bool doClear = clear;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (doClear)
                {
                    lock (_bufferLock) _buffer.Clear();

                    _allLines.Clear();
                    _fullLogBuilder.Clear();

                    if (!string.IsNullOrEmpty(single))
                    {
                        _allLines.Enqueue(single);
                        _fullLogBuilder.AppendLine(single);
                    }

                    SetLogText(BuildCurrentText(), scroll: false);
                    return;
                }

                List<string> pending;
                lock (_bufferLock)
                {
                    pending = new List<string>(_buffer);
                    _buffer.Clear();
                }
                if (!string.IsNullOrEmpty(single))
                    pending.Add(single);

                AppendToLog(pending);
                SetLogText(BuildCurrentText(), scroll: true);
            });
        }

        // MAIN THREAD ONLY.
        private static void AppendToLog(List<string> linesToAdd)
        {
            if (linesToAdd == null || linesToAdd.Count == 0) return;

            foreach (var line in linesToAdd)
            {
                _allLines.Enqueue(line);
                _fullLogBuilder.AppendLine(line);
            }

            if (_allLines.Count > MaxUILines)
            {
                int excess = _allLines.Count - MaxUILines;
                for (int i = 0; i < excess; i++)
                {
                    _allLines.Dequeue();
                }

                // === FIX Ś1: przebudowa TYLKO przy przycięciu (rzadko), nie co linię —
                // wcześniej '_fullLogText += line' = O(n²) + pełny re-parse TMP
                // przy każdym flushu (GC-thrash, zjadanie klatek przy zalewie logów).
                _fullLogBuilder.Clear();
                foreach (var l in _allLines)
                    _fullLogBuilder.AppendLine(l);
            }
        }

        // MAIN THREAD ONLY.
        private static string BuildCurrentText() => _fullLogBuilder.ToString();

        private static void SetLogText(string text, bool scroll)
        {
            if (SVNUI.Instance == null || SVNUI.Instance.LogText == null) return;
            SVNUI.Instance.LogText.text = text;

            if (scroll && SVNUI.Instance.LogScrollRect != null)
            {
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
            SVNLogger.Shutdown();

            lock (_timerLock)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                _flushScheduled = false;
            }
        }

        public static void LogToOutput(string message)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.OutputText == null)
                    return;

                SVNUI.Instance.OutputText.text = message;
            });
        }

        public static void LogErrorToOutput(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string formattedMessage = message.StartsWith("<color=")
                ? message
                : $"<color=#FF9900>{message}</color>";

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.OutputText == null)
                    return;

                SVNUI.Instance.OutputText.text = formattedMessage;
            });
        }

        // === FIX drobiazg: TraceBar korzysta ze wspólnego AppendToLog (main)
        // zamiast duplikować logikę przycinania.
        public static void TraceBar(string source, string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = $"<color=white>[{timestamp}][BAR:{source}] {message}</color>";

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                AppendToLog(new List<string> { line });
                SetLogText(BuildCurrentText(), scroll: true);
            });
        }

        public static void ClearConsole()
        {
            lock (_timerLock)
            {
                _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _flushScheduled = false;
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                lock (_bufferLock) _buffer.Clear();

                _allLines.Clear();
                _fullLogBuilder.Clear();

                if (SVNUI.Instance != null && SVNUI.Instance.LogText != null)
                {
                    SVNUI.Instance.LogText.text = string.Empty;
                }
            });
        }

        public static void LogWarning(string message, bool append = true)
        {
            LogLine($"<color=#FFAA00><b>[WARN]</b> {message}</color>", append, "WARN");
        }
    }
}