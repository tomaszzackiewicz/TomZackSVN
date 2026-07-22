using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Xml;
using System.Xml.Linq;

namespace SVN.Core
{
    public class SVNCheckout : SVNBase
    {
        private CancellationTokenSource _checkoutCTS;
        private readonly SynchronizationContext _mainThreadContext;
        private long _cachedTotalSizeBytes;
        private bool _canResume;

        private enum OperationState { Idle, Running, Pausing, Paused, Cancelling, Cancelled, Completed, Failed }
        private volatile OperationState _state = OperationState.Idle;
        private readonly object _stateLock = new object();

        private const double BytesInGB = 1024d * 1024d * 1024d;
        private const double BytesInMB = 1024d * 1024d;
        private const double MinSpeedThresholdMB = 0.01d;
        private const double SvnOverheadMultiplier = 2.0d;

        private DateTime _lastStartAttempt = DateTime.MinValue;
        private const double DebounceIntervalMs = 1000d;
        private string _resolvedKeyPath;

        private long _lastKnownDirectorySize;
        private DateTime _lastDirectorySizeCheck = DateTime.MinValue;
        private readonly object _sizeCacheLock = new object();

        public SVNCheckout(SVNUI svnUI, SVNManager manager) : base(svnUI, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        #region Key & SSH Configuration

        private string ResolveAndValidateKeyPath()
        {
            string keyPath = SvnRunner.KeyPath;
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                keyPath = SVNManager.Instance?.CurrentKey;
                if (!string.IsNullOrWhiteSpace(keyPath))
                    SVNLogBridge.LogLine("<color=yellow>[SVN]</color> Using fallback SSH key.");
            }

            if (string.IsNullOrWhiteSpace(keyPath)) return null;

            keyPath = keyPath.Replace("\"", string.Empty).Trim();
            if (keyPath.StartsWith("~"))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                keyPath = Path.Combine(home, keyPath.Substring(1).TrimStart('\\', '/'));
            }

            try { keyPath = Path.GetFullPath(keyPath); }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Invalid SSH key path: {ex.Message}");
                return null;
            }

            if (!File.Exists(keyPath))
            {
                SVNLogBridge.LogError($"[SVN] SSH key not found: {keyPath}");
                SVNLogBridge.LogError("[SVN] Please verify the SSH key path in Settings.");
                return null;
            }

            try
            {
                FileInfo fileInfo = new FileInfo(keyPath);
                if ((fileInfo.Attributes & FileAttributes.ReadOnly) != 0)
                    SVNLogBridge.LogLine("<color=yellow>[SVN]</color> Warning: SSH key is marked as read-only.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Cannot access SSH key: {ex.Message}");
                return null;
            }

