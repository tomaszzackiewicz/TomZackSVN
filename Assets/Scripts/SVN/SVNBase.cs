using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TMPro;

namespace SVN.Core
{
    public abstract class SVNBase
    {
        private const int MaxConsoleLines = 400;
        private const int MaxConsoleBufferChars = 128 * 1024;

        private static readonly char[] ExtraInvalidPathChars = { '*', '?' };

        private static readonly Regex BannerRegex =
            new Regex(@"\*+\s*WARNING![\s\S]*?@{5,}", RegexOptions.IgnoreCase);

        private readonly object _consoleLock = new object();
        private readonly StringBuilder _consoleBuffer = new StringBuilder(1024);
        private int _consoleFlushQueued;

        public SVNManager svnManager;
        public SVNUI svnUI;

        private int _isProcessing;

        public bool IsProcessing
        {
            get => Volatile.Read(ref _isProcessing) != 0;
            set => Interlocked.Exchange(ref _isProcessing, value ? 1 : 0);
        }

        public bool TryStart() => Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0;

        public bool End() => Interlocked.Exchange(ref _isProcessing, 0) == 1;

        protected SVNBase(SVNUI ui, SVNManager manager)
        {
            if (ui == null)
                throw new ArgumentNullException(nameof(ui), $"{GetType().Name}: UI is NULL!");
            if (manager == null)
                throw new ArgumentNullException(nameof(manager), $"{GetType().Name}: Manager is NULL!");

            svnUI = ui;
            svnManager = manager;
        }

        protected string StripBanner(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string cleaned = BannerRegex.Replace(text, "");

            string[] lines = cleaned.Split(
                new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None);

            var finalLines = new System.Collections.Generic.List<string>(lines.Length);

            foreach (var line in lines)
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("*****") || trimmed.StartsWith("@@@@@") ||
                    trimmed.EndsWith("*****") || trimmed.EndsWith("@@@@@"))
                    continue;

                finalLines.Add(line);
            }

