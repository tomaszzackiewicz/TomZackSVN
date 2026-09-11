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

        private int _commitFilesProcessed;
        private int _commitFilesTotal;

        private int _commitAddedCount;
        private int _commitDeletedCount;
        private int _commitUpdatedCount;
        private int _commitReplacedCount;

        private readonly List<long> _sendingFileSizes = new List<long>(256);
        private long _sendingTotalBytes;

        private string _lastSendingFilePath;
        private string _lastSendingLetter;
        private int _lastSendingCount;
        private int _lastSendingTotal;

        private int _transmittingDotsCount;
        private int _transmittingPhaseLogged;

        private const float ProgressSendingPhaseMax = 0.40f;
        private const float ProgressTransmittingPhase = 0.98f;
        private const float ProgressCommittingPhase = 0.99f;

        private const double BytesConversionFactor = 1024.0;
        private const int RefreshStatusTimeoutMs = 5000;

        // === FIX (konflikty): widok listy pokazuje TERAZ również C (konflikt)
        // i ~ (obstructed). NIE idą do commitu (patrz PreProcessStatuses) —
        // svn commit i tak zwróciłby E155015, a SVN zabrania commitować
        // nierozwiązane konflikty. Ale user MUSI je widzieć, żeby wiedzieć
        // co zrobić przed commitem.
        private const string DisplayStatuses = "MADR?!C~";   // co widzimy w liście

        // Bez zmian — co faktycznie idzie do svn commit. C i ~ celowo pominięte.
        private const string PreProcessStatuses = "MADR?!";  // co commit-ujemy

        private static readonly Regex CommittedRevisionRegex = new(@"Committed revision\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SanitizeMessageRegex = new(@"[\uFEFF\u200B-\u200D\u202A-\u202E\u2060-\u2069\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

        public SVNCommit(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();

            PostToMainThread(() =>
            {
                if (svnUI?.OperationProgressBar != null)
                    svnUI.OperationProgressBar.gameObject.SetActive(false);
            });
        }

        #region Processing Guard / Dispose

        private bool TryEnterProcessing()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;

            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
            {
                LogToConsole($"<color={SVNColors.Error}>Another commit operation is already running.</color>");
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

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "n/a";
            if (bytes == 0) return "0 B";

            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double d = bytes;
            while (d >= 1024.0 && order < suffix.Length - 1)
            {
                order++;
                d /= 1024.0;
            }
            return $"{d:0.##} {suffix[order]}";
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

        /// <summary>
        /// Szybka detekcja nierozwiązanych konfliktów w WC — czyta CurrentStatusDict
        /// (świeżo wypełniany przez SVNStatus refresh), bez dodatkowego 'svn status'.
        /// Zwraca listę ścieżek ze statusem 'C' (item lub prop).
        /// </summary>
        private List<string> GetUnresolvedConflicts()
        {
            var result = new List<string>();
            var dict = svnManager?.CurrentStatusDict;
            if (dict == null || dict.Count == 0) return result;

            foreach (var kv in dict)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                string status = kv.Value.status ?? "";
                if (status.IndexOf('C') >= 0)
                    result.Add(kv.Key);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Blokada commitu przy nierozwiązanych konfliktach. Loguje banner z listą
        /// plików i zwraca true gdy commit ma zostać PRZERWANY.
        ///
        /// === FIX: ASYNC — z fallbackiem na 'svn status' gdy cache managera
        /// jest pusty (user nie odświeżył drzewa). Fallback jest async, żeby
        /// nie blokować wątku na czas 'svn status'.
        /// </summary>
        private async Task<bool> AbortIfConflictsPresentAsync(string operationName)
        {
            var conflicts = GetUnresolvedConflicts();

            // Fallback: cache managera pusty → zrób 'svn status' by upewnić się.
            if (conflicts.Count == 0 &&
                (svnManager?.CurrentStatusDict == null || svnManager.CurrentStatusDict.Count == 0))
            {
                try
                {
                    string root = NormalizeRoot(svnManager.WorkingDir);
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        string raw = await SvnRunner.RunAsync("status", root).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            using var reader = new StringReader(raw);
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Length < 8) continue;
                                char item = line[0];
                                char prop = line[1];
                                char tree = line.Length > 6 ? line[6] : ' ';
                                if (item == 'C' || prop == 'C' || tree == 'C')
                                    conflicts.Add(line.Substring(8).Trim());
                            }
                            conflicts.Sort(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Nie blokujemy commitu gdy pre-check padł — svn commit
                    // i tak zwróci E155015, mamy to obsłużone w catch.
                    SVNLogBridge.LogWarning($"[Commit] Conflict pre-check failed ({ex.Message}) — proceeding.");
                    return false;
                }
            }

            if (conflicts.Count == 0) return false;

            var sb = new StringBuilder(conflicts.Count * 64 + 256);
            sb.AppendLine($"<color={SVNColors.Error}><b>=========================================</b></color>");
            sb.AppendLine($"<color={SVNColors.Error}><b>  COMMIT BLOCKED — {conflicts.Count} UNRESOLVED CONFLICT(S)</b></color>");
            sb.AppendLine($"<color={SVNColors.Error}><b>=========================================</b></color>");
            sb.AppendLine($"<color={SVNColors.Warning}>SVN cannot commit files in conflict state (status 'C').</color>");
            sb.AppendLine($"<color={SVNColors.Warning}>Resolve them first in the <b>Resolve</b> panel, then retry {operationName}.</color>");
            sb.AppendLine();
            sb.AppendLine($"<color={SVNColors.Dim}>Conflicted file(s):</color>");

            foreach (var p in conflicts.Take(20))
                sb.AppendLine($"  <color={SVNColors.StatusConflict}>[C]</color> {p}");
            if (conflicts.Count > 20)
                sb.AppendLine($"  <color={SVNColors.Dim}>... and {conflicts.Count - 20} more</color>");

            sb.AppendLine($"<color={SVNColors.Error}><b>=========================================</b></color>");

            LogToConsole(sb.ToString());
            SVNLogBridge.LogErrorToOutput($"[Commit] {operationName} aborted: {conflicts.Count} unresolved conflict(s).");

            // Otwórz panel Resolve — user może od razu rozwiązać.
            PostToMainThread(() =>
            {
                try { svnManager?.PanelHandler?.Button_OpenResolve(); } catch { }
            });

            return true;
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
                    LogToConsole($"<color={SVNColors.Error}><b>[System]</b> Cancellation requested — aborting commit...</color>");
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

        #region Conflict Pre-Resolve (E155015 fix) — OPENS OPTION FOR RESOLVE PANEL

        /// <summary>
        /// Auto-resolve konfliktów z '--accept working'. NIE jest wołane w ścieżce
        /// commitu (twarda blokada AbortIfConflictsPresentAsync wygrywa) — zostaje
        /// jako świadoma akcja z panelu Resolve ("Resolve + Retry") w przyszłości.
        /// </summary>
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

                LogToConsole($"<color={SVNColors.Warning}>Found {conflictedPaths.Count} unresolved conflict(s) — auto-resolving (accept working)...</color>");

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
                            LogToConsole($"<color={SVNColors.Success}>Resolved:</color> {p}");

                        return conflictedPaths.Count;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogToConsole($"<color={SVNColors.Warning}>Batch resolve failed ({ex.Message}). Retrying individually...</color>");
                    }
                    finally
                    {
                        try { if (File.Exists(targetsFile)) File.Delete(targetsFile); } catch { }
                    }
                }

                int resolved = 0;
                foreach (var cpath in conflictedPaths)
                {
                    try
                    {
                        await SvnRunner.RunAsync(
                            $"resolve --accept working \"{cpath}\"",
                            root, true, token).ConfigureAwait(false);
                        resolved++;
                        LogToConsole($"<color={SVNColors.Success}>Resolved:</color> {cpath}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception singleEx)
                    {
                        LogToConsole($"<color={SVNColors.Warning}>Failed to resolve {cpath}: {singleEx.Message}</color>");
                    }
                }

                return resolved;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return 0;
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

            var sb = new StringBuilder(commitables.Count * 48 + 256);
            sb.AppendLine("<b>Current working copy changes:</b>");

            // === FIX (konflikty): banner + sortowanie — konflikty i obstructed
            // lądują na górze, żeby user od razu widział co blokuje commit.
            int conflicts = commitables.Count(x => x.Value.status == "C");
            int obstructed = commitables.Count(x => x.Value.status == "~");

            if (conflicts > 0)
                sb.AppendLine($"<color={SVNColors.Error}><b>⚠ {conflicts} UNRESOLVED CONFLICT(S) — resolve before committing!</b></color>");
            if (obstructed > 0)
                sb.AppendLine($"<color={SVNColors.StatusObstructed}><b>⚠ {obstructed} OBSTRUCTED ITEM(S) (~) — fix before committing!</b></color>");
            if (conflicts > 0 || obstructed > 0)
                sb.AppendLine();

            var sorted = commitables
                .OrderBy(x => x.Value.status == "C" ? 0 : x.Value.status == "~" ? 1 : 2)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var item in sorted)
            {
                string color = SVNColors.GetStatusHex(item.Value.status);

                if (item.Value.status == "C")
                    sb.AppendLine($"<color={color}><b>[C]</b></color> {item.Key}  <color={SVNColors.Error}><b>← RESOLVE FIRST</b></color>");
                else if (item.Value.status == "~")
                    sb.AppendLine($"<color={color}><b>[~]</b></color> {item.Key}  <color={SVNColors.StatusObstructed}><b>← FIX FIRST</b></color>");
                else
                    sb.AppendLine($"<color={color}>[{item.Value.status}]</color> {item.Key}");
            }

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
                    catch { }
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
                var sb = new StringBuilder(items.Count * 48 + 256);

                // === FIX (konflikty): banner na górze — konflikty i obstructed NIE
                // wejdą do commitu (patrz PreProcessStatuses), ale user musi wiedzieć,
                // że są i że blokują commit zanim cokolwiek wyśle.
                int conflicts = items.Count(i => i.Status == "C");
                int obstructed = items.Count(i => i.Status == "~");

                if (conflicts > 0)
                {
                    sb.AppendLine($"<color={SVNColors.Error}><b>⚠ {conflicts} UNRESOLVED CONFLICT(S) — resolve before committing!</b></color>");
                    sb.AppendLine($"<color={SVNColors.Dim}><i>These will be skipped by Commit Selected. Use Resolve panel first.</i></color>");
                    sb.AppendLine();
                }
                if (obstructed > 0)
                {
                    sb.AppendLine($"<color={SVNColors.StatusObstructed}><b>⚠ {obstructed} OBSTRUCTED ITEM(S) (~) — fix before committing!</b></color>");
                    sb.AppendLine();
                }

                sb.AppendLine($"<b>Files to be committed</b> (Payload: <color={SVNColors.Header}>{FormatCommitSize(totalSize)}</color>):");

                // Sortowanie: konflikty na górze, potem obstructed, potem reszta.
                var sorted = items
                    .OrderBy(i => i.Status == "C" ? 0 : i.Status == "~" ? 1 : 2)
                    .ThenBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase);

                foreach (var item in sorted)
                {
                    string color = SVNColors.GetStatusHex(item.Status);

                    if (item.Status == "C")
                        sb.AppendLine($"<color={color}><b>[C]</b></color> {item.FullPath}  <color={SVNColors.Error}><b>← RESOLVE FIRST</b></color>");
                    else if (item.Status == "~")
                        sb.AppendLine($"<color={color}><b>[~]</b></color> {item.FullPath}  <color={SVNColors.StatusObstructed}><b>← FIX FIRST</b></color>");
                    else
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
                LogToConsole($"<color={SVNColors.Success}><b>[System]</b> Repair process finished.</color>");
            }
            catch (Exception ex)
            {
                LogToConsole($"<color={SVNColors.Warning}>Revert Error:</color> {ex.Message}");
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
                LogToConsole($"<color={SVNColors.Warning}>Error:</color> Working directory is not set.");
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
                LogToConsole($"<color={SVNColors.Success}>No missing files found.</color>");
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

            LogToConsole($"<color={SVNColors.Success}><b>SUCCESS!</b> Missing files restored.</color>");
        }

        #endregion

        #region Commit Entry Points

        public async void CommitSelected()
        {
            string rawMessage = svnUI.CommitMessageInput?.text;
            string message = SanitizeCommitMessage(rawMessage);

            if (string.IsNullOrWhiteSpace(message))
            {
                LogToConsole($"<color={SVNColors.Warning}>Error:</color> Please enter a commit message!");
                return;
            }

            // === FIX (konflikty): twarda blokada PRZED jakąkolwiek operacją SVN.
            // Async wersja — może zrobić fallback 'svn status' gdy cache pusty.
            try
            {
                if (await AbortIfConflictsPresentAsync("Commit Selected").ConfigureAwait(false))
                    return;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogWarning($"[Commit] Conflict pre-check error ({ex.Message}) — proceeding.");
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
                string rawMessage = svnUI.CommitMessageInput?.text;
                string message = SanitizeCommitMessage(rawMessage);

                if (string.IsNullOrWhiteSpace(message))
                {
                    LogToConsole($"<color={SVNColors.Warning}>Error:</color> Commit message is empty!");
                    return;
                }

                string root = NormalizeRoot(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(root))
                {
                    LogToConsole($"<color={SVNColors.Warning}>Error:</color> Working directory is not set.");
                    return;
                }

                // === FIX (konflikty): twarda blokada PRZED cleanupem i PRZED
                // jakąkolwiek operacją SVN.
                if (await AbortIfConflictsPresentAsync("Commit All").ConfigureAwait(false))
                    return;

                await svnManager.CancelBackgroundTasksAsync();

                var localCts = new CancellationTokenSource();
                SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _commitCTS, localCts));

                CancellationToken token = localCts.Token;
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                ClearCommitConsole();
                ResetCommitCounters();

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
                            LogToConsole($"<color={SVNColors.Success}>Indexing complete.</color>");
                        }
                        finally
                        {
                            TryDeleteFile(addTargetsFile);
                        }
                    }
                    else
                    {
                        LogToConsole($"<color={SVNColors.Success}>No new files to add.</color>");
                    }

                    // === UWAGA: AutoResolveConflictsAsync CELOWO USUNIĘTE z tej
                    // ścieżki. Blokada AbortIfConflictsPresentAsync wyżej gwarantuje,
                    // że tu nie ma już żadnych 'C'. Gdyby jednak jakiś konflikt pojawił
                    // się w międzyczasie (np. równoległy update z innego wątku), svn
                    // commit sam zwróci E155015 — obsłużone w catch niżej.

                    LogToConsole("<b>[4/4]</b> Sending to server...");
                    Interlocked.Exchange(ref _commitFilesProcessed, 0);
                    Interlocked.Exchange(ref _commitFilesTotal, 0);

                    ShowProgressBar(0.02f);

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
                        LogToConsole($"<color={SVNColors.Error}><b>[ABORTED]</b> Commit cancelled.</color>");
                        RunPostCancelCleanup(root);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("E155015"))
                        {
                            LogToConsole($"<color={SVNColors.Warning}><b>[BLOCKED]</b> Commit failed — unresolved conflict(s) remain.</color>");
                            LogToConsole($"<color={SVNColors.Warning}>Use the Resolve panel or Terminal (svn resolve) to fix them, then retry.</color>");
                        }
                        else
                        {
                            LogToConsole($"<color={SVNColors.Warning}>Error:</color> {ex.Message}");
                        }
                    }
                }
                finally
                {
                    ClearCommitCts(localCts);
                    HideProgressBarAndCurrentFile();
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
            message = SanitizeCommitMessage(message);
            if (string.IsNullOrWhiteSpace(message))
            {
                LogToConsole($"<color={SVNColors.Warning}>Error:</color> Commit message is empty!");
                return;
            }

            if (!TryEnterProcessing()) return;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                string root = NormalizeRoot(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(root))
                {
                    LogToConsole($"<color={SVNColors.Warning}>Error:</color> Working directory is not set.");
                    return;
                }

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule == null)
                {
                    LogToConsole($"<color={SVNColors.Warning}>Error:</color> SVN Status module not found.");
                    return;
                }

                var allElements = statusModule.GetCurrentData();
                var selectedItems = allElements?.Where(e => e.IsChecked).ToList() ?? new List<SvnTreeElement>();

                if (allElements == null || allElements.Count == 0)
                {
                    LogToConsole($"<color={SVNColors.Warning}>No SVN changes detected.</color>");
                    return;
                }

                if (selectedItems.Count == 0)
                {
                    LogToConsole($"<color={SVNColors.Error}>Nothing selected for commit.</color>");
                    return;
                }

                selectedItems = selectedItems.Where(e => PreProcessStatuses.Contains(e.Status ?? "")).ToList();
                if (selectedItems.Count == 0)
                {
                    LogToConsole($"<color={SVNColors.Warning}>No valid files to commit.</color>");
                    return;
                }

                var localCts = new CancellationTokenSource();
                SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _commitCTS, localCts));

                CancellationToken token = localCts.Token;
                string msgFile = Path.Combine(Path.GetTempPath(), $"svn_msg_{Guid.NewGuid():N}.txt");

                ClearCommitConsole();
                ResetCommitCounters();

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
                            LogToConsole($"<color={SVNColors.Warning}>[Warning] Invalid path skipped: {item.FullPath}</color>");
                            continue;
                        }

                        if (!selectedByPath.ContainsKey(norm))
                            selectedByPath[norm] = item;
                    }

                    if (selectedByPath.Count == 0)
                    {
                        LogToConsole($"<color={SVNColors.Warning}>No valid commit paths.</color>");
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
                        LogToConsole($"<color={SVNColors.Warning}>Warning: {missingOnDisk.Count} selected path(s) missing on disk — skipped:</color>");
                        foreach (var m in missingOnDisk.Take(10))
                            LogToConsole($"<color={SVNColors.Warning}> - {m}</color>");
                        if (missingOnDisk.Count > 10)
                            LogToConsole($"<color={SVNColors.Warning}> ... and {missingOnDisk.Count - 10} more</color>");
                    }

                    if (actualTargets.Count == 0)
                    {
                        LogToConsole($"<color={SVNColors.Warning}>No existing targets left to commit.</color>");
                        return;
                    }

                    var fileTargets = actualTargets
                        .Where(t => !Directory.Exists(Path.Combine(root, t.Replace('/', Path.DirectorySeparatorChar))))
                        .ToList();
                    var dirTargets = actualTargets.Except(fileTargets).ToList();

                    LogToConsole($"<color={SVNColors.Header}>Commit targets: {fileTargets.Count} file(s)" +
                        (dirTargets.Count > 0 ? $" + {dirTargets.Count} folder(s) [structure]" : "") + $"</color>");

                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                            svnUI.CommitCurrentFileText.text = $"Queued: {fileTargets.Count} file(s)";
                    });

                    string addTargetsFile = Path.Combine(Path.GetTempPath(), $"svn_commit_add_{Guid.NewGuid():N}.txt");
                    try
                    {
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

                    // === UWAGA: AutoResolveConflictsAsync CELOWO USUNIĘTE.

                    ShowProgressBar(0.02f);

                    try
                    {
                        await CommitTargetsLiveAsync(root, actualTargets, msgFile, token, actualTargets.Count);
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
                        LogToConsole($"<color={SVNColors.Error}><b>[ABORTED]</b> Commit cancelled.</color>");
                        RunPostCancelCleanup(root);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("E155015"))
                        {
                            LogToConsole($"<color={SVNColors.Warning}><b>[BLOCKED]</b> Commit failed — unresolved conflict(s) remain.</color>");
                        }
                        else
                        {
                            LogToConsole($"<color={SVNColors.Warning}>Error:</color> {ex.Message}");
                        }
                    }
                }
                finally
                {
                    ClearCommitCts(localCts);
                    HideProgressBarAndCurrentFile();
                    TryDeleteFile(msgFile);
                    await SafeRefreshStatusAsync();
                }
            }
            finally
            {
                ExitProcessing();
            }
        }

        /// <summary>
        /// Post-cancel cleanup WC — best effort, bez timeoutu.
        /// </summary>
        private void RunPostCancelCleanup(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    await SvnRunner.RunAsync("cleanup", root, true, CancellationToken.None).ConfigureAwait(false);

                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        LogToConsole($"<color={SVNColors.Success}><b>[ABORTED]</b> Working copy cleaned up — ready for retry.</color>");
                    });
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogErrorToOutput($"[Commit] Post-cancel cleanup failed: {ex.Message}");
                }
            });
        }

        #endregion

        #region SVN Operations

        private async Task ScheduleMissingForDeletion(string root, IEnumerable<string> relativePaths, CancellationToken token, string stepLog = null)
        {
            if (!string.IsNullOrEmpty(stepLog))
                LogToConsole(stepLog);

            if (relativePaths == null)
            {
                LogToConsole($"<color={SVNColors.Success}>No missing files to delete.</color>");
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
                LogToConsole($"<color={SVNColors.Success}>No missing files to delete.</color>");
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
                LogToConsole($"<color={SVNColors.Success}>No missing files to delete.</color>");
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
                LogToConsole($"<color={SVNColors.Warning}>No valid commit targets.</color>");
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

                    filePath = filePath.Replace('\\', '/');

                    string action = line.Substring(0, spaceIndex).Trim();

                    switch (action)
                    {
                        case "Sending": Interlocked.Increment(ref _commitUpdatedCount); break;
                        case "Adding": Interlocked.Increment(ref _commitAddedCount); break;
                        case "Deleting": Interlocked.Increment(ref _commitDeletedCount); break;
                        case "Replacing": Interlocked.Increment(ref _commitReplacedCount); break;
                    }

                    string actionLetter = action switch
                    {
                        "Sending" => "M",
                        "Adding" => "A",
                        "Deleting" => "D",
                        "Replacing" => "R",
                        _ => "?"
                    };

                    int current = Interlocked.Increment(ref _commitFilesProcessed);
                    int total = Volatile.Read(ref _commitFilesTotal);

                    try
                    {
                        string root = NormalizeRoot(svnManager.WorkingDir);
                        string native = Path.Combine(root, filePath.Replace('/', Path.DirectorySeparatorChar));
                        long size = 0;
                        if (File.Exists(native))
                        {
                            try { size = new FileInfo(native).Length; } catch { }
                        }
                        lock (_sendingFileSizes)
                        {
                            _sendingFileSizes.Add(size);
                            _sendingTotalBytes += size;
                        }
                    }
                    catch
                    {
                        lock (_sendingFileSizes) { _sendingFileSizes.Add(0); }
                    }

                    _lastSendingFilePath = filePath;
                    _lastSendingLetter = actionLetter;
                    _lastSendingCount = current;
                    _lastSendingTotal = total;

                    if (total > 0)
                    {
                        float sendingFraction = Math.Min(1f, (float)current / total);
                        UpdateProgress(0.02f + sendingFraction * (ProgressSendingPhaseMax - 0.02f));
                    }
                    else
                    {
                        float softFraction = (float)current / (current + 50f);
                        UpdateProgress(0.02f + softFraction * (ProgressSendingPhaseMax - 0.02f));
                    }

                    string capturedPath = filePath;
                    string capturedLetter = actionLetter;
                    int capturedCount = current;
                    int capturedTotal = total;
                    string capturedStats = BuildLiveStatsLine();
                    string letterHex = SVNColors.GetActionLetterHex(actionLetter);

                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText == null) return;

                        string counterPart = capturedTotal > 0
                            ? $"[{capturedCount}/{capturedTotal}]"
                            : $"[{capturedCount}]";

                        svnUI.CommitCurrentFileText.text =
                            $"<color={letterHex}><b>{capturedLetter}</b></color> " +
                            $"<color={SVNColors.Info}>{counterPart}</color> " +
                            $"<color={SVNColors.Info}>{capturedPath}</color>\n" +
                            $"<size=80%>{capturedStats}</size>";
                    });

                    continue;
                }

                if (line.StartsWith("Transmitting file data", StringComparison.OrdinalIgnoreCase))
                {
                    if (Interlocked.Exchange(ref _transmittingPhaseLogged, 1) == 0)
                        LogToConsole($"<color={SVNColors.Header}>Transmitting data...</color>");

                    string dotsPart = line.Substring("Transmitting file data".Length).Trim();
                    int dots = dotsPart.Count(c => c == '.');

                    if (dots > 0)
                        Interlocked.Exchange(ref _transmittingDotsCount, dots);

                    int totalDots = dots > 0 ? dots : Volatile.Read(ref _transmittingDotsCount);

                    long bytesSent = 0;
                    long bytesTotal = 0;
                    int sendingCount = 0;
                    lock (_sendingFileSizes)
                    {
                        sendingCount = _sendingFileSizes.Count;
                        int limit = Math.Min(totalDots, sendingCount);
                        for (int i = 0; i < limit; i++)
                            bytesSent += _sendingFileSizes[i];
                        bytesTotal = _sendingTotalBytes;
                    }

                    float transmittingFraction = bytesTotal > 0
                        ? Math.Min(1f, (float)bytesSent / bytesTotal)
                        : 0f;
                    float progressRange = ProgressTransmittingPhase - ProgressSendingPhaseMax;
                    UpdateProgress(ProgressSendingPhaseMax + transmittingFraction * progressRange);

                    int percent = bytesTotal > 0 ? (int)(bytesSent * 100 / bytesTotal) : 0;
                    string bytesPart = bytesTotal > 0
                        ? $"{FormatBytes(bytesSent)} / {FormatBytes(bytesTotal)} ({percent}%)"
                        : $"{totalDots} transmitted";

                    string lastFile = _lastSendingFilePath;
                    string lastLetter = _lastSendingLetter;
                    int lastCount = _lastSendingCount;
                    int lastTotal = _lastSendingTotal;
                    string stats = BuildLiveStatsLine();
                    string letterHex = SVNColors.GetActionLetterHex(lastLetter);

                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText == null) return;

                        if (!string.IsNullOrEmpty(lastFile))
                        {
                            string counterPart = lastTotal > 0
                                ? $"[{lastCount}/{lastTotal}]"
                                : $"[{lastCount}]";

                            svnUI.CommitCurrentFileText.text =
                                $"<color={SVNColors.Header}><b>Uploading</b></color> " +
                                $"<color={letterHex}><b>{lastLetter}</b></color> " +
                                $"<color={SVNColors.Info}>{counterPart}</color> " +
                                $"<color={SVNColors.Info}>{lastFile}</color>\n" +
                                $"<size=80%><color={SVNColors.Accent}><b>{bytesPart}</b></color>  {stats}</size>";
                        }
                        else
                        {
                            svnUI.CommitCurrentFileText.text =
                                $"<color={SVNColors.Header}><b>Uploading data to server...</b></color>\n" +
                                $"<size=80%><color={SVNColors.Accent}><b>{bytesPart}</b></color>  {stats}</size>";
                        }
                    });
                    continue;
                }

                if (line.StartsWith("Committing transaction", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateProgress(ProgressCommittingPhase);
                    LogToConsole($"<color={SVNColors.Warning}><b>Finalizing commit...</b></color>");

                    string lastFile = _lastSendingFilePath;
                    string lastLetter = _lastSendingLetter;
                    int lastTotal = _lastSendingTotal;
                    int lastCount = _lastSendingCount;
                    string stats = BuildLiveStatsLine();
                    string letterHex = SVNColors.GetActionLetterHex(lastLetter);

                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText == null) return;

                        if (!string.IsNullOrEmpty(lastFile))
                        {
                            string counterPart = lastTotal > 0
                                ? $"[{lastCount}/{lastTotal}]"
                                : $"[{lastCount}]";

                            svnUI.CommitCurrentFileText.text =
                                $"<color={SVNColors.Warning}><b>Finalizing</b></color> " +
                                $"<color={letterHex}><b>{lastLetter}</b></color> " +
                                $"<color={SVNColors.Info}>{counterPart}</color> " +
                                $"<color={SVNColors.Info}>{lastFile}</color>\n" +
                                $"<size=80%>{stats}</size>";
                        }
                        else
                        {
                            svnUI.CommitCurrentFileText.text =
                                $"<color={SVNColors.Warning}><b>Finalizing commit transaction...</b></color>\n" +
                                $"<size=80%>{stats}</size>";
                        }
                    });
                    continue;
                }

                if (line.StartsWith("Committed revision", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateProgress(1.0f);

                    int totalProcessed = Volatile.Read(ref _commitFilesProcessed);

                    LogToConsole($"<color={SVNColors.Success}><b>[SUCCESS] {line}</b></color>");

                    if (totalProcessed > 0)
                        LogToConsole($"<color={SVNColors.Success}>Files committed: <b>{totalProcessed}</b> — </color>{BuildLiveStatsLine()}");

                    string stats = BuildLiveStatsLine();
                    PostToMainThread(() =>
                    {
                        if (svnUI?.CommitCurrentFileText != null)
                            svnUI.CommitCurrentFileText.text =
                                $"<color={SVNColors.Success}><b>Done.</b></color>\n" +
                                $"<size=80%>{stats}</size>";
                    });
                    continue;
                }

                if (line.StartsWith("svn: E", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                {
                    LogToConsole($"<color={SVNColors.Error}><b>{line}</b></color>");
                }
            }
        }

        private string BuildLiveStatsLine()
        {
            int a = Volatile.Read(ref _commitAddedCount);
            int m = Volatile.Read(ref _commitUpdatedCount);
            int d = Volatile.Read(ref _commitDeletedCount);
            int r = Volatile.Read(ref _commitReplacedCount);

            var parts = new List<string>(4);
            if (a > 0) parts.Add($"<color={SVNColors.StatusAdded}>A:{a}</color>");
            if (m > 0) parts.Add($"<color={SVNColors.StatusModified}>M:{m}</color>");
            if (d > 0) parts.Add($"<color={SVNColors.StatusDeleted}>D:{d}</color>");
            if (r > 0) parts.Add($"<color={SVNColors.StatusReplaced}>R:{r}</color>");

            return parts.Count > 0 ? string.Join("  ", parts) : "";
        }

        private void ResetCommitCounters()
        {
            Interlocked.Exchange(ref _commitFilesProcessed, 0);
            Interlocked.Exchange(ref _commitFilesTotal, 0);
            Interlocked.Exchange(ref _commitAddedCount, 0);
            Interlocked.Exchange(ref _commitUpdatedCount, 0);
            Interlocked.Exchange(ref _commitDeletedCount, 0);
            Interlocked.Exchange(ref _commitReplacedCount, 0);

            _lastSendingFilePath = null;
            _lastSendingLetter = null;
            _lastSendingCount = 0;
            _lastSendingTotal = 0;
            _transmittingDotsCount = 0;
            Interlocked.Exchange(ref _transmittingPhaseLogged, 0);

            lock (_sendingFileSizes)
            {
                _sendingFileSizes.Clear();
                _sendingTotalBytes = 0;
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

        private void HideProgressBarAndCurrentFile() => PostToMainThread(() =>
        {
            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.value = 0f;
                svnUI.OperationProgressBar.gameObject.SetActive(false);
            }

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

            try
            {
                await SvnRunner.RunAsync("cleanup", root, false, token);
                LogToConsole($"<color={SVNColors.Success}>Cleanup complete</color>");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogToConsole($"<color={SVNColors.Warning}><b>[Warning]</b> Cleanup failed ({ex.Message}) — proceeding with commit anyway.</color>");
                return true;
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