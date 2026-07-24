using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNCommit : SVNBase
    {
        private CancellationTokenSource _commitCts;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private List<SVNStatusElement> _items = new List<SVNStatusElement>();

        private const double BytesConversionFactor = 1024.0;
        private const int RevertBatchSize = 20;
        private const int CleanupTimeoutSeconds = 30;
        private const int AddBatchSize = 20;

        private readonly SynchronizationContext _mainThreadContext;
        private readonly int _mainThreadId;

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        private bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private void RunOnMainThread(Action action)
        {
            if (IsMainThread)
            {
                try { action(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            else if (_mainThreadContext != null)
            {
                _mainThreadContext.Post(_ =>
                {
                    try { action(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }, null);
            }
        }

        private T GetOnMainThread<T>(Func<T> getter, T defaultValue = default)
        {
            if (IsMainThread)
                return getter();

            T result = defaultValue;
            using var mre = new ManualResetEventSlim(false);

            _mainThreadContext?.Post(_ =>
            {
                try { result = getter(); }
                catch { }
                finally { mre.Set(); }
            }, null);

            if (_mainThreadContext != null)
                mre.Wait(TimeSpan.FromSeconds(5));

            return result;
        }

        private string TsTag() => $"<color=blue>[{DateTime.Now:HH:mm:ss}]</color>";

        private void LogLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            AppendCommitConsole($"{TsTag()} {message}");
        }

        private void LogStage(int step, int total, string message)
        {
            LogLine($"<b>[{step}/{total}]</b> {message}");
        }

        private void SetCommitConsole(string msg, bool append)
        {
            if (svnUI?.CommitConsoleContent == null)
                return;

            string output = msg;
            if (!output.EndsWith("\n"))
                output += "\n";

            RunOnMainThread(() =>
                SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, output, append: append));
        }

        private void AppendCommitConsole(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            string line = msg.TrimStart('\r', '\n');
            if (!line.EndsWith("\n"))
                line += "\n";

            SetCommitConsole(line, append: true);
        }

        private void LogSystem(string msg, MessageType type)
        {
            string color = type == MessageType.Success ? "green" :
                           type == MessageType.Warning ? "orange" : "#FFAA00";
            LogLine($"<color={color}><b>[System]</b> {msg}</color>");
        }

        private void LogError(string msg)
        {
            LogLine($"<color=#FFAA00>Error:</color> {msg}");
        }

        public async void ShowWhatWillBeCommitted()
        {
            try { await ShowWhatWillBeCommittedAsync(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        public async void RefreshCommitList()
        {
            try { await RefreshCommitListAsync(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        public async void ExecuteRevertAllMissing()
        {
            try { await RevertAllMissingWrapperAsync(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        public async void CommitAll()
        {
            try { await CommitAllAsync(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        public void CommitSelected() => _ = CommitSelectedWrapperAsync();

        private async Task CommitSelectedWrapperAsync()
        {
            try { await CommitSelectedAsync(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        public void CancelOperation()
        {
            var cts = Interlocked.Exchange(ref _commitCts, null);
            if (cts?.IsCancellationRequested == false)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }

                LogSystem("Operation cancelled by user.", MessageType.Warning);
            }
        }

        public List<SvnTreeElement> GetSelectedFiles()
        {
            var status = svnManager?.GetModule<SVNStatus>();
            return status?.GetCurrentData()?.Where(e => e.IsChecked && !e.IsFolder).ToList()
                   ?? new List<SvnTreeElement>();
        }

        public void RenderCommitList(List<SVNStatusElement> items)
        {
            if (items == null || items.Count == 0)
            {
                SetCommitConsole("No changes to commit.", append: false);
                return;
            }

            string root = NormalizeRoot(svnManager.WorkingDir);
            long totalSize = 0;

            foreach (var item in items)
            {
                if (!item.IsChecked || item.Status == "!" || item.Status == "D")
                    continue;

                string fullPath = JoinPath(root, item.FullPath);
                if (File.Exists(fullPath))
                {
                    try { totalSize += new FileInfo(fullPath).Length; }
                    catch { }
                }
            }

            var sb = new StringBuilder(items.Count * 64);
            sb.AppendLine($"<b>Files to be committed</b> (Payload: <color=blue>{FormatCommitSize(totalSize)}</color>):");

            foreach (var item in items)
            {
                string color = item.Status switch
                {
                    "M" => "yellow",
                    "A" => "green",
                    "?" => "#00E5FF",
                    "D" => "red",
                    _ => "white"
                };
                sb.AppendLine($"<color={color}>[{item.Status}]</color> {item.FullPath}");
            }

            SetCommitConsole(sb.ToString(), append: false);
        }

        private async Task ShowWhatWillBeCommittedAsync()
        {
            if (svnManager == null)
                return;

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir);
                var commitables = statusDict.Where(x => "MADC?".Contains(x.Value.status)).ToList();

                if (commitables.Count == 0)
                {
                    SetCommitConsole("<b>Current changes to send:</b><color=grey>None</color>", append: false);
                    return;
                }

                var sb = new StringBuilder(commitables.Count * 48);
                sb.AppendLine("<b>Current changes to send:</b>");
                foreach (var item in commitables)
                    sb.AppendLine($"[{item.Value.status}] {SvnRunner.NormalizeRepositoryPath(item.Key)}");

                SetCommitConsole(sb.ToString(), append: true);
            }
            catch (Exception ex)
            {
                LogError($"Failed to read commit preview: {ex.Message}");
            }
        }

        private async Task RefreshCommitListAsync()
        {
            bool hasLock = false;

            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock || svnManager == null)
                    return;

                IsProcessing = true;

                var previouslyChecked = _items?
                    .ToDictionary(x => x.FullPath, x => x.IsChecked, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir);

                _items = statusDict
                    .Where(x => "MADC?".Contains(x.Value.status))
                    .Select(x =>
                    {
                        string path = SvnRunner.NormalizeRepositoryPath(x.Key);
                        bool isChecked = previouslyChecked.TryGetValue(path, out bool wasChecked)
                            ? wasChecked
                            : x.Value.status != "?";

                        return new SVNStatusElement
                        {
                            FullPath = path,
                            Status = x.Value.status,
                            IsChecked = isChecked
                        };
                    })
                    .ToList();

                RenderCommitList(_items);
            }
            catch (Exception ex)
            {
                LogError($"Refresh failed: {ex.Message}");
            }
            finally
            {
                if (hasLock)
                {
                    IsProcessing = false;
                    _operationLock.Release();
                }
            }
        }

        private async Task CommitAllAsync()
        {
            bool hasLock = false;
            bool success = false;

            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock || svnManager == null || svnUI == null)
                    return;

                IsProcessing = true;

                var token = ResetAndGetToken();
                string root = NormalizeRoot(svnManager.WorkingDir);

                ShowProgressBar(0.05f);
                SetCommitConsole($"{TsTag()} <b>Initiating commit...</b>\n\n", append: false);

                LogStage(1, 4, "Cleaning up database...");
                bool cleanupOk = await CleanupWorkingCopyAsync(root, token);
                LogLine(cleanupOk
                    ? "<color=green>[1/4] Cleanup complete.</color>"
                    : "<color=#FFAA00>[1/4] Cleanup timed out (30s), proceeding.</color>");
                UpdateProgress(0.2f);

                LogStage(2, 4, "Fixing missing/deleted files...");
                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);
                var (missing, deleted, unversioned) = ParseStatusForCommitAll(rawStatus);
                int fixedCount = await FixMissingOrDeletedAsync(root, missing.Concat(deleted).ToList(), token);
                LogLine(fixedCount > 0
                    ? $"<color=green>[2/4] Scheduled {fixedCount} item(s) for delete/fix.</color>"
                    : "<color=green>[2/4] No missing/deleted items.</color>");
                UpdateProgress(0.4f);

                LogStage(3, 4, $"Adding new files (selected: {unversioned.Count})...");
                int addedCount = await AddNewFilesAsync(root, unversioned, token);
                LogLine(addedCount > 0
                    ? $"<color=green>[3/4] Added/scheduled {addedCount} path(s).</color>"
                    : "<color=green>[3/4] No new files selected.</color>");
                UpdateProgress(0.6f);

                string message = GetSanitizedMessage();
                if (message == null)
                    return;

                LogStage(4, 4, "Committing all changes...");
                string command = $"commit -m \"{message}\" --non-interactive .";
                string result = await RunCommitAsync(command, root, token);

                success = HandleCommitResult(result);
            }
            catch (OperationCanceledException)
            {
                LogLine("<color=orange><b>[ABORTED]</b></color> User cancelled.");
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
            }
            finally
            {
                FinalizeCommit(hasLock, success);
            }
        }

        private async Task CommitSelectedAsync()
        {
            bool hasLock = false;
            bool success = false;
            var opStart = DateTime.UtcNow;

            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock || svnManager == null || svnUI == null)
                    return;

                IsProcessing = true;
                await svnManager.CancelBackgroundTasksAsync();

                string root = NormalizeRoot(svnManager.WorkingDir);
                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule == null)
                {
                    LogError("SVN Status module not found.");
                    return;
                }

                var all = statusModule.GetCurrentData() ?? new List<SvnTreeElement>();
                var selectedChanged = all
                    .Where(e => e.IsChecked &&
                                !string.IsNullOrWhiteSpace(e.Status) &&
                                e.Status != " " &&
                                e.Status != "DIR")
                    .ToList();

                if (selectedChanged.Count == 0)
                {
                    SetCommitConsole($"{TsTag()} <color=orange>Nothing selected for commit.</color>", append: false);
                    return;
                }

                var token = ResetAndGetToken();
                string message = GetSanitizedMessage();
                if (message == null)
                    return;

                SetCommitConsole($"{TsTag()} <b>Initiating commit...</b>\n\n", append: false);
                LogLine($"Selected changed items: {selectedChanged.Count}");
                ShowProgressBar(0.05f);

                // [1/4]
                LogStage(1, 4, "Cleaning up database...");
                bool cleanupOk = await CleanupWorkingCopyAsync(root, token);
                LogLine(cleanupOk
                    ? "<color=green>[1/4] Cleanup complete.</color>"
                    : "<color=#FFAA00>[1/4] Cleanup timed out (30s), proceeding.</color>");
                UpdateProgress(0.25f);

                // [2/4]
                var brokenPaths = selectedChanged
                    .Where(e => e.Status == "!" || e.Status == "D")
                    .Select(e => e.FullPath)
                    .ToList();

                LogStage(2, 4, $"Fixing missing/deleted files (selected: {brokenPaths.Count})...");
                int fixedCount = await FixMissingOrDeletedAsync(root, brokenPaths, token);
                LogLine(fixedCount > 0
                    ? $"<color=green>[2/4] Scheduled {fixedCount} item(s) for delete/fix.</color>"
                    : "<color=green>[2/4] No missing/deleted items selected.</color>");
                UpdateProgress(0.4f);

                // [3/4]
                var selectedNew = selectedChanged
                    .Where(e => e.Status == "?" || e.Status == "A")
                    .Select(e => e.FullPath)
                    .ToList();

                LogStage(3, 4, $"Adding new files (selected: {selectedNew.Count})...");
                int addedCount = await AddNewFilesAsync(root, selectedNew, token);
                LogLine(addedCount > 0
                    ? $"<color=green>[3/4] Added/scheduled {addedCount} path(s).</color>"
                    : "<color=green>[3/4] No new files selected.</color>");
                UpdateProgress(0.6f);

                // [4/4]
                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);
                var statusMap = ParseValidPaths(rawStatus);

                var directTargets = selectedChanged.Select(e => e.FullPath).ToList();
                var requiredParents = GetRequiredAddedParents(selectedNew, statusMap);

                var commitTargets = directTargets
                    .Concat(requiredParents)
                    .Select(SvnRunner.NormalizeRepositoryPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p.Count(c => c == '/'))
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                LogStage(4, 4, $"Committing {commitTargets.Count} target(s) (selected: {selectedChanged.Count}, fixed: {fixedCount}, added: {addedCount})...");
                string result = await RunCommitTargetsAsync(root, commitTargets, message, token, depthEmpty: true);
                success = HandleCommitResult(result);

                var durationMs = (long)(DateTime.UtcNow - opStart).TotalMilliseconds;
                LogLine($"Summary: selected={selectedChanged.Count}, fixed={fixedCount}, added={addedCount}, targets={commitTargets.Count}, durationMs={durationMs}");
            }
            catch (OperationCanceledException)
            {
                LogLine("<color=orange><b>[ABORTED]</b></color> Commit cancelled.");
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
            }
            finally
            {
                FinalizeCommit(hasLock, success);
            }
        }

        private List<string> GetRequiredAddedParents(
            IEnumerable<string> selectedNewPaths,
            Dictionary<string, string> statusMap)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in NormalizePaths(selectedNewPaths))
            {
                string parent = Path.GetDirectoryName(raw)?.Replace('\\', '/').Trim('/');
                while (!string.IsNullOrEmpty(parent))
                {
                    if (statusMap != null &&
                        statusMap.TryGetValue(parent, out var st) &&
                        (st == "A" || st == "?"))
                    {
                        result.Add(parent);
                    }

                    int idx = parent.LastIndexOf('/');
                    if (idx <= 0) break;
                    parent = parent.Substring(0, idx);
                }
            }

            return result
                .OrderBy(p => p.Count(c => c == '/'))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<string> EnsureParentDirectoriesAreIncluded(
            List<string> paths,
            string root,
            Dictionary<string, string> validSvnPaths = null)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in NormalizePaths(paths))
            {
                string current = rawPath.Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(current))
                    continue;

                while (!string.IsNullOrEmpty(current))
                {
                    string fullCurrent = Path.Combine(root, current).Replace('\\', '/');

                    bool canInclude =
                        validSvnPaths == null ||
                        validSvnPaths.ContainsKey(current) ||
                        Directory.Exists(fullCurrent);

                    if (canInclude)
                        result.Add(current);

                    int lastSlash = current.LastIndexOf('/');
                    if (lastSlash <= 0) break;
                    current = current.Substring(0, lastSlash);
                }
            }

            return result
                .OrderBy(p => p.Count(c => c == '/'))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<string> RunCommitAsync(string command, string root, CancellationToken token)
        {
            var sb = new StringBuilder();

            await SvnRunner.RunLiveAsync(command, root, line =>
            {
                sb.AppendLine(line);
                ProcessCommitLiveLine(line);
            }, token).ConfigureAwait(false);

            return sb.ToString();
        }

        private async Task<string> RunCommitTargetsAsync(
            string root,
            IEnumerable<string> targets,
            string message,
            CancellationToken token,
            bool depthEmpty = false)
        {
            var list = NormalizePaths(targets);
            if (list.Count == 0)
                return "";

            string tempFile = Path.Combine(Path.GetTempPath(), $"svn_commit_{Guid.NewGuid():N}.txt");
            File.WriteAllLines(tempFile, list, new UTF8Encoding(false));

            try
            {
                string depthArg = depthEmpty ? "--depth empty " : "";
                string command = $"commit {depthArg}--targets \"{tempFile}\" -m \"{message}\" --non-interactive";
                return await RunCommitAsync(command, root, token);
            }
            finally
            {
                SafeDelete(tempFile);
            }
        }

        private void ProcessCommitLiveLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return;

            string line = rawLine.Replace("\r", "").Trim();

            if (line.StartsWith("[SVN ERROR]", StringComparison.OrdinalIgnoreCase))
                line = line["[SVN ERROR]".Length..].Trim();

            if (string.IsNullOrWhiteSpace(line))
                return;

            if (line.Length > 3 &&
                (double)line.Count(c => "@*#=-_/\\|".Contains(c)) / line.Length > 0.75)
                return;

            string lower = line.ToLowerInvariant();
            if (lower.Contains("restricted access") ||
                lower.Contains("unauthorized access") ||
                lower.Contains("prosecution") ||
                lower.Contains("monitoring"))
                return;

            if (line.StartsWith("Sending ") || line.StartsWith("Adding ") ||
                line.StartsWith("Deleting ") || line.StartsWith("Replacing "))
                return;

            if (line.StartsWith("Transmitting file data", StringComparison.OrdinalIgnoreCase))
                return;

            if (line.StartsWith("Committing transaction", StringComparison.OrdinalIgnoreCase))
            {
                LogLine("<color=green><b>[4/4]</b> Finalizing commit...</color>");
                return;
            }

            if (line.StartsWith("Committed revision", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (line.StartsWith("svn: E", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                LogError(line);
            }
        }

        private bool HandleCommitResult(string result)
        {
            if (result.Contains("Committed revision"))
            {
                string rev = svnManager.ParseRevision(result);
                UpdateProgress(1.0f);
                LogLine($"<color=green><b>SUCCESS!</b></color> Revision: {rev}");

                SVNStatus.ClearLockCache();
                svnManager._diskChangesDetected = true;
                ClearCommitUI();

                RunOnMainThread(() =>
                {
                    if (svnUI?.CommitMessageInput != null)
                        svnUI.CommitMessageInput.text = "";
                });

                return true;
            }

            string info = string.IsNullOrWhiteSpace(result)
                ? "Nothing to commit."
                : (result.Length > 500 ? result.Substring(0, 500) + "..." : result);

            LogLine($"<color=yellow>Result:</color> {info}");
            return false;
        }

        private void FinalizeCommit(bool hasLock, bool wasSuccessful = false)
        {
            _commitCts?.Dispose();
            _commitCts = null;

            if (hasLock)
            {
                IsProcessing = false;
                try { _operationLock.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { }
            }

            HideProgressBarAfterDelay(2.0f);

            if (wasSuccessful && svnManager != null)
            {
                svnManager.GetModule<SVNStatus>()?.ClearCurrentData();
                _ = Task.Run(async () =>
                {
                    try { await svnManager.RefreshStatus(); }
                    catch { }
                });
            }
        }

        private async Task<bool> CleanupWorkingCopyAsync(string root, CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            try
            {
                await SvnRunner.RunAsync("cleanup", root, false, linkedCts.Token);
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                return false;
            }
        }

        private async Task<int> FixMissingOrDeletedAsync(string root, IEnumerable<string> files, CancellationToken token)
        {
            var items = NormalizePaths(files);
            if (items.Count == 0) return 0;

            string tempFile = Path.Combine(Path.GetTempPath(), $"svn_fix_{Guid.NewGuid():N}.txt");
            File.WriteAllLines(tempFile, items, new UTF8Encoding(false));

            try
            {
                await SvnRunner.RunAsync($"delete --force --targets \"{tempFile}\"", root, false, token);
                return items.Count;
            }
            finally
            {
                SafeDelete(tempFile);
            }
        }

        private async Task<int> AddNewFilesAsync(string root, IEnumerable<string> files, CancellationToken token)
        {
            var additions = NormalizePaths(files);
            if (additions.Count == 0) return 0;

            var allPathsToAdd = EnsureParentDirectoriesAreIncluded(additions, root);

            for (int i = 0; i < allPathsToAdd.Count; i += AddBatchSize)
            {
                token.ThrowIfCancellationRequested();
                var batch = allPathsToAdd.Skip(i).Take(AddBatchSize).Select(p => p.Contains(' ') ? $"\"{p}\"" : p);
                await SvnRunner.RunAsync($"add --force --parents {string.Join(" ", batch)}", root, false, token);
            }

            return allPathsToAdd.Count;
        }

        private async Task RevertAllMissingWrapperAsync()
        {
            await RevertAllMissingAsync();
            LogSystem("Repair process finished.", MessageType.Success);
        }

        private async Task RevertAllMissingAsync()
        {
            bool hasLock = false;

            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock)
                    return;

                IsProcessing = true;

                string root = svnManager.WorkingDir;
                SetCommitConsole($"{TsTag()} <b>[Revert]</b> Starting recovery of missing files...", append: false);

                string rawStatus = await SvnRunner.RunAsync("status", root);
                var filesToRevert = ParseStatusLines(rawStatus, "!");

                if (filesToRevert.Count == 0)
                {
                    LogLine("<color=green>No missing files found.</color>");
                    return;
                }

                LogLine($"Found {filesToRevert.Count} missing files. Restoring...");

                for (int i = 0; i < filesToRevert.Count; i += RevertBatchSize)
                {
                    var batch = filesToRevert.Skip(i).Take(RevertBatchSize).Select(p => $"\"{p}\"");
                    await SvnRunner.RunAsync($"revert {string.Join(" ", batch)}", root);
                }

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearCurrentData();
                    ClearTreeDisplays();
                    await statusModule.ExecuteRefreshWithAutoExpand();
                }

                LogLine("<color=green><b>SUCCESS!</b></color> Missing files restored.");
            }
            catch (Exception ex)
            {
                LogError($"Revert Error: {ex.Message}");
            }
            finally
            {
                if (hasLock)
                {
                    IsProcessing = false;
                    _operationLock.Release();
                }
            }
        }

        private (List<string> Missing, List<string> Deleted, List<string> Unversioned)
            ParseStatusForCommitAll(string rawStatus)
        {
            var missing = new List<string>();
            var deleted = new List<string>();
            var unversioned = new List<string>();

            if (string.IsNullOrWhiteSpace(rawStatus))
                return (missing, deleted, unversioned);

            using var reader = new StringReader(rawStatus);
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 8)
                    continue;

                char status = line[0];
                string rawPath = line.Substring(8).TrimStart();
                string cleanPath = SvnRunner.NormalizeRepositoryPath(rawPath);

                if (string.IsNullOrWhiteSpace(cleanPath))
                    continue;

                if (status == '!')
                {
                    if (cleanPath.Contains("@") && !cleanPath.EndsWith("@"))
                        cleanPath += "@";
                    missing.Add(cleanPath);
                }
                else if (status == 'D')
                {
                    deleted.Add(cleanPath);
                }
                else if (status == '?')
                {
                    unversioned.Add(cleanPath);
                }
            }

            return (missing, deleted, unversioned);
        }

        private Dictionary<string, string> ParseValidPaths(string rawStatus)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawStatus))
                return dict;

            using var reader = new StringReader(rawStatus);
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 8)
                    continue;

                char status = line[0];
                if (status == ' ')
                    continue;

                string rawPath = line.Substring(8).TrimStart();
                string cleanPath = SvnRunner.NormalizeRepositoryPath(rawPath).Replace('\\', '/');

                if (!string.IsNullOrWhiteSpace(cleanPath) && !dict.ContainsKey(cleanPath))
                    dict[cleanPath] = status.ToString();
            }

            return dict;
        }

        private List<string> ParseStatusLines(string rawStatus, string statusFilter)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(rawStatus))
                return result;

            using var reader = new StringReader(rawStatus);
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                string path = svnManager?.ExtractPathFromStatusLine(line, statusFilter);
                if (!string.IsNullOrWhiteSpace(path))
                    result.Add(SvnRunner.NormalizeRepositoryPath(path));
            }

            return result;
        }

        private void ClearCommitUI()
        {
            RunOnMainThread(() =>
            {
                svnUI?.SvnTreeView?.ClearView();
                svnUI?.SVNCommitTreeDisplay?.ClearView();
                ClearTreeDisplays();
            });
        }

        private void ClearTreeDisplays()
        {
            RunOnMainThread(() =>
            {
                if (svnUI?.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                if (svnUI?.CommitTreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
            });
        }

        private void UpdateProgress(float value)
        {
            RunOnMainThread(() =>
            {
                if (svnUI?.OperationProgressBar != null)
                    svnUI.OperationProgressBar.value = value;
            });
        }

        private void ShowProgressBar(float initialValue)
        {
            RunOnMainThread(() =>
            {
                if (svnUI?.OperationProgressBar != null)
                {
                    svnUI.OperationProgressBar.gameObject.SetActive(true);
                    svnUI.OperationProgressBar.value = initialValue;
                }
            });
        }

        private void HideProgressBarAfterDelay(float seconds)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay((int)(seconds * 1000)); }
                catch { }

                RunOnMainThread(() =>
                {
                    if (svnUI?.OperationProgressBar != null)
                    {
                        svnUI.OperationProgressBar.gameObject.SetActive(false);
                        svnUI.OperationProgressBar.value = 0f;
                    }
                });
            });
        }

        private string NormalizeRoot(string root) =>
            root?.Replace("\\", "/").TrimEnd('/') ?? "";

        private string JoinPath(string root, string relative) =>
            $"{root}/{relative.Replace('\\', '/').TrimStart('/')}".Replace("//", "/");

        private List<string> NormalizePaths(IEnumerable<string> paths) =>
            paths?.Select(SvnRunner.NormalizeRepositoryPath)
                 .Where(p => !string.IsNullOrWhiteSpace(p))
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList()
            ?? new List<string>();

        private string GetSanitizedMessage()
        {
            string msg = GetOnMainThread(() => svnUI?.CommitMessageInput?.text);
            if (string.IsNullOrWhiteSpace(msg))
            {
                LogError("Please enter a commit message!");
                return null;
            }
            return msg.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }

        private CancellationToken ResetAndGetToken()
        {
            var old = Interlocked.Exchange(ref _commitCts, null);
            old?.Dispose();

            _commitCts = new CancellationTokenSource();
            return _commitCts.Token;
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private string FormatCommitSize(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int order = 0;

            while (size >= BytesConversionFactor && order < suffixes.Length - 1)
            {
                order++;
                size /= BytesConversionFactor;
            }

            return $"{size:0.##} {suffixes[order]}";
        }

        private enum MessageType { Info, Success, Warning, Error }

        public class SVNStatusElement
        {
            public string FullPath;
            public string Status;
            public bool IsChecked;
            public bool IsExpanded;
            public bool IsFolder;
            public List<SVNStatusElement> Children;
        }
    }
}