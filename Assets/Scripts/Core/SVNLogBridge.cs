using UnityEngine;
using System.Collections.Concurrent;
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
        private const int TrimBatch = 50;
        private const int MaxFieldLines = 400;

        private static readonly object _bufferLock = new();
        private static List<string> _buffer = new();
        private static bool _flushScheduled;
        private static Timer _flushTimer;
        private static readonly object _timerLock = new();

        private static readonly object _fieldLock = new();
        private static readonly Dictionary<TextMeshProUGUI, StringBuilder> _fieldBuffers = new();
        private static int _fieldFlushQueued;

        [ThreadStatic] private static bool _inGlobalHandler;

        private static readonly StringBuilder _fullLogBuilder = new StringBuilder(16 * 1024);
        private static readonly Queue<string> _allLines = new Queue<string>(MaxUILines + TrimBatch + 8);

        private static bool _globalHandlingEnabled = false;
        private static int _clearTooltipQueued;

        // === FIX (late-log po Shutdown): flaga blokująca dalsze enqueue do pliku.
        // Wcześniej po Shutdown kolejny LogLine startował nowy flusher task piszący
        // do zamkniętego loggera.
        private static volatile bool _shutdown;

        // =====================================================================
        //  === FIX (perf): buforowany zapis do pliku
        //
        //  Wcześniej KAŻDA linia logu robiła `_ = Task.Run(() => SVNLogger.LogToFile(...))`.
        //  Commit progress z 500 plikami = 500 Tasków + 500 File.AppendAllText.
        //  Refresh z 5 subkomendami logującymi = dziesiątki Tasków w puli.
        //  GC pressure i I/O serializacja przez File lock.
        //
        //  Teraz: kolejka + jeden flusher task, flush co ~250 ms partiami.
        //  Setki zapisów/s → 4/s. Kolejność zachowana (ConcurrentQueue FIFO).
        // =====================================================================
        private static readonly ConcurrentQueue<(string text, string level)> _fileLogQueue = new();
        private static Task _fileFlushTask;

        private static void EnqueueFileLog(string text, string level)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_shutdown) return;   // po Shutdown nie piszemy

            _fileLogQueue.Enqueue((text, level));

            if (_fileFlushTask == null || _fileFlushTask.IsCompleted)
                _fileFlushTask = Task.Run(FileFlushLoop);
        }

        private static async Task FileFlushLoop()
        {
            await Task.Delay(250).ConfigureAwait(false);

            var sb = new StringBuilder(2048);
            string lastLevel = "INFO";

            while (_fileLogQueue.TryDequeue(out var item))
            {
                sb.AppendLine(item.text);
                lastLevel = item.level;
            }

            if (sb.Length == 0) return;

            // Po Shutdown SVNLogger jest zamknięty — pomiń.
            if (_shutdown) return;

            try { SVNLogger.LogToFile(sb.ToString(), lastLevel); } catch { }
        }

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

        // === FIX (perf): przez kolejkę zamiast Task.Run per linia.
        public static void LogToFile(string message, string level = "INFO")
        {
            if (string.IsNullOrEmpty(message)) return;
            string cleanMessage = StripRichText(NormalizeForDisplay(message));
            EnqueueFileLog(cleanMessage, level);
        }

        public static void LogLine(string message, bool append = true, string level = "INFO")
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            message = NormalizeForDisplay(message);

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string uiMessage = $"<color=blue>[{timestamp}]</color> {message}";
            string cleanMessage = StripRichText(message);

            EnqueueFileLog(cleanMessage, level);

            if (!append)
            {
                FlushImmediate(uiMessage, clear: true);
                return;
            }

            bool forceFlush =
                string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(level, "EXCEPTION", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(level, "UNHANDLED_DOMAIN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(level, "UNOBSERVED_TASK", StringComparison.OrdinalIgnoreCase);

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
                EnqueueFileLog(cleanContent, logLabel);

            if (!append)
            {
                string replacement = content.TrimEnd('\n', '\r') + "\n";
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (uiField == null) return;
                    uiField.text = replacement;
                });
                return;
            }

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

        private static void FlushFieldBuffers()
        {
            List<(TextMeshProUGUI field, string chunk)> chunks = null;
            List<TextMeshProUGUI> dead = null;

            lock (_fieldLock)
            {
                Interlocked.Exchange(ref _fieldFlushQueued, 0);

                foreach (var kvp in _fieldBuffers)
                {
                    if (kvp.Key == null)
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
                catch { }
            }
        }

        private static void AppendFieldChunk(TextMeshProUGUI field, string chunk)
        {
            string current = field.text;
            string combined = string.IsNullOrEmpty(current)
                ? chunk
                : current.TrimEnd('\n', '\r') + "\n" + chunk;

            field.text = TrimLeadingLines(combined, MaxFieldLines);
        }

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

        private static void Flush()
        {
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
            if (Interlocked.CompareExchange(ref _clearTooltipQueued, 1, 0) == 1)
                return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                Interlocked.Exchange(ref _clearTooltipQueued, 0);

                if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;
                SVNUI.Instance.TooltipText.text = "";
            });
        }

        public static void LogCheckoutConsole(string message)
        {
            message = NormalizeForDisplay(message);

            string cleanMessage = StripRichText(message);
            if (!string.IsNullOrEmpty(cleanMessage))
                EnqueueFileLog(cleanMessage, "CHECKOUT");

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

        // =====================================================================
        //  === FIX (late-log po Shutdown): flaga _shutdown ustawiana NA POCZĄTKU,
        //  przed jakimkolwiek dalszym enqueue; ostatni flush kolejki pliku
        //  PRZED zamknięciem loggera (best-effort).
        // =====================================================================
        public static void Shutdown()
        {
            // Flaga NA POCZĄTKU — blokuje nowe enqueue ZANIM zamkniemy logger.
            _shutdown = true;

            DisableGlobalExceptionHandling();
            FlushImmediate();

            lock (_fieldLock) _fieldBuffers.Clear();
            Interlocked.Exchange(ref _fieldFlushQueued, 0);
            Interlocked.Exchange(ref _clearTooltipQueued, 0);

            // Ostatni flush kolejki pliku — best effort, PRZED zamknięciem loggera.
            try
            {
                var sb = new StringBuilder();
                while (_fileLogQueue.TryDequeue(out var item))
                    sb.AppendLine(item.text);

                if (sb.Length > 0)
                    SVNLogger.LogToFile(sb.ToString(), "SHUTDOWN");
            }
            catch { }

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