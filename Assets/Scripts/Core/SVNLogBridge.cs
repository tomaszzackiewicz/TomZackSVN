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

        // Histeraza: rebuild bufora zdarzał się przy KAŻDYM flushu po osiągnięciu
        // limitu (stan stały sesji, nie "rzadki przypadek"). Tnę dopiero przy
        // nadmiarze >= TrimBatch → rebuild co ~50 linii.
        private const int MaxUILines = 500;
        private const int TrimBatch = 50;

        // Limit linii pól modułów (UpdateUIField append). Wcześniej ZEROWY limit
        // — konsole blame/diff/merge rosły bez końca + O(n) read+concat+set
        // per linia (pełny re-parse TMP).
        private const int MaxFieldLines = 400;

        private static readonly object _bufferLock = new();
        private static List<string> _buffer = new();
        private static bool _flushScheduled;            // strzeżone WYŁĄCZNIE _timerLock
        private static Timer _flushTimer;
        private static readonly object _timerLock = new();

        // Bufor appendów per pole UI (koaleskowanie per tick dispatchera).
        private static readonly object _fieldLock = new();
        private static readonly Dictionary<TextMeshProUGUI, StringBuilder> _fieldBuffers = new();
        private static int _fieldFlushQueued;           // 0/1

        // Guard rekurencji handlerów globalnych (LogLine → logger → Debug.Log
        // → logMessageReceivedThreaded → LogLine → ...).
        [ThreadStatic] private static bool _inGlobalHandler;

        // MAIN THREAD ONLY.
        private static readonly StringBuilder _fullLogBuilder = new StringBuilder(16 * 1024);
        private static readonly Queue<string> _allLines = new Queue<string>(MaxUILines + TrimBatch + 8);

        private static bool _globalHandlingEnabled = false;

        private static string NormalizeForDisplay(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace('\\', '/');
        }

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
            if (_inGlobalHandler) return;
            if (type != LogType.Exception && type != LogType.Error) return;

            _inGlobalHandler = true;
            try
            {
                string msg = $"<color=#FF0000><b>[UNITY {type}]</b> {logString}</color>\n<color=88CCFF>{stackTrace}</color>";
                LogLine(msg, true, type.ToString());
            }
            finally { _inGlobalHandler = false; }
        }

        private static void HandleAppDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            if (_inGlobalHandler) return;
            if (e.ExceptionObject is not Exception ex) return;

            _inGlobalHandler = true;
            try { LogException(ex, true, "UNHANDLED_DOMAIN"); }
            finally { _inGlobalHandler = false; }
        }

        private static void HandleUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            if (_inGlobalHandler) return;

            _inGlobalHandler = true;
            try
            {
                LogException(e.Exception, true, "UNOBSERVED_TASK");
                e.SetObserved();
            }
            finally { _inGlobalHandler = false; }
        }

        // Normalizacja też tutaj (spójność '\'-vs-'/' w pliku logu) + safe write
        // (late-log po Shutdown nie może rzucić w Task.Run).
        public static void LogToFile(string message, string level = "INFO")
        {
            if (string.IsNullOrEmpty(message)) return;
            string cleanMessage = StripRichText(NormalizeForDisplay(message));
            _ = Task.Run(() => { try { SVNLogger.LogToFile(cleanMessage, level); } catch { } });
        }

        public static void LogLine(string message, bool append = true, string level = "INFO")
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            message = NormalizeForDisplay(message);

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string uiMessage = $"<color=blue>[{timestamp}]</color> {message}";
            string cleanMessage = StripRichText(message);

            _ = Task.Run(() => { try { SVNLogger.LogToFile(cleanMessage, level); } catch { } });

            if (!append)
            {
                FlushImmediate(uiMessage, clear: true);
                return;
            }

            bool forceFlush = level.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("EXCEPTION", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("UNHANDLED_DOMAIN", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("UNOBSERVED_TASK", StringComparison.OrdinalIgnoreCase);

            int count;
            lock (_bufferLock)
            {
                _buffer.Add(uiMessage);
                count = _buffer.Count;
            }

            if (forceFlush || count >= FlushThreshold)
                FlushImmediate();
            else
                ScheduleFlush();
        }

        public static void LogError(string message, bool append = true)
        {
            LogLine($"<color=#FF8800><b>[ERROR]</b> {message}</color>", append, "ERROR");
        }

        public static void LogWarning(string message, bool append = true)
        {
            LogLine($"<color=#FFAA00><b>[WARN]</b> {message}</color>", append, "WARN");
        }

        public static void LogException(Exception ex, bool append = true, string level = "EXCEPTION")
        {
            if (ex == null) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"<color=#FF0000><b>[{level}] {ex.GetType().Name}:</b> {ex.Message}</color>");

            if (ex.TargetSite != null)
                sb.AppendLine($"<color=#FFAA00><b>Target:</b> {ex.TargetSite.DeclaringType?.Name}.{ex.TargetSite.Name}</color>");

            sb.AppendLine($"<color=88CCFF>{ex.StackTrace}</color>");

            Exception inner = ex.InnerException;
            int depth = 1;
            while (inner != null && depth <= 5)
            {
                sb.AppendLine($"<color=#FF9900><b>[INNER {depth}] {inner.GetType().Name}:</b> {inner.Message}</color>");
                sb.AppendLine($"<color=88CCFF>{inner.StackTrace}</color>");
                inner = inner.InnerException;
                depth++;
            }

            LogLine(sb.ToString(), append, level);
        }

        public static void UpdateUIField(TextMeshProUGUI uiField, string content, string logLabel = "UI", bool append = false)
        {
            if (uiField == null)
                return;

            if (string.IsNullOrWhiteSpace(content))
                return;

            content = NormalizeForDisplay(content);

            string cleanContent = StripRichText(content);
            if (!string.IsNullOrEmpty(cleanContent))
                _ = Task.Run(() => { try { SVNLogger.LogToFile(cleanContent, logLabel); } catch { } });

            if (!append)
            {
                // replace — caller świadomie nadpisuje pole (status/raport).
                string replacement = content.TrimEnd('\n', '\r') + "\n";
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (uiField == null) return;
                    uiField.text = replacement;
                });
                return;
            }

            // append przez bufor per pole + JEDEN flush na tick dispatchera.
            lock (_fieldLock)
            {
                if (!_fieldBuffers.TryGetValue(uiField, out var buffer))
                {
                    buffer = new StringBuilder(1024);
                    _fieldBuffers[uiField] = buffer;
                }
                buffer.Append(content.TrimEnd('\n', '\r')).Append('\n');
            }

            if (Interlocked.CompareExchange(ref _fieldFlushQueued, 1, 0) == 0)
                UnityMainThreadDispatcher.Enqueue(FlushFieldBuffers);
        }

        // MAIN THREAD ONLY (z dispatchera).
        private static void FlushFieldBuffers()
        {
            List<(TextMeshProUGUI field, string chunk)> chunks = null;
            List<TextMeshProUGUI> dead = null;

            lock (_fieldLock)
            {
                Interlocked.Exchange(ref _fieldFlushQueued, 0);

                foreach (var kvp in _fieldBuffers)
                {
                    if (kvp.Key == null)   // zniszczone UI (Unity ==) → sprzątamy wpis
                    {
                        (dead ??= new List<TextMeshProUGUI>()).Add(kvp.Key);
                        continue;
                    }
                    if (kvp.Value.Length == 0) continue;

                    (chunks ??= new List<(TextMeshProUGUI, string)>()).Add((kvp.Key, kvp.Value.ToString()));
                    kvp.Value.Clear();
                }

                if (dead != null)
                    foreach (var d in dead) _fieldBuffers.Remove(d);
            }

            if (chunks == null) return;

            foreach (var entry in chunks)
            {
                try { AppendFieldChunk(entry.field, entry.chunk); }
                catch { /* pole zniszczone w teardown */ }
            }
        }

        // MAIN THREAD ONLY.
        private static void AppendFieldChunk(TextMeshProUGUI field, string chunk)
        {
            string current = field.text;
            string combined = string.IsNullOrEmpty(current)
                ? chunk
                : current.TrimEnd('\n', '\r') + "\n" + chunk;

            field.text = TrimLeadingLines(combined, MaxFieldLines);
        }

        // MAIN THREAD ONLY. Tnie najstarsze linie; tagi rich text w naszych
        // komunikatach są zamknięte w obrębie jednej linii — cięcie po '\n'
        // nie rozbija tagów.
        private static string TrimLeadingLines(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int lineCount = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') lineCount++;
            if (text[text.Length - 1] != '\n') lineCount++;

            if (lineCount <= maxLines) return text;

            int linesToRemove = lineCount - maxLines;
            int cutIndex = 0;
            for (int i = 0; i < linesToRemove; i++)
            {
                int next = text.IndexOf('\n', cutIndex);
                if (next < 0) { cutIndex = text.Length; break; }
                cutIndex = next + 1;
            }

            return cutIndex <= text.Length ? text.Substring(cutIndex) : text;
        }

        // ScheduleFlush pod lockiem (dwa Timery). _flushScheduled strzeżone
        // TYLKO tym lockiem — patrz Flush.
        private static void ScheduleFlush()
        {
            lock (_timerLock)
            {
                if (_flushScheduled) return;
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

        // MAIN THREAD ONLY (z dispatchera).
        private static void Flush()
        {
            // Reset flagi pod WŁAŚNYM lockiem. Wcześniej ScheduleFlush czytało
            // ją pod _timerLock, a Flush zerował pod _bufferLock → flaga
            // utykała na 'true' i ciche linie siedziały w buforze bez timera.
            // Reset PRZED drainem: linie dodane po resecie uzbrają nowy timer.
            lock (_timerLock) _flushScheduled = false;

            List<string> linesToAdd;
            lock (_bufferLock)
            {
                if (_buffer.Count == 0) return;
                linesToAdd = new List<string>(_buffer);
                _buffer.Clear();
            }

            AppendToLog(linesToAdd);
            SetLogText(BuildCurrentText(), scroll: true);
        }

        public static void FlushImmediate(string singleMessage = null, bool clear = false)
        {
            singleMessage = NormalizeForDisplay(singleMessage);

            lock (_timerLock)
            {
                _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _flushScheduled = false;
            }

            string single = singleMessage;
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

            if (_allLines.Count > MaxUILines + TrimBatch)
            {
                int excess = _allLines.Count - MaxUILines;
                for (int i = 0; i < excess; i++)
                    _allLines.Dequeue();

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
                SVNUI.Instance.LogScrollRect.verticalNormalizedPosition = 0f;
        }

        public static void ShowNotification(string message)
        {
            message = NormalizeForDisplay(message);

            LogLine($"<color=blue>[NOTIFY]</color> {message}");

            string uiMessage = message;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null) return;
                SVNUI.Instance.ShowNotificationWithTimer(uiMessage, DefaultNotificationDuration);
            });
        }

        public static void LogTooltip(string message)
        {
            message = NormalizeForDisplay(message);

            string uiMessage = message;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;
                SVNUI.Instance.TooltipText.text = $"<color=#CCCCCC>{uiMessage}</color>";
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
            message = NormalizeForDisplay(message);

            string cleanMessage = StripRichText(message);
            if (!string.IsNullOrEmpty(cleanMessage))
                _ = Task.Run(() => { try { SVNLogger.LogToFile(cleanMessage, "CHECKOUT"); } catch { } });

            string uiMessage = message;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.CheckoutedFilesText == null) return;
                SVNUI.Instance.CheckoutedFilesText.text = uiMessage;
            });
        }

        public static void LogRaw(string message)
        {
            FlushImmediate(NormalizeForDisplay(message), clear: true);
        }

        private static string StripRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return RichTextRegex.Replace(input, string.Empty);
        }

        // Teardown best-effort: FlushImmediate enqueue'uje flush przez
        // dispatcher — po OnDestroy managera kolejka nie płynie (plik dostaje
        // linie z LogLine). Late-logi po Shutdown tolerowane (safe Task.Run).
        public static void Shutdown()
        {
            DisableGlobalExceptionHandling();
            FlushImmediate();

            lock (_fieldLock) _fieldBuffers.Clear();
            Interlocked.Exchange(ref _fieldFlushQueued, 0);

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
            message = NormalizeForDisplay(message);

            string uiMessage = message;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.OutputText == null)
                    return;

                SVNUI.Instance.OutputText.text = uiMessage;
            });
        }

        public static void LogErrorToOutput(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            message = NormalizeForDisplay(message);

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

        public static void TraceBar(string source, string message)
        {
            message = NormalizeForDisplay(message);

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
                    SVNUI.Instance.LogText.text = string.Empty;
            });
        }
    }
}