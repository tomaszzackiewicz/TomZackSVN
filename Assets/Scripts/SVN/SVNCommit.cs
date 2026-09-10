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
    public class SVNCommit : SVNBase, IDisposable
    {
        private CancellationTokenSource _commitCTS;
        private int _processingFlag;
        private int _disposed;

        // === Liczniki plików na żywo (dla CommitCurrentFileText i paska)
        private int _commitFilesProcessed;
        private int _commitFilesTotal;

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

        #region Processing Guard / Dispose

        private bool TryEnterProcessing()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;

            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
            {
                LogToConsole("<color=#8B0000>>Another commit operation is already running.</color>");
                return false;
            }

            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        // === FIX: IDisposable — spójność z SVNBlame/SVNDiff/SVNBranchTag/SVNMerge/
        // SVNStatus (manager dysponuje tylko moduły IDisposable). Cancel + delayed
        // dispose przez SVNShared (natychmiastowy rzucił ODE w locie operacji).
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _commitCTS, null));
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
                    LogToConsole("<color=#8B0000>><b>[System]</b> Cancellation requested...</color>");
                }
            }
            catch (ObjectDisposedException) { }
        }

        private void ClearCommitCts(CancellationTokenSource localCts)
        {
            if (Interlocked.CompareExchange(ref _commitCTS, null, localCts) == localCts)
            {
                _ = Task.Delay(1000).ContinueWith(_ => { try { localCts.Dispose(); } catch { } });
            }
        }

        #endregion

        #region Conflict Pre-Resolve (E155015 fix)

        /// === FIX E155015: auto-resolve tree-conflicts przed commit.
        /// Po merge/revert-marge SVN zostawia tree-conflicts (postpone) które
        /// blokują commit. Wykrywa konflikty we WSZYSTKICH kolumnach (0/1/6)
        /// i rozwiązuje automatycznie (accept working — użytkownik podjął decyzję
        /// przez merge/revert).
        private async Task<int> AutoResolveConflictsAsync(string root, CancellationToken token)
        {
            try
            {
                string statusOutput = await SvnRunner.RunAsync("status", root, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(statusOutput)) return 0;

                var conflictedPaths = new List<string>();

                foreach (string rawLine in statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (rawLine.Length < 8) continue;

                    // Kolumna 0 = item conflict ('C'), 1 = property conflict ('C'),
                    // 6 = tree conflict ('C') — pełne wykrywanie
                    char itemStatus = rawLine[0];
                    char propStatus = rawLine.Length > 1 ? rawLine[1] : ' ';
                    char treeConflict = rawLine.Length > 6 ? rawLine[6] : ' ';

                    if (itemStatus != 'C' && propStatus != 'C' && treeConflict != 'C') continue;

                    string path = rawLine.Substring(8).Trim();
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    conflictedPaths.Add(path);
                }

                conflictedPaths = conflictedPaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (conflictedPaths.Count == 0) return 0;

                LogToConsole($"<color=yellow>Found {conflictedPaths.Count} unresolved conflict(s) — auto-resolving (accept working)...</color>");

                // === batch TYLKO przy >1 konflikcie — dla pojedynczego zapisywanie
                // targets-file to narzut (1 proces i tak by rozwiązał 1 ścieżkę)
                if (conflictedPaths.Count > 1)
                {
                    string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_resolve_{Guid.NewGuid():N}.txt");
                    try
                    {
                        await Task.Run(() => File.WriteAllLines(targetsFile, conflictedPaths, new UTF8Encoding(false)), token)
                                  .ConfigureAwait(false);

                        await SvnRunner.RunAsync(
                            $"resolve --accept working --targets \"{targetsFile}\"",
                            root, true, token).ConfigureAwait(false);

                        foreach (var p in conflictedPaths)
                            LogToConsole($"<color=green>Resolved:</color> {p}");

                        return conflictedPaths.Count;
                    }
                    // === FIX (KRYTYCZNY): bez tego catcha cancel w trakcie batcha wpadał
                    // do "Batch resolve failed... Retrying individually" → pętla per-path
                    // z martwym tokenem spamowała "Failed to resolve X: canceled" per
                    // plik, zamiast trafić do "[ABORTED] User cancelled" w commit flow.
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogToConsole($"<color=#FFAA00>Batch resolve failed ({ex.Message}). Retrying individually...</color>");
                    }
                    finally
                    {
                        try { if (File.Exists(targetsFile)) File.Delete(targetsFile); } catch { }
                    }
                }

                // per-path (pojedynczy konflikt albo fallback po porażce batcha)
                int resolved = 0;
                foreach (var cpath in conflictedPaths)
                {
                    try
                    {
                        await SvnRunner.RunAsync(
                            $"resolve --accept working \"{cpath}\"",
                            root, true, token).ConfigureAwait(false);
                        resolved++;
                        LogToConsole($"<color=green>Resolved:</color> {cpath}");
                    }
                    // === FIX: jw. — cancel ≠ "Failed to resolve X"
                    catch (OperationCanceledException) { throw; }
                    catch (Exception singleEx)
                    {
                        LogToConsole($"<color=#FFAA00>Failed to resolve {cpath}: {singleEx.Message}</color>");
                    }
                }

                return resolved;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return 0; // non-fatal — commit sam zgłosi jeśli konflikt nadal blokuje
            }
        }

        #endregion

        #region Preview / List

        public async void ShowWhatWillBeCommitted()
        {
            if (!TryEnterProcessing()) return;
            try { await ShowWhatWillBeCommittedAsync(); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Commit] ShowWhatWillBeCommitted failed: {ex.Message}"); }
            finally { ExitProcessing(); }
        }

        private async Task ShowWhatWillBeCommittedAsync()
        {
            string root = NormalizeRoot(svnManager.WorkingDir);
            if (string.IsNullOrWhiteSpace(root)) return;

            var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root);
            var commitables = statusDict.Where(x => DisplayStatuses.Contains(x.Value.status ?? "")).ToList();

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

            var items = statusDict
                .Where(x => DisplayStatuses.Contains(x.Value.Status ?? ""))
                .Select(x => new SvnTreeElement
                {
                    FullPath = x.Key?.Replace('\\', '/'),
                    Status = x.Value.Status,
                    IsChecked = true
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.FullPath))
                .ToList();

            long totalSize = await Task.Run(() =>
            {
                long size = 0;

                foreach (var item in items)
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

            RenderCommitList(items, totalSize);
        }

        public void RenderCommitList(List<SvnTreeElement> items, long totalSize)
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
                        "R" => "<color=#8B0000>",
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

            PostToMainThread(() =>
            {
                if (svnUI.TreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);
                if (svnUI.CommitTreeDisplay != null)
                    SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
            });

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

                var localCts = new CancellationTokenSource();
                // === FIX: SVNShared zamiast natychmiastowego Dispose starego CTS
                SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _commitCTS, localCts));

                CancellationToken token = localCts.Token;
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                // === FIX (pasek): NIE pokazujemy na starcie — kroki [1/4]–[3/4]
                // (cleanup/delete/add/resolve) raportują w konsoli; pasek pojawia
                // się dopiero przy faktycznym wysyłaniu i jest napędzany postępem plików.
                ClearCommitConsole();

                try
                {
                    await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)), token);

                    LogToConsole("<b>Initiating Commit All...</b>");

                    bool cleanupOk = await CleanupWorkingCopy(root, token, "<b>[1/4]</b> Cleaning up working copy...");
                    if (!cleanupOk) return;

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
                            await SvnRunner.RunAsync($"add --parents --targets \"{addTargetsFile}\"", root, true, token);
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

                    // === FIX E155015: auto-resolve tree-conflicts PRZED commit
                    int conflictsResolved = await AutoResolveConflictsAsync(root, token).ConfigureAwait(false);
                    if (conflictsResolved > 0)
                    {
                        LogToConsole($"<color=green>Resolved {conflictsResolved} conflict(s). Proceeding with commit...</color>");
                    }

                    LogToConsole("<b>[4/4]</b> Sending to server...");
                    Interlocked.Exchange(ref _commitFilesProcessed, 0);
                    // === FIX (pasek): przybliżona liczba plików do wysłania (z pre-add
                    // statusu; add --parents rozwinie foldery "?", więc count może
                    // zaniżać — pasek wskaźnikowy, i tak domyka się na 1.0 przy sukcesie)
                    int commitableCount = currentStatus.Count(x =>
                        !string.IsNullOrEmpty(x.Value.status) && "MADR?".Contains(x.Value.status));
                    Interlocked.Exchange(ref _commitFilesTotal, commitableCount);

                    // === pasek pojawia się dopiero przy WŁAŚCIWYM commicie
                    ShowProgressBar(0f);

                    string command = $"commit --no-unlock -F \"{msgFile}\" --non-interactive .";

                    try
                    {
                        await RunCommitProcessAsync(command, root, token);
                        UpdateProgress(1.0f);

                        SVNStatus.ClearLockCache();
                        svnManager.DiskChangesDetected = true;

                        var statusModuleAfter = svnManager.GetModule<SVNStatus>();
                        statusModuleAfter?.ClearCurrentData();

                        PostToMainThread(() =>
                        {
                            ClearCommitUI();
                            if (svnUI.CommitMessageInput != null)
                                svnUI.CommitMessageInput.text = "";
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        LogToConsole("<color=#8B0000>><b>[ABORTED]</b> User cancelled.</color>");
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("E155015"))
                        {
                            LogToConsole("<color=#FFAA00><b>[BLOCKED]</b> Commit failed — unresolved conflict(s) remain.</color>");
                            LogToConsole("<color=#FFAA00>Use the Resolve panel or Terminal (svn resolve) to fix them, then retry.</color>");
                        }
                        else
                        {
                            LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}");
                        }
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
                            // === FIX E200009: 'A'-rodzice też muszą być w targets
                            if (parentEl.Status == "?" || parentEl.Status == "!" || parentEl.Status == "A")
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
            // === FIX (obronny): publiczne API — sanitizacja też tutaj
            // (callerzy zewnętrzni mogą podać surowy message)
            message = SanitizeCommitMessage(message);
            if (string.IsNullOrWhiteSpace(message))
            {
                LogToConsole("<color=#FFAA00>Error:</color> Commit message is empty!");
                return;
            }

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
                    LogToConsole("<color=#8B0000>>Nothing selected for commit.</color>");
                    return;
                }

                selectedItems = selectedItems.Where(e => PreProcessStatuses.Contains(e.Status ?? "")).ToList();
                if (selectedItems.Count == 0)
                {
                    LogToConsole("<color=yellow>No valid files to commit.</color>");
                    return;
                }

                var localCts = new CancellationTokenSource();
                // === FIX: SVNShared zamiast natychmiastowego Dispose starego CTS
                SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _commitCTS, localCts));

                CancellationToken token = localCts.Token;
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                // === FIX (pasek): ukryty do momentu faktycznego commitu
                ClearCommitConsole();

                try
                {
                    await Task.Run(() => File.WriteAllText(msgFile, message, new UTF8Encoding(false)), token);

                    LogToConsole("<b>Initiating Commit Selected...</b>");

                    bool cleanupOk = await CleanupWorkingCopy(root, token, "<b>[1/4]</b> Cleaning up working copy...");
                    if (!cleanupOk) return;

                    var missingRelPaths = selectedItems
                        .Where(e => e.Status == "!")
                        .Select(e => Path.IsPathRooted(e.FullPath) ? MakeRelative(root, e.FullPath) : e.FullPath)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();

                    await ScheduleMissingForDeletion(root, missingRelPaths, token, "<b>[2/4]</b> Scheduling missing files for deletion...");

                    LogToConsole("<b>[3/4]</b> Synchronizing final commit tree...");

                    // === FIX (.svn-root — GŁÓWNA poprawka) + O(n):
                    // Element "[Repository Root Change]" ma SZTUCZNĄ ścieżkę ".svn-root",
                    // która nie istnieje na dysku → pętla exists||isDelete wysyłała ją do
                    // missingOnDisk → zmiana roota (svn:mergeinfo po mergu!) NIGDY nie
                    // szła do commitu przez Selected → SVN przy następnym merge
                    // próbował mergować te same rewizje ponownie.
                    // Mapowanie na ".": katalog roota istnieje, a "commit --depth
                    // empty" z "." w targets commituje dokładnie property-change roota.
                    // Słownik zamiast O(n²) FirstOrDefault (duży commit = miliony
                    // porównań stringów przy normalizacji w pętli).
                    var selectedByPath = new Dictionary<string, SvnTreeElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in selectedItems)
                    {
                        string rel = Path.IsPathRooted(item.FullPath)
                            ? MakeRelative(root, item.FullPath)
                            : item.FullPath;

                        if (string.Equals(rel, ".svn-root", StringComparison.Ordinal))
                            rel = ".";

                        string norm = NormalizeRelativeTarget(rel);
                        if (string.IsNullOrWhiteSpace(norm))
                        {
                            LogToConsole($"<color=#FF8800>[Warning] Invalid path skipped: {item.FullPath}</color>");
                            continue;
                        }

                        if (!selectedByPath.ContainsKey(norm))
                            selectedByPath[norm] = item;
                    }

                    if (selectedByPath.Count == 0)
                    {
                        LogToConsole("<color=yellow>No valid commit paths.</color>");
                        return;
                    }

                    var finalTargets = InjectMissingParentPaths(
                        new HashSet<string>(selectedByPath.Keys, StringComparer.OrdinalIgnoreCase),
                        allElements);

                    var deleteStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "!", "D" };
                    var missingOnDisk = new List<string>();
                    var actualTargets = new List<string>();

                    foreach (var t in finalTargets)
                    {
                        string fullNative = Path.Combine(root, t.Replace('/', Path.DirectorySeparatorChar));
                        bool exists = File.Exists(fullNative) || Directory.Exists(fullNative);

                        selectedByPath.TryGetValue(t, out var match);
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

                    if (actualTargets.Count == 0)
                    {
                        LogToConsole("<color=yellow>No existing targets left to commit.</color>");
                        return;
                    }

                    var fileTargets = actualTargets
                        .Where(t => !Directory.Exists(Path.Combine(root, t.Replace('/', Path.DirectorySeparatorChar))))
                        .ToList();
                    var dirTargets = actualTargets.Except(fileTargets).ToList();

                    LogToConsole($"<color=#4FC3F7>Commit targets: {fileTargets.Count} file(s)" +
                        (dirTargets.Count > 0 ? $" + {dirTargets.Count} folder(s) [structure]" : "") + $"</color>");

                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                            svnUI.CommitCurrentFileText.text = $"Queued: {fileTargets.Count} file(s)";
                    });

                    string addTargetsFile = Path.Combine(Path.GetTempPath(), $"svn_commit_add_{Guid.NewGuid():N}.txt");
                    try
                    {
                        // === FIX: lookup przez słownik (było FirstOrDefault w pętli)
                        var toAdd = actualTargets
                            .Where(t => selectedByPath.TryGetValue(t, out var el) && el.Status == "?")
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
                                root, true, token);
                        }
                    }
                    finally
                    {
                        TryDeleteFile(addTargetsFile);
                    }

                    // === FIX E155015: auto-resolve przed commit też w Selected
                    int conflictsResolved = await AutoResolveConflictsAsync(root, token).ConfigureAwait(false);
                    if (conflictsResolved > 0)
                        LogToConsole($"<color=green>Resolved {conflictsResolved} conflict(s).</color>");

                    // === FIX (pasek): pojawia się dopiero teraz — napędzany postępem
                    // plików (total = fileTargets.Count, ustawiane w CommitTargetsLiveAsync)
                    ShowProgressBar(0f);

                    try
                    {
                        await CommitTargetsLiveAsync(root, actualTargets, msgFile, token, fileTargets.Count);
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
                        LogToConsole("<color=#8B0000>><b>[ABORTED]</b> Commit cancelled.</color>");
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("E155015"))
                        {
                            LogToConsole("<color=#FFAA00><b>[BLOCKED]</b> Commit failed — unresolved conflict(s) remain.</color>");
                        }
                        else
                        {
                            LogToConsole($"<color=#FFAA00>Error:</color> {ex.Message}");
                        }
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
                await SvnRunner.RunAsync($"delete --force --targets \"{targetsFile}\"", root, true, token);
            }
            finally
            {
                TryDeleteFile(targetsFile);
            }
        }

        #endregion

        #region Commit Process

        private async Task<string> CommitTargetsLiveAsync(string root, IEnumerable<string> targets, string msgFilePath, CancellationToken token, int fileCount = 0)
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

            Interlocked.Exchange(ref _commitFilesProcessed, 0);
            Interlocked.Exchange(ref _commitFilesTotal, fileCount);

            LogToConsole($"<b>[4/4]</b> Sending {list.Count} target(s) to server...");

            string first = list.FirstOrDefault(t => !Directory.Exists(Path.Combine(root, t.Replace('/', Path.DirectorySeparatorChar)))) ?? list[0];
            PostToMainThread(() =>
            {
                if (svnUI?.CommitCurrentFileText != null)
                    svnUI.CommitCurrentFileText.text = first;
            });

            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_targets_{Guid.NewGuid():N}.txt");
            await Task.Run(() => File.WriteAllLines(targetsFile, list, new UTF8Encoding(false)), token);

            try
            {
                string command = $"commit --depth empty --no-unlock --targets \"{targetsFile}\" -F \"{msgFilePath}\" --non-interactive";
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

                    string action = line.Substring(0, spaceIndex).Trim();
                    string actionSymbol = action switch
                    {
                        "Sending" => "↑",
                        "Adding" => "+",
                        "Deleting" => "−",
                        "Replacing" => "↻",
                        _ => "•"
                    };

                    int current = Interlocked.Increment(ref _commitFilesProcessed);

                    string capturedPath = filePath;
                    string capturedSymbol = actionSymbol;
                    int capturedCount = current;
                    int capturedTotal = Volatile.Read(ref _commitFilesTotal);

                    // === FIX (pasek): napędzany postępem plików — count/total.
                    // Pasek jest aktywny tylko w fazie commitu; UpdateProgress
                    // clamps, więc nadmiar (approx total w All) nie szkodzi.
                    if (capturedTotal > 0)
                        UpdateProgress((float)capturedCount / capturedTotal);

                    // === FIX: bez Canvas.ForceUpdateCanvases() — globalny, drogi
                    // wywoł na KAŻDEJ linii commitowanego pliku; Unity odświeży
                    // canvas w bieżącej klatce samo.
                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                        {
                            string progressPart = capturedTotal > 0
                                ? $"[{capturedCount}/{capturedTotal}] "
                                : $"[{capturedCount}] ";

                            svnUI.CommitCurrentFileText.text = $"{capturedSymbol} {progressPart}{capturedPath}";
                        }
                    });

                    continue;
                }

                if (line.StartsWith("Transmitting file data", StringComparison.OrdinalIgnoreCase))
                {
                    LogToConsole("<color=#4FC3F7>Transmitting data...</color>");
                    // === FIX: tekst fazy wysyłki — bez tego tekst stawał na ostatnim
                    // spakowanym pliku przez cały czas transmisji (mylące: "ten plik
                    // tak długo?"). Sending = pakowanie, to = faktyczne wysyłanie.
                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                            svnUI.CommitCurrentFileText.text = "⇪ Uploading data to server...";
                    });
                    continue;
                }

                if (line.StartsWith("Committing transaction", StringComparison.OrdinalIgnoreCase))
                {
                    LogToConsole("<color=#FFCC00><b>Finalizing commit...</b></color>");
                    continue;
                }

                if (line.StartsWith("Committed revision", StringComparison.OrdinalIgnoreCase))
                {
                    int totalProcessed = Interlocked.CompareExchange(ref _commitFilesProcessed, 0, 0);
                    LogToConsole($"<color=green><b>[SUCCESS] {line}</b></color>");
                    if (totalProcessed > 0)
                        LogToConsole($"<color=green>Files committed: <b>{totalProcessed}</b></color>");

                    UpdateProgress(1.0f);

                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                            svnUI.CommitCurrentFileText.text = "Done";
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

                if (refreshTask.IsFaulted)
                {
                    SVNLogBridge.LogError($"[Commit] Background refresh failed: {refreshTask.Exception?.GetBaseException().Message}");
                }
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
    }
}