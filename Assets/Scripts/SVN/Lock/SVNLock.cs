using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SVN.Core
{
    public class SVNLock : SVNBase
    {
        private const int CommandTimeoutSeconds = 30;

        private int _processingFlag;
        private int _isRefreshingLocksFlag;

        public SVNLock(SVNUI svnUI, SVNManager svnManager)
            : base(svnUI, svnManager)
        {
        }

        // =====================================================================
        // PUBLIC API
        // =====================================================================

        public void LockAllModified() => LockModifiedButton();

        public void RefreshStealPanel(LockPanel panel)
            => ShowAllLocksButton();

        public async void LockModifiedButton()
            => await LockModified();

        public async void ShowAllLocksButton()
            => await ShowAllLocks();

        public async void UnlockAllButton()
            => await UnlockAll();

        public async void CleanupLocksButton()
            => await CleanupLocks();

        // =====================================================================
        // UI LOG
        // =====================================================================

        private void LogToLockPanel(string message, bool append = true)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.LockDisplayArea != null)
                {
                    SVNLogBridge.UpdateUIField(
                        svnUI.LockDisplayArea,
                        message,
                        append: append);
                }
                else
                {
                    SVNLogBridge.LogLine(message, append);
                }
            });
        }

        // =====================================================================
        // PROCESSING GUARD
        // =====================================================================

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

        // =====================================================================
        // LOCK MODIFIED
        // =====================================================================

        public async Task LockModified()
        {
            if (!TryEnterProcessing())
                return;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

            LogToLockPanel("<b>[Lock]</b> Scanning for modified files (M)...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false)
                    .ConfigureAwait(false);

                var modifiedFiles = statusDict
                    .Where(x => string.Equals(x.Value.status, "M", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Key)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => MakeAbsolutePath(x, root))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (modifiedFiles.Count == 0)
                {
                    LogToLockPanel("<color=yellow>No modified files (M) found to lock.</color>");
                    return;
                }

                var currentServerLocks = await GetDetailedLocks(root).ConfigureAwait(false);
                var alreadyLockedPaths = new HashSet<string>(
                    currentServerLocks
                        .Where(l => l != null && !string.IsNullOrWhiteSpace(l.FullPath))
                        .Select(l => NormalizePath(l.FullPath)),
                    StringComparer.OrdinalIgnoreCase);

                var filesToLock = modifiedFiles
                    .Where(f => !alreadyLockedPaths.Contains(NormalizePath(f)))
                    .ToList();

                if (filesToLock.Count == 0)
                {
                    LogToLockPanel("<color=yellow>All modified files are already locked.</color>");
                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true).ConfigureAwait(false);
                    svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
                    return;
                }

                LogToLockPanel($"Locking {filesToLock.Count} new file(s)...");

                string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_lock_{Guid.NewGuid():N}.txt");
                try
                {
                    await File.WriteAllLinesAsync(targetsFile, filesToLock, new UTF8Encoding(false))
                        .ConfigureAwait(false);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));
                    await SvnRunner.RunAsync($"lock --targets \"{targetsFile}\"", root, token: cts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteFile(targetsFile);
                }

                LogToLockPanel("<color=green>Locking completed successfully.</color>");

                svnManager.DiskChangesDetected = true;
                SVNStatus.ClearLockCache();
                await RefreshLockCacheAsync(true).ConfigureAwait(false);

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);

                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=#8B0000>[Lock] Operation cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                if (IsAlreadyLockedError(ex))
                {
                    LogToLockPanel("<color=yellow>Some files are already locked.</color>");
                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true).ConfigureAwait(false);
                    svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
                }
                else
                {
                    LogToLockPanel($"<color=#FFAA00>Lock Error:</color> {ex.Message}");
                }
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // UNLOCK ALL MY LOCKS
        // =====================================================================

        public async Task UnlockAll()
        {
            if (!TryEnterProcessing())
                return;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

            LogToLockPanel("<b>[Unlock]</b> Forcing server to release locks...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var allLocks = await GetDetailedLocks(root).ConfigureAwait(false);
                string currentUser = (svnManager.CurrentUserName ?? "").Trim();

                var myLocksPaths = allLocks
                    .Where(l => l != null &&
                                !string.IsNullOrWhiteSpace(l.Owner) &&
                                !string.IsNullOrWhiteSpace(currentUser) &&
                                l.Owner.Trim().Equals(currentUser, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.FullPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (myLocksPaths.Count == 0)
                {
                    LogToLockPanel("You do not own any locked files.");
                    return;
                }

                string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_unlock_{Guid.NewGuid():N}.txt");
                try
                {
                    await File.WriteAllLinesAsync(targetsFile, myLocksPaths, new UTF8Encoding(false))
                        .ConfigureAwait(false);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));
                    await SvnRunner.RunAsync($"unlock --force --targets \"{targetsFile}\"", root, token: cts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteFile(targetsFile);
                }

                LogToLockPanel("<color=green>Locks released successfully.</color>");

                svnManager.DiskChangesDetected = true;
                SVNStatus.ClearLockCache();

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);

                await ShowAllLocksCoreAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=#8B0000>[Unlock] Operation cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FFAA00>Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // SHOW ALL LOCKS
        // =====================================================================

        public async Task ShowAllLocks()
        {
            if (!TryEnterProcessing())
                return;

            try
            {
                await ShowAllLocksCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task ShowAllLocksCoreAsync()
        {
            LogToLockPanel("<b><color=#8B0000>[Locks]</color> Fetching Repository Status...</b>", append: false);

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            var locks = await GetDetailedLocks(svnManager.WorkingDir).ConfigureAwait(false);
            string summary = "<b>Active Repository Locks:</b>\n----------------------------------\n";

            if (locks.Count == 0)
            {
                summary += "<color=yellow>No active locks found on server.</color>\n";
            }
            else
            {
                string currentUser = (svnManager.CurrentUserName ?? "").Trim();

                foreach (var lockItem in locks)
                {
                    bool isMe = !string.IsNullOrWhiteSpace(currentUser) &&
                                (lockItem.Owner ?? "").Trim().Equals(currentUser, StringComparison.OrdinalIgnoreCase);

                    string color = isMe ? "#00FF00" : "#FF4444";
                    string prefix = isMe ? "[MINE]" : "[LOCKED]";

                    summary += $"<color={color}><b>{prefix}</b></color> {lockItem.Path}\n";
                    summary += $"   User: <color=yellow>{lockItem.Owner}</color>\n";
                    if (!string.IsNullOrEmpty(lockItem.Comment))
                        summary += $"   Comment: <i>\"{lockItem.Comment}\"</i>\n";
                    summary += "----------------------------------\n";
                }
            }

            LogToLockPanel(summary, append: false);
        }

        // =====================================================================
        // GET DETAILED LOCKS
        // =====================================================================

        public async Task<List<SVNLockDetails>> GetDetailedLocks(string rootPath, CancellationToken token = default)
        {
            var locks = new List<SVNLockDetails>();
            string cleanRoot = SvnRunner.ForceCleanPath(rootPath);

            string xmlOutput = await SvnRunner.RunAsync("status --xml -u", cleanRoot, token: token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(xmlOutput))
                return locks;

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlOutput);

                XmlNodeList lockNodes = doc.SelectNodes("//repos-status/lock");
                if (lockNodes == null)
                    return locks;

                foreach (XmlNode lockNode in lockNodes)
                {
                    token.ThrowIfCancellationRequested();

                    XmlNode entryNode = lockNode.ParentNode?.ParentNode;
                    if (entryNode == null)
                        continue;

                    string svnPath = entryNode.Attributes?["path"]?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(svnPath))
                        continue;

                    string owner = lockNode.SelectSingleNode("owner")?.InnerText ?? "";
                    if (string.IsNullOrWhiteSpace(owner))
                        continue;

                    string normalizedSvnPath = svnPath.Replace('\\', '/');
                    string relativePath = NormalizePath(normalizedSvnPath, cleanRoot);

                    locks.Add(new SVNLockDetails
                    {
                        Path = relativePath,
                        FullPath = svnPath,
                        Owner = owner.Trim(),
                        Comment = lockNode.SelectSingleNode("comment")?.InnerText ?? "",
                        CreationDate = lockNode.SelectSingleNode("created")?.InnerText ?? ""
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError("SVN XML Parse Error: " + ex.Message);
            }

            return locks;
        }

        // =====================================================================
        // TOGGLE SINGLE ITEM
        // =====================================================================

        public async Task ToggleLockSingleItem(SvnTreeElement element)
        {
            if (element == null)
                return;

            if (!TryEnterProcessing())
                return;

            bool isLocked = element.LockedByMe;
            string elementName = element.Name;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);
            string relative = NormalizeProtectionPath(element.FullPath, root);
            string fullPath = MakeAbsolutePath(relative, root);

            string comment = ReadLockComment();
            string safeComment = SanitizeLockComment(comment);

            string cmd;
            if (isLocked)
                cmd = $"unlock --force \"{fullPath}\"";
            else
                cmd = string.IsNullOrEmpty(safeComment)
                    ? $"lock \"{fullPath}\""
                    : $"lock -m \"{safeComment}\" \"{fullPath}\"";

            LogToLockPanel($"<color=#00E5FF>[Lock]</color> Request: {cmd}");

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));
                await SvnRunner.RunAsync(cmd, root, token: cts.Token).ConfigureAwait(false);

                SVNStatus.ClearLockCache();

                if (isLocked)
                {
                    PostToMainThread(() =>
                    {
                        element.LockedByMe = false;
                        element.LockedByOther = false;
                    });
                    LogToLockPanel($"<color=green>Unlocked:</color> {elementName}");
                }
                else
                {
                    PostToMainThread(() =>
                    {
                        element.LockedByMe = true;
                        element.LockedByOther = false;
                    });
                    LogToLockPanel($"<color=green>Locked:</color> {elementName}");
                }

                PostToMainThread(() => ClearLockCommentInput());
                await RefreshLockCacheAsync(true).ConfigureAwait(false);
                PostToMainThread(() => svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly());
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=red>[SVN Lock] Operation timed out or was cancelled.</color>");
                PostToMainThread(() => svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly());
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=red>[SVN Lock Error]: {ex.Message}</color>");
                PostToMainThread(() => svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly());
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // NORMALIZE SERVER / WORKING COPY PATH
        // =====================================================================

        private string NormalizePath(string path)
        {
            return NormalizePath(path, svnManager?.WorkingDir);
        }

        private static string NormalizePath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            string p = path.Trim().Trim('"', '\'').Replace('\\', '/').Trim();
            string r = (root ?? "").Trim().Trim('"', '\'').Replace('\\', '/').TrimEnd('/');

            if (string.IsNullOrEmpty(p))
                return "";

            if (!string.IsNullOrEmpty(r))
            {
                if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
                    return "";

                if (p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                    return p.Substring(r.Length + 1).TrimStart('/');
            }

            return p.TrimStart('/');
        }

        // =====================================================================
        // CLEANUP
        // =====================================================================

        public async Task CleanupLocks()
        {
            if (!TryEnterProcessing())
                return;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

            LogToLockPanel(
                "<b>[Cleanup]</b> Cleaning working copy — finishing interrupted " +
                "operations, releasing internal locks...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                // FIX: '--remove-locks' NIE ISTNIEJE (svn: E200002: invalid option).
                // Goły 'svn cleanup' to właściwa komenda — dokańcza przerwane
                // operacje i zdejmuje wewnętrzne blokady working copy:
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(CommandTimeoutSeconds));

                await SvnRunner.RunAsync("cleanup", root, token: cts.Token)
                    .ConfigureAwait(false);

                LogToLockPanel("<color=green>[Cleanup]</color> Working copy clean.</color>");

                SVNStatus.ClearLockCache();

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);

                // NOWE — po cleanup resynchronizujemy stan locków (pliki
                // utkwione read-only po zerwaniu cudzego locka potrafią
                // wrócić do poprawnego stanu) i informujemy panele:
                await RefreshLockCacheAsync(true).ConfigureAwait(false);
                svnManager.RaiseRepositoryStateChanged();
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel(
                    "<color=#8B0000>[Cleanup] Operation cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FFAA00>[Cleanup] Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // REFRESH LOCK CACHE
        // =====================================================================

        // =====================================================================
        // REFRESH LOCK CACHE  (FIX c: preloadedLocks — oszczędza status -u)
        // =====================================================================

        public async Task RefreshLockCacheAsync(
            bool force = false,
            CancellationToken token = default,
            List<SVNLockDetails> preloadedLocks = null)
        {
            if (Interlocked.CompareExchange(ref _isRefreshingLocksFlag, 1, 0) != 0)
                return;

            try
            {
                if (!force && svnManager.LockCache.IsValid())
                    return;

                string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

                List<SVNLockDetails> locks = preloadedLocks;

                if (locks == null)
                    locks = await GetDetailedLocks(root, token).ConfigureAwait(false);

                var newLocks = new Dictionary<string, SVNLockDetails>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var l in locks)
                {
                    token.ThrowIfCancellationRequested();

                    if (l == null) continue;

                    string normalized = NormalizePath(l.FullPath);

                    if (string.IsNullOrWhiteSpace(normalized))
                        continue;

                    newLocks[normalized] = l;
                }

                svnManager.LockCache.ReplaceAll(newLocks);

                PostToMainThread(() => ApplyLocksToTree());
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=#8B0000>[Lock] Cache refresh cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Lock cache refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isRefreshingLocksFlag, 0);
            }
        }

        // =====================================================================
        // APPLY LOCKS TO TREE
        // =====================================================================

        private void ApplyLocksToTree(bool refreshUI = true)
        {
            var status = svnManager.GetModule<SVNStatus>();
            if (status == null)
                return;

            var data = status.GetCurrentData();
            if (data == null)
                return;

            string currentUser = (svnManager.CurrentUserName ?? "").Trim();

            foreach (var e in data)
            {
                if (e == null)
                    continue;

                e.LockedByMe = false;
                e.LockedByOther = false;

                string normalized = NormalizePath(e.FullPath);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (svnManager.LockCache.Locks.TryGetValue(normalized, out var lockInfo))
                {
                    bool isMine = !string.IsNullOrWhiteSpace(currentUser) &&
                                  (lockInfo.Owner ?? "").Trim().Equals(currentUser, StringComparison.OrdinalIgnoreCase);
                    e.LockedByMe = isMine;
                    e.LockedByOther = !isMine;
                }
            }

            if (refreshUI)
                status.RefreshVisibleUIOnly();
        }

        // =====================================================================
        // LOCK COMMENT
        // =====================================================================

        private string ReadLockComment() => svnUI?.LockCommentInput?.text ?? "";

        private void ClearLockCommentInput()
        {
            if (svnUI?.LockCommentInput != null)
                svnUI.LockCommentInput.text = "";
        }

        private static string SanitizeLockComment(string comment)
        {
            if (string.IsNullOrEmpty(comment))
                return "";

            const string allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,:;!?'()-_";
            var sb = new StringBuilder(comment.Length);

            foreach (char c in comment)
                if (allowed.IndexOf(c) >= 0)
                    sb.Append(c);

            string sanitized = sb.ToString().Trim();
            return sanitized.Length > 200 ? sanitized.Substring(0, 200) : sanitized;
        }

        // =====================================================================
        // LOCK ALL PROTECTED
        // =====================================================================

        // =====================================================================
        // LOCK ALL PROTECTED  (v3 — fix a/b/c/d)
        // =====================================================================

        public async Task LockAllProtectedAsync(string scopeFolder = null)
        {
            LogToLockPanel($"[SVNLock] LockAllProtectedAsync, scope='{scopeFolder}'");

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

            // ---------------------------------------------------------------
            // 1. VALIDATE SCOPE
            // ---------------------------------------------------------------

            if (string.IsNullOrWhiteSpace(scopeFolder))
            {
                LogToLockPanel(
                    "<color=#FFAA00>[Lock] No scope given — " +
                    "enter a folder path or '.' for the whole working copy. " +
                    "Nothing locked.</color>");
                return;
            }

            string scopeRel = NormalizeScopeFolder(scopeFolder, root, out string scopeError);

            if (scopeError != null)
            {
                LogToLockPanel($"<color=#FF5555>[Lock] {scopeError}</color>");
                return;
            }

            string scopeLabel = string.IsNullOrEmpty(scopeRel)
                ? "(whole working copy)"
                : $"(scope: {scopeRel})";

            // ---------------------------------------------------------------
            // 2. CHECK PATH EXISTS
            // ---------------------------------------------------------------

            if (!string.IsNullOrEmpty(scopeRel))
            {
                string scopeAbs = MakeAbsolutePath(scopeRel, root);

                if (!Directory.Exists(scopeAbs) && !File.Exists(scopeAbs))
                {
                    LogToLockPanel(
                        "<color=#FFAA00>[Lock] Path does not exist: " +
                        $"{scopeRel} — check the path. Nothing locked.</color>");
                    return;
                }
            }

            if (!TryEnterProcessing())
                return;

            try
            {
                LogToLockPanel(
                    $"<b><color=#1E90FF>[Lock]</color></b> " +
                    $"Locking protected files without a lock {scopeLabel}...",
                    append: false);

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var protection = svnManager.GetModule<SVNFileProtection>();

                if (protection == null)
                {
                    LogToLockPanel("<color=#FFAA00>[Lock] SVNFileProtection module unavailable.</color>");
                    return;
                }

                List<SVNFileProtection.ProtectionInfo> snapshot;

                try
                {
                    snapshot = await RunWithTimeoutAsync(
                        () => protection.GetProtectionSnapshotAsync(),
                        TimeSpan.FromSeconds(CommandTimeoutSeconds)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    LogToLockPanel(
                        "<color=#FF5555>[Lock] Protection snapshot timed out after 30s. " +
                        "SVN/background operation may be stuck.</color>");
                    return;
                }

                if (snapshot == null)
                {
                    LogToLockPanel("<color=#FF5555>[Lock] Protection snapshot is NULL.</color>");
                    return;
                }

                LogToLockPanel(
                    $"<color=#00E5FF>[Lock]</color> Protection snapshot: {snapshot.Count} item(s), " +
                    $"root='{root}', scope='{scopeRel}'");

                // -----------------------------------------------------------
                // 6. NORMALIZE + FILTER
                //    FIX (b): liczniki powodów odrzucenia — koniec z zagadką "3 → 2"
                // -----------------------------------------------------------

                int skippedState = 0, skippedLocked = 0, skippedScope = 0,
                    skippedPath = 0, skippedMissing = 0;

                var normalizedCandidates = new List<ProtectedLockCandidate>();

                foreach (var info in snapshot)
                {
                    if (info == null) { skippedPath++; continue; }

                    if (info.State != SVNFileProtection.ProtectionState.Protected)
                    { skippedState++; continue; }

                    string normalizedPath = NormalizeProtectionPath(info.Path, root);

                    if (string.IsNullOrWhiteSpace(normalizedPath))
                    { skippedPath++; continue; }                       // poza WC / pusta ścieżka

                    if (!IsPathInScope(normalizedPath, scopeRel))
                    { skippedScope++; continue; }

                    if (!string.IsNullOrWhiteSpace(info.LockOwner))
                    { skippedLocked++; continue; }                     // ma już lock → ok, ale wiemy o tym

                    string absolutePath = MakeAbsolutePath(normalizedPath, root);

                    if (!File.Exists(absolutePath))
                    {
                        skippedMissing++;
                        LogToLockPanel(
                            $"<color=#FFAA00>[Lock] Protected file does not exist locally: " +
                            $"{normalizedPath}</color>");
                        continue;
                    }

                    normalizedCandidates.Add(new ProtectedLockCandidate
                    {
                        Info = info,
                        RelativePath = normalizedPath,
                        AbsolutePath = absolutePath
                    });
                }

                // 7. DEDUP
                var candidates = normalizedCandidates
                    .GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                int duplicates = normalizedCandidates.Count - candidates.Count;

                // FIX (b): jedna linia, która odpowiada na "czemu nie wszystkie":
                LogToLockPanel(
                    $"<color=#00E5FF>[Lock]</color> Filter: {snapshot.Count} → {candidates.Count} " +
                    $"(state:{skippedState}, alreadyLocked:{skippedLocked}, outOfScope:{skippedScope}, " +
                    $"invalidPath:{skippedPath}, missingOnDisk:{skippedMissing}, duplicates:{duplicates})");

                foreach (var candidate in candidates.Take(20))
                    LogToLockPanel($"[Lock] Candidate: {candidate.RelativePath}");

                if (candidates.Count == 0)
                {
                    LogToLockPanel(
                        $"<color=yellow>[Lock] No protected files without a lock {scopeLabel}.</color>");

                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true).ConfigureAwait(false);
                    svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
                    return;
                }

                // -----------------------------------------------------------
                // 8. LOCK — batch + fallback per-plik (bez zmian)
                // -----------------------------------------------------------

                var toLock = candidates.Select(x => x.AbsolutePath).ToList();

                LogToLockPanel(
                    $"<color=#00E5FF>[Lock]</color> Locking {toLock.Count} protected file(s) {scopeLabel}...");

                int commandSuccess = 0;

                string targetsFile = Path.Combine(
                    Path.GetTempPath(),
                    $"svn_lock_{Guid.NewGuid():N}.txt");

                try
                {
                    await File.WriteAllLinesAsync(
                        targetsFile, toLock, new UTF8Encoding(false)).ConfigureAwait(false);

                    try
                    {
                        using var cts = new CancellationTokenSource(
                            TimeSpan.FromSeconds(CommandTimeoutSeconds));

                        await SvnRunner.RunAsync(
                            $"lock --targets \"{targetsFile}\"",
                            root, token: cts.Token).ConfigureAwait(false);

                        commandSuccess = toLock.Count;
                        LogToLockPanel("<color=#00FF00>[Lock] Batch svn lock completed.</color>");
                    }
                    catch (Exception batchEx)
                    {
                        LogToLockPanel(
                            "<color=#FFAA00>[Lock] Batch lock failed — retrying per file.</color>");
                        LogToLockPanel($"<color=#888888>SVN: {batchEx.Message}</color>");

                        foreach (string file in toLock)
                        {
                            try
                            {
                                using var fileCts = new CancellationTokenSource(
                                    TimeSpan.FromSeconds(CommandTimeoutSeconds));

                                await SvnRunner.RunAsync(
                                    $"lock \"{file}\"", root, token: fileCts.Token)
                                    .ConfigureAwait(false);

                                commandSuccess++;
                                LogToLockPanel($"<color=#00FF00>[Lock] OK:</color> {Path.GetFileName(file)}");
                            }
                            catch (OperationCanceledException)
                            {
                                LogToLockPanel(
                                    $"<color=#FF5555>[Lock] TIMEOUT:</color> {Path.GetFileName(file)}");
                            }
                            catch (Exception ex)
                            {
                                LogToLockPanel(
                                    $"<color=#FFAA00>[Lock] FAILED:</color> {Path.GetFileName(file)} — {ex.Message}");
                            }
                        }
                    }
                }
                finally
                {
                    TryDeleteFile(targetsFile);
                }

                // -----------------------------------------------------------
                // 9. VERIFY
                //    FIX (a): null = weryfikacja PADŁA — to NIE to samo co "0 locków"
                // -----------------------------------------------------------

                LogToLockPanel("<color=#00E5FF>[Lock]</color> Verifying locks on SVN server...");

                List<SVNLockDetails> verifiedLocks = null;

                try
                {
                    verifiedLocks = await GetDetailedLocks(root).ConfigureAwait(false);
                }
                catch (Exception verifyEx)
                {
                    LogToLockPanel(
                        $"<color=#FFAA00>[Lock] Verification failed: {verifyEx.Message}</color>");
                }

                bool verificationFailed = verifiedLocks == null;

                HashSet<string> verifiedSet = null;
                int actuallyLocked = 0;

                if (!verificationFailed)
                {
                    verifiedSet = new HashSet<string>(
                        verifiedLocks
                            .Where(x => x != null && !string.IsNullOrWhiteSpace(x.FullPath))
                            .Select(x => NormalizePath(x.FullPath, root)),
                        StringComparer.OrdinalIgnoreCase);

                    actuallyLocked = candidates.Count(x => verifiedSet.Contains(x.RelativePath));
                }

                // -----------------------------------------------------------
                // 10. RESULT
                //     FIX (a): rozdzielenie "wysłane" vs "potwierdzone serwerem"
                // -----------------------------------------------------------

                if (verificationFailed)
                {
                    LogToLockPanel(
                        $"<color=yellow>[Lock] Locks sent: {commandSuccess}/{candidates.Count} " +
                        $"protected file(s) {scopeLabel} — server verification unavailable. " +
                        "Use 'Show Locks' to confirm.</color>");
                }
                else
                {
                    LogToLockPanel(
                        $"<color=green>[Lock] Locked {actuallyLocked}/{candidates.Count} " +
                        $"protected file(s) {scopeLabel}.</color>");

                    if (commandSuccess != actuallyLocked)
                    {
                        LogToLockPanel(
                            $"<color=#FFAA00>[Lock] Command OK count: {commandSuccess}, " +
                            $"server-confirmed: {actuallyLocked}.</color>");
                    }

                    foreach (var candidate in candidates.Take(50))
                    {
                        bool locked = verifiedSet.Contains(candidate.RelativePath);

                        LogToLockPanel(
                            locked
                                ? $"<color=#66FF66>[LOCKED]</color> {candidate.RelativePath}"
                                : $"<color=#FF5555>[NOT LOCKED]</color> {candidate.RelativePath}");
                    }

                    if (candidates.Count > 50)
                        LogToLockPanel(
                            $"<color=#888888>... and {candidates.Count - 50} more (see 'Show Locks').</color>");
                }

                // -----------------------------------------------------------
                // 11. REFRESH
                //     FIX (c): reuse verifiedLocks — bez drugiego 'svn status -u'
                //     FIX (d): DiskChangesDetected po realnej zmianie na dysku
                // -----------------------------------------------------------

                if (commandSuccess > 0)
                    svnManager.DiskChangesDetected = true;

                SVNStatus.ClearLockCache();

                // verifiedLocks == null (weryfikacja padła) → metoda sama dociągnie dane:
                await RefreshLockCacheAsync(true, preloadedLocks: verifiedLocks)
                    .ConfigureAwait(false);

                svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
                svnManager.RaiseRepositoryStateChanged();
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=#FF5555>[Lock] Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FF5555>[Lock protected error]</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // TIMEOUT HELPER
        // =====================================================================

        private static async Task<T> RunWithTimeoutAsync<T>(Func<Task<T>> operation, TimeSpan timeout)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            Task<T> task = operation();
            Task completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);

            if (completed != task)
                throw new TimeoutException();

            return await task.ConfigureAwait(false);
        }

        // =====================================================================
        // SCOPE NORMALIZATION
        // =====================================================================

        private static string NormalizeScopeFolder(string input, string root, out string scopeError)
        {
            scopeError = null;

            string s = (input ?? "").Trim().Trim('"', '\'').Replace('\\', '/').Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(s))
            {
                scopeError = "No scope given — enter a folder path or '.' for the whole working copy.";
                return "";
            }

            if (s == ".")
                return "";

            string r = (root ?? "").Trim().Trim('"', '\'').Replace('\\', '/').TrimEnd('/');

            if (!string.IsNullOrEmpty(r))
            {
                if (string.Equals(s, r, StringComparison.OrdinalIgnoreCase))
                    return "";

                if (s.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(r.Length + 1);
                else if (Path.IsPathRooted(s) || s.IndexOf(':') >= 0 || s.StartsWith("//", StringComparison.Ordinal))
                {
                    scopeError = $"Scope folder is OUTSIDE the working copy: {s} — nothing locked.";
                    return "";
                }
            }

            return s.TrimStart('/');
        }

        // =====================================================================
        // PROTECTION PATH NORMALIZATION
        // =====================================================================

        private static string NormalizeProtectionPath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            string p = path.Trim().Trim('"', '\'').Replace('\\', '/').Trim();
            string r = (root ?? "").Trim().Trim('"', '\'').Replace('\\', '/').TrimEnd('/');

            if (string.IsNullOrWhiteSpace(p))
                return "";

            if (!string.IsNullOrWhiteSpace(r))
            {
                if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
                    return "";

                if (p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                    return p.Substring(r.Length + 1).TrimStart('/');

                if (Path.IsPathRooted(p) || p.IndexOf(':') >= 0 || p.StartsWith("//", StringComparison.Ordinal))
                    return "";
            }

            return p.TrimStart('/');
        }

        // =====================================================================
        // SCOPE TEST
        // =====================================================================

        private static bool IsPathInScope(string path, string scopeRel)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string p = path.Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
            string s = (scopeRel ?? "").Replace('\\', '/').Trim().Trim('/');

            if (string.IsNullOrEmpty(s))
                return true;

            if (string.Equals(p, s, StringComparison.OrdinalIgnoreCase))
                return true;

            return p.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        // ABSOLUTE PATH
        // =====================================================================

        private static string MakeAbsolutePath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            string cleanPath = path.Trim().Trim('"', '\'').Replace('\\', '/');
            string cleanRoot = (root ?? "").Trim().Trim('"', '\'').Replace('\\', '/').TrimEnd('/');

            if (string.IsNullOrWhiteSpace(cleanRoot))
                return cleanPath;

            if (Path.IsPathRooted(cleanPath) || cleanPath.IndexOf(':') >= 0 || cleanPath.StartsWith("//", StringComparison.Ordinal))
                return SvnRunner.ForceCleanPath(cleanPath);

            return SvnRunner.ForceCleanPath(cleanRoot + "/" + cleanPath.TrimStart('/'));
        }

        // =====================================================================
        // SINGLE FILE LOCK
        // =====================================================================

        public async Task<bool> LockSingleFileAsync(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            if (!TryEnterProcessing())
                return false;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string absolutePath = MakeAbsolutePath(fullPath, root);
                string safeComment = SanitizeLockComment(ReadLockComment());

                string cmd = string.IsNullOrEmpty(safeComment)
                    ? $"lock \"{absolutePath}\""
                    : $"lock -m \"{safeComment}\" \"{absolutePath}\"";

                LogToLockPanel($"<color=#00E5FF>[Lock]</color> {Path.GetFileName(absolutePath)}");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));
                await SvnRunner.RunAsync(cmd, root, token: cts.Token).ConfigureAwait(false);

                SVNStatus.ClearLockCache();
                PostToMainThread(() => ClearLockCommentInput());
                await RefreshLockCacheAsync(true).ConfigureAwait(false);
                PostToMainThread(() => svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly());

                LogToLockPanel($"<color=green>Locked:</color> {Path.GetFileName(absolutePath)}");
                return true;
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel($"<color=red>[Lock Timeout]:</color> {Path.GetFileName(fullPath)}");
                return false;
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=red>[Lock Error]:</color> {Path.GetFileName(fullPath)} — {ex.Message}");
                return false;
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // SINGLE FILE UNLOCK
        // =====================================================================

        public async Task<bool> UnlockSingleFileAsync(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            if (!TryEnterProcessing())
                return false;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string absolutePath = MakeAbsolutePath(fullPath, root);

                LogToLockPanel($"<color=#00E5FF>[Unlock]</color> {Path.GetFileName(absolutePath)}");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));
                await SvnRunner.RunAsync($"unlock --force \"{absolutePath}\"", root, token: cts.Token)
                    .ConfigureAwait(false);

                SVNStatus.ClearLockCache();
                await RefreshLockCacheAsync(true).ConfigureAwait(false);
                PostToMainThread(() => svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly());

                LogToLockPanel($"<color=green>Unlocked:</color> {Path.GetFileName(absolutePath)}");
                return true;
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel($"<color=red>[Unlock Timeout]:</color> {Path.GetFileName(fullPath)}");
                return false;
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=red>[Unlock Error]:</color> {Path.GetFileName(fullPath)} — {ex.Message}");
                return false;
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // STEAL LOCK
        // =====================================================================

        public async Task<bool> StealLockAsync(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            if (!TryEnterProcessing())
                return false;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string absolutePath = MakeAbsolutePath(fullPath, root);

                LogToLockPanel($"<color=#8B0000>[Steal]</color> {Path.GetFileName(absolutePath)} — taking over the lock...");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeoutSeconds));

                await SvnRunner.RunAsync($"unlock --force \"{absolutePath}\"", root, token: cts.Token)
                    .ConfigureAwait(false);

                string safeComment = SanitizeLockComment(ReadLockComment());
                string lockCmd = string.IsNullOrEmpty(safeComment)
                    ? $"lock \"{absolutePath}\""
                    : $"lock -m \"{safeComment}\" \"{absolutePath}\"";

                await SvnRunner.RunAsync(lockCmd, root, token: cts.Token).ConfigureAwait(false);

                SVNStatus.ClearLockCache();
                PostToMainThread(() => ClearLockCommentInput());
                await RefreshLockCacheAsync(true).ConfigureAwait(false);
                PostToMainThread(() => svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly());

                LogToLockPanel($"<color=green>Lock stolen and taken:</color> {Path.GetFileName(absolutePath)}");
                return true;
            }
            catch (OperationCanceledException)
            {
                SVNStatus.ClearLockCache();
                LogToLockPanel($"<color=red>[Steal Timeout]:</color> {Path.GetFileName(fullPath)}");
                return false;
            }
            catch (Exception ex)
            {
                SVNStatus.ClearLockCache();
                LogToLockPanel($"<color=red>[Steal Error]:</color> {Path.GetFileName(fullPath)} — {ex.Message}");
                return false;
            }
            finally
            {
                ExitProcessing();
            }
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        private static bool IsAlreadyLockedError(Exception ex)
        {
            if (ex == null)
                return false;

            string message = ex.Message ?? "";
            return message.Contains("W160035", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("E200009", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("already locked", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Intentionally ignored
            }
        }

        // =====================================================================
        // INTERNAL CANDIDATE
        // =====================================================================

        private sealed class ProtectedLockCandidate
        {
            public SVNFileProtection.ProtectionInfo Info;
            public string RelativePath;
            public string AbsolutePath;
        }
    }
}