            return string.Join("\n", finalLines);
        }

        protected virtual TMP_Text GetConsole() => null;

        protected void Append(string msg, string color)
        {
            if (string.IsNullOrEmpty(msg)) return;

            lock (_consoleLock)
            {
                if (_consoleBuffer.Length + msg.Length + 16 > MaxConsoleBufferChars)
                {
                    int half = _consoleBuffer.Length / 2;
                    int cut = -1;
                    for (int i = 0; i < half; i++)
                    {
                        if (_consoleBuffer[i] == '\n') { cut = i + 1; break; }
                    }
                    if (cut > 0) _consoleBuffer.Remove(0, cut);
                    else _consoleBuffer.Clear();
                }

                _consoleBuffer.Append("<color=").Append(color).Append('>')
                              .Append(msg).AppendLine("</color>");
            }

            if (Interlocked.CompareExchange(ref _consoleFlushQueued, 1, 0) == 0)
                PostToMainThread(FlushConsoleBuffer);
        }

        private void FlushConsoleBuffer()
        {
            var console = GetConsole();
            if (console == null)
            {
                Interlocked.Exchange(ref _consoleFlushQueued, 0);
                return;
            }

            string chunk;
            lock (_consoleLock)
            {
                if (_consoleBuffer.Length == 0)
                {
                    _consoleFlushQueued = 0;
                    return;
                }
                chunk = _consoleBuffer.ToString();
                _consoleBuffer.Clear();
                _consoleFlushQueued = 0;
            }

            try
            {
                console.text += chunk;
                TrimConsole(console);
            }
            catch { }
        }

        private static void TrimConsole(TMP_Text console)
        {
            try
            {
                string text = console.text;
                int lineCount = 0;
                for (int i = 0; i < text.Length; i++)
                    if (text[i] == '\n') lineCount++;
                if (text.Length > 0 && text[text.Length - 1] != '\n') lineCount++;

                if (lineCount <= MaxConsoleLines) return;

                int linesToRemove = lineCount - MaxConsoleLines;
                int cutIndex = 0;
                for (int i = 0; i < linesToRemove; i++)
                {
                    int next = text.IndexOf('\n', cutIndex);
                    if (next < 0) { cutIndex = text.Length; break; }
                    cutIndex = next + 1;
                }

                if (cutIndex > 0 && cutIndex <= text.Length)
                    console.text = text.Substring(cutIndex);
            }
            catch { }
        }

        public void LogSuccess(string msg)
        {
            Append(msg, "#01ff09");
            SVNLogBridge.LogToFile(msg, "MODULE");
        }

        public void LogWarning(string msg)
        {
            Append(msg, "#FFEB3B");
            SVNLogBridge.LogToFile(msg, "MODULE");
        }

        public void LogInfo(string msg) => Append(msg, "#0400ff");

        public void LogErrorLocal(string msg)
        {
            Append(msg, "#610402");
            SVNLogBridge.LogError(msg);
        }

        protected string ResolveAndValidateKeyPath()
        {
            string keyPath = SvnRunner.KeyPath;
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                keyPath = SVNManager.Instance?.CurrentKey;
                if (!string.IsNullOrWhiteSpace(keyPath))
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Using fallback SSH key.");
            }

            if (string.IsNullOrWhiteSpace(keyPath)) return null;

            keyPath = keyPath.Trim().Trim('"').Trim();

            if (keyPath.StartsWith("~"))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                keyPath = Path.Combine(home, keyPath.Substring(1).TrimStart('\\', '/'));
            }

            try { keyPath = Path.GetFullPath(keyPath); }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Invalid SSH key path: {ex.Message}");
                return null;
            }

            if (!File.Exists(keyPath))
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] SSH key not found: {keyPath}");
                SVNLogBridge.LogErrorToOutput("[SVN] Please verify the SSH key path in Settings.");
                return null;
            }

            try
            {
                FileInfo fileInfo = new FileInfo(keyPath);
                if ((fileInfo.Attributes & FileAttributes.ReadOnly) != 0)
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Warning: SSH key is marked as read-only.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Cannot access SSH key: {ex.Message}");
                return null;
            }

            return keyPath;
        }

        protected string BuildSshConfigOption(string keyPath)
        {
            if (string.IsNullOrWhiteSpace(keyPath)) return string.Empty;
            string normalizedKeyPath = keyPath.Replace("\\", "/");
            string nullDevice = Environment.OSVersion.Platform == PlatformID.Win32NT ? "NUL" : "/dev/null";
            string sshCommand = $"ssh -i \"{normalizedKeyPath}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile={nullDevice}";
            return $" --config-option config:tunnels:ssh=\"{sshCommand}\"";
        }

        protected void HandleOperationException(Exception ex)
        {
            bool wasRunning = End();

            SVNLogBridge.LogErrorToOutput(
                $"[SVN][{GetType().Name}] Unhandled operation exception:\n{ex}");
            ShowError(ex.Message);

            if (!wasRunning)
            {
                SVNLogBridge.LogErrorToOutput(
                    "[SVN] Note: processing flag was already clear — exception occurred " +
                    "outside active operation (or was already handled). No flag consumed.");
            }
        }

        protected virtual TextMeshProUGUI GetStatusText() => svnUI?.CheckoutStatusInfoText;

        protected void ShowError(string message)
        {
            PostToMainThread(() =>
            {
                var text = GetStatusText();
                if (text == null)
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN][{GetType().Name}] {message}");
                    return;
                }
                SVNLogBridge.UpdateUIField(text,
                    $"<color=#FFAA00>Error:</color> {message}", "Checkout");
            });
        }

        protected bool IsValidSvnUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            return url.StartsWith("svn://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        protected bool TryValidatePath(string inputPath, out string fullPath)
        {
            fullPath = null;

            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

            if (string.IsNullOrWhiteSpace(inputPath) ||
                inputPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                (isWindows && inputPath.IndexOfAny(ExtraInvalidPathChars) >= 0))
            {
                ShowError("Invalid destination path (empty or contains illegal characters).");
                return false;
            }

            try { fullPath = Path.GetFullPath(inputPath); }
            catch (Exception ex) { ShowError($"Invalid destination path: {ex.Message}"); return false; }

            if (isWindows && HasReservedOrMalformedSegment(fullPath))
            {
                ShowError("Invalid destination path (reserved device name or trailing dot/space).");
                return false;
            }

            if (isWindows)
            {
                try
                {
                    string root = Path.GetPathRoot(fullPath);
                    if (!string.IsNullOrEmpty(root) && root.Length <= 3)
                    {
                        DriveInfo drive = new DriveInfo(root);
                        if (!drive.IsReady)
                        {
                            ShowError($"The drive {root} is not ready. Please choose a valid location.");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput(
                        $"[SVN] Drive check for '{fullPath}' skipped: {ex.Message}");
                }
            }

            return true;
        }

        private static bool HasReservedOrMalformedSegment(string fullPath)
        {
            string[] segments = fullPath.Replace('/', '\\').Split('\\');
            foreach (string segment in segments)
            {
                if (segment.Length == 0) continue;

                if (segment.EndsWith(" ") || segment.EndsWith("."))
                    return true;

                string name = segment;
                int dot = segment.IndexOf('.');
                if (dot >= 0) name = segment.Substring(0, dot);

                if (name.Length == 3 || name.Length == 4)
                {
                    switch (name.ToUpperInvariant())
                    {
                        case "CON":
                        case "PRN":
                        case "AUX":
                        case "NUL":
                        case "COM1":
                        case "COM2":
                        case "COM3":
                        case "COM4":
                        case "COM5":
                        case "COM6":
                        case "COM7":
                        case "COM8":
                        case "COM9":
                        case "LPT1":
                        case "LPT2":
                        case "LPT3":
                        case "LPT4":
                        case "LPT5":
                        case "LPT6":
                        case "LPT7":
                        case "LPT8":
                        case "LPT9":
                            return true;
                    }
                }
            }
            return false;
        }

        protected void PostToMainThread(Action action)
        {
            if (action == null) return;
            UnityMainThreadDispatcher.Enqueue(action);
        }

        protected string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int order = 0;
            while (size >= 1024.0 && order < suffixes.Length - 1)
            {
                order++;
                size /= 1024.0;
            }
            return $"{size:0.##} {suffixes[order]}";
        }
    }
}