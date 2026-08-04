using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNCommit : SVNBase
    {
        private CancellationTokenSource _commitCTS;
        private List<SVNStatusElement> _items = new();
        private const double BytesConversionFactor = 1024.0;
        private const int CleanupTimeoutSeconds = 30;
        private const int RefreshStatusTimeoutMs = 5000;

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        private void PostToMainThread(Action action)
        {
            if (action == null) return;
            UnityMainThreadDispatcher.Enqueue(action);
        }

        private string FormatPathForSvn(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string cleanPath = SvnRunner.NormalizeRepositoryPath(path).Replace('\\', '/');
            if (cleanPath.Contains("@") && !cleanPath.EndsWith("@"))
            {
                cleanPath += "@";
            }
            return cleanPath;
        }

        private static string SanitizeCommitMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;

            string pattern = @"[\uFEFF\u200B-\u200D\u202A-\u202E\u2060-\u2069\x00-\x08\x0B\x0C\x0E-\x1F\x7F]";
            string cleaned = Regex.Replace(message, pattern, string.Empty);

            return cleaned.Trim();
        }

        public void CancelOperation()
        {
            var cts = Interlocked.CompareExchange(ref _commitCTS, null, null);
            if (cts == null || !IsProcessing) return;

            try { cts.Cancel(); } catch (ObjectDisposedException) { }

            IsProcessing = false;
            SafeResetProgress();
            LogToConsole("<color=orange><b>[System]</b> Operation cancelled by user.</color>");
        }

        private string FormatCommitSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
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

        public void ShowWhatWillBeCommitted()
        {
            _ = ShowWhatWillBeCommittedAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[Commit] ShowWhatWillBeCommitted failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        private async Task ShowWhatWillBeCommittedAsync()
        {
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir);
            var commitables = statusDict.Where(x => "MADC?".Contains(x.Value.status)).ToList();
            var sb = new StringBuilder(commitables.Count * 40 + 64);
            sb.AppendLine("<b>Current changes to send:</b>");
            foreach (var item in commitables)
            {
                string cleanPath = SvnRunner.NormalizeRepositoryPath(item.Key).Replace('\\', '/');
                sb.AppendLine($"[{item.Value.status}] {cleanPath}");
            }

            string resultText = sb.ToString();
            PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, resultText, append: true));
        }

        public void RefreshCommitList()
        {
            if (IsProcessing) return;
            _ = RefreshCommitListAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[Commit] RefreshCommitList failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        private async Task RefreshCommitListAsync()
        {
            IsProcessing = true;
            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir);
                _items = statusDict.Where(x => "MADC?".Contains(x.Value.status))
                    .Select(x => new SVNStatusElement
                    {
                        FullPath = SvnRunner.NormalizeRepositoryPath(x.Key).Replace('\\', '/'),
                        Status = x.Value.status,
                        IsChecked = true
                    }).ToList();

                long totalSize = await Task.Run(() =>
                {
                    long size = 0;
                    string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');

                    if (root.EndsWith(":")) root += "/";

                    foreach (var item in _items)
                    {
                        if (!item.IsChecked || item.Status == "!" || item.Status == "D") continue;
                        string full = Path.Combine(root, item.FullPath).Replace('\\', '/');
                        if (File.Exists(full))
                        {
                            try { size += new FileInfo(full).Length; }
                            catch { }
                        }
                    }
                    return size;
                });

                RenderCommitList(_items, totalSize);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void RenderCommitList(List<SVNStatusElement> items, long totalSize)
        {
            string uiOutput = null;

            if (items == null || items.Count == 0)
            {
                uiOutput = "No changes to commit.";
            }
            else
            {
                var sb = new StringBuilder(items.Count * 48 + 128);
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
                uiOutput = sb.ToString();
            }

            PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, uiOutput, append: false));
        }

        public List<SvnTreeElement> GetSelectedFiles()
        {
            var status = svnManager.GetModule<SVNStatus>();
            return status != null ? status.GetCurrentData().Where(e => e.IsChecked).ToList() : new List<SvnTreeElement>();
        }

        public void ExecuteRevertAllMissing()
        {
            _ = RevertAllMissingAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    SVNLogBridge.LogError($"[Commit] RevertAllMissing failed: {t.Exception?.InnerException?.Message}");
                    LogToConsole($"<color=#FFAA00>Revert Error:</color> {t.Exception?.InnerException?.Message}");
                }
                else LogToConsole("<color=green><b>[System]</b> Repair process finished.</color>");
            }, TaskScheduler.Default);
        }

        private async Task RevertAllMissingAsync()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            string root = svnManager.WorkingDir;
            LogToConsole("<b>[Revert]</b> Starting recovery of missing files...");

            try
            {
                string rawStatus = await SvnRunner.RunAsync("status", root);
                var filesToRevert = new List<string>();
                using (var reader = new StringReader(rawStatus))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string path = svnManager.ExtractPathFromStatusLine(line, "!");
                        if (path != null)
                        {
                            path = FormatPathForSvn(path);
                            if (!string.IsNullOrWhiteSpace(path)) filesToRevert.Add(path);
                        }
                    }
                }
                if (filesToRevert.Count == 0)
                {
                    LogToConsole("<color=green>No missing files found.</color>");
                    return;
                }

                LogToConsole($"Found {filesToRevert.Count} missing files. Restoring...");
                const int batchSize = 20;
                for (int i = 0; i < filesToRevert.Count; i += batchSize)
                {
                    var batch = filesToRevert.Skip(i).Take(batchSize).Select(p => $"\"{p}\"");
                    await SvnRunner.RunAsync($"revert --depth infinity {string.Join(" ", batch)}", root);
                }

                var statusModule = svnManager.GetModule<SVNStatus>();
                statusModule?.ClearCurrentData();
                if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                if (svnUI.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                if (statusModule != null) await statusModule.ExecuteRefreshWithAutoExpand();

                LogToConsole("<color=green><b>SUCCESS!</b></color> Missing files restored.");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00>Revert Error:</color> {ex.Message}");
                throw;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void CommitSelected()
        {
            string rawMessage = svnUI.CommitMessageInput?.text;
            string message = SanitizeCommitMessage(rawMessage);

            if (string.IsNullOrWhiteSpace(message))
            {
                LogToConsole("<color=#FFAA00>Error:</color> Please enter a commit message!");
                return;
            }

            _ = ExecuteCommitSelected(message).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[Commit] CommitSelected failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        public void CommitAll()
        {
            if (IsProcessing) return;
            _ = CommitAllAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[Commit] CommitAll failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        private async Task CommitAllAsync()
        {
            await svnManager.CancelBackgroundTasksAsync();

            string rawMessage = svnUI.CommitMessageInput?.text;
            string message = SanitizeCommitMessage(rawMessage);

            if (string.IsNullOrWhiteSpace(message))
            {
                LogToConsole("<color=#FFAA00>Error:</color> Commit message is empty!");
                return;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();
            var token = _commitCTS.Token;
            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');

            string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

            ShowProgressBar(0.05f);
            ClearCommitConsole();

            try
            {
                await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)));

                LogToConsole("<b>Initiating commit...</b>");
                bool cleanupOk = await CleanupWorkingCopy(root, token);
                if (!cleanupOk) LogToConsole("<color=yellow>Cleanup skipped (timeout).</color>");
                UpdateProgress(0.25f);

                var missing = new List<string>();
                var unversioned = new List<string>();
                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);

                if (!string.IsNullOrEmpty(rawStatus))
                {
                    using var reader = new StringReader(rawStatus);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("?"))
                        {
                            string rawPath = line.Substring(8).TrimStart();
                            string cleanPath = FormatPathForSvn(rawPath);
                            if (!string.IsNullOrWhiteSpace(cleanPath)) unversioned.Add(cleanPath);
                        }
                        else
                        {
                            string path = svnManager.ExtractPathFromStatusLine(line, "!");
                            if (path != null)
                            {
                                path = FormatPathForSvn(path);
                                if (!string.IsNullOrWhiteSpace(path)) missing.Add(path);
                            }
                        }
                    }
                }

                await ScheduleMissingForDeletion(root, missing, token);
                UpdateProgress(0.45f);

                await AddNewFiles(root, unversioned, token);
                UpdateProgress(0.65f);

                string result = await CommitDirectoryLiveAsync(root, msgFile, token);

                if (result.Contains("Committed revision"))
                {
                    UpdateProgress(1.0f);
                    SVNStatus.ClearLockCache();
                    svnManager.DiskChangesDetected = true;
                    ClearCommitUI();
                    svnManager.GetModule<SVNStatus>()?.ClearCurrentData();
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
                }
                else
                {
                    string info = string.IsNullOrWhiteSpace(result) ? "Nothing to commit." : result;
                    LogToConsole($"<color=yellow>Result:</color> {info}");
                    ClearCommitUI();
                }
            }
            catch (OperationCanceledException) { LogToConsole("<color=orange><b>[ABORTED]</b> User cancelled.</color>"); }
            catch (Exception ex) { LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}"); }
            finally
            {
                IsProcessing = false;
                _commitCTS?.Dispose();
                _commitCTS = null;
                SafeResetProgress();

                try { if (File.Exists(msgFile)) File.Delete(msgFile); } catch { }

                await SafeRefreshStatusAsync();
            }
        }

        public async Task ExecuteCommitSelected(string message)
        {
            if (IsProcessing) return;

            await svnManager.CancelBackgroundTasksAsync();
            string root = svnManager.WorkingDir.Replace('\\', '/').TrimEnd('/');
            var statusModule = svnManager.GetModule<SVNStatus>();

            if (statusModule == null)
            {
                LogToConsole("<color=#FFAA00>Error:</color> SVN Status module not found.");
                return;
            }

            var allElements = statusModule.GetCurrentData();
            var selectedItems = allElements?.Where(e => e.IsChecked).ToList() ?? new List<SvnTreeElement>();

            if (allElements == null || allElements.Count == 0)
            {
                LogToConsole("<color=yellow>No SVN changes detected. Working copy is already clean.</color>");
                return;
            }
            if (selectedItems.Count == 0)
            {
                LogToConsole("<color=orange>Nothing selected for commit.</color>");
                return;
            }

            selectedItems = selectedItems.Where(e => "MADC?!".Contains(e.Status)).ToList();
            if (selectedItems.Count == 0)
            {
                LogToConsole("<color=yellow>No valid files to commit.</color>");
                return;
            }

            IsProcessing = true;
            _commitCTS = new CancellationTokenSource();
            CancellationToken token = _commitCTS.Token;

            string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

            ShowProgressBar(0.05f);
            ClearCommitConsole();

            try
            {
                await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)));

                LogToConsole("<b>Initiating commit...</b>");

                bool cleanupOk = await CleanupWorkingCopy(root, token);
                if (!cleanupOk) LogToConsole("<color=yellow>Cleanup skipped (timeout).</color>");
                UpdateProgress(0.25f);

                var missingPaths = selectedItems.Where(e => e.Status == "!" || e.Status == "D").Select(e => e.FullPath);
                await ScheduleMissingForDeletion(root, missingPaths, token);
                UpdateProgress(0.45f);

                var newPaths = selectedItems.Where(e => e.Status == "?" || e.Status == "A").Select(e => e.FullPath);
                var actuallyAddedPaths = await AddNewFiles(root, newPaths, token);
                UpdateProgress(0.65f);

                var allTargets = await Task.Run(() =>
                {
                    var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var actuallyAddedSet = new HashSet<string>(actuallyAddedPaths, StringComparer.OrdinalIgnoreCase);

                    var unversionedOrAddedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (allElements != null)
                    {
                        foreach (var el in allElements)
                        {
                            if ("?A".Contains(el.Status))
                            {
                                unversionedOrAddedSet.Add(el.FullPath.Replace('\\', '/'));
                            }
                        }
                    }
                    foreach (var p in actuallyAddedPaths) unversionedOrAddedSet.Add(p.Replace('\\', '/'));

                    foreach (var item in selectedItems)
                    {
                        string itemPath = item.FullPath.Replace('\\', '/');

                        if ((item.Status == "?" || item.Status == "A") && !actuallyAddedSet.Contains(itemPath))
                            continue;

                        targets.Add(itemPath);

                        if ("?A".Contains(item.Status))
                        {
                            string dir = Path.GetDirectoryName(itemPath)?.Replace('\\', '/');
                            while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                            {
                                if (unversionedOrAddedSet.Contains(dir))
                                {
                                    targets.Add(dir);
                                    dir = Path.GetDirectoryName(dir)?.Replace('\\', '/');
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }

                    return targets;
                }, token);

                string result = await CommitTargetsLiveAsync(root, allTargets, msgFile, token);

                if (result.Contains("Committed revision"))
                {
                    UpdateProgress(1.0f);
                    SVNStatus.ClearLockCache();
                    svnManager.DiskChangesDetected = true;
                    statusModule.ClearCurrentData();
                    ClearCommitUI();
                    if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
                }
                else
                {
                    string shortResult = string.IsNullOrWhiteSpace(result) ? "Nothing to commit." : (result.Length > 500 ? result.Substring(0, 500) + "..." : result);
                    LogToConsole($"<color=yellow>Result:</color> {shortResult}");
                    ClearCommitUI();
                }
            }
            catch (OperationCanceledException)
            {
                LogToConsole("<color=orange><b>[ABORTED]</b> Commit cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _commitCTS?.Dispose();
                _commitCTS = null;
                SafeResetProgress();

                try { if (File.Exists(msgFile)) File.Delete(msgFile); } catch { }

                await SafeRefreshStatusAsync();
            }
        }

        private async Task<string> CommitDirectoryLiveAsync(string root, string msgFilePath, CancellationToken token)
        {
            LogToConsole("<b>[4/4]</b> Sending to server (please wait)...");
            string command = $"commit -F \"{msgFilePath}\" --non-interactive .";
            return await RunCommitProcessAsync(command, root, token);
        }

        private async Task<string> CommitTargetsLiveAsync(string root, IEnumerable<string> targets, string msgFilePath, CancellationToken token)
        {
            var list = targets.Select(FormatPathForSvn).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return "";

            LogToConsole($"<b>[4/4]</b> Sending {list.Count} target(s) to server (please wait)...");
            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_targets_{Guid.NewGuid():N}.txt");

            await Task.Run(() => File.WriteAllLines(targetsFile, list, new UTF8Encoding(false)));

            try
            {
                string command = $"commit --targets \"{targetsFile}\" -F \"{msgFilePath}\" --non-interactive";

                return await RunCommitProcessAsync(command, root, token);
            }
            finally
            {
                try { if (File.Exists(targetsFile)) File.Delete(targetsFile); } catch { }
            }
        }

        private async Task<string> RunCommitProcessAsync(string command, string root, CancellationToken token)
        {
            bool success = false;
            var errorSb = new StringBuilder();

            try
            {
                await SvnRunner.RunLiveAsync(command, root, line =>
                {
                    if (line.Contains("Committed revision", StringComparison.OrdinalIgnoreCase))
                        success = true;
                    else if (line.StartsWith("svn: E", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        errorSb.AppendLine(line);

                    ProcessCommitLiveLine(line);
                }, token);

                return success ? "Committed revision" : (errorSb.Length > 0 ? errorSb.ToString() : "Operation failed or cancelled.");
            }
            finally { }
        }

        private void ProcessCommitLiveLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return;
            string line = rawLine.Replace("\r", "").Trim();
            if (line.StartsWith("[SVN ERROR]", StringComparison.OrdinalIgnoreCase)) line = line["[SVN ERROR]".Length..].Trim();
            if (string.IsNullOrWhiteSpace(line)) return;

            if (line.Length > 3)
            {
                string decorative = "@*#=-_/\\|";
                int decCount = line.Count(c => decorative.Contains(c));
                if ((double)decCount / line.Length > 0.75) return;
            }
            string lower = line.ToLowerInvariant();
            if (lower.Contains("restricted access") || lower.Contains("unauthorized access") || lower.Contains("prosecution") ||
                lower.Contains("monitoring") || lower.Contains("by continuing, you consent") || lower.Contains("strictly prohibited") ||
                lower.Contains("all activity on this system") || lower.Contains("warning! you are entering") ||
                lower.Contains("you consent to monitoring") || lower.Contains("entering a restricted")) return;

            if (line.StartsWith("Sending ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Adding ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Deleting ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Replacing ", StringComparison.OrdinalIgnoreCase))
                return;

            if (line.StartsWith("Transmitting file data", StringComparison.OrdinalIgnoreCase))
                return;

            if (line.StartsWith("Committing transaction", StringComparison.OrdinalIgnoreCase))
            {
                LogToConsole("<color=#FFCC00><b>Finalizing commit...</b></color>");
                return;
            }

            if (line.StartsWith("Committed revision", StringComparison.OrdinalIgnoreCase))
            {
                LogToConsole($"<color=green><b>[SUCCESS] {line}</b></color>");
                return;
            }

            if (line.StartsWith("svn: E", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                LogToConsole($"<color=#FF4444><b>{line}</b></color>");
            }
        }

        private void ClearCommitConsole()
        {
            PostToMainThread(() =>
            {
                if (svnUI?.CommitConsoleContent != null) svnUI.CommitConsoleContent.text = "";
            });
        }

        private void ClearCommitUI()
        {
            PostToMainThread(() =>
            {
                if (svnUI?.SvnTreeView != null) svnUI.SvnTreeView.ClearView();
                if (svnUI?.SVNCommitTreeDisplay != null) svnUI.SVNCommitTreeDisplay.ClearView();
                if (svnUI?.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                if (svnUI?.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
            });
        }

        private void ShowProgressBar(float initialValue) =>
            PostToMainThread(() =>
            {
                if (svnUI.OperationProgressBar != null)
                {
                    svnUI.OperationProgressBar.gameObject.SetActive(true);
                    svnUI.OperationProgressBar.value = Mathf.Clamp01(initialValue);
                }
            });

        private void UpdateProgress(float value) =>
            PostToMainThread(() => { if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = Mathf.Clamp01(value); });

        private void SafeResetProgress() =>
            PostToMainThread(() =>
            {
                if (svnUI.OperationProgressBar != null)
                {
                    svnUI.OperationProgressBar.value = 0f;
                    svnUI.OperationProgressBar.gameObject.SetActive(false);
                }
            });

        private void LogToConsole(string msg)
        {
            string normalized = msg?.Trim() ?? "";
            if (string.IsNullOrEmpty(normalized)) return;
            PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, normalized + "\n", append: true));
        }

        private async Task SafeRefreshStatusAsync()
        {
            try
            {
                var refreshTask = svnManager.RefreshStatus();
                var timeoutTask = Task.Delay(RefreshStatusTimeoutMs);
                await Task.WhenAny(refreshTask, timeoutTask);
            }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] Background refresh failed: {ex.Message}"); }
        }

        private async Task<bool> CleanupWorkingCopy(string root, CancellationToken token)
        {
            LogToConsole("<b>[1/4]</b> Cleaning up database...");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            try
            {
                await SvnRunner.RunAsync("cleanup", root, false, linkedCts.Token);
                LogToConsole("<color=green>Cleanup complete</color>");
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                LogToConsole("<color=#FFAA00>Cleanup timed out (30s). Proceeding anyway...</color>");
                return false;
            }
        }

        private async Task ScheduleMissingForDeletion(string root, IEnumerable<string> files, CancellationToken token)
        {
            LogToConsole("<b>[2/4]</b> Scheduling missing files for deletion...");

            if (files == null || !files.Any())
            {
                LogToConsole("<color=green>No missing files to delete.</color>");
                return;
            }

            List<string> filteredDeletions = null;
            int rawCount = 0;

            await Task.Run(() =>
            {
                var normalized = files
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim().Replace('\\', '/').TrimEnd('/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                rawCount = normalized.Count;
                filteredDeletions = new List<string>(rawCount);

                foreach (var path in normalized)
                {
                    bool isNested = filteredDeletions.Any(parent =>
                        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase));

                    if (!isNested)
                    {
                        filteredDeletions.Add(path);
                    }
                }
            }, token);

            if (filteredDeletions.Count == 0)
            {
                LogToConsole("<color=green>No missing files to delete.</color>");
                return;
            }

            int skippedNested = rawCount - filteredDeletions.Count;
            LogToConsole($"Marking {filteredDeletions.Count} missing item(s) as deleted..." +
                (skippedNested > 0 ? $" <color=yellow>(Optimized: skipped {skippedNested} nested item(s) to prevent SVN conflicts)</color>" : ""));

            var formattedTargets = filteredDeletions.Select(FormatPathForSvn).ToList();

            string file = Path.Combine(Path.GetTempPath(), $"svn_delete_{Guid.NewGuid():N}.txt");

            await Task.Run(() => File.WriteAllLines(file, formattedTargets, new UTF8Encoding(false)), token);

            try
            {
                await SvnRunner.RunAsync($"delete --force --targets \"{file}\"", root, false, token);
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }

        private async Task<List<string>> AddNewFiles(string root, IEnumerable<string> files, CancellationToken token)
        {
            LogToConsole("<b>[3/4]</b> Checking for new files...");

            var rawAdditions = files.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (rawAdditions.Count == 0)
            {
                LogToConsole("<color=green>No new files to add.</color>");
                return new List<string>();
            }

            int skippedCount = 0;
            var validAdditions = new List<string>(rawAdditions.Count);

            await Task.Run(() =>
            {
                foreach (var rawPath in rawAdditions)
                {
                    string normalizedRaw = rawPath.Replace('\\', '/');
                    string fullPath = Path.Combine(root, normalizedRaw.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    {
                        validAdditions.Add(normalizedRaw);
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
            });

            if (skippedCount > 0)
            {
                LogToConsole($"<color=yellow>Warning: {skippedCount} file(s) disappeared from disk and were skipped.</color>");
            }

            if (validAdditions.Count == 0)
            {
                LogToConsole("<color=green>No existing new files to add.</color>");
                return new List<string>();
            }

            LogToConsole($"Adding {validAdditions.Count} new files to SVN index (processing database)...");
            string file = Path.Combine(Path.GetTempPath(), $"svn_add_{Guid.NewGuid():N}.txt");

            var escapedForSvn = validAdditions.Select(FormatPathForSvn).ToList();
            await Task.Run(() => File.WriteAllLines(file, escapedForSvn, new UTF8Encoding(false)), token);

            try
            {
                await SvnRunner.RunAsync($"add --force --parents --targets \"{file}\"", root, false, token);
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }

            return validAdditions;
        }

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