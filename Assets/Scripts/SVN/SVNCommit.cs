using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private int _processingFlag;

        private const double BytesConversionFactor = 1024.0;
        private const int CleanupTimeoutSeconds = 30;
        private const int RefreshStatusTimeoutMs = 5000;

        private const string DisplayStatuses = "MADR?!";
        private const string PreProcessStatuses = "MADR?!";

        private static readonly Regex CommittedRevisionRegex = new(@"Committed revision\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SanitizeMessageRegex = new(@"[\uFEFF\u200B-\u200D\u202A-\u202E\u2060-\u2069\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        #region Processing Guard (atomic)

        private bool TryEnterProcessing()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
                return false;

            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        #endregion

        #region Path Helpers

        private string MakeRelative(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return null;

            string cleanRoot = root.Replace('\\', '/').TrimEnd('/');
            string cleanPath = path.Replace('\\', '/').TrimEnd('/');

            if (string.Equals(cleanPath, cleanRoot, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            string rootWithSlash = cleanRoot + "/";
            if (cleanPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                return cleanPath.Substring(rootWithSlash.Length);

            return null;
        }

        private string FormatPathForSvn(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return path.Replace('\\', '/').Trim();
        }

        private string ResolvePhysicalPath(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string normalized = path.Replace('/', Path.DirectorySeparatorChar)
                                    .Replace('\\', Path.DirectorySeparatorChar)
                                    .Trim();

            if (normalized.StartsWith("." + Path.DirectorySeparatorChar))
                normalized = normalized.Substring(2);

            if (Path.IsPathRooted(normalized))
            {
                try { return Path.GetFullPath(normalized); }
                catch { return normalized; }
            }

            try
            {
                string rootNative = root.Replace('/', Path.DirectorySeparatorChar)
                                        .Replace('\\', Path.DirectorySeparatorChar);

                string combined = Path.Combine(rootNative, normalized);
                return Path.GetFullPath(combined);
            }
            catch
            {
                return null;
            }
        }

        private List<string> ReduceCommitTargets(IEnumerable<string> targets)
        {
            if (targets == null) return new List<string>();

            var sorted = targets
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Replace('\\', '/').Trim().Trim('/'))
                .Where(t => !string.IsNullOrWhiteSpace(t) && !Path.IsPathRooted(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(t => t.Length)
                .ThenByDescending(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new List<string>();
            foreach (string path in sorted)
            {
                bool isParentOfSelectedChild = result.Any(child =>
                    child.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

                if (!isParentOfSelectedChild)
                    result.Add(path);
            }
            return result;
        }

        #endregion

        #region General Helpers

        private static string SanitizeCommitMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            return SanitizeMessageRegex.Replace(message, string.Empty).Trim();
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

        private static long CalculateDirectorySize(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return 0;

            long size = 0;
            try
            {
                var dirInfo = new DirectoryInfo(folderPath);
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { size += file.Length; }
                    catch { }
                }
            }
            catch { }

            return size;
        }

        #endregion

        #region Cancellation

        public void CancelOperation()
        {
            var cts = Volatile.Read(ref _commitCTS);
            if (cts == null || !IsProcessing) return;

            try
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    LogToConsole("<color=orange><b>[System]</b> Cancellation requested...</color>");
                }
            }
            catch (ObjectDisposedException) { }
        }

        private void ClearCommitCts(CancellationTokenSource localCts)
        {
            if (Interlocked.CompareExchange(ref _commitCTS, null, localCts) == localCts)
            {
                try { localCts.Dispose(); } catch { }
            }
        }

        #endregion

        #region Preview / List

        public async void ShowWhatWillBeCommitted()
        {
            try { await ShowWhatWillBeCommittedAsync(); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] ShowWhatWillBeCommitted failed: {ex.Message}"); }
        }

        private async Task ShowWhatWillBeCommittedAsync()
        {
            string root = NormalizeRoot(svnManager.WorkingDir);
            if (string.IsNullOrWhiteSpace(root)) return;

            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root);
            var commitables = statusDict.Where(x => DisplayStatuses.Contains(x.Value.status)).ToList();

            var sb = new StringBuilder(commitables.Count * 48 + 64);
            sb.AppendLine("<b>Current working copy changes:</b>");
            foreach (var item in commitables)
                sb.AppendLine($"[{item.Value.status}] {item.Key}");

            PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: true));
        }

        public async void RefreshCommitList()
        {
            if (!TryEnterProcessing()) return;

            try
            {
                await RefreshCommitListAsync();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Commit] RefreshCommitList failed: {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task RefreshCommitListAsync()
        {
            string root = NormalizeRoot(svnManager.WorkingDir);
            if (string.IsNullOrWhiteSpace(root))
            {
                RenderCommitList(null, 0);
                return;
            }

            bool expandUnversioned = true;
            var statusDict = await SVNStatus.GetChangesDictionaryAsync(root, expandUnversioned);

            _items = statusDict
                .Where(x => DisplayStatuses.Contains(x.Value.Status))
                .Select(x => new SVNStatusElement
                {
                    FullPath = x.Key?.Replace('\\', '/'),
                    Status = x.Value.Status,
                    IsChecked = true
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.FullPath))
                .ToList();

            var localItems = _items;

            long totalSize = await Task.Run(() =>
            {
                long size = 0;

                foreach (var item in localItems)
                {
                    if (item.Status == "!" || item.Status == "D")
                        continue;

                    try
                    {
                        string fullPhysicalPath = ResolvePhysicalPath(root, item.FullPath);
                        if (string.IsNullOrEmpty(fullPhysicalPath))
                            continue;

                        if (File.Exists(fullPhysicalPath))
                        {
                            size += new FileInfo(fullPhysicalPath).Length;
                        }
                        else if (Directory.Exists(fullPhysicalPath))
                        {
                            size += CalculateDirectorySize(fullPhysicalPath);
                        }
                    }
                    catch
                    {
                    }
                }

                return size;
            });

            RenderCommitList(_items, totalSize);
        }

        public void RenderCommitList(List<SVNStatusElement> items, long totalSize)
        {
            string uiOutput;

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
                        "R" => "orange",
                        "!" => "#FF4444",
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
            return status?.GetCurrentData().Where(e => e.IsChecked).ToList() ?? new List<SvnTreeElement>();
        }

        #endregion

        #region Revert Missing

        public async void ExecuteRevertAllMissing()
        {
            if (!TryEnterProcessing()) return;

            try
            {
                await RevertAllMissingAsync();
                LogToConsole("<color=green><b>[System]</b> Repair process finished.</color>");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color=#FFAA00>Revert Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task RevertAllMissingAsync()
        {
            string root = NormalizeRoot(svnManager.WorkingDir);
            if (string.IsNullOrWhiteSpace(root))
            {
                LogToConsole("<color=#FFAA00>Error:</color> Working directory is not set.");
                return;
            }

            LogToConsole("<b>[Revert]</b> Starting recovery of missing files...");

            string rawStatus = await SvnRunner.RunAsync("status", root);
            var filesToRevert = new List<string>();

            using (var reader = new StringReader(rawStatus))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string path = svnManager.ExtractPathFromStatusLine(line, "!");
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    string relative = Path.IsPathRooted(path) ? MakeRelative(root, path) : path;
                    relative = FormatPathForSvn(relative);

                    if (!string.IsNullOrWhiteSpace(relative))
                        filesToRevert.Add(relative);
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
                var batch = filesToRevert.Skip(i).Take(batchSize)
                    .Select(p => $"\"{p.Replace("\"", "\\\"")}\"");

                string command = "revert --depth infinity " + string.Join(" ", batch);
                await SvnRunner.RunAsync(command, root);
            }

            var statusModule = svnManager.GetModule<SVNStatus>();
            statusModule?.ClearCurrentData();

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
            if (svnUI.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);

            if (statusModule != null)
                await statusModule.ExecuteRefreshWithAutoExpand();

            LogToConsole("<color=green><b>SUCCESS!</b> Missing files restored.</color>");
        }

        #endregion

        #region Commit Entry Points

        public async void CommitSelected()
        {
            string rawMessage = svnUI.CommitMessageInput?.text;
            string message = SanitizeCommitMessage(rawMessage);

            if (string.IsNullOrWhiteSpace(message))
            {
                LogToConsole("<color=#FFAA00>Error:</color> Please enter a commit message!");
                return;
            }

            try { await ExecuteCommitSelected(message); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] CommitSelected failed: {ex.Message}"); }
        }

        public async void CommitAll()
        {
            try { await CommitAllAsync(); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] CommitAll failed: {ex.Message}"); }
        }

        #endregion

        #region Commit All

        private async Task CommitAllAsync()
        {
            if (!TryEnterProcessing()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string rawMessage = svnUI.CommitMessageInput?.text;
                string message = SanitizeCommitMessage(rawMessage);

                if (string.IsNullOrWhiteSpace(message))
                {
                    LogToConsole("<color=#FFAA00>Error:</color> Commit message is empty!");
                    return;
                }

                string root = NormalizeRoot(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(root))
                {
                    LogToConsole("<color=#FFAA00>Error:</color> Working directory is not set.");
                    return;
                }

                using var localCts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _commitCTS, localCts);
                if (oldCts != null) { try { oldCts.Dispose(); } catch { } }

                CancellationToken token = localCts.Token;
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                ShowProgressBar(0.05f);
                ClearCommitConsole();

                try
                {
                    await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)), token);

                    LogToConsole("<b>Initiating Commit All...</b>");

                    // 1/4 Cleanup
                    bool cleanupOk = await CleanupWorkingCopy(root, token, "<b>[1/4]</b> Cleaning up working copy...");
                    if (!cleanupOk) return;
                    UpdateProgress(0.20f);

                    // 2/4 Missing files (!) → D
                    LogToConsole("<b>[2/4]</b> Scheduling missing files for deletion...");
                    var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root);
                    var missingRelPaths = statusDict
                        .Where(x => x.Value.status == "!")
                        .Select(x => MakeRelative(root, x.Key) ?? x.Key)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(NormalizeRelativeTarget)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();

                    await ScheduleMissingForDeletion(root, missingRelPaths, token);
                    UpdateProgress(0.40f);

                    // 3/4 Unversioned files (?) → A
                    LogToConsole("<b>[3/4]</b> Checking for new/unversioned files...");

                    var currentStatus = await SvnRunner.GetFullStatusDictionaryAsync(root);
                    var unversionedPaths = currentStatus
                        .Where(x => x.Value.status == "?")
                        .Select(x => MakeRelative(root, x.Key) ?? x.Key)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(NormalizeRelativeTarget)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();

                    if (unversionedPaths.Count > 0)
                    {
                        LogToConsole($"Found {unversionedPaths.Count} new file(s). Adding to SVN...");
                        string addTargetsFile = Path.Combine(Path.GetTempPath(), $"svn_add_{Guid.NewGuid():N}.txt");
                        try
                        {
                            await Task.Run(() => File.WriteAllLines(addTargetsFile, unversionedPaths, new UTF8Encoding(false)), token);
                            await SvnRunner.RunAsync($"add --targets \"{addTargetsFile}\"", root, false, token);
                            LogToConsole("<color=green>Indexing complete.</color>");
                        }
                        finally
                        {
                            TryDeleteFile(addTargetsFile);
                        }
                    }
                    else
                    {
                        LogToConsole("<color=green>No new files to add.</color>");
                    }

                    UpdateProgress(0.65f);

                    // 4/4 Commit
                    LogToConsole("<b>[4/4]</b> Sending to server...");
                    string command = $"commit -F \"{msgFile}\" --non-interactive .";

                    try
                    {
                        await RunCommitProcessAsync(command, root, token);
                        UpdateProgress(1.0f);

                        SVNStatus.ClearLockCache();
                        svnManager.DiskChangesDetected = true;

                        var statusModule = svnManager.GetModule<SVNStatus>();
                        statusModule?.ClearCurrentData();

                        PostToMainThread(() =>
                        {
                            ClearCommitUI();
                            if (svnUI.CommitMessageInput != null)
                                svnUI.CommitMessageInput.text = "";
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        LogToConsole("<color=orange><b>[ABORTED]</b> User cancelled.</color>");
                    }
                    catch (Exception ex)
                    {
                        LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}");
                    }
                }
                finally
                {
                    ClearCommitCts(localCts);
                    SafeResetProgress();
                    TryDeleteFile(msgFile);
                    await SafeRefreshStatusAsync();
                }
            }
            finally
            {
                ExitProcessing();
            }
        }

        #endregion

        #region Commit Selected

        private List<string> InjectMissingParentPaths(HashSet<string> selectedPaths, List<SvnTreeElement> allElements)
        {
            var result = new List<string>(selectedPaths);
            var elementDict = allElements.Where(e => e != null && !string.IsNullOrEmpty(e.FullPath))
                                      .ToDictionary(e => e.FullPath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

            foreach (var path in selectedPaths)
            {
                if (string.IsNullOrEmpty(path) || !path.Contains("/")) continue;

                var parts = path.Split('/');
                string currentParent = "";

                for (int i = 0; i < parts.Length - 1; i++)
                {
                    currentParent = string.IsNullOrEmpty(currentParent) ? parts[i] : currentParent + "/" + parts[i];

                    if (!result.Contains(currentParent, StringComparer.OrdinalIgnoreCase))
                    {
                        if (elementDict.TryGetValue(currentParent, out var parentEl))
                        {
                            if (parentEl.Status == "?" || parentEl.Status == "!")
                            {
                                result.Add(currentParent);
                            }
                        }
                        else
                        {
                            result.Add(currentParent);
                        }
                    }
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task ExecuteCommitSelected(string message)
        {
            if (!TryEnterProcessing()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string root = NormalizeRoot(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(root))
                {
                    LogToConsole("<color=#FFAA00>Error:</color> Working directory is not set.");
                    return;
                }

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
                    LogToConsole("<color=yellow>No SVN changes detected.</color>");
                    return;
                }

                if (selectedItems.Count == 0)
                {
                    LogToConsole("<color=orange>Nothing selected for commit.</color>");
                    return;
                }

                selectedItems = selectedItems.Where(e => PreProcessStatuses.Contains(e.Status)).ToList();
                if (selectedItems.Count == 0)
                {
                    LogToConsole("<color=yellow>No valid files to commit.</color>");
                    return;
                }

                using var localCts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _commitCTS, localCts);
                if (oldCts != null) { try { oldCts.Dispose(); } catch { } }

                CancellationToken token = localCts.Token;
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                ShowProgressBar(0.05f);
                ClearCommitConsole();

                try
                {
                    await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)), token);

                    LogToConsole("<b>Initiating Commit Selected...</b>");

                    bool cleanupOk = await CleanupWorkingCopy(root, token, "<b>[1/4]</b> Cleaning up working copy...");
                    if (!cleanupOk) return;
                    UpdateProgress(0.15f);

                    var missingRelPaths = selectedItems
                        .Where(e => e.Status == "!")
                        .Select(e => MakeRelative(root, e.FullPath))
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();

                    await ScheduleMissingForDeletion(root, missingRelPaths, token, "<b>[2/4]</b> Scheduling missing files for deletion...");
                    UpdateProgress(0.35f);

                    LogToConsole("<b>[3/4]</b> Synchronizing final commit tree...");

                    var selectedRelPathsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in selectedItems)
                    {
                        string relative = Path.IsPathRooted(item.FullPath)
                            ? MakeRelative(root, item.FullPath)
                            : item.FullPath;

                        string normalizedRelative = NormalizeRelativeTarget(relative);
                        if (!string.IsNullOrWhiteSpace(normalizedRelative))
                            selectedRelPathsSet.Add(normalizedRelative);
                        else
                            LogToConsole($"<color=#FF8800>[Warning] Invalid path skipped: {item.FullPath}</color>");
                    }

                    var finalTargets = InjectMissingParentPaths(selectedRelPathsSet, allElements);
                    var deleteStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "!", "D" };
                    var missingOnDisk = new List<string>();
                    var actualTargets = new List<string>();

                    foreach (var t in finalTargets)
                    {
                        string fullNative = Path.Combine(root, t.Replace('/', Path.DirectorySeparatorChar));
                        bool exists = File.Exists(fullNative) || Directory.Exists(fullNative);

                        var match = selectedItems.FirstOrDefault(e =>
                        {
                            string rel = Path.IsPathRooted(e.FullPath) ? MakeRelative(root, e.FullPath) : e.FullPath;
                            return string.Equals(NormalizeRelativeTarget(rel), t, StringComparison.OrdinalIgnoreCase);
                        });

                        bool isDelete = match != null && deleteStatuses.Contains(match.Status);

                        if (exists || isDelete)
                            actualTargets.Add(t);
                        else
                            missingOnDisk.Add(t);
                    }

                    if (missingOnDisk.Count > 0)
                    {
                        LogToConsole($"<color=#FFAA00>Warning: {missingOnDisk.Count} selected path(s) missing on disk — skipped:</color>");
                        foreach (var m in missingOnDisk.Take(10))
                            LogToConsole($"<color=#FFAA00> - {m}</color>");
                        if (missingOnDisk.Count > 10)
                            LogToConsole($"<color=#FFAA00> ... and {missingOnDisk.Count - 10} more</color>");
                    }

                    UpdateProgress(0.55f);

                    if (actualTargets.Count == 0)
                    {
                        LogToConsole("<color=yellow>No existing targets left to commit.</color>");
                        return;
                    }

                    LogToConsole($"<color=#4FC3F7>Final target set: {actualTargets.Count}</color>");
                    if (missingOnDisk.Count > 0)
                        LogToConsole($"<color=#FFAA00>Skipped missing on disk: {missingOnDisk.Count}</color>");

                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                            svnUI.CommitCurrentFileText.text = $"Queued: {actualTargets.Count} item(s)";
                    });

                    var injectedParents = actualTargets
                        .Where(t => !selectedRelPathsSet.Contains(t, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (injectedParents.Count > 0)
                    {
                        try
                        {
                            foreach (var dir in injectedParents)
                            {
                                string args = $"mkdir \"{dir.Replace("\"", "\\\"")}\"";
                                var psi = new ProcessStartInfo
                                {
                                    FileName = "svn",
                                    Arguments = args,
                                    WorkingDirectory = root,
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                };

                                using (var p = Process.Start(psi))
                                {
                                    p.StandardOutput.ReadToEnd();
                                    p.StandardError.ReadToEnd();
                                    p.WaitForExit(15000);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogToConsole($"<color=#FFAA00>[Warning] Failed to create parent directories: {ex.Message}</color>");
                        }
                    }

                    string addTargetsFile = Path.Combine(Path.GetTempPath(), $"svn_commit_add_{Guid.NewGuid():N}.txt");
                    try
                    {
                        var toAdd = actualTargets
                            .Where(t => !injectedParents.Contains(t, StringComparer.OrdinalIgnoreCase))
                            .Where(t =>
                            {
                                string fullNative = Path.Combine(root, t.Replace('/', Path.DirectorySeparatorChar));
                                return File.Exists(fullNative) || Directory.Exists(fullNative);
                            }).ToList();

                        if (toAdd.Count > 0)
                        {
                            await Task.Run(() => File.WriteAllLines(addTargetsFile, toAdd, new UTF8Encoding(false)), token);
                            await SvnRunner.RunAsync(
                                $"add --parents --force --targets \"{addTargetsFile}\"",
                                root, false, token);
                        }
                    }
                    finally
                    {
                        TryDeleteFile(addTargetsFile);
                    }

                    UpdateProgress(0.75f);

                    try
                    {
                        await CommitTargetsLiveAsync(root, actualTargets, msgFile, token);
                        UpdateProgress(1.0f);

                        SVNStatus.ClearLockCache();
                        svnManager.DiskChangesDetected = true;
                        statusModule.ClearCurrentData();

                        PostToMainThread(() =>
                        {
                            ClearCommitUI();
                            if (svnUI.CommitMessageInput != null)
                                svnUI.CommitMessageInput.text = "";
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        LogToConsole("<color=orange><b>[ABORTED]</b> Commit cancelled.</color>");
                    }
                    catch (Exception ex)
                    {
                        LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}");
                    }
                }
                finally
                {
                    ClearCommitCts(localCts);
                    SafeResetProgress();
                    TryDeleteFile(msgFile);
                    await SafeRefreshStatusAsync();
                }
            }
            finally
            {
                ExitProcessing();
            }
        }

        #endregion

        #region SVN Operations

        private async Task ScheduleMissingForDeletion(string root, IEnumerable<string> relativePaths, CancellationToken token, string stepLog = null)
        {
            if (!string.IsNullOrEmpty(stepLog))
                LogToConsole(stepLog);

            if (relativePaths == null)
            {
                LogToConsole("<color=green>No missing files to delete.</color>");
                return;
            }

            var sortedPaths = relativePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeRelativeTarget)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Length)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sortedPaths.Count == 0)
            {
                LogToConsole("<color=green>No missing files to delete.</color>");
                return;
            }

            var filteredDeletions = new List<string>();
            foreach (string path in sortedPaths)
            {
                bool isNested = filteredDeletions.Any(parent =>
                    path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase));

                if (!isNested)
                    filteredDeletions.Add(path);
            }

            if (filteredDeletions.Count == 0)
            {
                LogToConsole("<color=green>No missing files to delete.</color>");
                return;
            }

            LogToConsole($"Marking {filteredDeletions.Count} missing item(s) as deleted...");

            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_delete_{Guid.NewGuid():N}.txt");
            await Task.Run(() => File.WriteAllLines(targetsFile, filteredDeletions, new UTF8Encoding(false)), token);

            try
            {
                await SvnRunner.RunAsync($"delete --force --targets \"{targetsFile}\"", root, false, token);
            }
            finally
            {
                TryDeleteFile(targetsFile);
            }
        }

        #endregion

        #region Commit Process

        private async Task<string> CommitTargetsLiveAsync(string root, IEnumerable<string> targets, string msgFilePath, CancellationToken token)
        {
            var list = targets.Select(FormatPathForSvn)
                              .Where(x => !string.IsNullOrWhiteSpace(x))
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();

            if (list.Count == 0)
            {
                LogToConsole("<color=yellow>No valid commit targets.</color>");
                return string.Empty;
            }

            LogToConsole($"<b>[4/4]</b> Sending {list.Count} target(s) to server...");

            string first = list[0];
            PostToMainThread(() =>
            {
                if (svnUI?.CommitCurrentFileText != null)
                    svnUI.CommitCurrentFileText.text = first;
            });

            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_targets_{Guid.NewGuid():N}.txt");
            await Task.Run(() => File.WriteAllLines(targetsFile, list, new UTF8Encoding(false)), token);

            try
            {
                string command = $"commit --targets \"{targetsFile}\" -F \"{msgFilePath}\" --non-interactive";
                return await RunCommitProcessAsync(command, root, token);
            }
            finally
            {
                TryDeleteFile(targetsFile);
            }
        }

        private async Task<string> RunCommitProcessAsync(string command, string root, CancellationToken token)
        {
            string committedRevision = null;

            await SvnRunner.RunLiveAsync(command, root, line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;

                var match = CommittedRevisionRegex.Match(line);
                if (match.Success)
                    committedRevision = match.Groups[1].Value;

                ProcessCommitLiveLine(line);
            }, token);

            return committedRevision != null
                ? $"Committed revision {committedRevision}"
                : "Committed successfully.";
        }

        private void ProcessCommitLiveLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return;

            var segments = rawLine.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string rawSegment in segments)
            {
                string line = rawSegment.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("[SVN ERROR]", StringComparison.OrdinalIgnoreCase))
                    line = line["[SVN ERROR]".Length..].Trim();

                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Length > 3)
                {
                    const string decorative = "@*#=-_/\\|";
                    double decorativeRatio = (double)line.Count(c => decorative.Contains(c)) / line.Length;
                    if (decorativeRatio > 0.75) continue;
                }

                string lower = line.ToLowerInvariant();
                if (lower.Contains("restricted access") || lower.Contains("unauthorized access") ||
                    lower.Contains("prosecution") || lower.Contains("monitoring") ||
                    lower.Contains("by continuing, you consent") || lower.Contains("strictly prohibited") ||
                    lower.Contains("all activity on this system") || lower.Contains("warning! you are entering") ||
                    lower.Contains("you consent to monitoring") || lower.Contains("entering a restricted"))
                    continue;

                if (line.StartsWith("Sending ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Adding ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Deleting ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Replacing ", StringComparison.OrdinalIgnoreCase))
                {
                    int spaceIndex = line.IndexOf(' ');
                    if (spaceIndex < 0) continue;

                    string filePath = line[(spaceIndex + 1)..].Trim();
                    if (filePath.StartsWith("("))
                    {
                        int closeParen = filePath.IndexOf(')');
                        if (closeParen >= 0)
                            filePath = filePath[(closeParen + 1)..].Trim();
                    }

                    if (string.IsNullOrWhiteSpace(filePath)) continue;

                    string capturedPath = filePath;
                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                        {
                            svnUI.CommitCurrentFileText.text = capturedPath;
                            Canvas.ForceUpdateCanvases();
                        }
                    });

                    continue;
                }

                if (line.StartsWith("Transmitting file data", StringComparison.OrdinalIgnoreCase))
                {
                    LogToConsole("<color=#4FC3F7>Transmitting data...</color>");
                    continue;
                }

                if (line.StartsWith("Committing transaction", StringComparison.OrdinalIgnoreCase))
                {
                    LogToConsole("<color=#FFCC00><b>Finalizing commit...</b></color>");
                    continue;
                }

                if (line.StartsWith("Committed revision", StringComparison.OrdinalIgnoreCase))
                {
                    LogToConsole($"<color=green><b>[SUCCESS] {line}</b></color>");
                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                        {
                            svnUI.CommitCurrentFileText.text = "Done";
                            Canvas.ForceUpdateCanvases();
                        }
                    });
                    continue;
                }

                if (line.StartsWith("svn: E", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                {
                    LogToConsole($"<color=#FF4444><b>{line}</b></color>");
                }
            }
        }

        #endregion

        #region UI

        private void ClearCommitConsole() => PostToMainThread(() =>
        {
            if (svnUI?.CommitConsoleContent != null)
                svnUI.CommitConsoleContent.text = "";
        });

        private void ClearCommitUI() => PostToMainThread(() =>
        {
            svnUI?.SvnTreeView?.ClearView();
            svnUI?.SVNCommitTreeDisplay?.ClearView();

            if (svnUI?.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
            if (svnUI?.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
        });

        private void ShowProgressBar(float initialValue) => PostToMainThread(() =>
        {
            if (svnUI.OperationProgressBar == null) return;
            svnUI.OperationProgressBar.gameObject.SetActive(true);
            svnUI.OperationProgressBar.value = Mathf.Clamp01(initialValue);
        });

        private void UpdateProgress(float value) => PostToMainThread(() =>
        {
            if (svnUI.OperationProgressBar != null)
                svnUI.OperationProgressBar.value = Mathf.Clamp01(value);
        });

        private void SafeResetProgress() => PostToMainThread(() =>
        {
            if (svnUI.OperationProgressBar == null) return;
            svnUI.OperationProgressBar.value = 0f;
            svnUI.OperationProgressBar.gameObject.SetActive(false);

            if (svnUI.CommitCurrentFileText != null)
                svnUI.CommitCurrentFileText.text = "";
        });

        private void LogToConsole(string msg)
        {
            string normalized = msg?.Trim() ?? "";
            if (string.IsNullOrEmpty(normalized)) return;

            PostToMainThread(() =>
            {
                if (svnUI?.CommitConsoleContent != null)
                    SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, normalized + "\n", append: true);
            });
        }

        #endregion

        #region Status Refresh / Cleanup

        private async Task SafeRefreshStatusAsync()
        {
            try
            {
                var refreshTask = svnManager.RefreshStatus();
                var timeoutTask = Task.Delay(RefreshStatusTimeoutMs);
                await Task.WhenAny(refreshTask, timeoutTask);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Commit] Background refresh failed: {ex.Message}");
            }
        }

        private async Task<bool> CleanupWorkingCopy(string root, CancellationToken token, string stepLog = null)
        {
            if (!string.IsNullOrEmpty(stepLog))
                LogToConsole(stepLog);
            else
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
                LogToConsole("<color=#FF4444><b>[ABORTED]</b> Cleanup timed out. Commit aborted for safety.</color>");
                return false;
            }
        }

        #endregion

        #region Utility

        private string NormalizeRoot(string root) =>
            string.IsNullOrWhiteSpace(root) ? string.Empty : root.Replace('\\', '/').TrimEnd('/');

        private string NormalizeRelativeTarget(string path) =>
            string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Trim().Trim('/');

        private void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        #endregion

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