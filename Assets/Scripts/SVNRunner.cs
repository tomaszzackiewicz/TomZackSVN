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

        public static async Task<string> RunLiveAsync(string args, string workingDir, Action<string> onLineReceived, CancellationToken token = default)
        {
            int dynamicTimeout = args.Contains("update") ? 600 : 45;

            string safeKeyPath = KeyPath.Trim().Replace("\"", "").Replace('\\', '/');
            var psi = new ProcessStartInfo
            {
                FileName = "svn",
                Arguments = args + " --non-interactive --trust-server-cert",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            psi.EnvironmentVariables["SVN_SSH"] = $"ssh -i \"{safeKeyPath}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o BatchMode=yes";

            using var process = new Process { StartInfo = psi };
            var outputBuilder = new StringBuilder();

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    onLineReceived?.Invoke(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            DateTime startTime = DateTime.Now;
            while (!process.HasExited)
            {
                if (token.IsCancellationRequested) { process.Kill(); throw new OperationCanceledException(); }

                if ((DateTime.Now - startTime).TotalSeconds > dynamicTimeout)
                {
                    process.Kill();
                    throw new Exception($"SVN Timeout: Operation exceeded {dynamicTimeout} seconds.");
                }

                await Task.Delay(100);
            }

            return outputBuilder.ToString();
        }

        public static async Task<bool> CheckIfSvnInstalled()
        {
            try
            {
                string tempDir = Path.GetTempPath();
                string result = await RunAsync("--version", tempDir);
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

        public static async Task<string> LogAsync(string workingDir, int lastN = 10)
        {
            return await RunAsync($"log -l {lastN}", workingDir);
        }
        public static async Task<string> AddFolderOnlyAsync(string workingDir, string path)
        {
            string cmd = $"add \"{path}\" --depth empty";
            return await RunAsync(cmd, workingDir);
        }

        private static async Task<string> ExecuteAsync(string command, string workingDir)
        {
            var tcs = new TaskCompletionSource<string>();

            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "svn",
                    Arguments = command,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using (var process = new System.Diagnostics.Process { StartInfo = psi })
                {
                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();

                    process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await Task.Run(() => process.WaitForExit());

                    if (process.ExitCode == 0)
                    {
                        return output.ToString().Trim();
                    }
                    else
                    {
                        string errResult = error.ToString().Trim();
                        return $"Error (Code {process.ExitCode}): {errResult}";
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }

        public static async Task<string> GetInfoAsync(string workingDir)
        {
            return await RunAsync("info", workingDir);
        }

        private static void BuildTreeString(
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

        public static async Task<(string tree, SvnStats stats)> GetVisualTreeWithStatsAsync(string workingDir, HashSet<string> expandedPaths, bool showIgnored = false)
        {
            Dictionary<string, (string status, string size)> statusDict = await GetFullStatusDictionaryAsync(workingDir, true);

            var sb = new StringBuilder();
            var stats = new SvnStats();

            if (!Directory.Exists(workingDir)) return ("Path error.", stats);

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
                    isInteresting = !string.IsNullOrEmpty(stat) && stat != "I";
                }

                if (isInteresting)
                {
                    string[] parts = item.Key.Split('/');
                    string currentFolder = "";
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        currentFolder = string.IsNullOrEmpty(currentFolder) ? parts[i] : $"{currentFolder}/{parts[i]}";
                        foldersWithRelevantContent.Add(currentFolder);
                    }
                }
            }

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
            string output = await RunAsync("status --no-ignore", workingDir);
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

                string rawPath = line.Substring(8).Trim().Replace('\\', '/');
                string cleanPath = rawPath;

                bool isRelevant = "MA?!DC".Contains(stat) || (includeIgnored && stat == "I");

                if (isRelevant)
                {
                    string fullPathForSize = Path.Combine(workingDir, rawPath);
                    string size = GetFileSizeSafe(fullPathForSize);

                    statusDict[cleanPath] = (stat, size);
                }
            }

            UnityEngine.Debug.Log($"[SVN] Parser finished. Dictionary count: {statusDict.Count}");
            return statusDict;
        }

        private static string GetFileSizeSafe(string fullPath)
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

        public static async Task<SvnStats> GetStatsAsync(string workingDir)
        {
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

        private static string CleanSvnPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Replace('\\', '/').Trim('/');

            if (p.StartsWith("trunk/", StringComparison.OrdinalIgnoreCase))
                return p.Substring(6);

            if (p.StartsWith("branches/", StringComparison.OrdinalIgnoreCase))
            {
                int secondSlash = p.IndexOf('/', 9);
                return (secondSlash != -1) ? p.Substring(secondSlash + 1) : p;
            }

            return p;
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetChangesDictionaryAsync(string workingDir)
        {
            string output = await RunAsync("status", workingDir);
            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 8) continue;

                char rawCode = line[0];
                string stat = rawCode.ToString().ToUpper();

                if ("MA?!DC".Contains(stat))
                {
                    string rawPath = line.Substring(8).Trim();
                    string cleanPath = CleanSvnPath(rawPath);

                    string fullPath = Path.Combine(workingDir, cleanPath);
                    statusDict[cleanPath] = (stat, GetFileSizeSafe(fullPath));
                }
            }
            return statusDict;
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetIgnoredOnlyAsync(string workingDir)
        {
            string output = await RunAsync("status --no-ignore", workingDir);
            var ignoredDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return ignoredDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 8) continue;

                if (line[0] == 'I')
                {
                    string rawPath = line.Substring(8).Trim();
                    string cleanPath = CleanSvnPath(rawPath);

                    ignoredDict[cleanPath] = ("I", "<IGNORED>");
                }
            }
            return ignoredDict;
        }

        public static async Task<List<string>> GetIgnoreRulesFromSvnAsync(string workingDir)
        {
            List<string> rules = new List<string>();
            try
            {
                string globalOutput = await RunAsync("propget svn:global-ignores -R .", workingDir);
                string standardOutput = await RunAsync("propget svn:ignore -R .", workingDir);

                string combinedOutput = globalOutput + "\n" + standardOutput;

                if (string.IsNullOrEmpty(combinedOutput) || combinedOutput.Contains("ERROR"))
                    return rules;

                string[] lines = combinedOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string pattern = line;
                    if (line.Contains(" - "))
                    {
                        var parts = line.Split(new[] { " - " }, StringSplitOptions.None);
                        pattern = parts.Length > 1 ? parts[1] : parts[0];
                    }

                    string trimmed = pattern.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.Contains(" ") && !rules.Contains(trimmed))
                    {
                        rules.Add(trimmed);
                    }
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogError(e.Message); }
            return rules;
        }

        public static async Task<bool> SetSvnGlobalIgnorePropertyAsync(string workingDir, string rulesRawText)
        {
            string tempFilePath = Path.Combine(workingDir, "temp_global_ignore.txt");
            File.WriteAllText(tempFilePath, rulesRawText.Replace("\r\n", "\n"));

            string result = await RunAsync($"propset svn:global-ignores -F \"{tempFilePath}\" .", workingDir);

            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);

            if (result.StartsWith("ERROR"))
            {
                UnityEngine.Debug.LogError(result);
                return false;
            }
            return true;
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