            _resolvedKeyPath = keyPath;
            SVNLogBridge.LogLine($"<color=green>[SVN]</color> SSH key resolved: {keyPath}");
            return keyPath;
        }

        private string BuildSshConfigOption(string keyPath)
        {
            if (string.IsNullOrWhiteSpace(keyPath)) return string.Empty;
            string normalizedKeyPath = keyPath.Replace("\\", "/");
            string nullDevice = Environment.OSVersion.Platform == PlatformID.Win32NT ? "NUL" : "/dev/null";
            string sshCommand = $"ssh -i \"{normalizedKeyPath}\" -o StrictHostKeyChecking=no -o UserKnownHostsFile={nullDevice}";
            return $" --config-option config:tunnels:ssh=\"{sshCommand}\"";
        }

        #endregion

        #region Project Info

        public async void UpdateProjectInfo()
        {
            try { await UpdateProjectInfoAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"UpdateProjectInfo failed: {ex}");
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, $"<color=#FFAA00>Error: {ex.Message}</color>", "Info");
            }
        }

        private async Task UpdateProjectInfoAsync()
        {
            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string destPath = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(url)) return;
            if (string.IsNullOrWhiteSpace(destPath))
            {
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                    "<color=yellow><b>Info:</b> Enter destination path to check disk space.</color>", "Info");
                return;
            }

            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Analyzing repository...", "Info");

            string keyPath = ResolveAndValidateKeyPath();
            string sshConfig = BuildSshConfigOption(keyPath);
            _cachedTotalSizeBytes = await GetRemoteRepositorySizeAsync(url, sshConfig).ConfigureAwait(false);
            string structure = await GetRepositoryStructureAsync(url, sshConfig).ConfigureAwait(false);

            double repositoryGB = _cachedTotalSizeBytes / BytesInGB;
            double requiredGB = repositoryGB * SvnOverheadMultiplier;
            string driveLabel;
            double freeSpaceGB;

            try
            {
                string fullPath = Path.GetFullPath(destPath);
                driveLabel = Path.GetPathRoot(fullPath);
                DriveInfo drive = new DriveInfo(driveLabel);
                freeSpaceGB = drive.AvailableFreeSpace / BytesInGB;
            }
            catch { driveLabel = "?"; freeSpaceGB = 0; }

            string spaceColor = freeSpaceGB < requiredGB && requiredGB > 0 ? "red" : "green";
            var sb = new StringBuilder(512);
            sb.Append("<b>Repository Size:</b> ").AppendFormat("{0:F2}", repositoryGB).Append(" GB\n")
              .Append("<b>Required Space:</b> ").AppendFormat("{0:F2}", requiredGB).Append(" GB\n")
              .Append("<b>Available Space (").Append(driveLabel).Append("):</b> <color=")
              .Append(spaceColor).Append(">").AppendFormat("{0:F2}", freeSpaceGB).Append(" GB</color>\n\n")
              .Append("<b>Repository Structure:</b>\n").Append(structure).Append("\n\n");

            if (requiredGB > 0 && freeSpaceGB < requiredGB)
                sb.Append("<color=#FFAA00><b>ERROR:</b> Not enough disk space. SVN needs approximately ")
                  .AppendFormat("{0:F2}", requiredGB).Append(" GB.</color>");
            else if (_cachedTotalSizeBytes == 0)
                sb.Append("<color=yellow>Could not determine repository size. The repository may be empty or unreachable.</color>");
            else
                sb.Append("<color=green>Ready to checkout.</color>");

            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, sb.ToString(), "Info");
        }

        private async Task<string> GetRepositoryStructureAsync(string baseUrl, string sshConfig = "")
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
            baseUrl = baseUrl.TrimEnd('/');

            try
            {
                string output = await SvnRunner.RunAsync(
                    $"list \"{baseUrl}\" --non-interactive --trust-server-cert" + sshConfig,
                    Path.GetTempPath(), false, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output))
                    return "<color=yellow>Repository is empty or unreachable.</color>";

                var entries = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimEnd('/'))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                var directoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entry in entries)
                    if (!directoryMap.ContainsKey(entry)) directoryMap.Add(entry, entry);

                var result = new List<string>(3);
                if (directoryMap.TryGetValue("trunk", out string trunk)) result.Add($"{trunk}");
                if (directoryMap.TryGetValue("branches", out string branches))
                {
                    int count = await GetDirectoryCountAsync($"{baseUrl}/{branches}", sshConfig).ConfigureAwait(false);
                    result.Add($"{branches} ({count} branches)");
                }
                if (directoryMap.TryGetValue("tags", out string tags))
                {
                    int count = await GetDirectoryCountAsync($"{baseUrl}/{tags}", sshConfig).ConfigureAwait(false);
                    result.Add($"{tags} ({count} tags)");
                }

                if (result.Count == 0) return "<color=yellow>No standard SVN structure found (flat repository).</color>";
                return string.Join("\n", result);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Error loading repository structure: {ex.Message}");
                return "<color=#FFAA00>Error loading repository structure.</color>";
            }
        }

        private async Task<int> GetDirectoryCountAsync(string targetUrl, string sshConfig = "")
        {
            try
            {
                string output = await SvnRunner.RunAsync(
                    $"list \"{targetUrl}\" --xml --non-interactive --trust-server-cert" + sshConfig,
                    Path.GetTempPath(), false, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output)) return 0;
                XDocument document = XDocument.Parse(output);
                return document.Descendants("entry")
                    .Count(x => string.Equals((string)x.Attribute("kind"), "dir", StringComparison.OrdinalIgnoreCase));
            }
            catch { return 0; }
        }

        #endregion

        #region Checkout & Resume

        public async void StartCheckout()
        {
            try { await StartCheckoutAsync().ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task StartCheckoutAsync()
        {
            if (!CanStartOperation()) return;

            string url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(path))
            {
                ShowError("Repository URL and destination path cannot be empty.");
                return;
            }

            if (!IsValidSvnUrl(url))
            {
                ShowError("Invalid SVN URL. Expected svn://, svn+ssh://, http:// or https://.");
                return;
            }

            if (!TryValidatePath(path, out string fullPath)) return;

            if (Directory.Exists(fullPath) && Directory.GetFileSystemEntries(fullPath).Length > 0)
            {
                if (Directory.Exists(Path.Combine(fullPath, ".svn")))
                    ShowError("Destination already contains an SVN working copy. Use Resume instead.");
                else
                    ShowError("Destination folder is not empty.");
                return;
            }

            string keyPath = ResolveAndValidateKeyPath();
            if (url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(keyPath))
            {
                ShowError("SSH repository requires a valid private key.");
                return;
            }

            lock (_stateLock)
            {
                _state = OperationState.Idle;
                _canResume = false;
            }

            string sshConfig = BuildSshConfigOption(keyPath);
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Calculating repository size...", "SVN");
            _cachedTotalSizeBytes = await GetRemoteRepositorySizeAsync(url, sshConfig).ConfigureAwait(false);

            string checkoutArgs = $"checkout \"{url}\" \"{fullPath}\" --non-interactive --trust-server-cert" + sshConfig;
            await ExecuteSvnOperationAsync(url, fullPath, checkoutArgs, false, keyPath, "Downloading").ConfigureAwait(false);
        }

        public async void ResumeCheckout()
        {
            try { await ResumeCheckoutAsync().ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task ResumeCheckoutAsync()
        {
            if (!CanStartOperation()) return;

            lock (_stateLock)
            {
                if (!_canResume)
                {
                    ShowError("Cannot resume. The operation was explicitly cancelled.");
                    return;
                }
            }

            string url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                ShowError("Destination path cannot be empty.");
                return;
            }

            if (!TryValidatePath(path, out string fullPath)) return;

            if (!Directory.Exists(Path.Combine(fullPath, ".svn")))
            {
                ShowError("No .svn metadata found. Start a new checkout.");
                return;
            }

            string keyPath = ResolveAndValidateKeyPath();
            string sshConfig = BuildSshConfigOption(keyPath);

            lock (_stateLock) { _state = OperationState.Running; }
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow><b>Resuming checkout...</b></color>", "SVN");

            if (_cachedTotalSizeBytes <= 0)
                _cachedTotalSizeBytes = await GetRemoteRepositorySizeAsync(url, sshConfig).ConfigureAwait(false);

            string updateArgs = "update --non-interactive --trust-server-cert" + sshConfig;
            await ExecuteSvnOperationAsync(url, fullPath, updateArgs, true, keyPath, "Resuming").ConfigureAwait(false);
        }

        public void PauseCheckout()
        {
            lock (_stateLock)
            {
                if (!IsProcessing) return;
                _canResume = true;
                if (_state != OperationState.Running) return;
                _state = OperationState.Pausing;
            }

            SVNLogBridge.LogLine("<color=yellow>[SVN]</color> Pausing checkout...");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow>Pausing...</color>", "SVN");
            _checkoutCTS?.Cancel();
        }

        public void CancelCheckout()
        {
            lock (_stateLock)
            {
                if (!IsProcessing) return;
                _canResume = false;
                if (_state == OperationState.Cancelling) return;
                _state = OperationState.Cancelling;
            }

            SVNLogBridge.LogLine("<color=#FFAA00>[SVN]</color> Cancelling checkout...");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=#FFAA00>Cancelling...</color>", "SVN");
            _checkoutCTS?.Cancel();
        }

        #endregion

        #region Size Calculation

        private async Task<long> GetRemoteRepositorySizeAsync(string url, string sshConfig = "")
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;
            try
            {
                string args = $"list --xml -R \"{url}\" --non-interactive --trust-server-cert" + sshConfig;
                string output = await SvnRunner.RunAsync(args, Path.GetTempPath(), false, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return 0;

                long totalBytes = 0;
                using (var reader = new StringReader(output))
                using (var xmlReader = XmlReader.Create(reader))
                {
                    while (xmlReader.Read())
                    {
                        if (xmlReader.NodeType == XmlNodeType.Element && xmlReader.Name == "size")
                        {
                            if (xmlReader.Read() && long.TryParse(xmlReader.Value, out long size))
                                totalBytes += size;
                        }
                    }
                }

                SVNLogBridge.LogLine($"[SVN] Repository size: {totalBytes / BytesInMB:F2} MB");
                return totalBytes;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Failed to calculate repository size: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Core Execution

        private async Task ExecuteSvnOperationAsync(string url, string path, string command, bool isResume, string keyPath, string operationType)
        {
            if (IsProcessing)
            {
                SVNLogBridge.LogLine("<color=yellow>[SVN]</color> Operation already running.");
                return;
            }

            IsProcessing = true;
            CancellationTokenSource cts = null;
            Task monitorTask = null;
            Task logFlushTask = null;
            var logBuffer = new ConcurrentQueue<string>();

            try
            {
                lock (_stateLock) { _state = OperationState.Running; }

                cts = new CancellationTokenSource();
                _checkoutCTS = cts;
                CancellationToken token = cts.Token;

                DateTime startTime = DateTime.Now;
                DateTime lastActivity = DateTime.Now;
                long sizeBeforeSession = Directory.Exists(path) ? GetDirectorySizeFast(path) : 0;

                bool isExport = operationType == "Exporting";

                if (!isExport && !Directory.Exists(path))
                    Directory.CreateDirectory(path);

                if (isResume)
                {
                    PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Cleaning working copy...", "SVN"));
                    string sshConfig = BuildSshConfigOption(keyPath);
                    string cleanupResult = await SvnRunner.RunAsync(
                        $"cleanup --non-interactive --trust-server-cert" + sshConfig, path, false, token).ConfigureAwait(false);

                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    if (!string.IsNullOrWhiteSpace(cleanupResult) && cleanupResult.Contains("error", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"SVN cleanup failed:\n{cleanupResult}");
                }

                logFlushTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await Task.Delay(150, token).ConfigureAwait(false);
                            FlushLogBuffer(logBuffer);
                        }
                    }
                    catch (OperationCanceledException) { FlushLogBuffer(logBuffer); }
                }, token);

                monitorTask = Task.Run(async () =>
                {
                    try
                    {
                        var sb = new StringBuilder(256);
                        while (!token.IsCancellationRequested)
                        {
                            long currentSize = GetDirectorySizeFast(path);
                            long sessionBytes = Math.Max(currentSize - sizeBeforeSession, 0);
                            double sessionMB = sessionBytes / BytesInMB;
                            double totalGB = currentSize / BytesInGB;
                            double elapsedSeconds = Math.Max((DateTime.Now - startTime).TotalSeconds, 1);
                            double speedMB = sessionMB / elapsedSeconds;
                            double silentSeconds = (DateTime.Now - lastActivity).TotalSeconds;

                            double progress = 0;
                            if (_cachedTotalSizeBytes > 0)
                                progress = Math.Min(100, currentSize / (double)_cachedTotalSizeBytes * 100);

                            string timeText = "Estimating...";
                            if (_cachedTotalSizeBytes > 0 && speedMB > MinSpeedThresholdMB)
                            {
                                long remaining = Math.Max(0, _cachedTotalSizeBytes - currentSize);
                                double seconds = remaining / (speedMB * BytesInMB);
                                TimeSpan time = TimeSpan.FromSeconds(seconds);
                                timeText = time.TotalHours >= 1
                                    ? $"{(int)time.TotalHours}h {time.Minutes}m"
                                    : $"{time.Minutes}m {time.Seconds}s";
                            }

                            string stateText;
                            string color;
                            lock (_stateLock)
                            {
                                stateText = _state == OperationState.Pausing ? "Pausing" : operationType;
                                color = _state == OperationState.Pausing ? "yellow" : silentSeconds > 15 ? "yellow" : "green";
                            }

                            sb.Clear();
                            sb.Append("<b>Status:</b> <color=").Append(color).Append('>').Append(stateText).Append("</color>\n")
                              .Append("<b>Progress:</b> ").AppendFormat("{0:F1}", progress).Append("%\n")
                              .Append("<b>Time Remaining:</b> ").Append(timeText).Append('\n')
                              .Append("<b>Total on Disk:</b> ").AppendFormat("{0:F2}", totalGB).Append(" GB\n")
                              .Append("<b>Session:</b> ").AppendFormat("{0:F2}", sessionMB).Append(" MB\n")
                              .Append("<b>Speed:</b> ").AppendFormat("{0:F2}", speedMB).Append(" MB/s");

                            string statusText = sb.ToString();
                            PostToMainThread(() => svnUI.CheckoutStatusInfoText.text = statusText);

                            await Task.Delay(1000, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);

                string workingDirectory = isResume ? path : Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(workingDirectory)) workingDirectory = Path.GetTempPath();

                SVNLogBridge.LogLine($"<color=grey>[SVN]</color> Working Directory: {workingDirectory}");
                SVNLogBridge.LogLine($"<color=grey>[SVN]</color> Command: {command}");

                string result = await SvnRunner.RunLiveAsync(command, workingDirectory, line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    string cleanLine = line.Replace("\r", "").Replace("\\", "/").Trim();
                    if (string.IsNullOrWhiteSpace(cleanLine)) return;
                    if (cleanLine.All(c => c == '@' || c == '*')) return;
                    if (cleanLine.StartsWith("*****") || cleanLine.StartsWith("@@@@@")) return;
                    cleanLine = cleanLine.Replace("[SVN ERROR]", "").Trim();
                    lastActivity = DateTime.Now;

                    if (isExport)
                    {
                        string progressMsg = $"<color=#AAAAAA>Exporting:</color> {cleanLine}";
                        PostToMainThread(() =>
                        {
                            if (svnUI.CheckoutConsoleText != null)
                            {
                                string text = svnUI.CheckoutConsoleText.text;
                                string[] lines = text.Split('\n');
                                if (lines.Length > 0 && lines[lines.Length - 1].StartsWith("<color=#AAAAAA>Exporting:</color>"))
                                    lines[lines.Length - 1] = progressMsg;
                                else
                                    lines = lines.Append(progressMsg).ToArray();
                                svnUI.CheckoutConsoleText.text = string.Join("\n", lines);
                                Canvas.ForceUpdateCanvases();
                            }
                        });
                    }
                    else
                    {
                        logBuffer.Enqueue(cleanLine);
                    }
                }, token).ConfigureAwait(false);

                if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                if (isExport && svnUI.CheckoutConsoleText != null)
                {
                    string text = svnUI.CheckoutConsoleText.text;
                    string[] lines = text.Split('\n');
                    if (lines.Length > 0 && lines[lines.Length - 1].StartsWith("<color=#AAAAAA>Exporting:</color>"))
                    {
                        svnUI.CheckoutConsoleText.text = string.Join("\n", lines, 0, lines.Length - 1);
                        Canvas.ForceUpdateCanvases();
                    }
                }

                bool hasWorkingCopy = Directory.Exists(Path.Combine(path, ".svn"));
                bool hasError = !string.IsNullOrWhiteSpace(result) &&
                                (result.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                 result.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                                 result.Contains("failed", StringComparison.OrdinalIgnoreCase));

                if (isExport)
                {
                    if (hasError)
                    {
                        lock (_stateLock) { _state = OperationState.Failed; }
                        PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                            "<color=#FFAA00><b>Export Failed</b></color>\nCheck console for details.", "SVN"));
                        return;
                    }
                }
                else if (!hasWorkingCopy || hasError)
                {
                    lock (_stateLock) { _state = OperationState.Failed; }
                    PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        "<color=#FFAA00><b>Operation Failed</b></color>\nCheck console for details.", "SVN"));
                    return;
                }

                lock (_stateLock) { _state = OperationState.Completed; }
                SVNLogBridge.LogLine($"<color=green><b>[{operationType}]</b> Finished successfully.</color>");

                PostToMainThread(() =>
                {
                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        $"<color=green><b>{operationType} completed successfully</b></color>", "SVN");

                    if (operationType != "Exporting")
                        SVNManager.Instance?.ProjectSelectionPanel?.RefreshList();
                });

                if (SVNManager.Instance != null)
                {
                    var pollingService = SVNManager.Instance.GetComponent<SVNPollingService>();
                    if (pollingService != null) pollingService.ResetRevisionTracking();
                }

                if (!isExport)
                {
                    var activeProject = new SVNProject
                    {
                        projectName = Path.GetFileName(path.TrimEnd('/', '\\')),
                        repoUrl = url,
                        workingDir = path,
                        privateKeyPath = keyPath ?? _resolvedKeyPath,
                        lastOpened = DateTime.Now
                    };
                    SVNManager.Instance?.SetActiveProject(activeProject);
                    RegisterProjectInList(path, url, keyPath ?? _resolvedKeyPath);
                }
            }
            catch (OperationCanceledException)
            {
                lock (_stateLock)
                {
                    if (_state == OperationState.Pausing)
                    {
                        _state = OperationState.Paused;
                        PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                            "<color=yellow><b>Operation Paused</b></color>\nFiles preserved on disk.", "SVN"));
                    }
                    else
                    {
                        _state = OperationState.Cancelled;
                        PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                            "<color=#FFAA00><b>Operation Cancelled</b></color>", "SVN"));
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock) { _state = OperationState.Failed; }
                SVNLogBridge.LogError($"[SVN] Operation failed:\n{ex}");
                PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                    $"<color=#FFAA00>Error: {ex.Message}</color>", "SVN"));
            }
            finally
            {
                cts?.Cancel();
                try { if (monitorTask != null) await monitorTask.ConfigureAwait(false); } catch { }
                try { if (logFlushTask != null) await logFlushTask.ConfigureAwait(false); } catch { }
                FlushLogBuffer(logBuffer);
                cts?.Dispose();
                _checkoutCTS = null;
                IsProcessing = false;
                lock (_stateLock) { if (_state != OperationState.Paused) _state = OperationState.Idle; }
            }
        }

        #endregion

        #region Export

        public async void ExportRepository()
        {
            try { await ExportRepositoryAsync().ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task ExportRepositoryAsync()
        {
            if (!TryValidateExportCommon(out string url, out string fullPath, out string keyPath, out string errorMsg))
            {
                if (!string.IsNullOrEmpty(errorMsg)) ShowError(errorMsg);
                return;
            }

            try
            {
                lock (_stateLock)
                {
                    _state = OperationState.Running;
                    _canResume = false;
                }

                string sshConfig = BuildSshConfigOption(keyPath);
                string exportArgs = $"export \"{url}\" \"{fullPath}\" --force --non-interactive --trust-server-cert" + sshConfig;
                await ExecuteSvnOperationAsync(url, fullPath, exportArgs, false, keyPath, "Exporting").ConfigureAwait(false);

                SVNLogBridge.LogLine($"<color=green>Export completed. Files saved to: {fullPath}</color>");
            }
            catch (Exception ex)
            {
                lock (_stateLock) { _state = OperationState.Failed; }
                IsProcessing = false;
                SVNLogBridge.LogError($"[SVN] Export failed:\n{ex}");
                ShowError(ex.Message);
            }
        }

        public async void ExportRevision(string revision)
        {
            try { await ExportRevisionAsync(revision).ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task ExportRevisionAsync(string revision)
        {
            if (!TryValidateExportCommon(out string url, out string fullPath, out string keyPath, out string errorMsg))
            {
                if (!string.IsNullOrEmpty(errorMsg)) ShowError(errorMsg);
                return;
            }

            try
            {
                lock (_stateLock)
                {
                    _state = OperationState.Running;
                    _canResume = false;
                }

                string revArg = string.IsNullOrWhiteSpace(revision) ? "" : $" -r {revision}";
                string sshConfig = BuildSshConfigOption(keyPath);
                string exportArgs = $"export{revArg} \"{url}\" \"{fullPath}\" --force --non-interactive --trust-server-cert" + sshConfig;
                await ExecuteSvnOperationAsync(url, fullPath, exportArgs, false, keyPath, "Exporting").ConfigureAwait(false);

                SVNLogBridge.LogLine($"<color=green>Export completed. Files saved to: {fullPath}</color>");
            }
            catch (Exception ex)
            {
                lock (_stateLock) { _state = OperationState.Failed; }
                IsProcessing = false;
                SVNLogBridge.LogError($"[SVN] Export failed:\n{ex}");
                ShowError(ex.Message);
            }
        }

        private bool TryValidateExportCommon(out string url, out string fullPath, out string keyPath, out string errorMsg)
        {
            url = null;
            fullPath = null;
            keyPath = null;
            errorMsg = null;

            url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(path))
            {
                errorMsg = "Please enter both Repository URL and Destination Folder in the Checkout panel.";
                SVNLogBridge.LogLine("<color=#FFAA00>Export: Both URL and destination folder must be provided.</color>");
                return false;
            }

            if (!IsValidSvnUrl(url))
            {
                errorMsg = "Invalid SVN URL.";
                SVNLogBridge.LogLine("<color=#FFAA00>Export: Invalid SVN URL.</color>");
                return false;
            }

            if (!TryValidatePath(path, out fullPath)) return false;

            if (Directory.Exists(fullPath))
            {
                if (Directory.GetFileSystemEntries(fullPath).Length > 0)
                {
                    errorMsg = $"Destination folder is not empty: {fullPath}\nPlease choose an empty or non-existent folder.";
                    SVNLogBridge.LogLine($"<color=#FFAA00>{errorMsg}</color>");
                    return false;
                }

                try { Directory.Delete(fullPath, false); }
                catch (Exception ex)
                {
                    errorMsg = $"Cannot prepare destination: {ex.Message}";
                    SVNLogBridge.LogLine($"<color=#FFAA00>Export: Cannot delete empty folder {fullPath} – {ex.Message}</color>");
                    return false;
                }
            }

            keyPath = ResolveAndValidateKeyPath();
            if (url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(keyPath))
            {
                errorMsg = "SSH repository requires a valid private key.";
                SVNLogBridge.LogLine("<color=#FFAA00>Export: SSH key required but not provided.</color>");
                return false;
            }

            return true;
        }

        #endregion

        #region Helpers

        private bool CanStartOperation()
        {
            lock (_stateLock)
            {
                double elapsed = (DateTime.Now - _lastStartAttempt).TotalMilliseconds;
                if (elapsed < DebounceIntervalMs)
                {
                    SVNLogBridge.LogLine("<color=yellow>[SVN]</color> Please wait...");
                    return false;
                }
                _lastStartAttempt = DateTime.Now;

                if (IsProcessing)
                {
                    SVNLogBridge.LogLine("<color=yellow>[SVN]</color> Another operation is already running.");
                    return false;
                }
                return true;
            }
        }

        private bool IsValidSvnUrl(string url)
        {
            return url.StartsWith("svn://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryValidatePath(string inputPath, out string fullPath)
        {
            fullPath = null;
            try { fullPath = Path.GetFullPath(inputPath); }
            catch (Exception ex) { ShowError($"Invalid destination path: {ex.Message}"); return false; }

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

        private void ShowError(string message)
        {
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, $"<color=#FFAA00>Error:</color> {message}", "Checkout");
        }

        private void HandleOperationException(Exception ex)
        {
            IsProcessing = false;
            SVNLogBridge.LogError($"[SVN] Unhandled operation exception:\n{ex}");
            ShowError(ex.Message);
        }

        private void PostToMainThread(Action action)
        {
            if (action == null) return;
            if (_mainThreadContext != null) _mainThreadContext.Post(_ => action(), null);
            else action();
        }

        private void FlushLogBuffer(ConcurrentQueue<string> logBuffer)
        {
            if (logBuffer == null || logBuffer.IsEmpty) return;
            var lines = new List<string>();
            while (logBuffer.TryDequeue(out string line)) lines.Add(line);
            if (lines.Count == 0) return;
            string text = string.Join("\n", lines) + "\n";
            PostToMainThread(() => SVNLogBridge.LogCheckoutConsole(text));
        }

        private long GetDirectorySizeFast(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return 0;

            lock (_sizeCacheLock)
            {
                double elapsed = (DateTime.Now - _lastDirectorySizeCheck).TotalSeconds;
                if (elapsed < 5.0 && _lastKnownDirectorySize > 0)
                    return _lastKnownDirectorySize;
            }

            long size = 0;
            try
            {
                var directory = new DirectoryInfo(folderPath);
                foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { size += file.Length; }
                    catch (UnauthorizedAccessException) { }
                    catch { }
                }
            }
            catch { }

            lock (_sizeCacheLock)
            {
                _lastKnownDirectorySize = size;
                _lastDirectorySizeCheck = DateTime.Now;
            }
            return size;
        }

        private void RegisterProjectInList(string path, string url, string keyPath)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
            var projects = ProjectSettings.LoadProjects();
            int index = projects.FindIndex(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                string.Equals(p.workingDir.Replace("\\", "/").TrimEnd('/'), normalizedPath, StringComparison.OrdinalIgnoreCase));

            string projectName = GetRepoNameFromUrl(url);
            if (index >= 0)
            {
                projects[index].repoUrl = url;
                projects[index].lastOpened = DateTime.Now;
                projects[index].privateKeyPath = keyPath;
            }
            else
            {
                projects.Add(new SVNProject
                {
                    projectName = projectName,
                    repoUrl = url,
                    workingDir = normalizedPath,
                    privateKeyPath = keyPath,
                    lastOpened = DateTime.Now
                });
            }

            ProjectSettings.SaveProjects(projects);
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
            PlayerPrefs.Save();
        }

        private string GetRepoNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Repository";
            url = url.TrimEnd('/');
            if (url.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - "/trunk".Length);
            if (url.EndsWith("/branches", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - "/branches".Length);
            if (url.EndsWith("/tags", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - "/tags".Length);
            int slash = url.LastIndexOf('/');
            return slash >= 0 && slash < url.Length - 1 ? url.Substring(slash + 1) : url;
        }

        #endregion
    }
}