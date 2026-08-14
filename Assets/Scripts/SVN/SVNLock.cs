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
        private int _processingFlag;
        private int _isRefreshingLocksFlag;

        public SVNLock(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager) { }

        public void LockAllModified() => LockModifiedButton();
        public void RefreshStealPanel(LockPanel panel) => ShowAllLocksButton();

        private void LogToLockPanel(string message, bool append = true)
        {
            if (svnUI?.LockDisplayArea != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, message);
            }
            else
            {
                SVNLogBridge.LogLine(message, append);
            }
        }

        private bool TryEnterProcessing()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        public async void LockModifiedButton()
        {
            await LockModified();
        }

        public async void ShowAllLocksButton()
        {
            await ShowAllLocks();
        }

        public async void UnlockAllButton()
        {
            await UnlockAll();
        }

        public async void CleanupLocksButton()
        {
            await CleanupLocks();
        }

        public async Task LockModified()
        {
            if (!TryEnterProcessing()) return;

            string root = svnManager.WorkingDir;
            LogToLockPanel("<b>[Lock]</b> Scanning for modified files (M)...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false).ConfigureAwait(false);
                var modifiedFiles = statusDict
                    .Where(x => x.Value.status == "M")
                    .Select(x => x.Key)
                    .ToList();

                if (modifiedFiles.Count == 0)
                {
                    LogToLockPanel("<color=yellow>No modified files (M) found to lock.</color>");
                    return;
                }

                var currentServerLocks = await GetDetailedLocks(root).ConfigureAwait(false);
                var alreadyLockedPaths = new HashSet<string>(
                    currentServerLocks
                        .Where(l => !string.IsNullOrEmpty(l.FullPath))
                        .Select(l => NormalizePath(l.FullPath)),
                    StringComparer.OrdinalIgnoreCase
                );

                var filesToLock = modifiedFiles
                    .Where(f => !alreadyLockedPaths.Contains(NormalizePath(f)))
                    .ToList();

                if (filesToLock.Count > 0)
                {
                    LogToLockPanel($"Locking {filesToLock.Count} new files...");

                    string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_lock_{Guid.NewGuid():N}.txt");
                    await File.WriteAllLinesAsync(targetsFile, filesToLock, new UTF8Encoding(false)).ConfigureAwait(false);

                    try
                    {
                        await SvnRunner.RunAsync($"lock --targets \"{targetsFile}\"", root).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { if (File.Exists(targetsFile)) File.Delete(targetsFile); } catch { }
                    }

                    LogToLockPanel("<color=green>Locking completed successfully.</color>");

                    svnManager.DiskChangesDetected = true;
                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true).ConfigureAwait(false);

                    var statusModule = svnManager.GetModule<SVNStatus>();
                    if (statusModule != null)
                        await statusModule.RefreshAfterAction().ConfigureAwait(false);
                }
                else
                {
                    LogToLockPanel("<color=yellow>All modified files are already locked.</color>");

                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true).ConfigureAwait(false);
                    svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
                }

                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=orange>[Lock] Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("W160035") || ex.Message.Contains("E200009"))
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

        public async Task UnlockAll()
        {
            if (!TryEnterProcessing()) return;
            string root = svnManager.WorkingDir;

            LogToLockPanel("<b>[Unlock]</b> Forcing server to release locks...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var allLocks = await GetDetailedLocks(root).ConfigureAwait(false);
                var myLocksPaths = allLocks
                    .Where(l => l.Owner.Trim().Equals(svnManager.CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.FullPath)
                    .ToList();

                if (myLocksPaths.Count > 0)
                {
                    string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_unlock_{Guid.NewGuid():N}.txt");
                    await File.WriteAllLinesAsync(targetsFile, myLocksPaths, new UTF8Encoding(false)).ConfigureAwait(false);

                    try
                    {
                        await SvnRunner.RunAsync($"unlock --force --targets \"{targetsFile}\"", root).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { if (File.Exists(targetsFile)) File.Delete(targetsFile); } catch { }
                    }

                    LogToLockPanel("<color=green>Locks released successfully.</color>");

                    svnManager.DiskChangesDetected = true;
                    SVNStatus.ClearLockCache();

                    var statusModule = svnManager.GetModule<SVNStatus>();
                    if (statusModule != null)
                        await statusModule.RefreshAfterAction().ConfigureAwait(false);

                    ShowAllLocksButton();
                }
                else
                {
                    LogToLockPanel("You do not own any locked files.");
                }
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

        public async Task ShowAllLocks()
        {
            if (!TryEnterProcessing()) return;

            LogToLockPanel("<b><color=orange>Fetching Repository Status...</color></b>", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var locks = await GetDetailedLocks(svnManager.WorkingDir).ConfigureAwait(false);
                string summary = "<b>Active Repository Locks:</b>\n----------------------------------\n";

                if (locks.Count == 0)
                {
                    summary += "<color=yellow>No active locks found on server.</color>\n";
                }
                else
                {
                    foreach (var lockItem in locks)
                    {
                        bool isMe = !string.IsNullOrEmpty(svnManager.CurrentUserName) &&
                                    lockItem.Owner.Trim().Equals(svnManager.CurrentUserName.Trim(),
                                        StringComparison.OrdinalIgnoreCase);

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
            catch (Exception ex)
            {
                LogToLockPanel($"Error: {ex.Message}", append: true);
            }
            finally
            {
                ExitProcessing();
            }
        }

        public async Task<List<SVNLockDetails>> GetDetailedLocks(string rootPath, CancellationToken token = default)
        {
            List<SVNLockDetails> locks = new List<SVNLockDetails>();

            string xmlOutput = await SvnRunner.RunAsync("status --xml -u --no-ignore", rootPath, token: token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(xmlOutput)) return locks;

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlOutput);

                XmlNodeList lockNodes = doc.SelectNodes("//repos-status/lock");

                foreach (XmlNode lockNode in lockNodes)
                {
                    token.ThrowIfCancellationRequested();

                    XmlNode entryNode = lockNode.ParentNode.ParentNode;
                    if (entryNode == null) continue;

                    string svnPath = entryNode.Attributes["path"]?.Value ?? "";
                    string owner = lockNode.SelectSingleNode("owner")?.InnerText;
                    if (string.IsNullOrEmpty(owner)) continue;

                    string relativePath = svnPath;
                    if (svnPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                        relativePath = svnPath.Substring(rootPath.Length);

                    locks.Add(new SVNLockDetails
                    {
                        Path = relativePath.TrimStart('\\', '/'),
                        FullPath = svnPath,
                        Owner = owner,
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

        public async Task ToggleLockSingleItem(SvnTreeElement element)
        {
            if (element == null) return;

            bool isLocked = element.LockedByMe;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);
            string relative = SvnRunner.ForceCleanPath(element.FullPath);
            string fullPath = Path.Combine(root, relative);
            fullPath = SvnRunner.ForceCleanPath(fullPath);

            string comment = GetAndClearLockComment();
            string safeComment = SanitizeLockComment(comment);

            string cmd;
            if (isLocked)
            {
                cmd = $"unlock --force \"{fullPath}\"";
            }
            else
            {
                cmd = string.IsNullOrEmpty(safeComment)
                    ? $"lock --force \"{fullPath}\""
                    : $"lock -m \"{safeComment}\" --force \"{fullPath}\"";
            }

            LogToLockPanel($"<color=#00E5FF>[Lock]</color> Request: {cmd}");

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await SvnRunner.RunAsync(cmd, root, token: cts.Token).ConfigureAwait(false);

                SVNStatus.ClearLockCache();

                if (isLocked)
                {
                    PostToMainThread(() =>
                    {
                        element.LockedByMe = false;
                        element.LockedByOther = false;
                    });
                    LogToLockPanel($"<color=green>Unlocked:</color> {element.Name}");
                }
                else
                {
                    PostToMainThread(() =>
                    {
                        element.LockedByMe = true;
                        element.LockedByOther = false;
                    });
                    LogToLockPanel($"<color=green>Locked:</color> {element.Name}");
                }

                _ = RefreshLockCacheAsync(true);
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
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            string root = svnManager.WorkingDir?.Replace("\\", "/").TrimEnd('/') ?? "";
            path = path.Replace("\\", "/");

            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(root.Length);
            }
            return path.TrimStart('/');
        }

        public async Task CleanupLocks()
        {
            if (!TryEnterProcessing()) return;
            string root = svnManager.WorkingDir;

            LogToLockPanel("<b>[Cleanup Locks]</b> Removing stale local lock tokens...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                await SvnRunner.RunAsync("cleanup --remove-locks", root).ConfigureAwait(false);

                LogToLockPanel("<color=green>Local lock cleanup completed successfully.</color>");

                SVNStatus.ClearLockCache();

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=orange>[Cleanup Locks] Operation cancelled.</color>");
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

        public async Task RefreshLockCacheAsync(bool force = false, CancellationToken token = default)
        {
            if (Interlocked.CompareExchange(ref _isRefreshingLocksFlag, 1, 0) != 0) return;

            try
            {
                if (!force && svnManager.LockCache.IsValid())
                    return;

                string root = svnManager.WorkingDir;

                var locks = await GetDetailedLocks(root, token).ConfigureAwait(false);

                svnManager.LockCache.Clear();
                foreach (var l in locks)
                {
                    token.ThrowIfCancellationRequested();
                    string normalized = NormalizePath(l.FullPath);
                    svnManager.LockCache.Locks[normalized] = l;
                }
                svnManager.LockCache.LastRefreshUtc = DateTime.UtcNow;

                PostToMainThread(() => ApplyLocksToTree());
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[Lock] Cache refresh cancelled.</color>");
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

        private void ApplyLocksToTree(bool refreshUI = true)
        {
            var status = svnManager.GetModule<SVNStatus>();
            if (status == null) return;

            var data = status.GetCurrentData();
            if (data == null) return;

            string currentUser = svnManager.CurrentUserName?.Trim().ToLower();

            foreach (var e in data)
            {
                e.LockedByMe = false;
                e.LockedByOther = false;

                string normalized = NormalizePath(e.FullPath);
                if (svnManager.LockCache.Locks.TryGetValue(normalized, out var lockInfo))
                {
                    bool isMine = lockInfo.Owner.Trim().ToLower() == currentUser;
                    e.LockedByMe = isMine;
                    e.LockedByOther = !isMine;
                }
            }

            if (refreshUI)
                status.RefreshVisibleUIOnly();
        }

        private string GetAndClearLockComment()
        {
            if (svnUI?.LockCommentInput == null) return "";

            string comment = svnUI.LockCommentInput.text;
            svnUI.LockCommentInput.text = "";
            return comment;
        }

        private static string SanitizeLockComment(string comment)
        {
            if (string.IsNullOrEmpty(comment)) return "";

            const string allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,:;!?'()-_";
            var sb = new StringBuilder(comment.Length);

            foreach (char c in comment)
            {
                if (allowed.IndexOf(c) >= 0)
                    sb.Append(c);
            }

            string sanitized = sb.ToString().Trim();
            return sanitized.Length > 200 ? sanitized.Substring(0, 200) : sanitized;
        }
    }
}