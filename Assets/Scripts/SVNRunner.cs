using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public static class SvnRunner
    {
        private static string sshPath = "ssh"; // zakładamy, że ssh.exe jest w PATH
                                               //private static string privateKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".ssh","MontanaGitRepoKey").Replace('\\', '/');

        public static string KeyPath
        {
            get
            {
                if (string.IsNullOrEmpty(_keyPath))
                {
                    _keyPath = PlayerPrefs.GetString("SVN_SSHKeyPath", "");
                }
                return _keyPath;
            }
            set => _keyPath = value;
        }
        private static string _keyPath = "";

        public static async Task<string> RunAsync(string args, string workingDir, bool retryOnLock = true, CancellationToken token = default)
        {
            // 1. Prepare SSH Key path
            string safeKeyPath = KeyPath.Trim().Replace("\"", "").Replace('\\', '/');

            if (string.IsNullOrEmpty(safeKeyPath))
            {
                throw new Exception("Error: SSH Key path is empty!");
            }

            // 2. Process Configuration
            var psi = new ProcessStartInfo
            {
                FileName = "svn",
                Arguments = args,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Setting up SSH tunnel environment variable
            psi.EnvironmentVariables["SVN_SSH"] = $"ssh -i \"{safeKeyPath}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o BatchMode=yes";

            using var process = new Process { StartInfo = psi };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            // 3. Output Handling
            DataReceivedEventHandler outHandler = (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            DataReceivedEventHandler errHandler = (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.OutputDataReceived += outHandler;
            process.ErrorDataReceived += errHandler;

            // 4. Cancellation Registration
            using var registration = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch { /* Process might have already exited */ }
            });

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 5. Wait for Exit with Token and Timeout control
                DateTime startTime = DateTime.Now;
                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        throw new OperationCanceledException("SVN operation cancelled by user.");
                    }

                    if ((DateTime.Now - startTime).TotalSeconds > 45)
                    {
                        try { process.Kill(); } catch { }
                        throw new Exception("SVN Timeout: Operation exceeded 45 seconds.");
                    }

                    await Task.Delay(100);
                }

                // 6. Result Analysis
                if (process.ExitCode != 0)
                {
                    string err = errorBuilder.ToString();

                    // --- SMART RETRY LOGIC (Database Locks) ---
                    if (retryOnLock && (err.Contains("locked") || err.Contains("cleanup")))
                    {
                        UnityEngine.Debug.LogWarning("[SvnRunner] Database lock detected. Attempting automatic Cleanup...");

                        await RunAsync("cleanup", workingDir, false, token);

                        UnityEngine.Debug.Log("[SvnRunner] Cleanup finished. Retrying original command...");
                        return await RunAsync(args, workingDir, false, token);
                    }

                    if (!string.IsNullOrEmpty(err))
                        throw new Exception($"SVN Error (Code {process.ExitCode}): {err}");
                }

                return outputBuilder.ToString();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            finally
            {
                process.OutputDataReceived -= outHandler;
                process.ErrorDataReceived -= errHandler;
            }
        }

        public static async Task<bool> CheckIfSvnInstalled()
        {
            try
            {
                // Używamy folderu tymczasowego systemu, żeby nie przejmować się workingDir
                string tempDir = Path.GetTempPath();

                // Wykonujemy svn --version
                string result = await RunAsync("--version", tempDir);

                // Jeśli w odpowiedzi jest "svn, version", to znaczy że CLI działa
                return result.Contains("svn, version");
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> CheckIfSshInstalled()
        {
            try
            {
                // Wykonujemy 'ssh -V' (wersja)
                // Musimy to zrobić przez Process, bo RunAsync z SVN_SSH 
                // może rzucić błędem jeśli samo SSH nie działa.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ssh",
                    Arguments = "-V",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                string output = await p.StandardError.ReadToEndAsync();
                return output.ToLower().Contains("openssh");
            }
            catch { return false; }
        }

        public static async Task<bool> CheckIfIgnoreIsSet(string workingDir)
        {
            try
            {
                // Sprawdzamy listę zignorowanych plików dla folderu głównego
                string result = await RunAsync("propget svn:ignore .", workingDir);
                // Jeśli lista zawiera "Intermediate", uznajemy że ignore jest ustawiony
                return result.Contains("Intermediate");
            }
            catch { return false; }
        }

        public static bool CheckIfSshKeyExists()
        {
            if (string.IsNullOrEmpty(KeyPath)) return false;
            return File.Exists(KeyPath);
        }

        public static bool CheckWritePermissions(string workingDir)
        {
            try
            {
                if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir)) return false;

                string testPath = Path.Combine(workingDir, ".write_test_" + Path.GetRandomFileName());
                File.WriteAllText(testPath, "temp");
                File.Delete(testPath);
                return true;
            }
            catch { return false; }
        }

        public static bool IsWorkingCopy(string workingDir)
        {
            // Sprawdza czy istnieje ukryty folder .svn
            return Directory.Exists(Path.Combine(workingDir, ".svn"));
        }

        public static async Task<string> CheckoutAsync(string url, string path)
        {
            // Komenda checkout: svn checkout "URL" "PATH"
            // Możesz dodać --non-interactive, aby proces nie czekał na wpisanie hasła w konsoli
            string cmd = $"checkout \"{url}\" \"{path}\" --non-interactive";

            // Ważne: Checkout uruchamiamy bez WorkingDir, bo folder może jeszcze nie istnieć
            return await RunAsync(cmd, "");
        }

        public static async Task<(string status, string[] files)> StatusAsync(string workingDir)
        {
            string output = await RunAsync("status", workingDir);
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return (output, lines);
        }

        public static async Task<string> AddAsync(string workingDir, string[] files)
        {
            if (files == null || files.Length == 0) return "";

            // Dodajemy --parents, aby automatycznie dodać brakujące foldery nadrzędne
            string fileArgs = string.Join(" ", files.Select(f => $"\"{f}\""));
            return await RunAsync($"add {fileArgs} --force --parents", workingDir);
        }

        public static async Task<string> CommitAsync(string workingDir, string[] paths, string message)
        {
            // Jeśli tablica zawiera ".", wysyłamy wszystko
            string pathsArg = (paths != null && paths.Length > 0 && paths[0] == ".") ? "." : "";

            // Jeśli jednak chcesz wysyłać konkretne pliki (gdybyś wrócił do tej opcji):
            if (string.IsNullOrEmpty(pathsArg) && paths != null)
                pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            string cmd = $"commit -m \"{message}\" {pathsArg}";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> UpdateAsync(string workingDir)
        {
            // --accept postpone pozwala pobrać zmiany nawet jeśli wystąpią konflikty
            return await RunAsync("update --accept postpone", workingDir);
        }

        public static async Task<string> LogAsync(string workingDir, int lastN = 10)
        {
            return await RunAsync($"log -l {lastN}", workingDir);
        }

        // Dodaj te metody do klasy SvnRunner

        /// <summary>
        /// Naprawia zablokowane repozytorium (np. po crashu procesu).
        /// </summary>
        public static async Task<string> CleanupAsync(string workingDir)
        {
            // Najpierw próbujemy standardowy cleanup (najbezpieczniejszy, działa wszędzie)
            // Jeśli to nie pomoże, SVN zazwyczaj wymaga tylko podstawowej komendy do zdjęcia blokad.
            try
            {
                // Standardowa komenda cleanup (zdjęcie blokad zapisu)
                return await RunAsync("cleanup", workingDir);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SvnRunner] Standard cleanup failed, trying extended version: {ex.Message}");

                // Jeśli standardowy zawiedzie, próbujemy naprawić bazę (dostępne w nowszych wersjach)
                // Usunąłem --remove-unused-pristines, bo to najczęstsza przyczyna błędów.
                // --include-externals pomaga, jeśli projekt ma podpięte inne repozytoria.
                return await RunAsync("cleanup --include-externals", workingDir);
            }
        }

        public static async Task<string> VacuumCleanupAsync(string workingDir)
        {
            try
            {
                // Dodano --include-externals dla pełnego czyszczenia projektów z bibliotekami zewnętrznymi
                return await RunAsync("cleanup --vacuum-pristines --include-externals", workingDir);
            }
            catch
            {
                // Standardowy fallback, jeśli powyższe flagi nie są wspierane
                return await RunAsync("cleanup", workingDir);
            }
        }

        /// <summary>
        /// Cofa lokalne zmiany w wybranych plikach.
        /// </summary>
        public static async Task<string> RevertAsync(string workingDir, string[] files)
        {
            if (files == null || files.Length == 0) return "No files to revert.";

            // Dodajemy cudzysłowy dla bezpieczeństwa ścieżek
            string fileArgs = string.Join(" ", files.Select(f => $"\"{f}\""));

            // -R oznacza rekurencyjnie (przydatne, gdy revertujemy folder)
            return await RunAsync($"revert -R {fileArgs}", workingDir);
        }

        public static async Task<string> LockAsync(string workingDir, string[] paths)
        {
            if (paths == null || paths.Length == 0) return "Brak plików do zablokowania.";

            // Łączymy ścieżki w cudzysłowach, oddzielone spacjami
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            // --comment "..." jest opcjonalny, ale warto go dodać
            string cmd = $"lock {pathsArg} --comment \"Zablokowane przez Unity SVN Tool\"";

            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> UnlockAsync(string workingDir, string[] paths)
        {
            if (paths == null || paths.Length == 0) return "Brak plików do odblokowania.";

            // Przygotowanie ścieżek w cudzysłowach
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            // Wykonujemy komendę unlock
            string cmd = $"unlock {pathsArg}";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> ResolveAsync(string workingDir, string[] paths, bool useMine)
        {
            if (paths == null || paths.Length == 0) return "No paths to resolve.";

            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));
            string strategy = useMine ? "mine-full" : "theirs-full";

            // svn resolve --accept mine-full file1 file2...
            string cmd = $"resolve --accept {strategy} {pathsArg}";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> DeleteAsync(string workingDir, string[] paths)
        {
            if (paths == null || paths.Length == 0) return "Brak ścieżek do usunięcia.";

            // Przygotowanie ścieżek w cudzysłowach
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            // Używamy --force, aby usunąć pliki, które są 'missing' (!) 
            // lub mają lokalne modyfikacje.
            string cmd = $"delete --force {pathsArg}";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> StatusRemoteAsync(string workingDir)
        {
            // -u (lub --show-updates) kontaktuje się z serwerem
            // Przekazujemy ścieżkę ".", aby sprawdzić cały projekt
            string cmd = "status -u .";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> IgnoreDefaultsAsync(string workingDir)
        {
            // Lista najczęstszych folderów Unreal Engine do zignorowania
            string ignoreList = "Binaries Intermediate Saved DerivedDataCache .vs Build *.sln *.suo *.obj";

            // Używamy propset, aby przypisać listę do właściwości svn:ignore folderu kropka (.)
            // Musimy zamienić spacje na znaki nowej linii dla SVN
            string formattedList = ignoreList.Replace(" ", "\n");

            // Wywołujemy komendę (uważając na cudzysłowy w parametrach)
            string cmd = $"propset svn:ignore \"{formattedList}\" .";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> AddFolderOnlyAsync(string workingDir, string path)
        {
            // --depth empty dodaje sam folder bez dodawania plików, które są w środku
            string cmd = $"add \"{path}\" --depth empty";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> CopyAsync(string workingDir, string sourceUrl, string destUrl, string message)
        {
            // SVN copy wymaga wiadomości, bo tworzy nowy rewizję na serwerze
            string cmd = $"copy \"{sourceUrl}\" \"{destUrl}\" -m \"{message}\"";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> CopyBranchAsync(string workingDir, string sourceUrl, string targetUrl, string message)
        {
            // svn copy URL1 URL2 -m "wiadomość"
            // Cudzysłowy wokół message są kluczowe!
            string args = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"{message}\"";
            return await RunAsync(args, workingDir);
        }

        /// <summary>
        /// Przełącza working directory na inną gałąź (Branch).
        /// </summary>
        public static async Task<string> SwitchAsync(string workingDir, string targetUrl)
        {
            // Komenda switch zmienia powiązanie folderu roboczego z URL-em w repozytorium
            string cmd = $"switch \"{targetUrl}\" --accept postpone";
            return await RunAsync(cmd, workingDir);
        }

        /// <summary>
        /// Scala zmiany z podanego adresu URL do aktualnego working directory.
        /// </summary>
        public static async Task<string> MergeAsync(string workingDir, string sourceUrl)
        {
            // --accept postpone pozwala na dokończenie merge'a nawet przy konfliktach, 
            // abyś mógł je rozwiązać ręcznie później.
            string cmd = $"merge \"{sourceUrl}\" --accept postpone";
            return await RunAsync(cmd, workingDir);
        }

        /// <summary>
        /// Pobiera informacje o bieżącej kopii roboczej (URL, Rewizja itp.).
        /// </summary>
        public static async Task<string> GetInfoAsync(string workingDir)
        {
            // Komenda: svn info
            return await RunAsync("info", workingDir);
        }

        // --- POPRAWIONA FUNKCJA REKURENCYJNA ---
        private static void BuildTreeString(
        string currentDir,
        string rootDir,
        int indent,
        Dictionary<string, (string status, string size)> statusDict, // Poprawiony typ
        StringBuilder sb,
        SvnStats stats,
        HashSet<string> expandedPaths,
        bool[] parentIsLast,
        bool showIgnored,
        HashSet<string> foldersWithRelevantContent)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(currentDir); }
            catch { return; }

            var sortedEntries = entries
                .Select(e => e.Replace('\\', '/'))
                .OrderBy(e => !Directory.Exists(e))
                .ThenBy(e => e)
                .ToArray();

            for (int i = 0; i < sortedEntries.Length; i++)
            {
                string entry = sortedEntries[i];
                string name = Path.GetFileName(entry);

                if (name == ".svn" || name.EndsWith(".meta")) continue;

                bool isLast = (i == sortedEntries.Length - 1);
                bool isDirectory = Directory.Exists(entry);
                string relPath = entry.Replace(rootDir.Replace('\\', '/'), "").TrimStart('/');

                // --- POPRAWKA TUTAJ: Pobieramy krotkę i rozbijamy ją ---
                string status = "";
                string sizeDisplay = "";
                if (statusDict.TryGetValue(relPath, out var statusTuple))
                {
                    status = statusTuple.status;
                    sizeDisplay = statusTuple.size;
                }

                // --- Znajdź to w SvnRunner.cs wewnątrz BuildTreeString ---

                // Fragment wewnątrz pętli for w BuildTreeString:
                if (showIgnored)
                {
                    if (status != "I" && !foldersWithRelevantContent.Contains(relPath)) continue;
                    if (isDirectory && status != "I") status = "I";
                }
                else // Tryb Modified
                {
                    if (status == "I") continue; // Ukrywamy ignorowane

                    if (isDirectory)
                    {
                        bool hasChanges = foldersWithRelevantContent.Contains(relPath);
                        bool isExpanded = expandedPaths.Contains(relPath) || string.IsNullOrEmpty(relPath);

                        // Pokaż folder TYLKO jeśli ma status, prowadzi do zmian lub jest rozwinięty
                        if (string.IsNullOrEmpty(status) && !hasChanges && !isExpanded) continue;

                        // Jeśli folder sam nie ma statusu, ale ma zmiany w środku, oznaczamy go [M]
                        if (string.IsNullOrEmpty(status) && hasChanges) status = "M";
                    }
                    else
                    {
                        // PLIK: Pokaż tylko jeśli ma jakikolwiek status (M, A, ?, !, C)
                        if (string.IsNullOrEmpty(status)) continue;
                    }
                }

                // Statystyki (używamy już zmiennej 'status', która jest stringiem)
                if (isDirectory) stats.FolderCount++;
                else
                {
                    if (status == "I") stats.IgnoredCount++;
                    else
                    {
                        stats.FileCount++;
                        if (status == "M") stats.ModifiedCount++;
                        if (status == "A" || status == "?") stats.NewFilesCount++;
                        if (status == "C") stats.ConflictsCount++;
                        if (status == "!") stats.FileCount++;
                    }
                }

                // Rysowanie
                StringBuilder indentBuilder = new StringBuilder();
                for (int j = 0; j < indent - 1; j++)
                    indentBuilder.Append(parentIsLast[j] ? "    " : "│   ");

                if (indent > 0)
                    indentBuilder.Append(isLast ? "└── " : "├── ");

                string expandIcon = isDirectory ? (expandedPaths.Contains(relPath) ? "[-] " : "[+] ") : "    ";
                string statusIcon = GetStatusIcon(status);
                string typeTag = isDirectory ? "<color=#FFCA28><b><D></b></color>" : "<color=#4FC3F7><F></color>";

                // Używamy gotowego rozmiaru ze słownika
                string sizeInfo = !isDirectory && !string.IsNullOrEmpty(sizeDisplay) ? $" <color=#555555>.... ({sizeDisplay})</color>" : "";

                sb.AppendLine($"{indentBuilder}{statusIcon} {expandIcon}{typeTag} {name}{sizeInfo}");

                if (isDirectory && (expandedPaths.Contains(relPath) || string.IsNullOrEmpty(relPath)))
                {
                    if (indent < parentIsLast.Length) parentIsLast[indent] = isLast;
                    BuildTreeString(entry, rootDir, indent + 1, statusDict, sb, stats, expandedPaths, parentIsLast, showIgnored, foldersWithRelevantContent);
                }
            }
        }

        // --- POPRAWIONA METODA STARTOWA DRZEWA ---
        public static async Task<(string tree, SvnStats stats)> GetVisualTreeWithStatsAsync(string workingDir, HashSet<string> expandedPaths, bool showIgnored = false)
        {
            // 1. POBIERANIE STATUSÓW: Wymuszamy pobranie wszystkiego (w tym plików '?' i 'I')
            Dictionary<string, (string status, string size)> statusDict = await GetFullStatusDictionaryAsync(workingDir, true);

            var sb = new StringBuilder();
            var stats = new SvnStats();

            if (!Directory.Exists(workingDir)) return ("Path error.", stats);

            // 2. IDENTYFIKACJA INTERESUJĄCYCH ŚCIEŻEK
            HashSet<string> foldersWithRelevantContent = new HashSet<string>();

            foreach (var item in statusDict)
            {
                string stat = item.Value.status;
                bool isInteresting = false;

                if (showIgnored)
                {
                    isInteresting = (stat == "I");
                }
                else
                {
                    // Widok zmian: Musi pokazać M (Modified), A (Added), ? (Nowy), ! (Brakujący)
                    // Czyli wszystko co ma status, ale nie jest ignorowane ("I")
                    isInteresting = !string.IsNullOrEmpty(stat) && stat != "I";
                }

                if (isInteresting)
                {
                    // Dodajemy foldery nadrzędne do listy "do pokazania"
                    string[] parts = item.Key.Split('/');
                    string currentFolder = "";
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        currentFolder = string.IsNullOrEmpty(currentFolder) ? parts[i] : $"{currentFolder}/{parts[i]}";
                        foldersWithRelevantContent.Add(currentFolder);
                    }
                }
            }

            // 3. BUDOWANIE DRZEWA
            // Przekazujemy foldersWithRelevantContent, aby rekurencja wiedziała które foldery rozwijać
            BuildTreeString(workingDir, workingDir, 0, statusDict, sb, stats, expandedPaths, new bool[128], showIgnored, foldersWithRelevantContent);

            return (sb.ToString(), stats);
        }

        public static string GetStatusIcon(string status) => status switch
        {
            "M" => "<color=#FFD700><b>[M]</b></color>",
            "A" => "<color=#00FF41><b>[A]</b></color>",
            "I" => "<color=#888888>[I]</color>",
            "?" => "<color=#00E5FF><b>[?]</b></color>",
            "C" => "<color=#FF3D00><b>[C]</b></color>",
            "!" => "<color=#FF00FF><b>[!]</b></color>",
            _ => "<color=#444444>[ ]</color>"
        };

        private static string FormatBytes(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB" };
            int i; double dblSByte = bytes;
            for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024) dblSByte = bytes / 1024.0;
            return $"{dblSByte:0.##}{Suffix[i]}";
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetFullStatusDictionaryAsync(string workingDir, bool includeIgnored = true)
        {
            // Zawsze używamy --no-ignore, żeby widzieć wszystko
            string output = await RunAsync("status --no-ignore", workingDir);
            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length >= 8)
                {
                    // SVN status ma kod na 1. pozycji (index 0)
                    char rawCode = line[0];
                    string path = line.Substring(8).Trim().Replace('\\', '/');
                    string fullPath = Path.Combine(workingDir, path);

                    // Standaryzacja kodu statusu
                    string stat = rawCode.ToString().ToUpper();

                    // LOGIKA:
                    // Jeśli szukamy ignorowanych (I)
                    if (includeIgnored && stat == "I")
                    {
                        statusDict[path] = ("I", GetFileSizeSafe(fullPath));
                    }
                    // Jeśli szukamy zmian (M, A, ?, !, C, D)
                    else if (stat != " " && stat != "I" && stat != "X")
                    {
                        // SVN czasami zwraca '?' dla nowych plików - upewnijmy się, że to łapiemy
                        statusDict[path] = (stat, GetFileSizeSafe(fullPath));
                    }
                }
            }
            return statusDict;
        }

        private static string GetFileSizeSafe(string fullPath)
        {
            if (Directory.Exists(fullPath)) return "";
            try
            {
                long bytes = new FileInfo(fullPath).Length;
                return FormatBytes(bytes);
            }
            catch { return "err"; }
        }

        public static async Task<string> AddWithMetasAsync(string workingDir, string[] files)
        {
            List<string> filesWithMetas = new List<string>();
            foreach (var f in files)
            {
                filesWithMetas.Add(f);
                string metaPath = f + ".meta";
                if (File.Exists(Path.Combine(workingDir, metaPath)))
                    filesWithMetas.Add(metaPath);
            }
            return await AddAsync(workingDir, filesWithMetas.ToArray());
        }

        public static async Task<string> GetRepoUrlAsync(string workingDir)
        {
            // Komenda 'info' zwraca szczegóły o repozytorium
            string output = await RunAsync("info --xml", workingDir);

            // Prosty parsing XML, aby wyciągnąć tag <url>
            if (output.Contains("<url>"))
            {
                int start = output.IndexOf("<url>") + 5;
                int end = output.IndexOf("</url>");
                return output.Substring(start, end - start).Trim();
            }

            throw new Exception("Nie udało się pobrać URL repozytorium.");
        }

        public static async Task<string[]> GetRepoListAsync(string workingDir, string subFolder)
        {
            // 1. Pobieramy bazowy URL repozytorium
            string repoUrl = await GetRepoUrlAsync(workingDir);

            // 2. Wycinamy końcówkę (np. /trunk), aby dostać się do korzenia
            // Jeśli repoUrl to "https://svn.com/repo/trunk", baseUrl będzie "https://svn.com/repo"
            string baseUrl = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));

            // 3. Budujemy pełny adres do podfolderu (branches lub tags)
            string targetUrl = $"{baseUrl}/{subFolder}";

            // 4. Wywołujemy komendę svn ls
            string output = await RunAsync($"ls \"{targetUrl}\"", workingDir);

            // 5. Czyścimy wynik: dzielimy na linie, usuwamy puste i końcowe slashe (/)
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.TrimEnd('/'))
                         .ToArray();
        }

        public static async Task<string> DeleteRemotePathAsync(string workingDir, string remoteUrl, string message)
        {
            // Komenda: svn rm "URL" -m "wiadomość"
            // Parametr --parents jest przydatny, ale przy usuwaniu pojedynczych branchy wystarczy standardowe rm
            string args = $"rm \"{remoteUrl}\" -m \"{message}\"";

            // Uruchamiamy proces i zwracamy wynik (output)
            return await RunAsync(args, workingDir);
        }

        // Dodaj do SvnRunner.cs
        public static async Task<string[]> ListBranchesAsync(string repositoryRootUrl)
        {
            // Czyścimy URL i celujemy w folder branches
            string branchesUrl = repositoryRootUrl.TrimEnd('/') + "/branches";

            // Komenda: svn list [URL]
            string output = await RunAsync($"list {branchesUrl}", "");

            if (string.IsNullOrEmpty(output)) return new string[0];

            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.TrimEnd('/')) // Usuwamy ukośniki z nazw folderów
                         .ToArray();
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir)
        {
            // Wykonujemy komendę svn status
            string output = await RunAsync("status", workingDir);

            SvnStats stats = new SvnStats();
            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                if (line.Length < 8) continue;
                char statusChar = line[0];

                switch (statusChar)
                {
                    case 'M': stats.ModifiedCount++; break;
                    case 'A': stats.AddedCount++; break;
                    case 'D': stats.DeletedCount++; break;
                    case 'C': stats.ConflictsCount++; break;
                    case '?': stats.NewFilesCount++; break;
                    case 'I': stats.IgnoredCount++; break;
                }
            }
            return stats;
        }

        public static async Task<bool> RemotePathExistsAsync(string workingDir, string url)
        {
            try
            {
                // 'svn info' returns 0 (success) if path exists, non-zero if not.
                await RunAsync($"info {url}", workingDir);
                return true;
            }
            catch
            {
                // If the command fails, it usually means the path doesn't exist.
                return false;
            }
        }

    }
}