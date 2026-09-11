using System;
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

        public static string SshOptions { get; set; } = "-o ServerAliveInterval=15 -o ServerAliveCountMax=10 -o IPQoS=throughput -o ConnectionAttempts=3";

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

            var stdoutSb = new StringBuilder(4096);
            var stderrSb = new StringBuilder(1024);
            var stdoutLock = new object();
            var stderrLock = new object();

            try
            {
                if (!process.Start()) throw new Exception("Failed to start SVN process.");

                // === FIX (cancel deadlock, KRYTYCZNE): Kill procesu na cancel.
                //
                // `Task.WhenAll` czeka na WSZYSTKIE taski. WaitForExitAsync rzuca OCE
                // na cancel, ALE pumpy są zablokowane w stream.ReadAsync — to synchroniczny
                // strumień Process (__ConsoleStream), nie obserwuje tokenu. Bez tego
                // callbacka: WaitForExitAsync rzuca → WhenAll czeka na pumpy → pumpy
                // czekają aż proces umrze → proces nie umiera, bo `catch` w którym jest
                // Kill nigdy się nie wykona → DEADLOCK.
                //
                // Ten callback zabija proces NATYCHMIAST gdy token jest anulowany,
                // co zamyka pipe i odblokowuje pumpy (ReadAsync zwraca 0/IOException).
                using var killOnCancel = token.Register(() =>
                {
                    try { SvnProcessTracker.Kill(process); } catch { }
                });

                var stdoutPump = PumpStreamAsync(
                    process.StandardOutput.BaseStream,
                    stdoutSb, stdoutLock,
                    segment =>
                    {
                        try { stdoutCb?.Invoke(segment); }
                        catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] stdout callback error: {ex.Message}"); }
                    },
                    isStderr: false,
                    token);

                var stderrPump = PumpStreamAsync(
                    process.StandardError.BaseStream,
                    stderrSb, stderrLock,
                    segment =>
                    {
                        if (IsSshBannerNoise(segment)) return;
                        try { stderrCb?.Invoke(segment); }
                        catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] stderr callback error: {ex.Message}"); }
                    },
                    isStderr: true,
                    token);

                await Task.WhenAll(
                    WaitForExitAsync(process, token),
                    stdoutPump,
                    stderrPump).ConfigureAwait(false);

                string stdout, stderr;
                lock (stdoutLock) stdout = stdoutSb.ToString();
                lock (stderrLock) stderr = stderrSb.ToString();

                return (stdout.TrimEnd('\r', '\n'), stderr.TrimEnd('\r', '\n'), process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                SvnProcessTracker.Kill(process);   // idempotent — jeśli już ubity, no-op
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
            bool networkRetrySafe = IsNetworkRetrySafe(argList);

            if (write) await _svnLock.EnterWriteAsync(token).ConfigureAwait(false);
            else await _svnLock.EnterReadAsync(token).ConfigureAwait(false);

            try
            {
                IncrementOperations();
                SVNLogBridge.LogToOutput($"[SVN QUEUE]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} Acquired: svn {safeArgs}");

                // === FIX: było "throw new Exception("Working Directory is null!")" —
                // blokowało operacje na URL-ach bez załadowanego projektu (np. blame
                // po URL z listy rewizji przy braku working copy). svn z absolutnym
                // URL działa z dowolnego CWD — fallback na katalog tymczasowy.
                // IsNullOrWhiteSpace (zamiast IsNullOrEmpty) łapie też same-spacje,
                // które przeszłyby przez stary check i wywaliły się dopiero głębiej
                // w Path.GetFullPath.
                if (string.IsNullOrWhiteSpace(workingDir))
                    workingDir = Path.GetTempPath();

                var finalArgs = new List<string>(argList);
                if (!finalArgs.Contains("--non-interactive")) finalArgs.Add("--non-interactive");
                if (!finalArgs.Contains("--trust-server-cert")) finalArgs.Add("--trust-server-cert");

                // === RETRY: lock (max 1x, tylko retryOnLock)
                //          + przejściowe błędy sieci (max 2x, tylko komendy bezpieczne)
                const int MaxNetworkRetries = 2;
                bool lockRetryUsed = false;
                int netRetries = 0;

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    var (output, error, exitCode) = await ExecuteRawProcessAsync(finalArgs, workingDir, stdoutCb, stderrCb, token).ConfigureAwait(false);

                    if (exitCode == 0 && !string.IsNullOrWhiteSpace(error))
                        // '\'→'/' — inaczej "\trunk" w warningu renderuje się jako TAB
                        SVNLogBridge.LogToOutput($"[SvnRunner]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} svn warning: {NormalizeForLog(error.Trim())}");

                    if (exitCode == 0)
                    {
                        SVNLogBridge.LogToOutput($"[SvnRunner]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} Completed (exit code {exitCode}).");
                        return (output, error, exitCode);
                    }

                    // ---------------- failure handling ----------------

                    bool isLockError = error.Contains("E155004") || error.Contains("is locked");

                    // 1) Lock-retry — tylko raz, tylko gdy retryOnLock
                    if (isLockError && retryOnLock && !lockRetryUsed)
                    {
                        lockRetryUsed = true;
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

                    // 2) Retry po przejściowym błędzie sieci (E210002 / reset / timeout).
                    //    Update/switch są wznawialne — svn kontynuuje od miejsca awarii.
                    if (networkRetrySafe && netRetries < MaxNetworkRetries &&
                        !token.IsCancellationRequested && IsTransientNetworkError(error))
                    {
                        netRetries++;
                        int delayMs = 2000 * netRetries;   // 2s, potem 4s

                        SVNLogBridge.LogToOutput(
                            $"<color=yellow><b>[SvnRunner]</b> Connection dropped — retry {netRetries}/{MaxNetworkRetries} in {delayMs / 1000}s" +
                            (write ? " (svn resumes where it stopped)." : ".") + "</color>");

                        await Task.Delay(delayMs, token).ConfigureAwait(false);

                        // przerwany update/switch potrafi zostawić blokadę WC —
                        // cleanup przed wznowieniem (best-effort; nie blokuje retry)
                        if (write)
                        {
                            try
                            {
                                var cleanupArgs = new List<string> { "cleanup", "--non-interactive", "--trust-server-cert" };
                                await ExecuteRawProcessAsync(cleanupArgs, workingDir, null, null, token).ConfigureAwait(false);
                            }
                            catch { /* best-effort */ }
                        }
                        continue;
                    }

                    // 3) Błąd twardy — error już bez banneru (filtrowany u źródła)
                    if (throwOnError)
                    {
                        string diagnostic = error.Contains("E170013") || error.Contains("can't connect") ? " [Connection/URL issue]" : error.Contains("E215004") ? " [Authorization/Password error]" : "";

                        // zabezpieczenie przed pustym message (po filtrze banneru)
                        string errText = string.IsNullOrWhiteSpace(error) ? "(no error output)" : error;

                        // '\'→'/' w exception message — normalizacja u źródła czyści
                        // wszystkie miejsca wyświetlania (labels, GetShortError, result.error)
                        string fullError = $"SVN Error (Code {exitCode}): {NormalizeForLog(errText)}{diagnostic}";

                        SVNLogBridge.LogErrorToOutput(fullError);

                        // === FIX (spójność): InvokeOnOperationError obsługuje
                        // wielokrotnych subskrybentów z osobnym try/catch per handler
                        // (GetInvocationList + iteracja). Inline `OnOperationError?.Invoke(...)`
                        // przerywa łańcuch na PIERWSZYM rzucającym handlerze — reszta
                        // subskrybentów nie dostaje powiadomienia o błędzie operacji SVN.
                        InvokeOnOperationError(fullError);

                        throw new Exception(fullError);
                    }

                    SVNLogBridge.LogToOutput($"[SvnRunner]{(!string.IsNullOrEmpty(logPrefix) ? " " + logPrefix : "")} Completed (exit code {exitCode}).");
                    return (output, error, exitCode);
                }
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

        /// <summary>
        /// === FIX (Windows CRLF): poprzednia wersja traktowała '\r' jako terminator
        /// linii TYLKO dla callbacku — segment NIE trafiał do akumulatora (output).
        /// Na Windows SVN pisze linie jako '\r\n', więc:
        ///   - '\r' emitował linię tylko do callbacku,
        ///   - '\n' był po nim konsumowany (skipNextLF),
        ///   - akumulator (output/error) zostawał PUSTY.
        /// Skutek: `RunAsync` zwracał "" mimo że svn coś wypisał → GetRepoUrlAsync
        /// rzucał "Failed to retrieve repository URL" i cały LoadProject padał.
        ///
        /// Teraz: '\r' i '\n' OBA są terminatorami linii — segment idzie i do
        /// callbacku, i do akumulatora. '\n' bezpośrednio po '\r' jest konsumowany
        /// jako część tego samego CRLF.
        /// </summary>
        private static async Task PumpStreamAsync(
            Stream stream,
            StringBuilder accumulator,
            object accumulatorLock,
            Action<string> onSegment,
            bool isStderr,
            CancellationToken token)
        {
            var buffer = new byte[4096];
            var current = new StringBuilder(256);

            long lastEmitTicks = DateTime.UtcNow.Ticks;
            long intervalTicks = TimeSpan.FromMilliseconds(300).Ticks;

            // '\r' widziany na końcu bufora ma "zjeść" '\n' na początku następnego.
            bool skipNextLF = false;

            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (ObjectDisposedException) { break; }
                catch (IOException) { break; }

                if (read <= 0) break;

                bool emittedTerminatedSegment = false;

                for (int i = 0; i < read; i++)
                {
                    char c = (char)buffer[i];

                    // Część CRLF: pomiń '\n' po '\r'
                    if (skipNextLF)
                    {
                        skipNextLF = false;
                        if (c == '\n') continue;
                    }

                    if (c == '\r')
                    {
                        EmitSegment(current, accumulator, accumulatorLock, onSegment, isStderr);
                        skipNextLF = true;
                        emittedTerminatedSegment = true;
                        continue;
                    }

                    if (c == '\n')
                    {
                        EmitSegment(current, accumulator, accumulatorLock, onSegment, isStderr);
                        emittedTerminatedSegment = true;
                        continue;
                    }

                    current.Append(c);
                }

                // Partial-emit: SVN wypchnął kropki progresu, ale bez '\n'.
                // Co ~300 ms wysyłamy snapshot do callbacku — BEZ resetowania bufora
                // i BEZ wpisu do akumulatora (to nie jest pełna linia).
                if (!emittedTerminatedSegment && current.Length > 0)
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (now - lastEmitTicks >= intervalTicks)
                    {
                        string snapshot = current.ToString();
                        if (!(isStderr && IsSshBannerNoise(snapshot)))
                            onSegment?.Invoke(snapshot);

                        lastEmitTicks = now;
                    }
                }

                if (emittedTerminatedSegment)
                    lastEmitTicks = DateTime.UtcNow.Ticks;
            }

            // Ostatni fragment bez terminatora (strumień zamknięty w trakcie linii)
            if (current.Length > 0)
                EmitSegment(current, accumulator, accumulatorLock, onSegment, isStderr);
        }

        /// <summary>
        /// Pomocnik: wyemituj segment z `current` do callbacku i do akumulatora.
        /// Wołane przez terminator ('\r' lub '\n') oraz na końcu strumienia.
        /// </summary>
        private static void EmitSegment(
            StringBuilder current,
            StringBuilder accumulator,
            object accumulatorLock,
            Action<string> onSegment,
            bool isStderr)
        {
            if (current.Length == 0) return;

            string segment = current.ToString();
            current.Clear();

            if (isStderr && IsSshBannerNoise(segment))
                return;

            onSegment?.Invoke(segment);

            lock (accumulatorLock)
                accumulator.AppendLine(segment);
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
            // === FIX (display): jw.
            Action<string> stderr = (line) => onOutput?.Invoke($"<color=#FFAA00>{NormalizeForLog(line)}</color>");

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
            
            Action<string> stderr = (line) => onOutput?.Invoke($"<color=#FFAA00>{NormalizeForLog(line)}</color>");

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
                // === FIX (memory leak): lazy cleanup wygasłych wpisów przy przekroczeniu progu.
                if (_infoCache.Count > 32)
                {
                    var expired = _infoCache
                        .Where(kv => DateTime.UtcNow - kv.Value.time >= InfoCacheDuration)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in expired)
                        _infoCache.Remove(key);
                }

                if (_infoCache.TryGetValue(cleanWd, out var cached) && DateTime.UtcNow - cached.time < InfoCacheDuration)
                {
                    SVNLogBridge.LogLine($"<color={SVNColors.Header}>[SVN CACHE]</color> Using cached svn info", false);
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

        #region === NEW: Network resilience (transient errors + SSH banner noise) ===

        // Markery błędów przejściowych (transport/SSH). Świadomie BEZ E170013 —
        // to zwykle trwały błąd konfiguracji URL, retry tylko opóźniłby komunikat.
        private static readonly string[] TransientNetworkMarkers =
        {
    "E210002",                          // network connection closed unexpectedly
    "E175002",                          // connection timed out
    "E175012",                          // unable to connect to a repository
    "connection reset",
    "connection closed unexpectedly",
    "connection timed out",
    "client_loop: send disconnect",
    "broken pipe"
};

        public static bool IsTransientNetworkError(string errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText)) return false;
            foreach (var marker in TransientNetworkMarkers)
                if (errorText.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // Frazy banneru SSH serwera — czysty szum w stderr, wycinany u źRÓDŁA,
        // żeby nie trafiały do error string, exception message ani logów.
        private static readonly string[] BannerPhrases =
        {
    "restricted access system",
    "unauthorized access is strictly prohibited",
    "subject to prosecution",
    "you consent to monitoring",
    "recording of all activity"
};

        private static bool IsSshBannerNoise(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            string t = line.Trim();

            // ściany '****' / '@@@@' — delimiter banneru (pojawia się też przy sukcesie)
            string compact = t.Replace(" ", "");
            if (compact.Length >= 8 && compact.All(c => c == '*' || c == '@'))
                return true;

            foreach (var phrase in BannerPhrases)
                if (t.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            return false;
        }

        // Komendy bezpieczne do auto-retry po błędzie sieci:
        // - read-only (restart nic nie psuje)
        // - update/switch — svn WZNAWIA od miejsca awarii (pliki już pobrane
        //   nie wracają, nie są wypisywane ponownie)
        // Świadomie BEZ "commit" — zerwane połączenie w trakcie commitu zostawia
        // niepewność, czy rewizja weszła; retry mógłby stworzyć DUPLIKAT rewizji.
        // (Jeśli używacie checkout — można dopisać "checkout", "co" — svn checkout
        // do istniejącego katalogu kontynuuje jak update.)
        private static readonly HashSet<string> NetworkRetrySafeCommands = new(StringComparer.OrdinalIgnoreCase)
{
    "update", "up", "switch", "sw", "checkout"
};

        private static bool IsNetworkRetrySafe(List<string> argList)
        {
            string command = argList?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("-"));
            if (string.IsNullOrEmpty(command)) return false;
            return NetworkRetrySafeCommands.Contains(command) || ReadCommands.Contains(command);
        }

        #endregion

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
            "I" => "<color=yellow>[I]</color>",
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

        /// <summary>
        /// Zbiór ścieżek (root-relatywnych, '/') z property svn:needs-lock
        /// w working copy. Lokalne (bez serwera); widzi także NIEZACOMMITOWANE
        /// propset/propdel (czyta stan WC, nie HEAD). Klucze są normalizowane
        /// do postaci root-relatywnej niezależnie od formy ścieżek w XML.
        /// Brak '*' w argumentach — bezpieczne przy kliencie globującym argv
        /// (root cause "Models" — patrz SVNFileProtection, FIX 7).
        /// </summary>
        public static async Task<HashSet<string>> GetProtectedPathsAsync(
            string workingDir, CancellationToken token = default)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(workingDir)) return set;

            string xml = await RunAsync("propget svn:needs-lock -R --xml .", workingDir, token: token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml)) return set;

            string wd = workingDir.Replace('\\', '/').TrimEnd('/');

            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var t in doc.Descendants("target"))
                {
                    string p = CleanSvnPath((string)t.Attribute("path"));
                    if (string.IsNullOrWhiteSpace(p)) continue;

                    string norm = p.Replace('\\', '/');
                    if (wd.Length > 0 && norm.StartsWith(wd + "/", StringComparison.OrdinalIgnoreCase))
                        norm = norm.Substring(wd.Length + 1);   // absolutna → root-relatywna

                    if (!string.IsNullOrWhiteSpace(norm))
                        set.Add(norm);
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput("[SvnRunner] Protected-list parse error: " + ex.Message);
            }

            return set;
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

        private static string NormalizeForLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace('\\', '/');
        }

        public static string SanitizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return new string(path.Where(c => !char.IsControl(c) && c != '\u00A0' && c != '\u200B').ToArray()).Trim();
        }

        /// <summary>
        /// Normalizacja ścieżki — obsługuje DWA konteksty:
        ///
        ///  A) ścieżka z `svn status` (względna) — tylko czyści `\`→`/`, usuwa znaki
        ///     kontrolne i wiodące `/`. NIE przycina segmentów `trunk/`/`branches/`/`tags/`
        ///     bo one są REALNYMI folderami w working copy.
        ///
        ///  B) URL (svn+ssh://..., http://...) — przycina do pierwszego markera
        ///     `/trunk/`, `/branches/`, `/tags/` żeby dostać ścieżkę repo-relative.
        ///
        /// === FIX (manglujący bug): poprzednia wersja używała `path.IndexOf("trunk/")`
        /// dla KAŻDEJ ścieżki — plik/folder o nazwie `trunk` gdziekolwiek w drzewie
        /// (np. `Assets/trunk/Foo.cs`) był cicho przycinany do `trunk/Foo.cs`. Skutek:
        /// drzewo zmian pokazywało ścieżki, które nie istnieją na dysku (a refresh
        /// gubił wpisy bo `File.Exists(workingDir + "/" + cleanPath)` zwracał false).
        /// Teraz stripping markerów dotyczy TYLKO inputów URL-owych, a wewnątrz URL-i
        /// szukamy markera jako PEŁNEGO segmentu (z wiodącym '/').
        /// </summary>
        public static string SanitizeSvnPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            // Sanityzacja: usuń znaki kontrolne i NBSP (mógł przyjść z svn output).
            path = new string(path
                .Where(c => !char.IsControl(c) && c != '\u00A0')
                .ToArray());

            path = path.Replace('\\', '/').Trim();

            if (path.Length == 0) return string.Empty;

            // === Kontekst URL: tylko wtedy przycinamy trunk/branches/tags do granicy.
            // Sprawdzenie "://" odróżnia URL od ścieżki względnej (Windows drive "C:"
            // ma dwukropek, ale nie "://"; ścieżka absolutna "/trunk" też tego nie ma).
            bool looksLikeUrl = path.IndexOf("://", StringComparison.Ordinal) > 0;

            if (!looksLikeUrl)
            {
                // Ścieżka względna z `svn status` (lub absolutna z dysku) — nie
                // ruszamy segmentów. Tylko wiodący '/' (jeśli jest) na out.
                return path.TrimStart('/');
            }

            // URL — przycina do pierwszego markera struktury repo (jako PEŁNY segment).
            // Bierzemy NAJWCZEŚNIEJSZE wystąpienie, żeby np.
            // `/srv/svn/repos/x/branches/trunk-fix/...` nie łapał się fałszywie.
            string[] markers = { "/trunk/", "/branches/", "/tags/" };

            int cut = int.MaxValue;
            foreach (var marker in markers)
            {
                int idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx < cut)
                    cut = idx;
            }

            if (cut != int.MaxValue)
                return path.Substring(cut + 1);   // skip leading '/'

            // URL bez markera struktury — zwróć znormalizowany URL (bez wiodącego '/').
            return path.TrimStart('/');
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

        private static void InvokeOnOperationError(string message)
        {
            var handlers = OnOperationError?.GetInvocationList();
            if (handlers == null) return;
            foreach (var h in handlers)
            {
                try { ((Action<string>)h).Invoke(message); }
                catch (Exception ex) { SVNLogBridge.LogErrorToOutput($"[SVN] OnOperationError handler error: {ex.Message}"); }
            }
        }

        /// <summary>
        /// === FIX (defensywny): kill procesu na cancel. CopyToAsync z tokenem
        /// zwykle przerwie się sam, ale przy zablokowanym pipe (ssh backpressure,
        /// wolny dysk) może to zająć sekundy — token.Register(Kill) zamyka pipe
        /// natychmiast i odblokowuje CopyToAsync.
        /// </summary>
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
                if (e.Data == null) { stderrTcs.TrySetResult(true); return; }

                if (IsSshBannerNoise(e.Data)) return;

                stderrSb.AppendLine(e.Data);
            };

            try
            {
                if (!process.Start()) throw new Exception("Failed to start SVN process.");

                process.BeginErrorReadLine();

                using var killOnCancel = token.Register(() =>
                {
                    try { SvnProcessTracker.Kill(process); } catch { }
                });

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
        /// <summary>
        /// Reset stanu statycznych pól po przerwanym Play mode (Domain Reload wyłączony).
        ///
        /// === FIX (krytyczne, deadlock): _svnLock MUSI być zresetowany tak samo jak
        /// _managerLock w SVNManager. Bez tego stop Play w trakcie aktywnego svn
        /// command zostawiał semafory zajęte (task przerwany przed finally { ExitWrite }),
        /// a następna sesja Play deadlockowała na pierwszym RunAsync.
        /// </summary>
        public static void ResetStaticState()
        {
            lock (_infoCacheLock)
            {
                _infoCache.Clear();
            }

            lock (_processingLock)
            {
                _activeOperationsCount = 0;
                if (_processingState)
                {
                    _processingState = false;
                    InvokeProcessingStateChanged(false);
                }
            }

            // === FIX (deadlock): reset semaforów locka SvnRunner.
            _svnLock.ResetForEditorReload();
        }
#endif
    }
}