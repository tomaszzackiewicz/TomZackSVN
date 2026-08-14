using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using TMPro;

namespace SVN.Core
{
    public abstract class SVNBase
    {
        public SVNManager svnManager;
        public SVNUI svnUI;

        private int _isProcessing = 0; // 0 = false, 1 = true

        public bool IsProcessing
        {
            get => Interlocked.CompareExchange(ref _isProcessing, 0, 0) == 1;
            set => Interlocked.Exchange(ref _isProcessing, value ? 1 : 0);
        }

        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0;
        }

        public void End()
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }

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

            string pattern = @"\*+\s*WARNING![\s\S]*?@{5,}";
            string cleaned = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);

            string[] lines = cleaned.Split(
                new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None);

            var finalLines = new System.Collections.Generic.List<string>(lines.Length);

            foreach (var line in lines)
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("*****") || trimmed.StartsWith("@@@@@") ||
                    trimmed.EndsWith("*****") || trimmed.EndsWith("@@@@@"))
                {
                    continue;
                }

                finalLines.Add(line);
            }

            return string.Join("\n", finalLines);
        }

        protected virtual TMP_Text GetConsole() => null;

        protected void Append(string msg, string color)
        {
            var console = GetConsole();
            if (console != null)
                console.text += $"<color={color}>{msg}</color>\n";
        }

        public void LogInfo(string msg) => Append(msg, "#0400ff");
        public void LogSuccess(string msg) => Append(msg, "#01ff09");
        public void LogWarning(string msg) => Append(msg, "#FFEB3B");

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

            keyPath = keyPath.Replace("\"", string.Empty).Trim();
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
            IsProcessing = false;
            SVNLogBridge.LogErrorToOutput($"[SVN] Unhandled operation exception:\n{ex}");
            ShowError(ex.Message);
        }

        protected void ShowError(string message)
        {
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, $"<color=#FFAA00>Error:</color> {message}", "Checkout");
        }

        protected bool IsValidSvnUrl(string url)
        {
            return url.StartsWith("svn://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        protected bool TryValidatePath(string inputPath, out string fullPath)
        {
            fullPath = null;
            try { fullPath = Path.GetFullPath(inputPath); }
            catch (Exception ex) { ShowError($"Invalid destination path: {ex.Message}"); return false; }

            try
            {
                string root = Path.GetPathRoot(fullPath);
                if (!string.IsNullOrEmpty(root))
                {
                    DriveInfo drive = new DriveInfo(root);
                    if (!drive.IsReady)
                    {
                        ShowError($"The drive {root} is not ready. Please choose a valid location.");
                        return false;
                    }
                }
            }
            catch (Exception ex) { ShowError($"Cannot access destination drive: {ex.Message}"); return false; }

            return true;
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