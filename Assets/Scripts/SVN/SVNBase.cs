using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using TMPro;

namespace SVN.Core
{
    public abstract class SVNBase
    {
        // === FIX (drobiazg): limit linii konsoli modułu (merge loguje blokami;
        // terminal ma własny trim — tu brakowało jakiegokolwiek).
        private const int MaxConsoleLines = 400;

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

        // === FIX K1: Append przez dispatcher. LogInfo/LogSuccess/LogWarning/
        // LogErrorLocal wołane są m.in. z SVNMerge/SVNBranchTag PO ConfigureAwait(false)
        // (thread pool) — 'console.text +=' poza main thread to niezdefiniowane
        // zachowanie Unity. Naprawione RAZ, tutaj, dla wszystkich modułów.
        // (Efekt uboczny: log pojawia się o klatkę później — nieszkodliwe.)
        protected void Append(string msg, string color)
        {
            PostToMainThread(() =>
            {
                var console = GetConsole();
                if (console == null) return;

                console.text += $"<color={color}>{msg}</color>\n";
                TrimConsole(console);
            });
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

        // === Ś3: DEPRECATED — SvnRunner ustawia env SVN_SSH (z aktualnym kluczem)
        // per-proces; ten --config-option jest redundantny i konkurujący. Już
        // wycięty z Merge/BranchTag — JEDYNE żywe użycie: SVNCheckout (do wycięcia
        // jako follow-up; wtedy usunąć tę metodę).
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
            // === FIX (drobiazg): End() zamiast IsProcessing=false — symetria
            // z TryStart() (oba atomowe).
            End();
            SVNLogBridge.LogErrorToOutput($"[SVN] Unhandled operation exception:\n{ex}");
            ShowError(ex.Message);
        }

        // === FIX K2: pole UI wyłącznie przez dispatcher — HandleOperationException
        // bywa wołane z thread poolu (catch w async void po ConfigureAwait(false),
        // np. SVNCheckout.StartCheckout).
        protected void ShowError(string message)
        {
            PostToMainThread(() =>
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                    $"<color=#FFAA00>Error:</color> {message}", "Checkout"));
        }

        // === FIX Ś1: guard na null (kiedyś NRE).
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

            // === FIX Ś2: na nowoczesnym .NET (Unity) Path.GetFullPath NIE waliduje
            // nielegalnych znaków ścieżki ('|', '<', '*', ...?) — GetFullPath przechodzi,
            // a Directory.CreateDirectory/Process.Start rzucają głęboko w operacji.
            // Jawny filtr znaków jako pierwsza linia obrony.
            if (string.IsNullOrWhiteSpace(inputPath) ||
                inputPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                ShowError("Invalid destination path (empty or contains illegal characters).");
                return false;
            }

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