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

        // === FIX A: '*' i '?' NIE są w Path.GetInvalidPathChars (nielegalne
        // tylko w nazwach plików) — przechodziły przez walidację i wybuchały
        // dopiero w Directory.CreateDirectory/Process.Start.
        private static readonly char[] ExtraInvalidPathChars = { '*', '?' };

        // === OPT: wzorzec jako statyczne pole.
        private static readonly Regex BannerRegex =
            new Regex(@"\*+\s*WARNING![\s\S]*?@{5,}", RegexOptions.IgnoreCase);

        // === FIX D: bufor koaleskujący — wcześniej każdy Append = odczyt CAŁEGO
        // text + konkatenacja + set + Trim → pełny re-layout TMP per LINIĘ
        // (merge loguje blokami → dziesiątki re-layoutów na operację).
        // Teraz: linie kumulowane pod lockiem, JEDEN flush na tick dispatchera.
        private readonly object _consoleLock = new object();
        private readonly StringBuilder _consoleBuffer = new StringBuilder(1024);
        private int _consoleFlushQueued; // 0 = nie, 1 = flush już w kolejce

        public SVNManager svnManager;
        public SVNUI svnUI;

        private int _isProcessing; // 0 = false, 1 = true

        public bool IsProcessing
        {
            get => Interlocked.CompareExchange(ref _isProcessing, 0, 0) == 1;
            // Setter celowo zostaje publiczny — nie widzę pozostałych modułów
            // (Checkout/Merge/BranchTag) i nie chcę zepsuć ich kompilacji.
            // Preferuj TryStart()/End().
            set => Interlocked.Exchange(ref _isProcessing, value ? 1 : 0);
        }

        public bool TryStart() => Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0;

        public void End() => Interlocked.Exchange(ref _isProcessing, 0);

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

        // === FIX K1 (zachowany) + FIX D: append kumulowany. Thread-safe (lock) —
        // wołane także z puli wątków po ConfigureAwait(false).
        // Notka: linie zalogowane MIĘDZY queue'owaniem flusha a jego
        // wykonaniem dołączą do tego samego chunka (mikro-reordering
        // względem innych akcji w kolejce — kosmetyka).
        protected void Append(string msg, string color)
        {
            lock (_consoleLock)
            {
                _consoleBuffer.Append("<color=").Append(color).Append('>')
                              .Append(msg).AppendLine("</color>");
            }

            if (Interlocked.CompareExchange(ref _consoleFlushQueued, 1, 0) == 0)
                PostToMainThread(FlushConsoleBuffer);
        }

        private void FlushConsoleBuffer()
        {
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
                _consoleFlushQueued = 0; // appendy po tym punkcie zaqueue'ują nowy flush
            }

            var console = GetConsole();
            if (console == null) return; // paritet: moduł bez konsoli → brak UI (plik loguje wołający)

            try
            {
                console.text += chunk;
                TrimConsole(console);
            }
            catch { /* UI zniszczone w teardown — ignorujemy */ }
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

        // UWAGA (sink, do decyzji): LogInfo NIE trafia do pliku, podczas gdy
        // Success/Warning/Error tak. Jeśli to zamierzone (chatter tylko do UI)
        // — zostaw; jeśli nie — dodaj SVNLogBridge.LogToFile(msg, "MODULE").
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

            // === FIX E: Trim('"') zamiast Replace("\"", "") — tamten wycinał
            // WSZYSTKIE cudzysłowy (cicha mutacja ścieżki). Cudzysłów w środku
            // ścieżki i tak spadnie na File.Exists z czytelnym błędem.
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

        // === Ś3: DEPRECATED — bez zmian (ostatnie żywe użycie: SVNCheckout;
        // po jego oczyszczeniu usunąć całą metodę).
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
            // UWAGA: End() bezwarunkowo zeruje flagę — wywołane spoza własnego
            // finally może zgasić flagę równoległej operacji na tej instancji.
            // Zachowane dla paritetu z SVNCheckout; docelowo wołaj End() w
            // finally operacji, a tu tylko loguj.
            End();
            SVNLogBridge.LogErrorToOutput($"[SVN] Unhandled operation exception:\n{ex}");
            ShowError(ex.Message);
        }

        // === FIX C: pole statusu było zahardkodowane do CheckoutStatusInfoText —
        // błędy z merge/blame/branch lądowały w panelu checkoutu. Wirtualny
        // target; domyślne zachowanie bez zmian (moduły mogą nadpisać).
        protected virtual TMP_Text GetStatusText() => svnUI?.CheckoutStatusInfoText;

        // === FIX K2: pole UI wyłącznie przez dispatcher.
        protected void ShowError(string message)
        {
            PostToMainThread(() =>
                SVNLogBridge.UpdateUIField(GetStatusText(),
                    $"<color=#FFAA00>Error:</color> {message}", "Checkout"));
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

            // === FIX Ś2 + A: jawny filtr znaków (GetFullPath nie waliduje na
            // .NET Core/Unity) — teraz także '*' i '?' (tylko Windows; na
            // unixach to legalne znaki nazw plików).
            if (string.IsNullOrWhiteSpace(inputPath) ||
                inputPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                (isWindows && inputPath.IndexOfAny(ExtraInvalidPathChars) >= 0))
            {
                ShowError("Invalid destination path (empty or contains illegal characters).");
                return false;
            }

            try { fullPath = Path.GetFullPath(inputPath); }
            catch (Exception ex) { ShowError($"Invalid destination path: {ex.Message}"); return false; }

            // === FIX A: nazwy zarezerwowane Windows (CON, PRN, AUX, NUL,
            // COM1-9, LPT1-9 — także z "rozszerzeniem", np. "CON.txt") oraz
            // końcowe kropki/spacje przechodzą przez GetFullPath, a potem
            // Win32 cicho obcina kropki/spacje (powstaje INNA ścieżka niż
            // pokazana) albo CreateDirectory failuje enigmatycznie.
            if (isWindows && HasReservedOrMalformedSegment(fullPath))
            {
                ShowError("Invalid destination path (reserved device name or trailing dot/space).");
                return false;
            }

            // UWAGA (Windows-centryzm, paritet z oryginałem): DriveInfo na
            // unixowym rootzie ("/") może rzucać → catch odrzuca POPRAWNĄ
            // ścieżkę. Przy wsparciu unix trzeba tu ifa na OS.
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

        private static bool HasReservedOrMalformedSegment(string fullPath)
        {
            string[] segments = fullPath.Replace('/', '\\').Split('\\');
            foreach (string segment in segments)
            {
                if (segment.Length == 0) continue;

                if (segment.EndsWith(" ") || segment.EndsWith("."))
                    return true;

                // zarezerwowana jest część przed pierwszą kropką ("CON.txt" = CON)
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