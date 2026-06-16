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
        public static string KeyPath
        {
            get
            {
                if (string.IsNullOrEmpty(_keyPath))
                    _keyPath = PlayerPrefs.GetString("SVN_SSHKeyPath", "");
                return _keyPath;
            }
            set => _keyPath = value ?? "";
        }
        private static string _keyPath = "";

        private static readonly SemaphoreSlim _svnSemaphore = new(1, 1);
        private static readonly SemaphoreSlim _writeSemaphore = new(1, 1);

        public static event Action<bool> OnProcessingStateChanged;
        private static int _activeOperationsCount = 0;
        private static bool _processingState = false;
        private static readonly object _processingLock = new();

        private static DateTime _lastInfoCacheTime = DateTime.MinValue;
        private static string _lastInfoCache = "";
        private static readonly TimeSpan InfoCacheDuration = TimeSpan.FromSeconds(2);

        private static void IncrementOperations()
        {
            int count = Interlocked.Increment(ref _activeOperationsCount);
            lock (_processingLock)
            {
                if (!_processingState && count > 0)
                {
                    _processingState = true;
                    SVNLogBridge.LogLine("<color=#00FFAA>[SVN]</color> Processing START", false);
                    OnProcessingStateChanged?.Invoke(true);
                }
            }
        }

        private static void DecrementOperations()
        {
            int count = Interlocked.Decrement(ref _activeOperationsCount);
            if (count < 0) { _activeOperationsCount = 0; count = 0; }
            lock (_processingLock)
            {
                if (_processingState && count == 0)
                {
                    _processingState = false;
                    SVNLogBridge.LogLine("<color=#FFCC00>[SVN]</color> Processing END", true);
                    OnProcessingStateChanged?.Invoke(false);
                }
            }
        }

        public static async Task<string> RunAsync(
    string args,
    string workingDir,
    bool retryOnLock = true,
    CancellationToken token = default)
        {
            SVNLogBridge.LogLine($"[SVN QUEUE] Waiting: svn {args}", false);

            bool write = IsWriteCommand(args);

            if (write)
                await _writeSemaphore.WaitAsync(token);
            else
                await _svnSemaphore.WaitAsync(token);

            try
            {
                IncrementOperations();

                SVNLogBridge.LogLine($"[SVN QUEUE] Acquired: svn {args}", false);

                if (string.IsNullOrEmpty(workingDir))
                {
                    SVNLogBridge.LogError("Working Directory is null!");
                    throw new Exception("Working Directory is null!");
                }

                SVNLogBridge.LogLine($"[SvnRunner] Preparing command: svn {args}");

                string cleanWorkingDir = Path.GetFullPath(
                    workingDir
                        .Trim()
                        .Where(c => !char.IsControl(c) && (int)c != 160)
                        .ToArray()
                        .Aggregate("", (s, c) => s + c));

                string safeKeyPath = (!string.IsNullOrEmpty(KeyPath))
                    ? KeyPath.Trim().Replace("\"", "").Replace('\\', '/')
                    : "";

                string finalArgs = args.Contains("--non-interactive")
                    ? args
                    : args + " --non-interactive";

                int maxAttempts = retryOnLock ? 2 : 1;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();

                    var psi = new ProcessStartInfo
                    {
                        FileName = "svn",
                        Arguments = finalArgs,
                        WorkingDirectory = cleanWorkingDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    if (!string.IsNullOrEmpty(safeKeyPath))
                    {
                        psi.EnvironmentVariables["SVN_SSH"] =
                            $"ssh -i \"{safeKeyPath}\" " +
                            "-o IdentitiesOnly=yes " +
                            "-o StrictHostKeyChecking=no " +
                            "-o BatchMode=yes " +
                            "-o LogLevel=QUIET " +
                            "-o ServerAliveInterval=15 " +
                            "-o ServerAliveCountMax=10 " +
                            "-o IPQoS=throughput";
                    }

                    using var process = new Process
                    {
                        StartInfo = psi,
                        EnableRaisingEvents = true
                    };

                    SvnProcessTracker.Register(process);

                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();
                    string lastLoggedError = "";

                    DataReceivedEventHandler outHandler = (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            outputBuilder.AppendLine(e.Data);
                    };

                    DataReceivedEventHandler errHandler = (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            string currentError = e.Data.Trim();
                            if (currentError != lastLoggedError)
                            {
                                errorBuilder.AppendLine(currentError);
                                lastLoggedError = currentError;
                            }
                        }
                    };

                    process.OutputDataReceived += outHandler;
                    process.ErrorDataReceived += errHandler;

                    try
                    {
                        SVNLogBridge.LogLine($"[SvnRunner] Starting process...");

                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        await WaitForExitAsync(process, token);
                        process.WaitForExit();

                        string err = errorBuilder.ToString().Trim();

                        if (process.ExitCode != 0)
                        {
                            bool isLockError =
                                err.Contains("locked") ||
                                err.Contains("cleanup");

                            if (attempt == 0 && retryOnLock && isLockError)
                            {
                                SVNLogBridge.LogError(
                                    "[SvnRunner] Lock detected. Running Cleanup..."
                                );

                                SVNLogBridge.LogLine(
                                    "<color=orange>[SVN]</color> Performing automatic cleanup...",
                                    false
                                );

                                await RunAsync(
                                    "cleanup",
                                    workingDir,
                                    false,
                                    token
                                );

                                SVNLogBridge.LogLine(
                                    "<color=green>[SVN]</color> Cleanup completed. Retrying...",
                                    false
                                );

                                continue;
                            }

                            string diagnostic =
                                err.Contains("E170013") ||
                                err.Contains("can't connect")
                                    ? " [Connection/URL issue]"
                                    : err.Contains("E215004")
                                        ? " [Authorization/Password error]"
                                        : "";

                            string fullError =
                                $"SVN Error (Code {process.ExitCode}): {err}{diagnostic}";

                            SVNLogBridge.LogError(fullError);

                            throw new Exception(fullError);
                        }

                        SVNLogBridge.LogLine(
                            $"[SvnRunner] Completed successfully."
                        );

                        string finalOutput = outputBuilder.ToString();

                        if (!string.IsNullOrWhiteSpace(finalOutput))
                        {
                            SVNLogger.LogToFile(
                                $"[SVN OUTPUT]\n{finalOutput.Trim()}",
                                "DEBUG"
                            );
                        }

                        return finalOutput;
                    }
                    catch (OperationCanceledException)
                    {
                        SvnProcessTracker.Kill(process);
                        throw;
                    }
                    finally
                    {
                        process.OutputDataReceived -= outHandler;
                        process.ErrorDataReceived -= errHandler;

                        try
                        {
                            process.CancelOutputRead();
                        }
                        catch { }

                        try
                        {
                            process.CancelErrorRead();
                        }
                        catch { }
                    }
                }

                throw new Exception("SVN retry system failed.");
            }
            finally
            {
                if (write)
                    _writeSemaphore.Release();
                else
                    _svnSemaphore.Release();

                DecrementOperations();
            }
        }

        public static async Task<string> RunLiveAsync(
    string args,
    string workingDir,
    Action<string> onLineReceived,
    CancellationToken token = default)
        {
            SVNLogBridge.LogLine(
                $"[SVN QUEUE] Waiting LIVE: svn {args}"
            );

            bool write = IsWriteCommand(args);

            if (write)
                await _writeSemaphore.WaitAsync(token);
            else
                await _svnSemaphore.WaitAsync(token);

            Process process = null;

            try
            {
                IncrementOperations();

                SVNLogBridge.LogLine(
                    $"[SVN QUEUE] Acquired LIVE: svn {args}"
                );

                string cleanWorkingDir = Path.GetFullPath(
                    new string(
                        (workingDir ?? "")
                            .Trim()
                            .Where(c => !char.IsControl(c) && (int)c != 160)
                            .ToArray()
                    ));

                string safeKeyPath = !string.IsNullOrEmpty(KeyPath)
                    ? KeyPath.Trim().Replace("\"", "").Replace('\\', '/')
                    : "";

                var psi = new ProcessStartInfo
                {
                    FileName = "svn",
                    Arguments =
                        $"{args} --non-interactive --trust-server-cert",
                    WorkingDirectory = cleanWorkingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };

                if (!string.IsNullOrEmpty(safeKeyPath))
                {
                    psi.EnvironmentVariables["SVN_SSH"] =
                        $"ssh -i \"{safeKeyPath}\" " +
                        "-o IdentitiesOnly=yes " +
                        "-o StrictHostKeyChecking=no " +
                        "-o BatchMode=yes " +
                        "-o LogLevel=QUIET " +
                        "-o ServerAliveInterval=15 " +
                        "-o ServerAliveCountMax=10 " +
                        "-o IPQoS=throughput";
                }

                process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                SvnProcessTracker.Register(process);

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;

                    outputBuilder.AppendLine(e.Data);

                    SVNLogger.LogToFile(e.Data, "DEBUG");

                    onLineReceived?.Invoke(e.Data);
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;

                    errorBuilder.AppendLine(e.Data);

                    SVNLogger.LogToFile(e.Data, "ERROR");

                    onLineReceived?.Invoke($"[SVN ERROR] {e.Data}");
                };

                SVNLogBridge.LogLine(
                    "[SvnRunner Live] Starting process..."
                );

                if (!process.Start())
                {
                    throw new Exception(
                        "Failed to start SVN process."
                    );
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await WaitForExitAsync(process, token);

                token.ThrowIfCancellationRequested();

                string output = outputBuilder.ToString();
                string errors = errorBuilder.ToString();

                if (process.ExitCode != 0)
                {
                    string finalError =
                        $"SVN Error (Code {process.ExitCode})\n{errors}";

                    SVNLogBridge.LogError(finalError);

                    throw new Exception(finalError);
                }

                SVNLogBridge.LogLine(
                    "[SvnRunner Live] Completed successfully."
                );

                return output;
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine(
                    "<color=#FFD700>[CANCEL]</color> SVN operation canceled.",
                    false
                );

                if (process != null)
                {
                    try
                    {
                        SvnProcessTracker.Kill(process);
                    }
                    catch { }
                }

                throw;
            }
            finally
            {
                if (process != null)
                {
                    try
                    {
                        process.CancelOutputRead();
                    }
                    catch { }

                    try
                    {
                        process.CancelErrorRead();
                    }
                    catch { }

                    try
                    {
                        process.Dispose();
                    }
                    catch { }
                }

                if (write)
                    _writeSemaphore.Release();
                else
                    _svnSemaphore.Release();

                DecrementOperations();
            }
        }

        private static async Task WaitForExitAsync(Process process, CancellationToken token = default)
        {
            process.Refresh();
            if (process.HasExited) return;

            while (!process.HasExited && !token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                process.Refresh();
            }

            if (!process.HasExited && token.IsCancellationRequested)
            {
                try { process.Kill(); } catch { }
            }
        }

        public static async Task WaitForSemaphoreFreeAsync(CancellationToken token = default)
        {
            await _svnSemaphore.WaitAsync(token);
            _svnSemaphore.Release();
        }

        public static async Task<string> GetInfoAsync(string workingDir, CancellationToken token = default)
        {
            if (!string.IsNullOrWhiteSpace(_lastInfoCache))
            {
                TimeSpan age = DateTime.Now - _lastInfoCacheTime;
                if (age < InfoCacheDuration)
                {
                    SVNLogBridge.LogLine("<color=#8888FF>[SVN CACHE]</color> Using cached svn info", false);
                    return _lastInfoCache;
                }
            }

            string result = await RunAsync("info", workingDir, true, token);
            _lastInfoCache = result;
            _lastInfoCacheTime = DateTime.Now;
            return result;
        }

        public static async Task<string> GetInfoAsync(string workingDir)
        {
            return await GetInfoAsync(workingDir, CancellationToken.None);
        }

        private static bool IsWriteCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return false;
            string firstToken = args.TrimStart().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstToken == null) return false;
            return WriteCommands.Contains(firstToken.ToLowerInvariant());
        }

        private static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
{
    "update", "commit", "lock", "unlock", "switch", "cleanup", "revert",
    "merge", "copy", "delete", "mkdir", "propset", "propdel",
    "shelf-save", "shelf-restore", "shelf-drop",
    "import", "export"
};

        public static void BuildTreeString(
    string currentDir,
    string rootDir,
    int indent,
    Dictionary<string, (string status, string size)> statusDict,
    StringBuilder sb,
    SvnStats stats,
    HashSet<string> expandedPaths,
    bool[] parentIsLast,
    bool showIgnored,
    HashSet<string> foldersWithRelevantContent)
        {
            string normRootDir = rootDir.Replace('\\', '/').TrimEnd('/');
            string normCurrentDir = currentDir.Replace('\\', '/').TrimEnd('/');

            string currentRelDir = "";
            if (normCurrentDir.Length > normRootDir.Length)
            {
                currentRelDir = normCurrentDir.Substring(normRootDir.Length).TrimStart('/').Replace('\\', '/');
            }

            HashSet<string> combinedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(normCurrentDir))
            {
                try
                {
                    foreach (var fsEntry in Directory.GetFileSystemEntries(normCurrentDir))
                    {
                        string cleanFsEntry = fsEntry.Replace('\\', '/');
                        combinedEntries.Add(cleanFsEntry);
                    }
                }
                catch { }
            }

            foreach (var kvp in statusDict)
            {
                string svnPath = kvp.Key.Replace('\\', '/').Trim('/');

                int lastSlash = svnPath.LastIndexOf('/');
                string svnParent = (lastSlash == -1) ? "" : svnPath.Substring(0, lastSlash);

                if (string.Equals(svnParent, currentRelDir, StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = $"{normRootDir}/{svnPath}";
                    combinedEntries.Add(fullPath);
                }
            }

            foreach (var fPath in foldersWithRelevantContent)
            {
                string f = fPath.Replace('\\', '/').Trim('/');
                int lastSlash = f.LastIndexOf('/');
                string fParent = (lastSlash == -1) ? "" : f.Substring(0, lastSlash);

                if (string.Equals(fParent, currentRelDir, StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = $"{normRootDir}/{f}";
                    combinedEntries.Add(fullPath);
                }
            }

            var allEntries = combinedEntries
                .OrderBy(e =>
                {
                    string rP = e.Length > normRootDir.Length ? e.Substring(normRootDir.Length).TrimStart('/') : "";
                    bool isDir = Directory.Exists(e) || foldersWithRelevantContent.Contains(rP) || string.IsNullOrEmpty(Path.GetExtension(e));
                    return !isDir;
                })
                .ThenBy(e => e)
                .ToArray();

            for (int i = 0; i < allEntries.Length; i++)
            {
                string entry = allEntries[i];
                string name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name == ".svn" || name.EndsWith(".meta")) continue;

                string relPath = entry.Length > normRootDir.Length
                    ? entry.Substring(normRootDir.Length).TrimStart('/')
                    : "";

                relPath = relPath.Replace('\\', '/');

                string status = "";
                string sizeDisplay = "";
                if (statusDict.TryGetValue(relPath, out var statusTuple))
                {
                    status = statusTuple.status;
                    sizeDisplay = statusTuple.size;
                }

                bool isDirectory = Directory.Exists(entry) || foldersWithRelevantContent.Contains(relPath) ||
                                  (string.IsNullOrEmpty(Path.GetExtension(name)) && (status == "!" || status == "D"));
                bool isLast = (i == allEntries.Length - 1);

                if (!showIgnored)
                {
                    if (status == "I") continue;
                    if (isDirectory)
                    {
                        if (string.IsNullOrEmpty(status) && !foldersWithRelevantContent.Contains(relPath) && !expandedPaths.Contains(relPath))
                            continue;
                    }
                    else if (string.IsNullOrEmpty(status)) continue;
                }

                if (!isDirectory)
                {
                    if (status != "" && status != "I")
                    {
                        stats.FileCount++;
                        if (status == "M") stats.ModifiedCount++;
                        else if (status == "A" || status == "?") stats.NewFilesCount++;
                        else if (status == "C") stats.ConflictsCount++;
                        else if (status == "!" || status == "D") stats.DeletedCount++;
                    }
                }
                else
                {
                    stats.FolderCount++;
                    if (status == "!" || status == "D") stats.DeletedCount++;
                }

                StringBuilder indentStr = new StringBuilder();
                for (int j = 0; j < indent - 1; j++)
                    indentStr.Append(parentIsLast[j] ? "    " : "│   ");

                if (indent > 0)
                    indentStr.Append(isLast ? "└── " : "├── ");

                string expandIcon = isDirectory ? (expandedPaths.Contains(relPath) ? "[-] " : "[+] ") : "    ";
                string statusIcon = GetStatusIcon(status);
                string typeTag = isDirectory ? "<color=#FFCA28><b><D></b></color>" : "<color=#4FC3F7><F></color>";
                string displayName = (status == "!" || status == "D") ? $"<color=#FF4444>{name}</color>" : name;
                string sizeStr = (!isDirectory && !string.IsNullOrEmpty(sizeDisplay)) ? $" <color=#555555>({sizeDisplay})</color>" : "";

                sb.AppendLine($"{indentStr}{statusIcon} {expandIcon}{typeTag} {displayName}{sizeStr}");

                if (isDirectory && (expandedPaths.Contains(relPath) || string.IsNullOrEmpty(relPath) || foldersWithRelevantContent.Contains(relPath)))
                {
                    if (indent < parentIsLast.Length) parentIsLast[indent] = isLast;
                    BuildTreeString(entry, rootDir, indent + 1, statusDict, sb, stats, expandedPaths, parentIsLast, showIgnored, foldersWithRelevantContent);
                }
            }
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

        public static async Task<Dictionary<string, (string status, string size)>> GetFullStatusDictionaryAsync(
    string workingDir,
    bool includeIgnored = true)
        {
            string cleanWorkingDir = Path.GetFullPath(workingDir.Trim());

            string output = await RunAsync("status --no-ignore", cleanWorkingDir);

            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output))
                return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                string line = rawLine;

                if (line.Length < 9)
                    continue;

                char contentStatus = line[0];
                char propStatus = line[1];

                string stat = contentStatus.ToString();

                if (stat == " " && propStatus == 'C')
                    stat = "C";

                string pathPart = line.Length >= 9 ? line.Substring(8).TrimStart() : "";

                if (string.IsNullOrWhiteSpace(pathPart))
                    continue;

                string rawPath = new string(pathPart
                    .Where(c =>
                        !char.IsControl(c) &&
                        c != '\t' &&
                        c != '\u00A0')
                    .ToArray())
                    .Trim();

                if (string.IsNullOrWhiteSpace(rawPath))
                    continue;

                string cleanPath = rawPath
                    .Replace('\\', '/')
                    .Trim('/');

                bool isRelevant =
                    "MA?!DC".Contains(stat) ||
                    (includeIgnored && stat == "I");

                if (!isRelevant)
                    continue;

                string fullPath = Path.Combine(cleanWorkingDir, rawPath);

                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    statusDict[cleanPath] = (stat, "");
                    continue;
                }

                string size = "";

                try
                {
                    size = GetFileSizeSafe(fullPath);
                }
                catch
                {
                    size = "";
                }

                statusDict[cleanPath] = (stat, size);
            }

            SVNLogBridge.LogLine($"<color=green>[SVN]</color> Parser finished. Dictionary count: {statusDict.Count}");
            return statusDict;
        }

        public static string GetFileSizeSafe(string fullPath)
        {
            if (Directory.Exists(fullPath) || !File.Exists(fullPath)) return "";

            try
            {
                FileInfo fi = new FileInfo(fullPath);
                return FormatBytes(fi.Length);
            }
            catch
            {
                return "";
            }
        }

        public static async Task<string> GetRepoUrlAsync(string workingDir)
        {
            string output = await RunAsync("info --xml", workingDir);

            if (!string.IsNullOrEmpty(output) && output.Contains("<url>"))
            {
                int start = output.IndexOf("<url>") + 5;
                int end = output.IndexOf("</url>");
                return output.Substring(start, end - start).Trim();
            }

            string errorMsg = "Failed to retrieve repository URL. Is this a valid SVN working copy?";

            SVNLogBridge.LogError(errorMsg);

            throw new Exception(errorMsg);
        }

        public static async Task<string[]> GetRepoListAsync(string workingDir, string subFolder)
        {
            string targetUrl = "";

            if (subFolder.Contains("://") || subFolder.StartsWith("^"))
            {
                targetUrl = subFolder;
            }
            else
            {
                string repoUrl = await GetRepoUrlAsync(workingDir);
                repoUrl = repoUrl.TrimEnd('/');

                string projectRoot = repoUrl;

                if (repoUrl.Contains("/trunk"))
                    projectRoot = repoUrl.Substring(0, repoUrl.IndexOf("/trunk"));
                else if (repoUrl.Contains("/branches/"))
                    projectRoot = repoUrl.Substring(0, repoUrl.IndexOf("/branches/"));
                else if (repoUrl.Contains("/tags/"))
                    projectRoot = repoUrl.Substring(0, repoUrl.IndexOf("/tags/"));
                else if (repoUrl.Contains("/"))
                    projectRoot = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));

                targetUrl = $"{projectRoot}/{subFolder}";
            }

            string output = await RunAsync($"ls \"{targetUrl}\"", workingDir);

            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.TrimEnd('/'))
                         .ToArray();
        }

        public static string CleanSvnPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            string p = path.Replace('\\', '/').Trim(' ', '"', '/');

            if (p.StartsWith("./")) p = p.Substring(2);

            return p;
        }
    }
}