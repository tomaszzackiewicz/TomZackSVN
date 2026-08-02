using System;
using System.Collections.Concurrent;
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
        private static string _keyPath = "";

        private static readonly AsyncReaderWriterLock _svnLock = new();

        public static event Action<bool> OnProcessingStateChanged;
        private static int _activeOperationsCount = 0;
        private static bool _processingState = false;
        private static readonly object _processingLock = new();

        private static readonly Dictionary<string, (string output, DateTime time)> _infoCache = new();
        private static readonly TimeSpan InfoCacheDuration = TimeSpan.FromSeconds(2);
        private static readonly object _infoCacheLock = new();

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
                    OnProcessingStateChanged?.Invoke(true);
                }
            }
        }

        private static void DecrementOperations()
        {
            lock (_processingLock)
            {
                _activeOperationsCount--;
                if (_activeOperationsCount < 0)
                    _activeOperationsCount = 0;

                if (_processingState && _activeOperationsCount == 0)
                {
                    _processingState = false;
                    SVNLogBridge.LogToOutput("<color=#FFCC00>[SVN]</color> Processing END");
                    OnProcessingStateChanged?.Invoke(false);
                }
            }
        }

        private static string BuildSafeArguments(IEnumerable<string> args)
        {
            var escaped = new List<string>();
            foreach (var arg in args)
            {
                if (string.IsNullOrEmpty(arg))
                    escaped.Add("\"\"");
                else
                    escaped.Add(EscapeSingleArgument(arg));
            }
            return string.Join(" ", escaped);
        }

        private static string EscapeSingleArgument(string arg)
        {
            if (!arg.Contains(' ') && !arg.Contains('"'))
                return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        private static bool IsWriteCommand(IEnumerable<string> args)
        {
            if (args == null) return false;
            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (arg.StartsWith("-")) continue;
                return WriteCommands.Contains(arg.ToLowerInvariant());
            }
            return false;
        }

        public static async Task<string> RunAsync(
            string command,
            string workingDir,
            bool retryOnLock = true,
            CancellationToken token = default)
        {
            var parts = SplitArguments(command);
            return await RunAsync(parts, workingDir, retryOnLock, token);
        }

        public static async Task<string> RunAsync(
            IEnumerable<string> args,
            string workingDir,
            bool retryOnLock = true,
            CancellationToken token = default)
        {
            string safeArgs = BuildSafeArguments(args);
            SVNLogBridge.LogToOutput($"[SVN QUEUE] Waiting: svn {safeArgs}");

            bool write = IsWriteCommand(args);
            if (write)
                await _svnLock.EnterWriteAsync(token);
            else
                await _svnLock.EnterReadAsync(token);

            try
            {
                IncrementOperations();
                SVNLogBridge.LogToOutput($"[SVN QUEUE] Acquired: svn {safeArgs}");

                if (string.IsNullOrEmpty(workingDir))
                    throw new Exception("Working Directory is null!");

                string cleanWorkingDir = Path.GetFullPath(workingDir.Trim());

                string safeKeyPath = (!string.IsNullOrEmpty(KeyPath))
                    ? KeyPath.Trim().Replace("\"", "").Replace('\\', '/')
                    : "";

                var finalArgs = new List<string>(args);
                if (!finalArgs.Contains("--non-interactive"))
                    finalArgs.Add("--non-interactive");
                if (!finalArgs.Contains("--trust-server-cert"))
                    finalArgs.Add("--trust-server-cert");

                int maxAttempts = retryOnLock ? 2 : 1;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();

                    var psi = new ProcessStartInfo
                    {
                        FileName = "svn",
                        WorkingDirectory = cleanWorkingDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    foreach (var arg in finalArgs)
                        psi.ArgumentList.Add(arg);

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

                    var outputQueue = new ConcurrentQueue<string>();
                    var errorQueue = new ConcurrentQueue<string>();

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            outputQueue.Enqueue(e.Data);
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            errorQueue.Enqueue(e.Data);
                    };

                    try
                    {
                        SVNLogBridge.LogToOutput($"[SvnRunner] Starting process...");
                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        await WaitForExitAsync(process, token);

                        string err = string.Join("\n", errorQueue);

                        if (process.ExitCode != 0)
                        {
                            bool isLockError =
                                err.Contains("locked") ||
                                err.Contains("cleanup");

                            if (attempt == 0 && retryOnLock && isLockError)
                            {
                                SVNLogBridge.LogErrorToOutput("[SvnRunner] Lock detected. Running Cleanup...");
                                SVNLogBridge.LogToOutput("<color=orange>[SVN]</color> Performing automatic cleanup...");

                                await RunAsync("cleanup", workingDir, false, token);

                                SVNLogBridge.LogToOutput("<color=green>[SVN]</color> Cleanup completed. Retrying...");
                                continue;
                            }

                            string diagnostic =
                                err.Contains("E170013") || err.Contains("can't connect")
                                    ? " [Connection/URL issue]"
                                    : err.Contains("E215004")
                                        ? " [Authorization/Password error]"
                                        : "";

                            string fullError = $"SVN Error (Code {process.ExitCode}): {err}{diagnostic}";
                            SVNLogBridge.LogErrorToOutput(fullError);
                            throw new Exception(fullError);
                        }

                        SVNLogBridge.LogToOutput($"[SvnRunner] Completed successfully.");
                        return string.Join("\n", outputQueue);
                    }
                    catch (OperationCanceledException)
                    {
                        SvnProcessTracker.Kill(process);
                        throw;
                    }
                }

                throw new Exception("SVN retry system failed.");
            }
            finally
            {
                try
                {
                    if (write) _svnLock.ExitWrite();
                    else _svnLock.ExitRead();
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SvnRunner] Lock release failed: {ex.Message}");
                }
                DecrementOperations();
            }
        }

        private static List<string> SplitArguments(string command)
        {
            var args = new List<string>();
            if (string.IsNullOrWhiteSpace(command)) return args;

            using var reader = new StringReader(command.Trim());
            var current = new StringBuilder();
            bool inQuotes = false;

            while (true)
            {
                int ch = reader.Read();
                if (ch == -1) break;
                if (ch == '"')
                    inQuotes = !inQuotes;
                else if (ch == ' ' && !inQuotes)
                {
                    if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
                }
                else
                    current.Append((char)ch);
            }
            if (current.Length > 0) args.Add(current.ToString());
            return args;
        }

        public static async Task<string> RunLiveAsync(
            string args,
            string workingDir,
            Action<string> onLineReceived,
            CancellationToken token = default)
        {
            var argList = SplitArguments(args);
            return await RunLiveAsync(argList, workingDir, onLineReceived, token);
        }

        public static async Task<string> RunLiveAsync(
            IEnumerable<string> args,
            string workingDir,
            Action<string> onLineReceived,
            CancellationToken token = default)
        {
            string safeArgs = BuildSafeArguments(args);
            SVNLogBridge.LogToOutput($"[SVN QUEUE] Waiting LIVE: svn {safeArgs}");

            bool write = IsWriteCommand(args);
            if (write) await _svnLock.EnterWriteAsync(token);
            else await _svnLock.EnterReadAsync(token);

            Process process = null;

            try
            {
                IncrementOperations();
                SVNLogBridge.LogToOutput($"[SVN QUEUE] Acquired LIVE: svn {safeArgs}");

                string cleanWorkingDir = Path.GetFullPath((workingDir ?? "").Trim());

                var finalArgs = new List<string>(args);
                if (!finalArgs.Contains("--non-interactive"))
                    finalArgs.Add("--non-interactive");
                if (!finalArgs.Contains("--trust-server-cert"))
                    finalArgs.Add("--trust-server-cert");

                var psi = new ProcessStartInfo
                {
                    FileName = "svn",
                    WorkingDirectory = cleanWorkingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };

                foreach (var arg in finalArgs)
                    psi.ArgumentList.Add(arg);

                if (!string.IsNullOrEmpty(KeyPath))
                {
                    string safeKey = KeyPath.Trim().Replace("\"", "").Replace('\\', '/');
                    psi.EnvironmentVariables["SVN_SSH"] =
                        $"ssh -i \"{safeKey}\" " +
                        "-o IdentitiesOnly=yes " +
                        "-o StrictHostKeyChecking=no " +
                        "-o BatchMode=yes " +
                        "-o LogLevel=QUIET " +
                        "-o ServerAliveInterval=15 " +
                        "-o ServerAliveCountMax=10 " +
                        "-o IPQoS=throughput";
                }

                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                SvnProcessTracker.Register(process);

                var outputQueue = new ConcurrentQueue<string>();
                var errorQueue = new ConcurrentQueue<string>();

                process.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    outputQueue.Enqueue(e.Data);
                    try { onLineReceived?.Invoke(e.Data); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner Live] Callback error: {ex.Message}"); }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    errorQueue.Enqueue(e.Data);
                    try { onLineReceived?.Invoke($"[SVN ERROR] {e.Data}"); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner Live] Callback error: {ex.Message}"); }
                };

                SVNLogBridge.LogToOutput("[SvnRunner Live] Starting process...");

                if (!process.Start())
                    throw new Exception("Failed to start SVN process.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await WaitForExitAsync(process, token);

                string errors = string.Join("\n", errorQueue);
                if (process.ExitCode != 0)
                {
                    string finalError = $"SVN Error (Code {process.ExitCode})\n{errors}";
                    SVNLogBridge.LogErrorToOutput(finalError);
                    throw new Exception(finalError);
                }

                SVNLogBridge.LogLine("[SvnRunner Live] Completed successfully.");
                return string.Join("\n", outputQueue);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogToOutput("<color=#FFD700>[CANCEL]</color> SVN operation canceled.");
                if (process != null) { try { SvnProcessTracker.Kill(process); } catch { } }
                throw;
            }
            finally
            {
                if (process != null)
                {
                    try { process.CancelOutputRead(); } catch { }
                    try { process.CancelErrorRead(); } catch { }
                    try { process.Dispose(); } catch { }
                }
                try
                {
                    if (write) _svnLock.ExitWrite();
                    else _svnLock.ExitRead();
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SvnRunner] Lock release failed: {ex.Message}");
                }
                DecrementOperations();
            }
        }

        public static async Task<int> RunStreamedAsync(
           string arguments,
           string workingDirectory,
           Action<string> onOutput,
           CancellationToken token)
        {
            var argList = SplitArguments(arguments);
            return await RunStreamedAsync(argList, workingDirectory, onOutput, token);
        }

        public static async Task<int> RunStreamedAsync(
            IEnumerable<string> args,
            string workingDirectory,
            Action<string> onOutput,
            CancellationToken token)
        {
            SVNLogBridge.LogToOutput($"<color=#00FFFF>[SvnRunner]</color> Starting SVN STREAMED: svn {BuildSafeArguments(args)}");

            bool write = IsWriteCommand(args);
            if (write) await _svnLock.EnterWriteAsync(token);
            else await _svnLock.EnterReadAsync(token);

            try
            {
                IncrementOperations();

                string cleanWorkingDir = Path.GetFullPath((workingDirectory ?? "").Trim());

                var finalArgs = new List<string>(args);
                if (!finalArgs.Contains("--non-interactive"))
                    finalArgs.Add("--non-interactive");
                if (!finalArgs.Contains("--trust-server-cert"))
                    finalArgs.Add("--trust-server-cert");

                var psi = new ProcessStartInfo
                {
                    FileName = "svn",
                    WorkingDirectory = cleanWorkingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                foreach (var arg in finalArgs)
                    psi.ArgumentList.Add(arg);

                string sshKeyPath = KeyPath;
                if (!string.IsNullOrWhiteSpace(sshKeyPath))
                {
                    string safeKeyPath = sshKeyPath.Trim().Trim('"').Replace("\\", "/");
                    psi.EnvironmentVariables["SVN_SSH"] =
                        $"ssh -i \"{safeKeyPath}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o BatchMode=yes -o LogLevel=QUIET";
                }

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                SvnProcessTracker.Register(process);

                process.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    try { onOutput?.Invoke(e.Data); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner] Streamed callback error: {ex.Message}"); }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    try { onOutput?.Invoke($"<color=#FFAA00>{e.Data}</color>"); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner] Streamed callback error: {ex.Message}"); }
                };

                try
                {
                    if (!process.Start()) throw new Exception("Process.Start() returned false.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await WaitForExitAsync(process, token);
                    return process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) try { process.Kill(); } catch { }
                    throw;
                }
            }
            finally
            {
                try
                {
                    if (write) _svnLock.ExitWrite();
                    else _svnLock.ExitRead();
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SvnRunner] Lock release failed: {ex.Message}");
                }
                DecrementOperations();
            }
        }

        public static async Task<int> RunStreamedLiveAsync(
            string arguments,
            string workingDirectory,
            Action<string> onOutput,
            CancellationToken token)
        {
            var argList = SplitArguments(arguments);
            return await RunStreamedLiveAsync(argList, workingDirectory, onOutput, token);
        }

        public static async Task<int> RunStreamedLiveAsync(
            IEnumerable<string> args,
            string workingDirectory,
            Action<string> onOutput,
            CancellationToken token)
        {
            SVNLogBridge.LogToOutput($"<color=#00FFFF>[SvnRunner]</color> Starting SVN LIVE STREAMED: svn {BuildSafeArguments(args)}");

            bool write = IsWriteCommand(args);
            if (write) await _svnLock.EnterWriteAsync(token);
            else await _svnLock.EnterReadAsync(token);

            try
            {
                IncrementOperations();

                string cleanWorkingDir = Path.GetFullPath((workingDirectory ?? "").Trim());

                var finalArgs = new List<string>(args);
                if (!finalArgs.Contains("--non-interactive"))
                    finalArgs.Add("--non-interactive");
                if (!finalArgs.Contains("--trust-server-cert"))
                    finalArgs.Add("--trust-server-cert");

                var psi = new ProcessStartInfo
                {
                    FileName = "svn",
                    WorkingDirectory = cleanWorkingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                foreach (var arg in finalArgs)
                    psi.ArgumentList.Add(arg);

                string sshKeyPath = KeyPath;
                if (!string.IsNullOrWhiteSpace(sshKeyPath))
                {
                    string safeKeyPath = sshKeyPath.Trim().Trim('"').Replace("\\", "/");
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

                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                SvnProcessTracker.Register(process);

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    try { onOutput?.Invoke(e.Data); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner] Live Streamed callback error: {ex.Message}"); }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    try { onOutput?.Invoke($"<color=#FFAA00>{e.Data}</color>"); }
                    catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SvnRunner] Live Streamed callback error: {ex.Message}"); }
                };

                try
                {
                    if (!process.Start()) throw new Exception("Process.Start() returned false.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await WaitForExitAsync(process, token);

                    token.ThrowIfCancellationRequested();
                    return process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    throw;
                }
            }
            finally
            {
                try
                {
                    if (write) _svnLock.ExitWrite();
                    else _svnLock.ExitRead();
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[SvnRunner] Lock release failed: {ex.Message}");
                }
                DecrementOperations();
            }
        }

        private static async Task WaitForExitAsync(Process process, CancellationToken token)
        {
            if (process == null) return;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            EventHandler handler = null;
            handler = (s, e) => tcs.TrySetResult(true);
            process.Exited += handler;

            try
            {
                if (process.HasExited)
                {
                    tcs.TrySetResult(true);
                    return;
                }

                using (token.Register(() => tcs.TrySetCanceled(token)))
                {
                    await tcs.Task;
                }
            }
            finally
            {
                process.Exited -= handler;
            }
        }

        public static async Task WaitForSemaphoreFreeAsync(CancellationToken token = default)
        {
            await _svnLock.EnterWriteAsync(token);
            _svnLock.ExitWrite();
        }

        public static async Task<string> GetInfoAsync(string workingDir, CancellationToken token = default)
        {
            string cleanWd = string.IsNullOrEmpty(workingDir) ? "" : Path.GetFullPath(workingDir.Trim());

            lock (_infoCacheLock)
            {
                if (_infoCache.TryGetValue(cleanWd, out var cached))
                {
                    if (DateTime.UtcNow - cached.time < InfoCacheDuration)
                    {
                        SVNLogBridge.LogLine("<color=#8888FF>[SVN CACHE]</color> Using cached svn info", false);
                        return cached.output;
                    }
                }
            }

            string result = await RunAsync("info", cleanWd, true, token).ConfigureAwait(false);

            lock (_infoCacheLock)
            {
                _infoCache[cleanWd] = (result, DateTime.UtcNow);
            }

            return result;
        }

        private static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "update", "commit", "lock", "unlock", "switch", "cleanup", "revert",
            "merge", "copy", "delete", "mkdir", "propset", "propdel",
            "shelf-save", "shelf-restore", "shelf-drop",
            "import", "export"
        };

        public static string ForceCleanPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return new string(path.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
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

            var combinedEntries = new List<string>(64);

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
                    if (!combinedEntries.Contains(fullPath))
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
                    if (!combinedEntries.Contains(fullPath))
                        combinedEntries.Add(fullPath);
                }
            }

            combinedEntries.Sort((a, b) =>
            {
                bool aIsDir = Directory.Exists(a) || string.IsNullOrEmpty(Path.GetExtension(a));
                bool bIsDir = Directory.Exists(b) || string.IsNullOrEmpty(Path.GetExtension(b));
                if (aIsDir != bIsDir) return aIsDir ? -1 : 1;
                return string.CompareOrdinal(a, b);
            });

            for (int i = 0; i < combinedEntries.Count; i++)
            {
                string entry = combinedEntries[i];
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
                bool isLast = (i == combinedEntries.Count - 1);

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
                    sb.Append(parentIsLast[j] ? "    " : "│   ");

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
            catch
            {
                return "";
            }
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

            string output = await RunAsync($"ls \"{targetUrl}\"", workingDir, token: token);

            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.TrimEnd('/'))
                         .ToArray();
        }

        public static async Task<string> GetRepoUrlAsync(string workingDir, CancellationToken token = default)
        {
            string output = await RunAsync("info --xml", workingDir, true, token).ConfigureAwait(false);

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

            string[] roots =
            {
                "trunk/",
                "branches/",
                "tags/"
            };

            foreach (string root in roots)
            {
                int index = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                    return path.Substring(index);
            }

            return path;
        }
    }
}