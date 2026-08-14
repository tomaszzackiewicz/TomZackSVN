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

        private const string DisplayStatuses = "MADR?!";
        private const string PreProcessStatuses = "MADR?!";
        private const string CommittableStatuses = "MADR";

        private static readonly Regex CommittedRevisionRegex = new(@"Committed revision\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SanitizeMessageRegex = new(@"[\uFEFF\u200B-\u200D\u202A-\u202E\u2060-\u2069\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        #region Path Helpers

        private string MakeRelative(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return null;
            string cleanRoot = root.Replace('\\', '/').TrimEnd('/');
            string cleanPath = path.Replace('\\', '/').TrimEnd('/');
            if (string.Equals(cleanPath, cleanRoot, StringComparison.OrdinalIgnoreCase)) return string.Empty;
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

        private List<string> ReduceCommitTargets(IEnumerable<string> targets)
        {
            if (targets == null) return new List<string>();
            var sorted = targets
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Replace('\\', '/').Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t) && !Path.IsPathRooted(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t.Length)
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new List<string>();
            foreach (string path in sorted)
            {
                bool isNested = result.Any(parent => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase));
                if (!isNested) result.Add(path);
            }
            return result;
        }

        private (HashSet<string> targets, bool includesTopLevelAddedFolder) ResolveRequiredTargets(
            HashSet<string> selectedRelPaths,
            Dictionary<string, (string status, string size)> currentStatusDict)
        {
            var requiredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasTopLevelAddedFolder = false;

            foreach (var selectedPath in selectedRelPaths)
            {
                string currentSegment = selectedPath;

                while (!string.IsNullOrWhiteSpace(currentSegment))
                {
                    if (requiredTargets.Contains(currentSegment)) break;

                    bool isTopLevel = !currentSegment.Contains("/");

                    if (currentStatusDict.TryGetValue(currentSegment, out var statusData) &&
                        CommittableStatuses.Contains(statusData.status))
                    {
                        if (isTopLevel && ("AR".Contains(statusData.status)))
                        {
                            hasTopLevelAddedFolder = true;
                        }

                        requiredTargets.Add(currentSegment);
                    }
                    else
                    {
                        break;
                    }

                    string parentDir = Path.GetDirectoryName(currentSegment)?.Replace('\\', '/').Trim();
                    if (string.IsNullOrWhiteSpace(parentDir)) break;

                    currentSegment = parentDir;
                }
            }

            return (requiredTargets, hasTopLevelAddedFolder);
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
            while (size >= BytesConversionFactor && order < suffixes.Length - 1) { order++; size /= BytesConversionFactor; }
            return $"{size:0.##} {suffixes[order]}";
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
            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root);
            var commitables = statusDict.Where(x => DisplayStatuses.Contains(x.Value.status)).ToList();
            var sb = new StringBuilder(commitables.Count * 48 + 64);
            sb.AppendLine("<b>Current working copy changes:</b>");
            foreach (var item in commitables) sb.AppendLine($"[{item.Value.status}] {item.Key}");
            PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: true));
        }

        public async void RefreshCommitList()
        {
            if (IsProcessing) return;
            try { await RefreshCommitListAsync(); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] RefreshCommitList failed: {ex.Message}"); }
        }

        private async Task RefreshCommitListAsync()
        {
            IsProcessing = true;
            try
            {
                string root = NormalizeRoot(svnManager.WorkingDir);
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root);
                _items = statusDict.Where(x => DisplayStatuses.Contains(x.Value.status))
                    .Select(x => new SVNStatusElement { FullPath = x.Key?.Replace('\\', '/'), Status = x.Value.status, IsChecked = true })
                    .Where(x => !string.IsNullOrWhiteSpace(x.FullPath))
                    .ToList();

                var localItems = _items;

                long totalSize = await Task.Run(() =>
                {
                    long size = 0;
                    foreach (var item in localItems)
                    {
                        if (!item.IsChecked || item.Status == "!" || item.Status == "D") continue;
                        string fullPhysicalPath = Path.Combine(root, item.FullPath.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(fullPhysicalPath)) continue;
                        try { size += new FileInfo(fullPhysicalPath).Length; } catch { }
                    }
                    return size;
                });
                RenderCommitList(_items, totalSize);
            }
            finally { IsProcessing = false; }
        }

        public void RenderCommitList(List<SVNStatusElement> items, long totalSize)
        {
            string uiOutput;
            if (items == null || items.Count == 0) uiOutput = "No changes to commit.";
            else
            {
                var sb = new StringBuilder(items.Count * 48 + 128);
                sb.AppendLine($"<b>Files to be committed</b> (Payload: <color=blue>{FormatCommitSize(totalSize)}</color>):");
                foreach (var item in items)
                {
                    string color = item.Status switch { "M" => "yellow", "A" => "green", "R" => "orange", "!" => "#FF4444", "?" => "#00E5FF", "D" => "red", _ => "white" };
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
            try
            {
                await RevertAllMissingAsync();
                LogToConsole("<color=green><b>[System]</b> Repair process finished.</color>");
            }
            catch (Exception ex) { LogToConsole($"<color=#FFAA00>Revert Error:</color> {ex.Message}"); }
        }

        private async Task RevertAllMissingAsync()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            string root = NormalizeRoot(svnManager.WorkingDir);
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
                        if (string.IsNullOrWhiteSpace(path)) continue;

                        string relative;
                        if (Path.IsPathRooted(path))
                            relative = MakeRelative(root, path);
                        else
                            relative = path;

                        relative = FormatPathForSvn(relative);
                        if (!string.IsNullOrWhiteSpace(relative)) filesToRevert.Add(relative);
                    }
                }
                if (filesToRevert.Count == 0) { LogToConsole("<color=green>No missing files found.</color>"); return; }
                LogToConsole($"Found {filesToRevert.Count} missing files. Restoring...");

                const int batchSize = 20;
                for (int i = 0; i < filesToRevert.Count; i += batchSize)
                {
                    var batch = filesToRevert.Skip(i).Take(batchSize).Select(p => $"\"{p.Replace("\"", "\\\"")}\"");
                    string command = "revert --depth infinity " + string.Join(" ", batch);
                    await SvnRunner.RunAsync(command, root);
                }

                var statusModule = svnManager.GetModule<SVNStatus>();
                statusModule?.ClearCurrentData();
                if (svnUI.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                if (svnUI.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                if (statusModule != null) await statusModule.ExecuteRefreshWithAutoExpand();
                LogToConsole("<color=green><b>SUCCESS!</b> Missing files restored.</color>");
            }
            finally { IsProcessing = false; }
        }

        #endregion

        #region Commit Entry Points

        public async void CommitSelected()
        {
            string rawMessage = svnUI.CommitMessageInput?.text;
            string message = SanitizeCommitMessage(rawMessage);
            if (string.IsNullOrWhiteSpace(message)) { LogToConsole("<color=#FFAA00>Error:</color> Please enter a commit message!"); return; }
            try { await ExecuteCommitSelected(message); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] CommitSelected failed: {ex.Message}"); }
        }

        public async void CommitAll()
        {
            if (IsProcessing) return;
            try { await CommitAllAsync(); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] CommitAll failed: {ex.Message}"); }
        }

        #endregion

        #region Commit All

        private async Task CommitAllAsync()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                string rawMessage = svnUI.CommitMessageInput?.text;
                string message = SanitizeCommitMessage(rawMessage);
                if (string.IsNullOrWhiteSpace(message)) { LogToConsole("<color=#FFAA00>Error:</color> Commit message is empty!"); return; }

                using CancellationTokenSource localCts = new CancellationTokenSource();
                _commitCTS = localCts;
                CancellationToken token = localCts.Token;
                string root = NormalizeRoot(svnManager.WorkingDir);
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                ShowProgressBar(0.05f);
                ClearCommitConsole();
                try
                {
                    await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)), token);
                    LogToConsole("<b>Initiating Commit All...</b>");

                    bool cleanupOk = await CleanupWorkingCopy(root, token);
                    if (!cleanupOk) return;
                    UpdateProgress(0.2f);

                    LogToConsole("<b>[1/2]</b> Indexing all new files...");
                    await SvnRunner.RunAsync("add --force .", root, false, token);
                    UpdateProgress(0.5f);

                    LogToConsole("<b>[2/2]</b> Sending to server...");
                    string command = $"commit -F \"{msgFile}\" --non-interactive .";

                    try
                    {
                        await RunCommitProcessAsync(command, root, token);

                        UpdateProgress(1.0f);
                        SVNStatus.ClearLockCache();
                        svnManager.DiskChangesDetected = true;

                        PostToMainThread(() =>
                        {
                            ClearCommitUI();
                            if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
                        });
                    }
                    catch (OperationCanceledException) { LogToConsole("<color=orange><b>[ABORTED]</b> User cancelled.</color>"); }
                    catch (Exception ex) { LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}"); }
                }
                finally
                {
                    ClearCommitCts(localCts);
                    SafeResetProgress();
                    TryDeleteFile(msgFile);
                    await SafeRefreshStatusAsync();
                }
            }
            finally { IsProcessing = false; }
        }

        #endregion

        #region Commit Selected

        public async Task ExecuteCommitSelected(string message)
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                string root = NormalizeRoot(svnManager.WorkingDir);
                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule == null) { LogToConsole("<color=#FFAA00>Error:</color> SVN Status module not found."); return; }

                var allElements = statusModule.GetCurrentData();
                var selectedItems = allElements?.Where(e => e.IsChecked).ToList() ?? new List<SvnTreeElement>();
                if (allElements == null || allElements.Count == 0) { LogToConsole("<color=yellow>No SVN changes detected.</color>"); return; }
                if (selectedItems.Count == 0) { LogToConsole("<color=orange>Nothing selected for commit.</color>"); return; }

                selectedItems = selectedItems.Where(e => PreProcessStatuses.Contains(e.Status)).ToList();
                if (selectedItems.Count == 0) { LogToConsole("<color=yellow>No valid files to commit.</color>"); return; }

                using CancellationTokenSource localCts = new CancellationTokenSource();
                _commitCTS = localCts;
                CancellationToken token = localCts.Token;
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                ShowProgressBar(0.05f);
                ClearCommitConsole();
                try
                {
                    await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)), token);
                    LogToConsole("<b>Initiating Commit Selected...</b>");

                    bool cleanupOk = await CleanupWorkingCopy(root, token);
                    if (!cleanupOk) return;
                    UpdateProgress(0.15f);

                    var missingRelPaths = selectedItems.Where(e => e.Status == "!")
                        .Select(e => MakeRelative(root, e.FullPath)).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                    await ScheduleMissingForDeletion(root, missingRelPaths, token);
                    UpdateProgress(0.35f);

                    var newRelPaths = selectedItems.Where(e => e.Status == "?")
                        .Select(e => MakeRelative(root, e.FullPath)).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                    await AddNewFiles(root, newRelPaths, token);
                    UpdateProgress(0.55f);

                    LogToConsole("<b>[3/4]</b> Synchronizing final commit tree...");
                    var currentStatusDict = await SvnRunner.GetFullStatusDictionaryAsync(root);

                    var selectedRelPathsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in selectedItems)
                    {
                        string relative;
                        if (Path.IsPathRooted(item.FullPath))
                            relative = MakeRelative(root, item.FullPath);
                        else
                            relative = item.FullPath;

                        string normalizedRelative = NormalizeRelativeTarget(relative);

                        if (!string.IsNullOrWhiteSpace(normalizedRelative))
                            selectedRelPathsSet.Add(normalizedRelative);
                        else
                            LogToConsole($"<color=#FF8800>[Warning] Invalid path skipped: {item.FullPath}</color>");
                    }

                    var (rawFinalTargets, includesTopLevelAddedFolder) = ResolveRequiredTargets(selectedRelPathsSet, currentStatusDict);

                    if (includesTopLevelAddedFolder)
                    {
                        var topFolders = rawFinalTargets.Where(t => !t.Contains("/") && currentStatusDict.TryGetValue(t, out var s) && "AR".Contains(s.status));
                        foreach (var folder in topFolders)
                        {
                            LogToConsole($" <color=#FFD700>[WARNING]</color> To commit the selected file(s), the new folder <b>{folder}</b> must also be committed. All files inside it will be included in this commit.");
                        }
                    }

                    var finalTargets = ReduceCommitTargets(rawFinalTargets);
                    UpdateProgress(0.7f);

                    if (finalTargets.Count == 0) { LogToConsole("<color=yellow>Filtered targets resulted in 0 items to commit.</color>"); return; }
                    LogToConsole($"<color=#AAAAAA>Final target set: {finalTargets.Count}</color>");

                    try
                    {
                        await CommitTargetsLiveAsync(root, finalTargets, msgFile, token);

                        UpdateProgress(1.0f);
                        SVNStatus.ClearLockCache();
                        svnManager.DiskChangesDetected = true;
                        statusModule.ClearCurrentData();

                        PostToMainThread(() =>
                        {
                            ClearCommitUI();
                            if (svnUI.CommitMessageInput != null) svnUI.CommitMessageInput.text = "";
                        });
                    }
                    catch (OperationCanceledException) { LogToConsole("<color=orange><b>[ABORTED]</b> Commit cancelled.</color>"); }
                    catch (Exception ex) { LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}"); }
                }
                finally
                {
                    ClearCommitCts(localCts);
                    SafeResetProgress();
                    TryDeleteFile(msgFile);
                    await SafeRefreshStatusAsync();
                }
            }
            finally { IsProcessing = false; }
        }

        #endregion

        #region SVN Operations

        private async Task ScheduleMissingForDeletion(string root, IEnumerable<string> relativePaths, CancellationToken token)
        {
            LogToConsole("<b>[2/4]</b> Scheduling missing files for deletion...");
            if (relativePaths == null) { LogToConsole("<color=green>No missing files to delete.</color>"); return; }
            var sortedPaths = relativePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeRelativeTarget)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Length)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sortedPaths.Count == 0) { LogToConsole("<color=green>No missing files to delete.</color>"); return; }
            var filteredDeletions = new List<string>();
            foreach (string path in sortedPaths)
            {
                bool isNested = filteredDeletions.Any(parent => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase));
                if (!isNested) filteredDeletions.Add(path);
            }
            if (filteredDeletions.Count == 0) { LogToConsole("<color=green>No missing files to delete.</color>"); return; }
            LogToConsole($"Marking {filteredDeletions.Count} missing item(s) as deleted...");
            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_delete_{Guid.NewGuid():N}.txt");
            await Task.Run(() => File.WriteAllLines(targetsFile, filteredDeletions, new UTF8Encoding(false)), token);
            try { await SvnRunner.RunAsync($"delete --force --targets \"{targetsFile}\"", root, false, token); }
            finally { TryDeleteFile(targetsFile); }
        }

        private async Task AddNewFiles(string root, IEnumerable<string> relativePaths, CancellationToken token)
        {
            LogToConsole("<b>[3/4]</b> Indexing new files...");
            if (relativePaths == null) { LogToConsole("<color=green>No new files to add.</color>"); return; }
            var rawAdditions = relativePaths.Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeRelativeTarget).Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (rawAdditions.Count == 0) { LogToConsole("<color=green>No new files to add.</color>"); return; }

            var validAdditions = new List<string>();
            await Task.Run(() =>
            {
                foreach (string relPath in rawAdditions)
                {
                    string fullPhysicalPath = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullPhysicalPath) || Directory.Exists(fullPhysicalPath)) validAdditions.Add(relPath);
                }
            }, token);
            if (validAdditions.Count == 0) { LogToConsole("<color=green>No existing new files to add.</color>"); return; }

            var minimalAddTargets = ReduceCommitTargets(validAdditions);
            if (minimalAddTargets.Count == 0) { LogToConsole("<color=yellow>No valid add targets remained.</color>"); return; }

            LogToConsole($"Adding {minimalAddTargets.Count} target(s) to SVN index...");
            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_add_{Guid.NewGuid():N}.txt");
            var formattedTargets = minimalAddTargets.Select(FormatPathForSvn).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            await Task.Run(() => File.WriteAllLines(targetsFile, formattedTargets, new UTF8Encoding(false)), token);
            try { await SvnRunner.RunAsync($"add --force --parents --targets \"{targetsFile}\"", root, false, token); }
            finally { TryDeleteFile(targetsFile); }
        }

        #endregion

        #region Commit Process

        private async Task<string> CommitTargetsLiveAsync(string root, IEnumerable<string> targets, string msgFilePath, CancellationToken token)
        {
            var list = targets.Select(FormatPathForSvn).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) { LogToConsole("<color=yellow>No valid commit targets.</color>"); return string.Empty; }

            LogToConsole($"<b>[4/4]</b> Sending {list.Count} target(s) to server...");
            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_targets_{Guid.NewGuid():N}.txt");
            await Task.Run(() => File.WriteAllLines(targetsFile, list, new UTF8Encoding(false)), token);
            try
            {
                string command = $"commit --targets \"{targetsFile}\" -F \"{msgFilePath}\" --non-interactive";
                return await RunCommitProcessAsync(command, root, token);
            }
            finally { TryDeleteFile(targetsFile); }
        }

        private async Task<string> RunCommitProcessAsync(string command, string root, CancellationToken token)
        {
            string committedRevision = null;

            await SvnRunner.RunLiveAsync(command, root, line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;

                var match = CommittedRevisionRegex.Match(line);
                if (match.Success)
                {
                    committedRevision = match.Groups[1].Value;
                }

                ProcessCommitLiveLine(line);
            }, token);

            return committedRevision != null
                ? $"Committed revision {committedRevision}"
                : "Committed successfully.";
        }

        private void ProcessCommitLiveLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return;
            string line = rawLine.Replace("\r", "").Trim();
            if (line.StartsWith("[SVN ERROR]", StringComparison.OrdinalIgnoreCase))
                line = line["[SVN ERROR]".Length..].Trim();
            if (string.IsNullOrWhiteSpace(line)) return;

            if (line.Length > 3)
            {
                const string decorative = "@*#=-_/\\|";
                double decorativeRatio = (double)line.Count(c => decorative.Contains(c)) / line.Length;
                if (decorativeRatio > 0.75) return;
            }

            string lower = line.ToLowerInvariant();
            if (lower.Contains("restricted access") || lower.Contains("unauthorized access") || lower.Contains("prosecution") ||
                lower.Contains("monitoring") || lower.Contains("by continuing, you consent") || lower.Contains("strictly prohibited") ||
                lower.Contains("all activity on this system") || lower.Contains("warning! you are entering") ||
                lower.Contains("you consent to monitoring") || lower.Contains("entering a restricted")) return;

            if (line.StartsWith("Sending ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Adding ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Deleting ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Replacing ", StringComparison.OrdinalIgnoreCase))
            {
                string filePath = line.Substring(line.IndexOf(' ') + 1).Trim();

                if (filePath.StartsWith("("))
                {
                    int closeParen = filePath.IndexOf(')');
                    if (closeParen != -1)
                    {
                        filePath = filePath.Substring(closeParen + 1).Trim();
                    }
                }

                PostToMainThread(() =>
                {
                    if (svnUI?.CommitCurrentFileText != null)
                    {
                        svnUI.CommitCurrentFileText.text = filePath;
                    }
                });
                return;
            }

            if (line.StartsWith("Transmitting file data", StringComparison.OrdinalIgnoreCase)) { LogToConsole("<color=#AAAAAA>Transmitting data...</color>"); return; }
            if (line.StartsWith("Committing transaction", StringComparison.OrdinalIgnoreCase)) { LogToConsole("<color=#FFCC00><b>Finalizing commit...</b></color>"); return; }
            if (line.StartsWith("Committed revision", StringComparison.OrdinalIgnoreCase)) { LogToConsole($"<color=green><b>[SUCCESS] {line}</b></color>"); return; }
            if (line.StartsWith("svn: E", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            { LogToConsole($"<color=#FF4444><b>{line}</b></color>"); }
        }

        #endregion

        #region UI

        private void ClearCommitConsole() => PostToMainThread(() => { if (svnUI?.CommitConsoleContent != null) svnUI.CommitConsoleContent.text = ""; });
        private void ClearCommitUI() => PostToMainThread(() =>
        {
            svnUI?.SvnTreeView?.ClearView();
            svnUI?.SVNCommitTreeDisplay?.ClearView();
            if (svnUI?.TreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
            if (svnUI?.CommitTreeDisplay != null) SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
        });

        private void ShowProgressBar(float initialValue) => PostToMainThread(() =>
        {
            if (svnUI.OperationProgressBar == null) return;
            svnUI.OperationProgressBar.gameObject.SetActive(true);
            svnUI.OperationProgressBar.value = Mathf.Clamp01(initialValue);
        });

        private void UpdateProgress(float value) => PostToMainThread(() => { if (svnUI.OperationProgressBar != null) svnUI.OperationProgressBar.value = Mathf.Clamp01(value); });

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
            PostToMainThread(() => { if (svnUI?.CommitConsoleContent != null) SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, normalized + "\n", append: true); });
        }

        #endregion

        #region Status Refresh / Cleanup

        private async Task SafeRefreshStatusAsync()
        {
            try { var refreshTask = svnManager.RefreshStatus(); var timeoutTask = Task.Delay(RefreshStatusTimeoutMs); await Task.WhenAny(refreshTask, timeoutTask); }
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
                LogToConsole("<color=#FF4444><b>[ABORTED]</b> Cleanup timed out. Commit aborted for safety.</color>");
                return false;
            }
        }

        #endregion

        #region Utility

        private string NormalizeRoot(string root) => string.IsNullOrWhiteSpace(root) ? string.Empty : root.Replace('\\', '/').TrimEnd('/');
        private string NormalizeRelativeTarget(string path) => string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Trim().Trim('/');
        private void ClearCommitCts(CancellationTokenSource localCts) { try { if (ReferenceEquals(_commitCTS, localCts)) _commitCTS = null; } finally { localCts.Dispose(); } }
        private void TryDeleteFile(string path) { if (string.IsNullOrWhiteSpace(path)) return; try { if (File.Exists(path)) File.Delete(path); } catch { } }

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