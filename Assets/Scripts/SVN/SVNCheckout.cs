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
        private long _cachedTotalSizeBytes;
        private bool _canResume;

        private enum OperationState { Idle, Running, Pausing, Paused, Cancelling, Cancelled, Completed, Failed }
        private OperationState _state = OperationState.Idle;
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
            UnityMainThreadDispatcher.EnsureExists();
        }

        private string ResolveAndValidateKeyPath()
        {
            string keyPath = SvnRunner.KeyPath;
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                keyPath = SVNManager.Instance?.CurrentKey;
                if (!string.IsNullOrWhiteSpace(keyPath))
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Using fallback SSH key.");
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
                SVNLogBridge.LogErrorToOutput($"[SVN] Invalid SSH key path: {ex.Message}");
                return null;
            }

            if (!File.Exists(keyPath))
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] SSH key not found: {keyPath}");
                SVNLogBridge.LogErrorToOutput("[SVN] Please verify the SSH key path in Settings.");
                return null;
            }

            try
            {
                FileInfo fileInfo = new FileInfo(keyPath);
                if ((fileInfo.Attributes & FileAttributes.ReadOnly) != 0)
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Warning: SSH key is marked as read-only.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Cannot access SSH key: {ex.Message}");
                return null;
            }

            _resolvedKeyPath = keyPath;
            SVNLogBridge.LogToOutput($"<color=green>[SVN]</color> SSH key resolved: {keyPath}");
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
                PostToMainThread(() =>
                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        "<color=yellow><b>Info:</b> Enter destination path to check disk space.</color>", "Info"));
                return;
            }

            PostToMainThread(() =>
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Analyzing repository...", "Info"));

            string keyPath = ResolveAndValidateKeyPath();
            string sshConfig = BuildSshConfigOption(keyPath);
            _cachedTotalSizeBytes = await GetRemoteRepositorySizeAsync(url, sshConfig).ConfigureAwait(false);
            string structure = await GetRepositoryStructureAsync(url, sshConfig).ConfigureAwait(false);

            string driveLabel;
            long freeSpaceBytes = 0;

            try
            {
                string fullPath = Path.GetFullPath(destPath);
                driveLabel = Path.GetPathRoot(fullPath);
                DriveInfo drive = new DriveInfo(driveLabel);
                freeSpaceBytes = drive.AvailableFreeSpace;
            }
            catch { driveLabel = "?"; freeSpaceBytes = 0; }

            string repoSizeStr = FormatSize(_cachedTotalSizeBytes);
            string requiredStr = FormatSize((long)(_cachedTotalSizeBytes * SvnOverheadMultiplier));
            string freeSpaceStr = FormatSize(freeSpaceBytes);

            string spaceColor = freeSpaceBytes < (_cachedTotalSizeBytes * SvnOverheadMultiplier) && _cachedTotalSizeBytes > 0 ? "red" : "green";
            var sb = new StringBuilder(512);
            sb.Append("<b>Repository Size:</b> ").Append(repoSizeStr).Append('\n')
              .Append("<b>Required Space:</b> ").Append(requiredStr).Append('\n')
              .Append("<b>Available Space (").Append(driveLabel).Append("):</b> <color=")
              .Append(spaceColor).Append(">").Append(freeSpaceStr).Append("</color>\n\n")
              .Append("<b>Repository Structure:</b>\n").Append(structure).Append("\n\n");

            if (_cachedTotalSizeBytes > 0 && freeSpaceBytes < (_cachedTotalSizeBytes * SvnOverheadMultiplier))
                sb.Append("<color=#FFAA00><b>ERROR:</b> Not enough disk space. SVN needs approximately ")
                  .Append(requiredStr).Append(".</color>");
            else if (_cachedTotalSizeBytes == 0)
                sb.Append("<color=yellow>Could not determine repository size. The repository may be empty or unreachable.</color>");
            else
                sb.Append("<color=green>Ready to checkout.</color>");

            string finalText = sb.ToString();
            PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, finalText, "Info"));
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

        public async void StartCheckout()
        {
            try { await StartCheckoutAsync().ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task StartCheckoutAsync()
        {
            if (!CanStartOperation()) return;

            ClearPausedState();

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

            string url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                ShowError("Destination path cannot be empty.");
                return;
            }

            if (!TryValidatePath(path, out string fullPath)) return;

            lock (_stateLock)
            {
                if (!_canResume)
                {
                    if (TryRestorePausedState(fullPath, url))
                    {
                        _canResume = true;
                        string savedKey = PlayerPrefs.GetString("SVN_CheckoutPaused_KeyPath", "");
                        if (!string.IsNullOrEmpty(savedKey) && File.Exists(savedKey))
                            _resolvedKeyPath = savedKey;
                    }
                    else
                    {
                        ShowError("Cannot resume. The operation was explicitly cancelled or no paused state found.");
                        return;
                    }
                }
            }

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

            string path = svnUI.CheckoutDestFolderInput.text.Trim();
            string url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            SavePausedState(path, url, _resolvedKeyPath);

            SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Pausing checkout...");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow>Pausing...</color>", "SVN");

            var cts = _checkoutCTS;
            cts?.Cancel();
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

            ClearPausedState();

            SVNLogBridge.LogToOutput("<color=#FFAA00>[SVN]</color> Cancelling checkout...");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=#FFAA00>Cancelling...</color>", "SVN");

            var cts = _checkoutCTS;
            cts?.Cancel();
        }

        private long _cachedRepoSizeBytes;
        private string _cachedRepoSizeUrl;
        private readonly object _repoSizeLock = new object();

        private async Task<long> GetRemoteRepositorySizeAsync(string url, string sshConfig = "")
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;

            lock (_repoSizeLock)
            {
                if (_cachedRepoSizeBytes > 0 &&
                    string.Equals(_cachedRepoSizeUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    return _cachedRepoSizeBytes;
                }
            }

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

                SVNLogBridge.LogToOutput($"[SVN] Repository size: {totalBytes / BytesInMB:F2} MB");

                lock (_repoSizeLock)
                {
                    _cachedRepoSizeBytes = totalBytes;
                    _cachedRepoSizeUrl = url;
                }

                return totalBytes;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Failed to calculate repository size: {ex.Message}");
                return 0;
            }
        }

        private async Task ExecuteSvnOperationAsync(string url, string path, string command, bool isResume, string keyPath, string operationType)
        {
            if (IsProcessing)
            {
                SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Operation already running.");
                return;
            }

            IsProcessing = true;
            CancellationTokenSource cts = null;
            Task monitorTask = null;
            Task logFlushTask = null;
            var logBuffer = new ConcurrentQueue<string>();

            int addedCount = 0;
            int updatedCount = 0;
            int conflictCount = 0;

            DateTime startTime = DateTime.Now;
            DateTime lastActivity = DateTime.Now;

            try
            {
                lock (_stateLock) { _state = OperationState.Running; }

                cts = new CancellationTokenSource();
                _checkoutCTS = cts;
                CancellationToken token = cts.Token;

                long sizeBeforeSession = Directory.Exists(path) ? GetDirectorySizeFast(path) : 0;
                bool isExport = operationType == "Exporting";

                if (!isExport && !Directory.Exists(path))
                    Directory.CreateDirectory(path);

                PostToMainThread(() =>
                {
                    SVNLogBridge.LogCheckoutConsole($"<b>[{operationType}]</b> Starting...\n");
                    SVNLogBridge.LogCheckoutConsole($"<b>[Target]</b> {url}\n");
                    SVNLogBridge.LogCheckoutConsole($"<b>[Dest]</b> {path}\n\n");
                });

                if (isResume)
                {
                    PostToMainThread(() =>
                    {
                        SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow>Cleaning working copy...</color>", "SVN");
                        SVNLogBridge.LogCheckoutConsole($"<color=yellow>[Cleanup]</color> Cleaning working copy...\n");
                    });

                    string sshConfig = BuildSshConfigOption(keyPath);
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, cleanupTimeout.Token);

                    try
                    {
                        await SvnRunner.RunAsync(
                            $"cleanup --non-interactive --trust-server-cert" + sshConfig, path, false, linkedCts.Token).ConfigureAwait(false);
                        PostToMainThread(() =>
                            SVNLogBridge.LogCheckoutConsole($"<color=green>[Cleanup]</color> Complete.\n"));
                    }
                    catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        PostToMainThread(() =>
                            SVNLogBridge.LogCheckoutConsole($"<color=#FFAA00>[Cleanup]</color> Timed out (30s), proceeding...\n"));
                    }

                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                }

                logFlushTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await Task.Delay(200, token).ConfigureAwait(false);
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
                            double elapsedSeconds = Math.Max((DateTime.Now - startTime).TotalSeconds, 1);
                            double silentSeconds = (DateTime.Now - lastActivity).TotalSeconds;

                            int curAdded = Volatile.Read(ref addedCount);
                            int curUpdated = Volatile.Read(ref updatedCount);
                            int curConflicts = Volatile.Read(ref conflictCount);

                            double speedFiles = curAdded / elapsedSeconds;

                            string stateText;
                            string statusColor;
                            lock (_stateLock)
                            {
                                stateText = _state == OperationState.Pausing ? "Pausing" : operationType;
                                statusColor = _state == OperationState.Pausing ? "yellow" : silentSeconds > 15 ? "yellow" : "green";
                            }

                            sb.Clear();
                            sb.Append("<b>Status:</b> <color=").Append(statusColor).Append('>').Append(stateText).Append("</color>\n")
                              .Append("<b>Time Elapsed:</b> ").AppendFormat("{0:F1}s", elapsedSeconds).Append('\n')
                              .Append("<b>Speed:</b> ").AppendFormat("{0:F1}", speedFiles).Append(" files/sec\n")
                              .Append("<b>Items Added:</b> ").Append(curAdded);

                            if (curUpdated > 0)
                                sb.Append(" | <b>Updated:</b> ").Append(curUpdated);
                            if (curConflicts > 0)
                                sb.Append(" | <b><color=#FFAA00>Conflicts: ").Append(curConflicts).Append("</color></b>");

                            PostToMainThread(() => svnUI.CheckoutStatusInfoText.text = sb.ToString());
                            await Task.Delay(1000, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);

                string workingDirectory = isResume ? path : Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(workingDirectory)) workingDirectory = Path.GetTempPath();

                PostToMainThread(() =>
                    SVNLogBridge.LogCheckoutConsole($"<color=blue><b>[Download]</b> In progress...\n</color>"));

                string result = await SvnRunner.RunLiveAsync(command, workingDirectory, line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;

                    string cleanLine = line.Replace("\r", "").Replace("\\", "/").Trim();
                    if (string.IsNullOrWhiteSpace(cleanLine)) return;
                    if (cleanLine.All(c => c == '@' || c == '*')) return;
                    if (cleanLine.StartsWith("*****") || cleanLine.StartsWith("@@@@@")) return;

                    cleanLine = cleanLine.Replace("[SVN ERROR]", "").Trim();
                    lastActivity = DateTime.Now;

                    if (cleanLine.Length >= 3)
                    {
                        char statusChar = cleanLine[0];

                        if ("UAGDCR ".Contains(statusChar) && (cleanLine[1] == ' ' || cleanLine[1] == '\t'))
                        {
                            switch (statusChar)
                            {
                                case 'A': Interlocked.Increment(ref addedCount); break;
                                case 'U':
                                case 'G':
                                case 'R': Interlocked.Increment(ref updatedCount); break;
                                case 'C': Interlocked.Increment(ref conflictCount); break;
                            }
                        }
                    }

                    if (isExport)
                    {
                        PostToMainThread(() =>
                        {
                            if (svnUI.CheckoutConsoleText != null)
                            {
                                string text = svnUI.CheckoutConsoleText.text;
                                string[] textLines = text.Split('\n');
                                if (textLines.Length > 0 && textLines[textLines.Length - 1].StartsWith("Exporting:"))
                                    textLines[textLines.Length - 1] = cleanLine;
                                else
                                    textLines = textLines.Append(cleanLine).ToArray();
                                svnUI.CheckoutConsoleText.text = string.Join("\n", textLines);
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
                ClearPausedState();

                var elapsed = DateTime.Now - startTime;
                long finalSize = GetDirectorySizeFast(path);
                long downloadedBytes = Math.Max(0, finalSize - sizeBeforeSession);
                double avgSpeedMB = (downloadedBytes / BytesInMB) / Math.Max(elapsed.TotalSeconds, 1);

                int finalAdded = addedCount;
                int finalUpdated = updatedCount;
                int finalConflicts = conflictCount;

                PostToMainThread(() =>
                {
                    var report = new StringBuilder(512);
                    report.AppendLine();
                    report.AppendLine($"<color=green><b>=========================================</b></color>");
                    report.AppendLine($"<color=green><b>     {operationType.ToUpper()} COMPLETED</b></color>");
                    report.AppendLine($"<color=green><b>=========================================</b></color>");
                    report.AppendLine($"Items added:  <b>{finalAdded}</b>");
                    if (finalUpdated > 0)
                        report.AppendLine($"Updated:      <b>{finalUpdated}</b>");
                    report.AppendLine($"Disk usage:   <b>{FormatSize(finalSize)}</b>");
                    report.AppendLine($"Downloaded:   <b>{FormatSize(downloadedBytes)}</b>");
                    report.AppendLine($"Duration:     <b>{elapsed.TotalSeconds:F1}s</b>");
                    report.AppendLine($"Avg speed:    <b>{avgSpeedMB:F2} MB/s</b>");
                    if (finalConflicts > 0)
                        report.AppendLine($"<color=#FFAA00><b>Conflicts: {finalConflicts}</b></color>");
                    report.AppendLine($"<color=green><b>=========================================</b></color>");

                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, report.ToString(), "SVN");

                    SVNLogBridge.LogCheckoutConsole($"<color=green><b>[{operationType}]</b> Finished. {finalAdded} items, {elapsed.TotalSeconds:F1}s</color>\n");

                    if (operationType != "Exporting")
                        SVNManager.Instance?.ProjectSelectionPanel?.RefreshList();
                });

                SVNLogBridge.LogLine($"<color=green><b>[{operationType}]</b> Finished. {finalAdded} items, {elapsed.TotalSeconds:F1}s</color>");

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
                var elapsed = DateTime.Now - startTime;
                int finalAdded = addedCount;
                long diskSize = GetDirectorySizeFast(path);

                string statusMsg;
                lock (_stateLock)
                {
                    statusMsg = _state == OperationState.Pausing ? "PAUSED" : "CANCELLED";
                    _state = _state == OperationState.Pausing ? OperationState.Paused : OperationState.Cancelled;
                }

                if (statusMsg == "CANCELLED")
                    ClearPausedState();

                PostToMainThread(() =>
                {
                    string statusMsg;
                    lock (_stateLock)
                    {
                        statusMsg = _state == OperationState.Pausing ? "PAUSED" : "CANCELLED";
                        _state = _state == OperationState.Pausing ? OperationState.Paused : OperationState.Cancelled;
                    }

                    var report = new StringBuilder(256);
                    report.AppendLine();
                    report.AppendLine($"<color=#FFAA00><b>=========================================</b></color>");
                    report.AppendLine($"<color=#FFAA00><b>     OPERATION {statusMsg}</b></color>");
                    report.AppendLine($"<color=#FFAA00><b>=========================================</b></color>");
                    report.AppendLine($"Items downloaded: <b>{finalAdded}</b>");
                    report.AppendLine($"Duration:         <b>{elapsed.TotalSeconds:F1}s</b>");
                    report.AppendLine($"Disk preserved:   <b>{FormatSize(diskSize)}</b>");
                    report.AppendLine($"<color=#FFAA00><b>=========================================</b></color>");

                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, report.ToString(), "SVN");

                    SVNLogBridge.LogCheckoutConsole($"<color=#FFAA00><b>[{operationType}]</b> {statusMsg}. {finalAdded} items, {elapsed.TotalSeconds:F1}s</color>\n");
                });
            }
            catch (Exception ex)
            {
                lock (_stateLock) { _state = OperationState.Failed; }

                PostToMainThread(() =>
                {
                    SVNLogBridge.LogCheckoutConsole($"\n<color=#FF4444><b>ERROR:</b> {ex.Message}</color>\n\n");
                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        $"<color=#FFAA00>Error: {ex.Message}</color>", "SVN");
                });

                SVNLogBridge.LogErrorToOutput($"[SVN] Operation failed:\n{ex}");
            }
            finally
            {
                try { cts?.Cancel(); } catch { }
                try { if (monitorTask != null) await monitorTask.ConfigureAwait(false); } catch { }
                try { if (logFlushTask != null) await logFlushTask.ConfigureAwait(false); } catch { }
                FlushLogBuffer(logBuffer);
                cts?.Dispose();
                _checkoutCTS = null;
                IsProcessing = false;
                lock (_stateLock) { if (_state != OperationState.Paused) _state = OperationState.Idle; }
            }
        }

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

            lock (_stateLock)
            {
                _state = OperationState.Running;
                _canResume = false;
            }

            string sshConfig = BuildSshConfigOption(keyPath);
            string exportArgs = $"export \"{url}\" \"{fullPath}\" --force --non-interactive --trust-server-cert" + sshConfig;
            await ExecuteSvnOperationAsync(url, fullPath, exportArgs, false, keyPath, "Exporting").ConfigureAwait(false);
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

            lock (_stateLock)
            {
                _state = OperationState.Running;
                _canResume = false;
            }

            string revArg = string.IsNullOrWhiteSpace(revision) ? "" : $" -r {revision}";
            string sshConfig = BuildSshConfigOption(keyPath);
            string exportArgs = $"export{revArg} \"{url}\" \"{fullPath}\" --force --non-interactive --trust-server-cert" + sshConfig;
            await ExecuteSvnOperationAsync(url, fullPath, exportArgs, false, keyPath, "Exporting").ConfigureAwait(false);
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

        private bool CanStartOperation()
        {
            lock (_stateLock)
            {
                double elapsed = (DateTime.Now - _lastStartAttempt).TotalMilliseconds;
                if (elapsed < DebounceIntervalMs)
                {
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Please wait...");
                    return false;
                }
                _lastStartAttempt = DateTime.Now;

                if (IsProcessing)
                {
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Another operation is already running.");
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
            SVNLogBridge.LogErrorToOutput($"[SVN] Unhandled operation exception:\n{ex}");
            ShowError(ex.Message);
        }

        private void PostToMainThread(Action action)
        {
            if (action == null) return;
            UnityMainThreadDispatcher.Enqueue(action);
        }

        private void FlushLogBuffer(ConcurrentQueue<string> logBuffer)
        {
            if (logBuffer == null || logBuffer.IsEmpty) return;
            var lines = new List<string>();
            while (logBuffer.TryDequeue(out string line))
                lines.Add($"{line}");
            if (lines.Count == 0) return;
            string text = string.Join("\n", lines) + "\n";
            PostToMainThread(() => SVNLogBridge.LogCheckoutConsole(text));
        }

        private long GetDirectorySizeFast(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return 0;

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

        private void SavePausedState(string path, string url, string keyPath)
        {
            PlayerPrefs.SetString("SVN_CheckoutPaused_Path", path ?? "");
            PlayerPrefs.SetString("SVN_CheckoutPaused_Url", url ?? "");
            PlayerPrefs.SetString("SVN_CheckoutPaused_KeyPath", keyPath ?? "");
            PlayerPrefs.Save();
        }

        private void ClearPausedState()
        {
            PlayerPrefs.DeleteKey("SVN_CheckoutPaused_Path");
            PlayerPrefs.DeleteKey("SVN_CheckoutPaused_Url");
            PlayerPrefs.DeleteKey("SVN_CheckoutPaused_KeyPath");
            PlayerPrefs.Save();
        }

        private bool TryRestorePausedState(string currentPath, string currentUrl)
        {
            string savedPath = PlayerPrefs.GetString("SVN_CheckoutPaused_Path", "");
            string savedUrl = PlayerPrefs.GetString("SVN_CheckoutPaused_Url", "");

            if (string.IsNullOrEmpty(savedPath) || string.IsNullOrEmpty(savedUrl))
                return false;

            string normSavedPath = savedPath.Replace("\\", "/").TrimEnd('/');
            string normCurrentPath = currentPath.Replace("\\", "/").TrimEnd('/');
            string normSavedUrl = savedUrl.TrimEnd('/');
            string normCurrentUrl = currentUrl.TrimEnd('/');

            return string.Equals(normSavedPath, normCurrentPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(normSavedUrl, normCurrentUrl, StringComparison.OrdinalIgnoreCase);
        }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int order = 0;
            while (size >= 1024.0 && order < suffixes.Length - 1)
            {
                order++;
                size /= 1024.0;
            }
            return $"{size:0.##} {suffixes[order]}";
        }
    }
}