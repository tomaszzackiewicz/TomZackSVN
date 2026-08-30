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

        // === FIX K1: append PRZEKAZYWANY (wcześniej parametr martwy — każdy log
        // nadpisywał panel, lista locków znikała pod kolejnym komunikatem)
        // + wywołanie przez dispatcher (LogToLockPanel wołane też po
        // ConfigureAwait(false), czyli z thread poolu).
        private void LogToLockPanel(string message, bool append = true)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.LockDisplayArea != null)
                    SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, message, append: append);
                else
                    SVNLogBridge.LogLine(message, append);
            });
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

        public async void LockModifiedButton() => await LockModified();
        public async void ShowAllLocksButton() => await ShowAllLocks();
        public async void UnlockAllButton() => await UnlockAll();
        public async void CleanupLocksButton() => await CleanupLocks();

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
                string currentUser = (svnManager.CurrentUserName ?? "").Trim();
                var myLocksPaths = allLocks
                    .Where(l => !string.IsNullOrEmpty(l.Owner) &&
                                l.Owner.Trim().Equals(currentUser, StringComparison.OrdinalIgnoreCase))
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

                    // === FIX K2: rdzeń bez guardu. Wcześniej 'ShowAllLocksButton()' wołane
                    // Z WNĘTRZA try (flaga wciąż zajęta) → ShowAllLocks → TryEnterProcessing
                    // → false → cichy return → panel locków NIGDY nie odświeżał się po unlocku.
                    await ShowAllLocksCoreAsync().ConfigureAwait(false);
                }
                else
                {
                    LogToLockPanel("You do not own any locked files.");
                }
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=orange>[Unlock] Operation cancelled.</color>");
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

            try
            {
                await ShowAllLocksCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                ExitProcessing();
            }
        }

        // === FIX K2: rdzeń bez guardu — wywoływalny z wnętrza innych operacji.
        private async Task ShowAllLocksCoreAsync()
        {
            LogToLockPanel("<b><color=orange>Fetching Repository Status...</color></b>", append: false);

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
                    bool isMe = !string.IsNullOrEmpty(currentUser) &&
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

        public async Task<List<SVNLockDetails>> GetDetailedLocks(string rootPath, CancellationToken token = default)
        {
            List<SVNLockDetails> locks = new List<SVNLockDetails>();

            // --no-ignore zbędne przy pytaniu o locki (nit); zostawiono -u (remote).
            string xmlOutput = await SvnRunner.RunAsync("status --xml -u", rootPath, token: token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(xmlOutput)) return locks;

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlOutput);

                XmlNodeList lockNodes = doc.SelectNodes("//repos-status/lock");

                foreach (XmlNode lockNode in lockNodes)
                {
                    token.ThrowIfCancellationRequested();

                    XmlNode entryNode = lockNode.ParentNode?.ParentNode;
                    if (entryNode == null) continue;

                    // === FIX K4: guard na brak/pusty atrybut path (pusta ścieżka
                    // trafiały potem do targets-file → błąd svn).
                    string svnPath = entryNode.Attributes?["path"]?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(svnPath)) continue;

                    string owner = lockNode.SelectSingleNode("owner")?.InnerText;
                    if (string.IsNullOrEmpty(owner)) continue;

                    string relativePath = svnPath;
                    if (!string.IsNullOrWhiteSpace(rootPath))
                    {
                        string root = rootPath.Replace("\\", "/").TrimEnd('/');

                        // === FIX K4: strip prefixu ze SEPARATOREM — gołe StartsWith
                        // łapało 'D:/Repo' → 'D:/RepoOther/...' i zostawiało
                        // 'Other/...' jako ścieżkę względną.
                        if (string.Equals(svnPath.Replace("\\", "/"), root, StringComparison.OrdinalIgnoreCase))
                        {
                            relativePath = "";
                        }
                        else if (svnPath.Replace("\\", "/").StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            relativePath = svnPath.Replace("\\", "/").Substring(root.Length + 1);
                        }
                    }

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

        // === FIX K3: guard — wcześniej ToggleLockSingleItem (publiczne wejście
        // z drzewa) biegł RÓWNOLEGLE do LockModified/UnlockAll (brak _processingFlag).
        public async Task ToggleLockSingleItem(SvnTreeElement element)
        {
            if (element == null) return;
            if (!TryEnterProcessing()) return;

            // === FIX Ś1-style: snapshot pól na main thread (tu jesteśmy — klik z UI).
            bool isLocked = element.LockedByMe;
            string elementName = element.Name;
            string elementFullPath = element.FullPath;

            string root = SvnRunner.ForceCleanPath(svnManager.WorkingDir);
            string relative = SvnRunner.ForceCleanPath(elementFullPath);
            string fullPath = Path.Combine(root, relative);
            fullPath = SvnRunner.ForceCleanPath(fullPath);

            // === FIX K3b: komentarz CZYTANY, ale czyszczony dopiero po SUKCESIE —
            // wcześniej pole czyszczone było przed komendą i porażka (timeout/błąd)
            // bezpowrotnie gubiła wpisany przez użytkownika komentarz.
            string comment = ReadLockComment();
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

                // === FIX K3b: czyszczenie pola komentarza dopiero po sukcesie.
                PostToMainThread(() => ClearLockCommentInput());

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
            finally
            {
                ExitProcessing();
            }
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            string root = svnManager.WorkingDir?.Replace("\\", "/").TrimEnd('/') ?? "";
            path = path.Replace("\\", "/");

            if (!string.IsNullOrEmpty(root) &&
                (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                path = path.Substring(root.Length + 1);
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

                // === FIX K1: budowa LOKALNA + ReplaceAll (atomowa podmiana).
                var newLocks = new Dictionary<string, SVNLockDetails>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in locks)
                {
                    token.ThrowIfCancellationRequested();
                    string normalized = NormalizePath(l.FullPath);
                    newLocks[normalized] = l;
                }
                svnManager.LockCache.ReplaceAll(newLocks);

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
                    bool isMine = (lockInfo.Owner ?? "").Trim().ToLower() == currentUser;
                    e.LockedByMe = isMine;
                    e.LockedByOther = !isMine;
                }
            }

            if (refreshUI)
                status.RefreshVisibleUIOnly();
        }

        // === FIX K3b: czytanie/czyszczenie rozdzielone (clear dopiero po sukcesie).
        private string ReadLockComment() => svnUI?.LockCommentInput?.text ?? "";
        private void ClearLockCommentInput()
        {
            if (svnUI?.LockCommentInput != null)
                svnUI.LockCommentInput.text = "";
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