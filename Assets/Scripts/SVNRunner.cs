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
            set => _keyPath = value;
        }
        private static string _keyPath = "";

        public static async Task<string> RunAsync(string args, string workingDir, bool retryOnLock = true, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(workingDir)) throw new Exception("Working Directory is null!");

            string cleanWorkingDir = Path.GetFullPath(workingDir.Trim().Where(c => !char.IsControl(c) && (int)c != 160).ToArray().Aggregate("", (s, c) => s + c));

            string safeKeyPath = "";
            if (!string.IsNullOrEmpty(KeyPath))
            {
                safeKeyPath = KeyPath.Trim().Replace("\"", "").Replace('\\', '/');
            }

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

            if (!string.IsNullOrEmpty(safeKeyPath))
            {
                psi.EnvironmentVariables["SVN_SSH"] = $"ssh -i \"{safeKeyPath}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o BatchMode=yes";
            }

            using var process = new Process { StartInfo = psi };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            DataReceivedEventHandler outHandler = (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            DataReceivedEventHandler errHandler = (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.OutputDataReceived += outHandler;
            process.ErrorDataReceived += errHandler;

            using var registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            });

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                //0 - inifinity
                float timeoutLimit = IsLongRunningOperation(args) ? 0f : 60f;
                DateTime startTime = DateTime.Now;

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        throw new OperationCanceledException("SVN operation cancelled by user.");
                    }

                    if (timeoutLimit > 0 && (DateTime.Now - startTime).TotalSeconds > timeoutLimit)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        throw new Exception($"SVN Timeout: Operation exceeded {timeoutLimit} seconds.");
                    }

                    await Task.Delay(100);
                }

                if (process.ExitCode != 0)
                {
                    string err = errorBuilder.ToString();
                    if (retryOnLock && (err.Contains("locked") || err.Contains("cleanup")))
                    {
                        UnityEngine.Debug.LogWarning("[SvnRunner] Database lock detected. Attempting automatic Cleanup...");
                        await RunAsync("cleanup", workingDir, false, token);
                        return await RunAsync(args, workingDir, false, token);
                    }

                    if (!string.IsNullOrEmpty(err))
                        throw new Exception($"SVN Error (Code {process.ExitCode}): {err}");
                }

                return outputBuilder.ToString();
            }
            finally
            {
                process.OutputDataReceived -= outHandler;
                process.ErrorDataReceived -= errHandler;
            }
        }

        private static bool IsLongRunningOperation(string args)
        {
            if (string.IsNullOrEmpty(args)) return false;
            string low = args.ToLower();

            string[] longOps = { "commit", "update", "checkout", "switch", "export", "upgrade" };

            return longOps.Any(op => low.Contains(op)) || low.Contains("--targets");
        }

        public static async Task<string> RunLiveAsync(string args, string workingDir, Action<string> onLineReceived, CancellationToken token = default)
        {
            string safeKeyPath = KeyPath.Trim().Replace("\"", "").Replace('\\', '/');

            var psi = new ProcessStartInfo
            {
                FileName = "svn",
                Arguments = $"{args} --non-interactive --trust-server-cert",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            psi.EnvironmentVariables["SVN_SSH"] = $"ssh -i \"{safeKeyPath}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o BatchMode=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=10 -o IPQoS=throughput";

            using var process = new Process { StartInfo = psi };
            var processExitTcs = new TaskCompletionSource<bool>();
            bool hasError = false;

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) Task.Run(() => onLineReceived?.Invoke(e.Data));
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    if (e.Data.Contains("E:") || e.Data.ToLower().Contains("error")) hasError = true;
                    Task.Run(() => onLineReceived?.Invoke($"[SVN ERROR]: {e.Data}"));
                }
            };

            using (token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();

                        processExitTcs.TrySetCanceled();
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"Error while killing process: {ex.Message}");
                }
            }))
            {
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) => processExitTcs.TrySetResult(true);

                if (!process.Start()) throw new Exception("Failed to start SVN process.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await processExitTcs.Task;
                }
                catch (OperationCanceledException)
                {
                    return "Canceled";
                }

                process.WaitForExit();
                return hasError ? "Error" : "Success";
            }
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

        public static async Task<Dictionary<string, (string status, string size)>> GetFullStatusDictionaryAsync(string workingDir, bool includeIgnored = true)
        {
            string cleanWorkingDir = Path.GetFullPath(workingDir.Trim());
            string output = await RunAsync("status --no-ignore", cleanWorkingDir);

            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Length < 9) continue;

                char contentStatus = line[0];
                char propStatus = line[1];

                string stat = contentStatus.ToString().ToUpper();

                if (stat == " " && propStatus == 'C') stat = "C";

                string pathPart = line.Substring(8).TrimStart();

                string rawPath = new string(pathPart
                    .Where(c => !char.IsControl(c) && (int)c != 160)
                    .ToArray()).TrimEnd();

                string cleanPath = rawPath.Replace('\\', '/');

                bool isRelevant = "MA?!DC".Contains(stat) || (includeIgnored && stat == "I");

                if (isRelevant)
                {
                    string fullPathForSize = Path.Combine(cleanWorkingDir, rawPath).Replace('/', '\\');

                    string size = GetFileSizeSafe(fullPathForSize);

                    statusDict[cleanPath] = (stat, size);
                }
            }

            UnityEngine.Debug.Log($"<color=green>[SVN]</color> Parser finished. Dictionary count: {statusDict.Count}");
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

            if (output.Contains("<url>"))
            {
                int start = output.IndexOf("<url>") + 5;
                int end = output.IndexOf("</url>");
                return output.Substring(start, end - start).Trim();
            }

            throw new Exception("Failed to retrieve repository URL.");
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
        public static async Task<string> GetCommitSizeReportAsync(string workingDir)
        {
            var statusDict = await GetFullStatusDictionaryAsync(workingDir, false);
            if (statusDict == null || statusDict.Count == 0) return "Files: 0, Size: 0B";

            long totalBytes = 0;
            int filesToCommit = 0;
            string normRoot = workingDir.Replace("\\", "/").TrimEnd('/');

            foreach (var item in statusDict)
            {
                string status = item.Value.status;
                if (!"MA?".Contains(status)) continue;

                string relPath = item.Key.Replace("\\", "/").TrimStart('/');

                string standardPath = $"{normRoot}/{relPath}";

                if (File.Exists(standardPath))
                {
                    totalBytes += new FileInfo(standardPath).Length;
                    filesToCommit++;
                }
                else
                {

                    string fileName = Path.GetFileName(relPath);
                    string[] foundFiles = Directory.GetFiles(normRoot, fileName, SearchOption.AllDirectories);

                    if (foundFiles.Length > 0)
                    {
                        totalBytes += new FileInfo(foundFiles[0]).Length;
                        filesToCommit++;
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning($"[SVN] File not found: {relPath} (Searched at: {standardPath})");
                    }
                }
            }

            return $"Files: {filesToCommit}, Size: {FormatBytes(totalBytes)}";
        }
    }
}