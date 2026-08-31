using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;

namespace SVN.Core
{
    public static class SvnRunner
    {
        private static string _keyPath = "";
        private static readonly AsyncReaderWriterLock _svnLock = new();

        public static event Action<bool> OnProcessingStateChanged;
        public static event Action<string> OnOperationError;

        private static int _activeOperationsCount = 0;
        private static bool _processingState = false;
        private static readonly object _processingLock = new();
        private static readonly SemaphoreSlim _infoFetchLock = new SemaphoreSlim(1, 1);

        private static readonly Dictionary<string, (string output, DateTime time)> _infoCache = new();
        private static readonly TimeSpan InfoCacheDuration = TimeSpan.FromSeconds(2);
        private static readonly object _infoCacheLock = new();

        public static string SshOptions { get; set; } = "-o ServerAliveInterval=15 -o ServerAliveCountMax=10 -o IPQoS=throughput";

        public static string KeyPath
        {
            get => string.IsNullOrEmpty(_keyPath)
                ? (_keyPath = SVNPrefs.GetString("SVN_SSHKeyPath", ""))
                : _keyPath;
            set
            {
                _keyPath = value ?? "";
                SVNPrefs.SetString("SVN_SSHKeyPath", _keyPath);
            }
        }

        public static int ActiveOperationsCount
        {
            get { lock (_processingLock) { return _activeOperationsCount; } }
        }

        private static void IncrementOperations()
        {
            lock (_processingLock)
            {
                _activeOperationsCount++;
                if (!_processingState)
                {
                    _processingState = true;
                    SVNLogBridge.LogToOutput("<color=#00FFAA>[SVN]</color> Processing START");
                    InvokeProcessingStateChanged(true);
                }
            }
        }

        private static void DecrementOperations()
        {
            lock (_processingLock)
            {
                _activeOperationsCount--;
                if (_activeOperationsCount < 0) _activeOperationsCount = 0;

                if (_processingState && _activeOperationsCount == 0)
                {
                    _processingState = false;
                    SVNLogBridge.LogToOutput("<color=#FFCC00>[SVN]</color> Processing END");
                    InvokeProcessingStateChanged(false);
                }
            }
        }

        private static string BuildSshEnvironmentString(string keyPath)
        {
            string baseCmd = "ssh -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o BatchMode=yes -o LogLevel=QUIET";
            if (!string.IsNullOrEmpty(keyPath))
            {
                string safeKeyPath = keyPath.Trim().Replace("\"", "").Replace('\\', '/');
                baseCmd += $" -i \"{safeKeyPath}\"";
            }
            if (!string.IsNullOrWhiteSpace(SshOptions)) baseCmd += " " + SshOptions.Trim();
            return baseCmd;
        }

        private static void InvokeProcessingStateChanged(bool state)
        {
            var handlers = OnProcessingStateChanged?.GetInvocationList();
            if (handlers == null) return;
            foreach (var h in handlers)
            {
                try { ((Action<bool>)h).Invoke(state); }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] Event handler error: {ex.Message}"); }
            }
        }

        private static string BuildSafeArguments(IEnumerable<string> args)
        {
            var escaped = new List<string>();
            foreach (var arg in args) escaped.Add(string.IsNullOrEmpty(arg) ? "\"\"" : EscapeSingleArgument(arg));
            return string.Join(" ", escaped);
        }

        private static string EscapeSingleArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";

            bool needsQuotes = arg.Contains(' ') || arg.Contains('"') || arg.EndsWith("\\");
            if (!needsQuotes) return arg;

