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
        private static string sshPath = "ssh";                          
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

            process.OutputDataReceived += (s, e) => {
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

        public static async Task<bool> CheckIfIgnoreIsSet(string workingDir)
        {
            try
            {
                string result = await RunAsync("propget svn:ignore .", workingDir);
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
            return Directory.Exists(Path.Combine(workingDir, ".svn"));
        }

        public static async Task<string> CheckoutAsync(string url, string path)
        {
            string cmd = $"checkout \"{url}\" \"{path}\" --non-interactive";
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

            string fileArgs = string.Join(" ", files.Select(f => $"\"{f}\""));
            return await RunAsync($"add {fileArgs} --force --parents", workingDir);
        }

        public static async Task<string> CommitAsync(string workingDir, string[] paths, string message)
        {
            string pathsArg = (paths != null && paths.Length > 0 && paths[0] == ".") ? "." : "";
            if (string.IsNullOrEmpty(pathsArg) && paths != null)
                pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            string cmd = $"commit -m \"{message}\" {pathsArg}";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> UpdateAsync(string workingDir)
        {
            return await RunAsync("update --accept postpone", workingDir);
        }

        public static async Task<string> LogAsync(string workingDir, int lastN = 10)
        {
            return await RunAsync($"log -l {lastN}", workingDir);
        }

        public static async Task<string> CleanupAsync(string workingDir)
        {
            try
            {
                return await RunAsync("cleanup", workingDir);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SvnRunner] Standard cleanup failed, trying extended version: {ex.Message}");

                return await RunAsync("cleanup --include-externals", workingDir);
            }
        }

        public static async Task<string> VacuumCleanupAsync(string workingDir)
        {
            try
            {
                // Added --include-externals for a full cleanup of projects with external libraries
                return await RunAsync("cleanup --vacuum-pristines --include-externals", workingDir);
            }
            catch
            {
                // Standard fallback if the above flags are not supported
                return await RunAsync("cleanup", workingDir);
            }
        }

        /// <summary>
        /// Reverts local changes in selected files.
        /// </summary>
        public static async Task<string> RevertAsync(string workingDir, string[] files)
        {
            if (files == null || files.Length == 0) return "No files to revert.";

            // Add quotes for path safety
            string fileArgs = string.Join(" ", files.Select(f => $"\"{f}\""));

            // -R stands for recursive (useful when reverting a folder)
            return await RunAsync($"revert -R {fileArgs}", workingDir);
        }

        public static async Task<string> LockAsync(string workingDir, string[] paths)
        {
            if (paths == null || paths.Length == 0) return "No files to lock.";

            // Join paths in quotes, separated by spaces
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            // --comment "..." is optional, but worth adding
            string cmd = $"lock {pathsArg} --comment \"Locked by Unity SVN Tool\"";

            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> UnlockAsync(string workingDir, string[] paths)
        {
            if (paths == null || paths.Length == 0) return "No files to unlock.";

            // Prepare paths in quotes
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            // Execute unlock command
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
            if (paths == null || paths.Length == 0) return "No paths to delete.";

            // Prepare paths in quotes
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));

            // Use --force to delete files that are 'missing' (!) 
            // or have local modifications.
            string cmd = $"delete --force {pathsArg}";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> StatusRemoteAsync(string workingDir)
        {
            // -u (or --show-updates) contacts the server
            // Pass "." to check the entire project
            string cmd = "status -u .";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> IgnoreDefaultsAsync(string workingDir)
        {
            // List of common Unreal Engine folders to ignore
            string ignoreList = "Binaries Intermediate Saved DerivedDataCache .vs Build *.sln *.suo *.obj";

            // Use propset to assign the list to the svn:ignore property of the root folder (.)
            // We must replace spaces with newlines for SVN
            string formattedList = ignoreList.Replace(" ", "\n");

            // Execute command (careful with quotes in parameters)
            string cmd = $"propset svn:ignore \"{formattedList}\" .";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> AddFolderOnlyAsync(string workingDir, string path)
        {
            // --depth empty adds the folder itself without adding the files inside
            string cmd = $"add \"{path}\" --depth empty";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> CopyAsync(string workingDir, string sourceUrl, string destUrl, string message)
        {
            // SVN copy requires a message because it creates a new revision on the server
            string cmd = $"copy \"{sourceUrl}\" \"{destUrl}\" -m \"{message}\"";
            return await RunAsync(cmd, workingDir);
        }

        public static async Task<string> CopyBranchAsync(string workingDir, string sourceUrl, string targetUrl, string message)
        {
            // svn copy URL1 URL2 -m "message"
            // Quotes around message are crucial!
            string args = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"{message}\"";
            return await RunAsync(args, workingDir);
        }

        /// <summary>
        /// Switches the working directory to another branch.
        /// </summary>
        public static async Task<string> SwitchAsync(string workingDir, string targetUrl)
        {
            // The switch command changes the working folder's association with a URL in the repository
            string cmd = $"switch \"{targetUrl}\" --accept postpone";
            return await RunAsync(cmd, workingDir);
        }

        /// <summary>
        /// Merges changes from the given URL into the current working directory.
        /// </summary>
        public static async Task<string> MergeAsync(string workingDir, string sourceUrl)
        {
            // --accept postpone allows the merge to complete even with conflicts, 
            // so you can resolve them manually later.
            string cmd = $"merge \"{sourceUrl}\" --accept postpone";
            return await RunAsync(cmd, workingDir);
        }

        /// <summary>
        /// Retrieves information about the current working copy (URL, Revision, etc.).
        /// </summary>
        public static async Task<string> GetInfoAsync(string workingDir)
        {
            // Command: svn info
            return await RunAsync("info", workingDir);
        }

        // --- IMPROVED RECURSIVE FUNCTION ---
        private static void BuildTreeString(
        string currentDir,
        string rootDir,
        int indent,
        Dictionary<string, (string status, string size)> statusDict, // Corrected type
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

                // --- FIX HERE: Retrieve tuple and decompose it ---
                string status = "";
                string sizeDisplay = "";
                if (statusDict.TryGetValue(relPath, out var statusTuple))
                {
                    status = statusTuple.status;
                    sizeDisplay = statusTuple.size;
                }

                if (showIgnored)
                {
                    if (status != "I" && !foldersWithRelevantContent.Contains(relPath)) continue;
                    if (isDirectory && status != "I") status = "I";
                }
                else // Modified Mode
                {
                    if (status == "I") continue; // Hide ignored

                    if (isDirectory)
                    {
                        bool hasChanges = foldersWithRelevantContent.Contains(relPath);
                        bool isExpanded = expandedPaths.Contains(relPath) || string.IsNullOrEmpty(relPath);

                        // Show folder ONLY if it has a status, leads to changes, or is expanded
                        if (string.IsNullOrEmpty(status) && !hasChanges && !isExpanded) continue;

                        // If folder itself has no status but contains changes inside, mark it as [M]
                        if (string.IsNullOrEmpty(status) && hasChanges) status = "M";
                    }
                    else
                    {
                        // FILE: Show only if it has any status (M, A, ?, !, C)
                        if (string.IsNullOrEmpty(status)) continue;
                    }
                }

                // Statistics
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

                // Drawing
                StringBuilder indentBuilder = new StringBuilder();
                for (int j = 0; j < indent - 1; j++)
                    indentBuilder.Append(parentIsLast[j] ? "    " : "│   ");

                if (indent > 0)
                    indentBuilder.Append(isLast ? "└── " : "├── ");

                string expandIcon = isDirectory ? (expandedPaths.Contains(relPath) ? "[-] " : "[+] ") : "    ";
                string statusIcon = GetStatusIcon(status);
                string typeTag = isDirectory ? "<color=#FFCA28><b><D></b></color>" : "<color=#4FC3F7><F></color>";

                // Use pre-calculated size from dictionary
                string sizeInfo = !isDirectory && !string.IsNullOrEmpty(sizeDisplay) ? $" <color=#555555>.... ({sizeDisplay})</color>" : "";

                sb.AppendLine($"{indentBuilder}{statusIcon} {expandIcon}{typeTag} {name}{sizeInfo}");

                if (isDirectory && (expandedPaths.Contains(relPath) || string.IsNullOrEmpty(relPath)))
                {
                    if (indent < parentIsLast.Length) parentIsLast[indent] = isLast;
                    BuildTreeString(entry, rootDir, indent + 1, statusDict, sb, stats, expandedPaths, parentIsLast, showIgnored, foldersWithRelevantContent);
                }
            }
        }

        // --- IMPROVED TREE START METHOD ---
        public static async Task<(string tree, SvnStats stats)> GetVisualTreeWithStatsAsync(string workingDir, HashSet<string> expandedPaths, bool showIgnored = false)
        {
            // 1. FETCH STATUSES: Force fetching everything (including '?' and 'I' files)
            Dictionary<string, (string status, string size)> statusDict = await GetFullStatusDictionaryAsync(workingDir, true);

            var sb = new StringBuilder();
            var stats = new SvnStats();

            if (!Directory.Exists(workingDir)) return ("Path error.", stats);

            // 2. IDENTIFY RELEVANT PATHS
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
                    // Changes view: Must show M (Modified), A (Added), ? (New), ! (Missing)
                    // Basically everything that has a status but is not ignored ("I")
                    isInteresting = !string.IsNullOrEmpty(stat) && stat != "I";
                }

                if (isInteresting)
                {
                    // Add parent folders to the "to show" list
                    string[] parts = item.Key.Split('/');
                    string currentFolder = "";
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        currentFolder = string.IsNullOrEmpty(currentFolder) ? parts[i] : $"{currentFolder}/{parts[i]}";
                        foldersWithRelevantContent.Add(currentFolder);
                    }
                }
            }

            // 3. BUILD TREE
            // Pass foldersWithRelevantContent so recursion knows which folders to expand
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
            // Always use --no-ignore to see everything
            string output = await RunAsync("status --no-ignore", workingDir);
            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length >= 8)
                {
                    // SVN status has the code at the 1st position (index 0)
                    char rawCode = line[0];
                    string path = line.Substring(8).Trim().Replace('\\', '/');
                    string fullPath = Path.Combine(workingDir, path);

                    // Standardize status code
                    string stat = rawCode.ToString().ToUpper();

                    // LOGIC:
                    // If looking for ignored (I)
                    if (includeIgnored && stat == "I")
                    {
                        statusDict[path] = ("I", GetFileSizeSafe(fullPath));
                    }
                    // If looking for changes (M, A, ?, !, C, D)
                    else if (stat != " " && stat != "I" && stat != "X")
                    {
                        // SVN sometimes returns '?' for new files - ensure we catch it
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
            // 'info' command returns repository details
            string output = await RunAsync("info --xml", workingDir);

            // Simple XML parsing to extract <url> tag
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
            // 1. Get base repository URL
            string repoUrl = await GetRepoUrlAsync(workingDir);

            // 2. Trim the end (e.g., /trunk) to reach the root
            // If repoUrl is "https://svn.com/repo/trunk", baseUrl will be "https://svn.com/repo"
            string baseUrl = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));

            // 3. Build full address to the subfolder (branches or tags)
            string targetUrl = $"{baseUrl}/{subFolder}";

            // 4. Call svn ls command
            string output = await RunAsync($"ls \"{targetUrl}\"", workingDir);

            // 5. Clean result: split into lines, remove empty entries and trailing slashes (/)
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.TrimEnd('/'))
                         .ToArray();
        }

        public static async Task<string> DeleteRemotePathAsync(string workingDir, string remoteUrl, string message)
        {
            // Command: svn rm "URL" -m "message"
            string args = $"rm \"{remoteUrl}\" -m \"{message}\"";

            // Run process and return output
            return await RunAsync(args, workingDir);
        }

        public static async Task<string[]> ListBranchesAsync(string repositoryRootUrl)
        {
            // Clean URL and target the branches folder
            string branchesUrl = repositoryRootUrl.TrimEnd('/') + "/branches";

            // Command: svn list [URL]
            string output = await RunAsync($"list {branchesUrl}", "");

            if (string.IsNullOrEmpty(output)) return new string[0];

            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.TrimEnd('/')) // Remove slashes from folder names
                         .ToArray();
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir)
        {
            // Execute svn status command
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