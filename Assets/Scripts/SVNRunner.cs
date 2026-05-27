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
                {
                    _keyPath = PlayerPrefs.GetString("SVN_SSHKeyPath", "");
                }
                return _keyPath;
            }
            set => _keyPath = value ?? "";
        }
        private static string _keyPath = "";

        private static readonly SemaphoreSlim _svnSemaphore = new(1, 1);

        public static event Action<bool> OnProcessingStateChanged;
        private static int _activeOperationsCount = 0;

        private static void IncrementOperations()
        {
            if (System.Threading.Interlocked.Increment(ref _activeOperationsCount) == 1)
            {
                OnProcessingStateChanged?.Invoke(true);
            }
        }

        private static void DecrementOperations()
        {
            if (System.Threading.Interlocked.Decrement(ref _activeOperationsCount) == 0)
            {
                OnProcessingStateChanged?.Invoke(false);
            }
        }

        public static async Task<string> RunAsync(
    string args,
    string workingDir,
    bool retryOnLock = true,
    CancellationToken token = default)
        {
            SVNLogBridge.LogLine($"[SVN QUEUE] Waiting: svn {args}");

            IncrementOperations();

            try
            {
                await _svnSemaphore.WaitAsync(token);

                try
                {
                    SVNLogBridge.LogLine($"[SVN QUEUE] Acquired: svn {args}");

                    return await RunInternalAsync(
                        args,
                        workingDir,
                        retryOnLock,
                        token
                    );
                }
                finally
                {
                    _svnSemaphore.Release();
                }
            }
            finally
            {
                DecrementOperations();
            }
        }

        private static async Task<string> RunInternalAsync(
    string args,
    string workingDir,
    bool retryOnLock,
    CancellationToken token)
        {
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

            string finalArgs = args.Contains("--non-interactive") ? args : args + " --non-interactive";

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
                    $"ssh -v -i \"{safeKeyPath}\" " +
                    "-o IdentitiesOnly=yes " +
                    "-o StrictHostKeyChecking=no " +
                    "-o BatchMode=yes " +
                    "-o ServerAliveInterval=30 " +
                    "-o ServerAliveCountMax=5";
            }

            var process = new Process { StartInfo = psi };
            SvnProcessTracker.Register(process);

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            string lastLoggedError = "";

            DataReceivedEventHandler outHandler = (s, e) => { if (!string.IsNullOrEmpty(e.Data)) outputBuilder.AppendLine(e.Data); };
            DataReceivedEventHandler errHandler = (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    string currentError = e.Data.Trim();
                    if (currentError != lastLoggedError) { errorBuilder.Clear(); errorBuilder.AppendLine(currentError); lastLoggedError = currentError; }
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

                while (!process.HasExited)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(100, token);
                }

                process.WaitForExit(100);

                if (process.ExitCode != 0)
                {
                    string err = errorBuilder.ToString().Trim();

                    if (retryOnLock && (err.Contains("locked") || err.Contains("cleanup")))
                    {
                        SVNLogBridge.LogError("[SvnRunner] Lock detected. Running Cleanup...");
                        await RunInternalAsync("cleanup", workingDir, false, token);
                        return await RunInternalAsync(args, workingDir, false, token);
                    }

                    if (!string.IsNullOrEmpty(err))
                    {
                        string diagnostic = err.Contains("E170013") || err.Contains("can't connect") ? " [Connection/URL issue]" : (err.Contains("E215004") ? " [Authorization/Password error]" : "");
                        string fullError = $"SVN Error (Code {process.ExitCode}): {err}{diagnostic}";
                        SVNLogBridge.LogError(fullError);
                        throw new Exception(fullError);
                    }
                }

                SVNLogBridge.LogLine($"[SvnRunner] Completed successfully.");

                string finalOutput = outputBuilder.ToString();
                if (!string.IsNullOrWhiteSpace(finalOutput))
                    SVNLogger.LogToFile($"[SVN OUTPUT]\n{finalOutput.Trim()}", "DEBUG");

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
                process.Dispose();
            }
        }

        public static async Task<string> RunLiveAsync(
            string args,
            string workingDir,
            Action<string> onLineReceived,
            CancellationToken token = default)
        {
            SVNLogBridge.LogLine($"[SVN QUEUE] Waiting LIVE: svn {args}");

            await _svnSemaphore.WaitAsync(token);

            try
            {
                SVNLogBridge.LogLine($"[SVN QUEUE] Acquired LIVE: svn {args}");
                SVNLogBridge.LogLine($"[SvnRunner Live] Preparing stream operation for: {args}");

                string cleanWorkingDir = Path.GetFullPath(new string(
                    (workingDir ?? "")
                        .Trim()
                        .Where(c => !char.IsControl(c) && (int)c != 160)
                        .ToArray()));

                string safeKeyPath = !string.IsNullOrEmpty(KeyPath)
                    ? KeyPath.Trim().Replace("\"", "").Replace('\\', '/')
                    : "";

                var psi = new ProcessStartInfo
                {
                    FileName = "svn",
                    Arguments = $"{args} --non-interactive --trust-server-cert",
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
                        "-o ServerAliveInterval=15 " +
                        "-o ServerAliveCountMax=10 " +
                        "-o IPQoS=throughput";
                }

                var process = new Process { StartInfo = psi };
                SvnProcessTracker.Register(process);

                var processExitTcs = new TaskCompletionSource<bool>();
                bool hasError = false;

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        SVNLogger.LogToFile(e.Data, "DEBUG");
                        onLineReceived?.Invoke(e.Data);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        SVNLogger.LogToFile(e.Data, "ERROR");

                        if (e.Data.Contains("E:") || e.Data.ToLower().Contains("error"))
                        {
                            hasError = true;
                        }

                        onLineReceived?.Invoke($"[SVN ERROR]: {e.Data}");
                    }
                };

                try
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (s, e) => processExitTcs.TrySetResult(true);

                    SVNLogBridge.LogLine("[SvnRunner Live] Opening SVN process stream...");

                    DateTime startTime = DateTime.Now;

                    if (!process.Start())
                    {
                        string startupError = "Critical Error: Failed to start SVN process.";
                        SVNLogBridge.LogError(startupError);
                        throw new Exception(startupError);
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    using (token.Register(() =>
                    {
                        try
                        {
                            SvnProcessTracker.Kill(process);
                            processExitTcs.TrySetCanceled();
                        }
                        catch (Exception ex)
                        {
                            SVNLogBridge.LogError($"Error while killing process: {ex.Message}");
                        }
                    }))
                    {
                        await processExitTcs.Task;
                    }

                    const int processExitWaitTimeMs = 100;
                    process.WaitForExit(processExitWaitTimeMs);

                    double elapsed = (DateTime.Now - startTime).TotalSeconds;
                    SVNLogBridge.LogLine($"[SvnRunner Live] Stream closed after {elapsed:F2}s. Final status: {(hasError ? "Errors detected" : "Success")}");

                    return hasError ? "Error" : "Success";
                }
                catch (OperationCanceledException)
                {
                    SVNLogBridge.LogLine("<color=#FFD700>[CANCEL]</color> Task was successfully canceled.", append: true);
                    return "Canceled";
                }
                finally
                {
                    process.Dispose();
                }
            }
            finally
            {
                _svnSemaphore.Release();
            }
        }

        private static bool IsLongRunningOperation(string args)
        {
            if (string.IsNullOrEmpty(args)) return false;
            string low = args.ToLower();

            string[] longOps = { "commit", "update", "checkout", "switch", "export", "upgrade" };

            return longOps.Any(op => low.Contains(op)) || low.Contains("--targets");
        }

        public static async Task<string> GetInfoAsync(string workingDir)
        {
            return await RunAsync("info", workingDir);
        }

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

                string pathPart = line.Length >= 9 ? line.Substring(8) : "";

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
        public static async Task<string> GetCommitSizeReportAsync(Dictionary<string, string> changedFiles, string workingDir)
        {
            if (changedFiles == null || changedFiles.Count == 0)
            {
                return "No files to commit. Total Size: 0.00 KB";
            }

            SVNLogBridge.LogLine($"[SVN] Calculating commit size report for {changedFiles.Count} entries...");

            string safeWorkingDir = !string.IsNullOrEmpty(workingDir)
                ? workingDir.Trim().Replace("\"", "").Replace('\\', '/')
                : "";

            long totalBytes = 0;
            int directMatches = 0;
            int fallbackMatches = 0;
            int missingFiles = 0;

            await Task.Run(() =>
            {
                foreach (var entry in changedFiles)
                {
                    string filePath = entry.Key;
                    string status = entry.Value?.Trim().ToUpper() ?? "";

                    if (status != "M" && status != "A" && status != "?")
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(filePath)) continue;

                    string normalizedFilePath = filePath.Trim().Replace("\"", "").Replace('\\', '/');
                    string fullPath = Path.Combine(safeWorkingDir, normalizedFilePath).Replace('\\', '/');

                    if (File.Exists(fullPath))
                    {
                        totalBytes += new FileInfo(fullPath).Length;
                        directMatches++;
                    }
                    else
                    {
                        string fileName = Path.GetFileName(normalizedFilePath);
                        bool foundByFallback = false;

                        if (!string.IsNullOrEmpty(safeWorkingDir) && Directory.Exists(safeWorkingDir))
                        {
                            try
                            {
                                var foundFiles = Directory.EnumerateFiles(safeWorkingDir, fileName, SearchOption.AllDirectories);
                                string firstMatch = foundFiles.FirstOrDefault();

                                if (firstMatch != null)
                                {
                                    totalBytes += new FileInfo(firstMatch).Length;
                                    fallbackMatches++;
                                    foundByFallback = true;
                                    SVNLogBridge.LogLine($"[SVN Fallback] Located file by name: {fileName} -> {firstMatch}");
                                }
                            }
                            catch (Exception ex)
                            {
                                SVNLogger.LogToFile($"Error during fallback search for {fileName}: {ex.Message}", "ERROR");
                            }
                        }

                        if (!foundByFallback)
                        {
                            missingFiles++;
                            SVNLogger.LogToFile($"Could not locate local file for size report: {normalizedFilePath}", "WARNING");
                        }
                    }
                }
            });

            const double BytesInKB = 1024.0;
            const double BytesInMB = 1024.0 * 1024.0;

            string sizeReport;
            if (totalBytes >= BytesInMB)
            {
                double sizeInMB = totalBytes / BytesInMB;
                sizeReport = $"{sizeInMB:F2} MB";
            }
            else
            {
                double sizeInKB = totalBytes / BytesInKB;
                sizeReport = $"{sizeInKB:F2} KB";
            }

            int totalProcessedFiles = directMatches + fallbackMatches;
            string statusSummary = $"Processed: {totalProcessedFiles} files (Direct: {directMatches}, Fallback: {fallbackMatches}, Missing: {missingFiles}). Total Size: {sizeReport}";

            SVNLogBridge.LogLine($"[SVN] Commit size calculation finished. {statusSummary}");

            return statusSummary;
        }
    }
}