            var sb = new StringBuilder();
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }

                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }

                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);

            return "\"" + sb + "\"";
        }

        private static bool IsWriteCommand(IEnumerable<string> args)
        {
            if (args == null) return false;

            string command = args.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a) && !a.StartsWith("-"));

            if (string.IsNullOrEmpty(command))
                return false;

            command = command.ToLowerInvariant();

            if (WriteCommands.Contains(command)) return true;
            if (ReadCommands.Contains(command)) return false;

            return true;
        }

        private static List<string> SplitArguments(string command)
        {
            var args = new List<string>();
            if (string.IsNullOrWhiteSpace(command)) return args;

            int i = 0, n = command.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(command[i])) i++;
                if (i >= n) break;

                var current = new StringBuilder();
                bool inQuotes = false;
                bool hasContent = false;

                while (i < n)
                {
                    char c = command[i];

                    if (c == '"')
                    {
                        inQuotes = !inQuotes;
                        hasContent = true;
                        i++;
                    }
                    else if (c == '\\')
                    {
                        int bs = 0, j = i;
                        while (j < n && command[j] == '\\') { bs++; j++; }

                        if (j < n && command[j] == '"')
                        {
                            current.Append('\\', bs / 2);
                            if (bs % 2 == 1)
                            {
                                current.Append('"');
                                hasContent = true;
                                i = j + 1;
                            }
                            else
                            {
                                i = j;
                            }
                        }
                        else
                        {
                            current.Append('\\', bs);
                            hasContent = true;
                            i = j;
                        }
                    }
                    else if (!inQuotes && char.IsWhiteSpace(c))
                    {
                        break;
                    }
                    else
                    {
                        current.Append(c);
                        hasContent = true;
                        i++;
                    }
                }

                if (hasContent) args.Add(current.ToString());
            }
            return args;
        }

        #region Core Execution Engines

        private static async Task<(string output, string error, int exitCode)> ExecuteRawProcessAsync(
            List<string> args, string workingDir, Action<string> stdoutCb, Action<string> stderrCb, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "svn",
                WorkingDirectory = Path.GetFullPath((workingDir ?? "").Trim()),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                Arguments = BuildSafeArguments(args)
            };
            psi.EnvironmentVariables["SVN_SSH"] = BuildSshEnvironmentString(KeyPath);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            SvnProcessTracker.Register(process);

            var stdoutQueue = new ConcurrentQueue<string>();
            var stderrQueue = new ConcurrentQueue<string>();

            var stdoutTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    stdoutQueue.Enqueue(e.Data);
                    try { stdoutCb?.Invoke(e.Data); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] Callback error: {ex.Message}"); }
                }
                else
                {
                    stdoutTcs.TrySetResult(true);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    stderrQueue.Enqueue(e.Data);
                    try { stderrCb?.Invoke(e.Data); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] Callback error: {ex.Message}"); }
                }
                else
                {
                    stderrTcs.TrySetResult(true);
                }
            };

            try
            {
                if (!process.Start()) throw new Exception("Failed to start SVN process.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await WaitForExitAsync(process, token).ConfigureAwait(false);

                await Task.WhenAll(stdoutTcs.Task, stderrTcs.Task).ConfigureAwait(false);

                return (
                    string.Join("\n", stdoutQueue),
                    string.Join("\n", stderrQueue),
                    process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                SvnProcessTracker.Kill(process);
                throw;
            }
            finally
            {
                try { SvnProcessTracker.Unregister(process); } catch { }
            }
        }

        private static async Task<(string output, string error, int exitCode)> ExecuteSvnProcessAsync(
            List<string> argList, string workingDir, bool retryOnLock, Action<string> stdoutCb, Action<string> stderrCb, bool throwOnError, string logPrefix, CancellationToken token)
        {
            string safeArgs = BuildSafeArguments(argList);
            SVNLogBridge.LogToOutput($"[SVN QUEUE]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} Waiting: svn {safeArgs}");

            bool write = IsWriteCommand(argList);

            if (write) await _svnLock.EnterWriteAsync(token).ConfigureAwait(false);
            else await _svnLock.EnterReadAsync(token).ConfigureAwait(false);

            try
            {
                IncrementOperations();
                SVNLogBridge.LogToOutput($"[SVN QUEUE]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} Acquired: svn {safeArgs}");

                if (string.IsNullOrEmpty(workingDir)) throw new Exception("Working Directory is null!");

                var finalArgs = new List<string>(argList);
                if (!finalArgs.Contains("--non-interactive")) finalArgs.Add("--non-interactive");
                if (!finalArgs.Contains("--trust-server-cert")) finalArgs.Add("--trust-server-cert");

                int maxAttempts = retryOnLock ? 2 : 1;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();

                    var (output, error, exitCode) = await ExecuteRawProcessAsync(finalArgs, workingDir, stdoutCb, stderrCb, token).ConfigureAwait(false);

                    if (exitCode == 0 && !string.IsNullOrWhiteSpace(error))
                        SVNLogBridge.LogToOutput($"[SvnRunner]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} svn warning: {error.Trim()}");

                    if (exitCode != 0)
                    {
                        bool isLockError = error.Contains("E155004") || error.Contains("is locked");

                        if (attempt == 0 && retryOnLock && isLockError)
                        {
                            if (write)
                            {
                                SVNLogBridge.LogErrorToOutput("[SvnRunner] Lock detected. Running Cleanup...");
                                var cleanupArgs = new List<string> { "cleanup", "--non-interactive", "--trust-server-cert" };

                                await ExecuteRawProcessAsync(cleanupArgs, workingDir, null, null, token).ConfigureAwait(false);

                                SVNLogBridge.LogToOutput("<color=green>[SVN]</color> Cleanup completed. Retrying...");
                            }
                            else
                            {
                                SVNLogBridge.LogToOutput("[SvnRunner] Lock detected on read-only op. Waiting 500ms before retry...");
                                await Task.Delay(500, token).ConfigureAwait(false);
                            }
                            continue;
                        }

                        if (throwOnError)
                        {
                            string diagnostic = error.Contains("E170013") || error.Contains("can't connect") ? " [Connection/URL issue]" : error.Contains("E215004") ? " [Authorization/Password error]" : "";
                            string fullError = $"SVN Error (Code {exitCode}): {error}{diagnostic}";
                            SVNLogBridge.LogErrorToOutput(fullError);

                            try { OnOperationError?.Invoke(fullError); }
                            catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] OnOperationError handler error: {ex.Message}"); }

                            throw new Exception(fullError);
                        }
                    }

                    SVNLogBridge.LogToOutput($"[SvnRunner]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} Completed (exit code {exitCode}).");
                    return (output, error, exitCode);
                }

                return (string.Empty, "SVN retry loop exhausted.", 1);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogToOutput("<color=#FFD700>[CANCEL]</color> SVN operation canceled.");
                throw;
            }
            finally
            {
                try { if (write) _svnLock.ExitWrite(); else _svnLock.ExitRead(); }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner] Lock release failed: {ex.Message}"); }
                DecrementOperations();
            }
        }

        #endregion

        #region Public API Methods

        public static async Task<string> RunAsync(string command, string workingDir, bool retryOnLock = true, CancellationToken token = default)
        {
            return await RunAsync(SplitArguments(command), workingDir, retryOnLock, token).ConfigureAwait(false);
        }

        public static async Task<string> RunAsync(IEnumerable<string> args, string workingDir, bool retryOnLock = true, CancellationToken token = default)
        {
            var result = await ExecuteSvnProcessAsync(args.ToList(), workingDir, retryOnLock, null, null, true, "", token).ConfigureAwait(false);
            return result.output;
        }

        public static async Task<(string output, string error, int exitCode)> RunDetailedAsync(
            string command, string workingDir, bool retryOnLock = true, bool throwOnError = false, CancellationToken token = default)
        {
            return await RunDetailedAsync(SplitArguments(command), workingDir, retryOnLock, throwOnError, token).ConfigureAwait(false);
        }

        public static async Task<(string output, string error, int exitCode)> RunDetailedAsync(
            IEnumerable<string> args, string workingDir, bool retryOnLock = true, bool throwOnError = false, CancellationToken token = default)
        {
            return await ExecuteSvnProcessAsync(args.ToList(), workingDir, retryOnLock, null, null, throwOnError, "DETAIL", token).ConfigureAwait(false);
        }

        public static async Task<string> RunLiveAsync(string args, string workingDir, Action<string> onLineReceived, CancellationToken token = default)
        {
            return await RunLiveAsync(SplitArguments(args), workingDir, onLineReceived, token).ConfigureAwait(false);
        }

        public static async Task<string> RunLiveAsync(IEnumerable<string> args, string workingDir, Action<string> onLineReceived, CancellationToken token = default)
        {
            Action<string> stdout = (line) => { if (!string.IsNullOrWhiteSpace(line)) onLineReceived?.Invoke(line); };
            Action<string> stderr = (line) => { if (!string.IsNullOrWhiteSpace(line)) onLineReceived?.Invoke($"[SVN ERROR] {line}"); };

            var result = await ExecuteSvnProcessAsync(args.ToList(), workingDir, false, stdout, stderr, true, "LIVE", token).ConfigureAwait(false);
            return result.output;
        }

        public static async Task<int> RunStreamedAsync(string arguments, string workingDirectory, Action<string> onOutput, CancellationToken token)
        {
            return await RunStreamedAsync(SplitArguments(arguments), workingDirectory, onOutput, token).ConfigureAwait(false);
        }

        public static async Task<int> RunStreamedAsync(IEnumerable<string> args, string workingDirectory, Action<string> onOutput, CancellationToken token)
        {
            Action<string> stdout = (line) => onOutput?.Invoke(line);
            Action<string> stderr = (line) => onOutput?.Invoke($"<color=#FFAA00>{line}</color>");

            var result = await ExecuteSvnProcessAsync(args.ToList(), workingDirectory, false, stdout, stderr, false, "STREAMED", token).ConfigureAwait(false);
            return result.exitCode;
        }

        public static async Task<int> RunStreamedLiveAsync(string arguments, string workingDirectory, Action<string> onOutput, CancellationToken token)
        {
            return await RunStreamedLiveAsync(SplitArguments(arguments), workingDirectory, onOutput, token).ConfigureAwait(false);
        }

        public static async Task<int> RunStreamedLiveAsync(IEnumerable<string> args, string workingDirectory, Action<string> onOutput, CancellationToken token)
        {
            Action<string> stdout = (line) => onOutput?.Invoke(line);
            Action<string> stderr = (line) => onOutput?.Invoke($"<color=#FFAA00>{line}</color>");

            var result = await ExecuteSvnProcessAsync(args.ToList(), workingDirectory, false, stdout, stderr, false, "LIVE STREAMED", token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            return result.exitCode;
        }

        #endregion

        #region Utility Methods

        private static async Task WaitForExitAsync(Process process, CancellationToken token)
        {
            if (process == null) return;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (s, e) => tcs.TrySetResult(true);
            process.Exited += handler;
            try
            {
                if (process.HasExited) { tcs.TrySetResult(true); return; }
                using (token.Register(() => tcs.TrySetCanceled(token)))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            finally { process.Exited -= handler; }
        }

        public static async Task WaitForSemaphoreFreeAsync(CancellationToken token = default)
        {
            await _svnLock.EnterWriteAsync(token).ConfigureAwait(false);
            _svnLock.ExitWrite();
        }

        public static async Task<string> GetInfoAsync(string workingDir, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(workingDir))
                throw new ArgumentException("Working directory cannot be null or empty.", nameof(workingDir));

            string cleanWd = Path.GetFullPath(workingDir.Trim());

            lock (_infoCacheLock)
            {
                if (_infoCache.TryGetValue(cleanWd, out var cached) && DateTime.UtcNow - cached.time < InfoCacheDuration)
                {
                    SVNLogBridge.LogLine("<color=#8888FF>[SVN CACHE]</color> Using cached svn info", false);
                    return cached.output;
                }
            }

            await _infoFetchLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (_infoCacheLock)
                {
                    if (_infoCache.TryGetValue(cleanWd, out var cached) && DateTime.UtcNow - cached.time < InfoCacheDuration)
                        return cached.output;
                }

                string result = await RunAsync("info", cleanWd, true, token).ConfigureAwait(false);

                lock (_infoCacheLock) { _infoCache[cleanWd] = (result, DateTime.UtcNow); }
                return result;
            }
            finally { _infoFetchLock.Release(); }
        }

        private static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "update", "up",
            "checkout", "co",
            "commit", "ci",
            "add",
            "delete", "del", "remove", "rm",
            "move", "mv", "rename", "ren",
            "copy", "cp",
            "mkdir",
            "revert",
            "lock", "unlock",
            "switch", "sw",
            "relocate",
            "merge",
            "resolve",
            "propset", "ps",
            "propdel", "pd",
            "propedit", "pe",
            "changelist", "cl",
            "patch",
            "cleanup",
            "upgrade",
            "import", "export",
            "shelve", "unshelve",
            "shelf-save", "shelf-restore", "shelf-drop",
            "absorb"
        };

        private static readonly HashSet<string> ReadCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "status", "st", "stat",
            "info", "auth",
            "ls", "list",
            "log",
            "diff", "di",
            "blame", "annotate", "praise",
            "cat",
            "propget", "pget", "pg",
            "proplist", "plist", "pl",
            "mergeinfo",
            "youngest",
            "help", "h",
            "version",
            "shelf-list", "shelf-diff", "shelf-log"
        };

        public static string ForceCleanPath(string path) => string.IsNullOrEmpty(path) ? string.Empty : new string(path.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();

        public static void BuildTreeString(
            string currentDir,
            string rootDir,
            int indent,
            Dictionary<string, (string status, string size)> statusDict,
            StringBuilder sb,
            SvnStats stats,
            HashSet<string> expandedPaths,
            List<bool> parentIsLast,
            bool showIgnored,
            HashSet<string> foldersWithRelevantContent)
        {
            if (statusDict == null || expandedPaths == null || foldersWithRelevantContent == null || parentIsLast == null)
                return;

            string normRootDir = rootDir.Replace('\\', '/').TrimEnd('/');
            string normCurrentDir = currentDir.Replace('\\', '/').TrimEnd('/');

            string currentRelDir = "";
            if (normCurrentDir.Length > normRootDir.Length)
            {
                currentRelDir = normCurrentDir.Substring(normRootDir.Length).TrimStart('/').Replace('\\', '/');
            }

            var combinedEntries = new HashSet<string>();

            if (Directory.Exists(normCurrentDir))
            {
                try
                {
                    foreach (var fsEntry in Directory.GetFileSystemEntries(normCurrentDir))
                    {
                        string cleanFsEntry = fsEntry.Replace('\\', '/');
                        if (!cleanFsEntry.EndsWith(".meta") && !cleanFsEntry.EndsWith("/.svn") && !cleanFsEntry.EndsWith("\\.svn"))
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

            var sortedEntries = combinedEntries.ToList();
            sortedEntries.Sort((a, b) =>
            {
                bool aIsDir = Directory.Exists(a) || string.IsNullOrEmpty(Path.GetExtension(a));
                bool bIsDir = Directory.Exists(b) || string.IsNullOrEmpty(Path.GetExtension(b));
                if (aIsDir != bIsDir) return aIsDir ? -1 : 1;
                return string.CompareOrdinal(a, b);
            });

            for (int i = 0; i < sortedEntries.Count; i++)
            {
                string entry = sortedEntries[i];
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

                bool isDirectory = Directory.Exists(entry) || foldersWithRelevantContent.Contains(relPath);
                bool isLast = (i == sortedEntries.Count - 1);

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

                for (int j = 0; j < indent - 1; j++)
                {
                    bool isLastParent = j < parentIsLast.Count && parentIsLast[j];
                    sb.Append(isLastParent ? "    " : "│   ");
                }

                if (indent > 0)
                    sb.Append(isLast ? "└── " : "├── ");

                string expandIcon = isDirectory ? (expandedPaths.Contains(relPath) ? "[-] " : "[+] ") : "    ";
                string statusIcon = GetStatusIcon(status);
                string typeTag = isDirectory ? "<color=#FFCA28><b><D></b></color>" : "<color=#4FC3F7><F></color>";
                string displayName = (status == "!" || status == "D") ? $"<color=#FF4444>{name}</color>" : name;
                string sizeStr = (!isDirectory && !string.IsNullOrEmpty(sizeDisplay)) ? $" <color=#555555>({sizeDisplay})</color>" : "";

                sb.AppendLine($"{statusIcon} {expandIcon}{typeTag} {displayName}{sizeStr}");

                if (isDirectory && (expandedPaths.Contains(relPath) || string.IsNullOrEmpty(relPath) || foldersWithRelevantContent.Contains(relPath)))
                {
                    while (parentIsLast.Count <= indent)
                    {
                        parentIsLast.Add(false);
                    }
                    parentIsLast[indent] = isLast;

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
            if (bytes == 0) return "0B";

            int i = 0;
            double dblSByte = bytes;
            while (dblSByte >= 1024 && i < Suffix.Length - 1)
            {
                dblSByte /= 1024.0;
                i++;
            }
            return $"{dblSByte:0.##}{Suffix[i]}";
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetFullStatusDictionaryAsync(
            string workingDir,
            bool includeIgnored = true)
        {
            if (string.IsNullOrWhiteSpace(workingDir))
                throw new ArgumentException("Working directory cannot be null or empty.", nameof(workingDir));

            string cleanWorkingDir = Path.GetFullPath(workingDir.Trim());

            string output = await RunAsync("status --no-ignore", cleanWorkingDir).ConfigureAwait(false);

            var statusDict = new Dictionary<string, (string status, string size)>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(output))
                return statusDict;

            using var reader = new StringReader(output);
            string rawLine;

            while ((rawLine = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                string line = rawLine;

                if (line.Length < 9) continue;

                char contentStatus = line[0];
                char propertyStatus = line[1];
                char treeConflictStatus = line.Length > 6 ? line[6] : ' ';

                string stat = contentStatus.ToString();

                if (propertyStatus == 'C' || treeConflictStatus == 'C')
                {
                    stat = "C";
                }

                string pathPart = line.Substring(8).TrimStart();
                if (string.IsNullOrWhiteSpace(pathPart)) continue;

                string rawPath = new string(pathPart.Where(c => !char.IsControl(c) && c != '\t' && c != '\u00A0').ToArray()).Trim();
                if (string.IsNullOrWhiteSpace(rawPath)) continue;

                string cleanPath = rawPath.Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(cleanPath)) continue;

                bool isRelevant = "MADR?!C".Contains(stat) || (includeIgnored && stat == "I");
                if (!isRelevant) continue;

                string fullPath = Path.Combine(cleanWorkingDir, rawPath);
                string size = "";

                if (File.Exists(fullPath))
                {
                    try { size = GetFileSizeSafe(fullPath); }
                    catch { size = ""; }
                }

                statusDict[cleanPath] = (stat, size);
            }

            SVNLogBridge.LogToOutput($"<color=green>[SVN]</color> Parser finished. Dictionary count: {statusDict.Count}");
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
            catch { return ""; }
        }

        public static async Task<string[]> GetRepoListAsync(string workingDir, string subFolder, CancellationToken token = default)
        {
            string targetUrl = "";

            if (subFolder.Contains("://") || subFolder.StartsWith("^"))
            {
                targetUrl = subFolder;
            }
            else
            {
                string repoUrl = await GetRepoUrlAsync(workingDir, token).ConfigureAwait(false);
                repoUrl = repoUrl.TrimEnd('/');

                string projectRoot = repoUrl;

                int trunkIdx = repoUrl.IndexOf("/trunk/", StringComparison.OrdinalIgnoreCase);
                if (trunkIdx == -1 && repoUrl.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase))
                    trunkIdx = repoUrl.IndexOf("/trunk", StringComparison.OrdinalIgnoreCase);

                int branchIdx = repoUrl.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
                int tagIdx = repoUrl.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);

                if (trunkIdx != -1)
                    projectRoot = repoUrl.Substring(0, trunkIdx);
                else if (branchIdx != -1)
                    projectRoot = repoUrl.Substring(0, branchIdx);
                else if (tagIdx != -1)
                    projectRoot = repoUrl.Substring(0, tagIdx);
                else if (repoUrl.Contains("/"))
                    projectRoot = repoUrl.Substring(0, repoUrl.LastIndexOf('/'));

                targetUrl = $"{projectRoot}/{subFolder}";
            }

            string output = await RunAsync(new List<string> { "ls", targetUrl }, workingDir, token: token).ConfigureAwait(false);

            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.TrimEnd('/'))
                         .ToArray();
        }

        public static async Task<string> GetRepoUrlAsync(string workingDir, CancellationToken token = default)
        {
            string output = await RunAsync("info --xml", workingDir, true, token).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(output))
            {
                try
                {
                    var doc = XDocument.Parse(output);
                    var urlElement = doc.Descendants("url").FirstOrDefault();
                    if (urlElement != null)
                    {
                        return urlElement.Value.Trim();
                    }
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SVN] Failed to parse XML from svn info. Falling back. Error: {ex.Message}");
                }
            }

            string errorMsg = "Failed to retrieve repository URL. Is this a valid SVN working copy?";
            SVNLogBridge.LogError(errorMsg);
            throw new Exception(errorMsg);
        }

        public static string CleanSvnPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            string p = new string(path.Where(c => !char.IsControl(c)).ToArray());
            p = p.Replace('\\', '/').Trim(' ', '"', '/');

            if (p.StartsWith("./")) p = p.Substring(2);

            return p;
        }

        public static string SanitizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return new string(path.Where(c => !char.IsControl(c) && c != '\u00A0' && c != '\u200B').ToArray()).Trim();
        }

        public static string NormalizeRepositoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            path = new string(path
                .Where(c => !char.IsControl(c) && c != '\u00A0')
                .ToArray());

            path = path.Replace('\\', '/').Trim();

            string[] roots = { "trunk/", "branches/", "tags/" };

            foreach (string root in roots)
            {
                int index = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                    return path.Substring(index);
            }

            return path;
        }
        #endregion

        #region Binary-safe execution (raw byte streaming)

        public static async Task<(int exitCode, string error)> RunToFileAsync(
            string command, string workingDir, string destFilePath, CancellationToken token = default)
        {
            return await RunToFileAsync(SplitArguments(command), workingDir, destFilePath, token).ConfigureAwait(false);
        }

        public static async Task<(int exitCode, string error)> RunToFileAsync(
            IEnumerable<string> args, string workingDir, string destFilePath, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(destFilePath))
                throw new ArgumentException("Destination file path cannot be null or empty.", nameof(destFilePath));

            var argList = args.ToList();
            string safeArgs = BuildSafeArguments(argList);
            SVNLogBridge.LogToOutput($"[SVN QUEUE] Waiting (TOFILE): svn {safeArgs}");

            bool write = IsWriteCommand(argList);
            if (write) await _svnLock.EnterWriteAsync(token).ConfigureAwait(false);
            else await _svnLock.EnterReadAsync(token).ConfigureAwait(false);

            try
            {
                IncrementOperations();
                SVNLogBridge.LogToOutput($"[SVN QUEUE] Acquired (TOFILE): svn {safeArgs}");

                if (string.IsNullOrEmpty(workingDir)) throw new Exception("Working Directory is null!");

                var finalArgs = new List<string>(argList);
                if (!finalArgs.Contains("--non-interactive")) finalArgs.Add("--non-interactive");
                if (!finalArgs.Contains("--trust-server-cert")) finalArgs.Add("--trust-server-cert");

                var result = await ExecuteRawProcessToFileAsync(finalArgs, workingDir, destFilePath, token).ConfigureAwait(false);
                SVNLogBridge.LogToOutput($"[SvnRunner] TOFILE completed (code {result.exitCode}).");
                return result;
            }
            finally
            {
                try { if (write) _svnLock.ExitWrite(); else _svnLock.ExitRead(); }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner] Lock release failed: {ex.Message}"); }
                DecrementOperations();
            }
        }

        private static async Task<(int exitCode, string error)> ExecuteRawProcessToFileAsync(
            List<string> args, string workingDir, string destFilePath, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "svn",
                WorkingDirectory = Path.GetFullPath((workingDir ?? "").Trim()),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = BuildSafeArguments(args)
            };
            psi.EnvironmentVariables["SVN_SSH"] = BuildSshEnvironmentString(KeyPath);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            SvnProcessTracker.Register(process);

            var stderrTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrSb = new StringBuilder();

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) stderrSb.AppendLine(e.Data);
                else stderrTcs.TrySetResult(true);
            };

            try
            {
                if (!process.Start()) throw new Exception("Failed to start SVN process.");

                process.BeginErrorReadLine();

                using (var fs = new FileStream(destFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(fs, 81920, token).ConfigureAwait(false);
                    await fs.FlushAsync(token).ConfigureAwait(false);
                }

                await WaitForExitAsync(process, token).ConfigureAwait(false);
                await stderrTcs.Task.ConfigureAwait(false);

                return (process.ExitCode, stderrSb.ToString());
            }
            catch (OperationCanceledException)
            {
                SvnProcessTracker.Kill(process);
                throw;
            }
            finally
            {
                try { SvnProcessTracker.Unregister(process); } catch { }
            }
        }

        #endregion

#if UNITY_EDITOR
        public static void ResetStaticState()
        {
            lock (_infoCacheLock)
            {
                _infoCache.Clear();
            }
        }
#endif
    }
}