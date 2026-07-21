using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SVN.Core
{
    public class SVNLock : SVNBase
    {
        public SVNLock(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager) { }

        public void LockAllModified() => LockModifiedButton();
        public void RefreshStealPanel(LockPanel panel) => ShowAllLocksButton();

        private bool _isRefreshingLocks;

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

        public async void BreakAllLocksButton()
        {
            await BreakAllLocks();
        }

        public async Task LockModified()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            IsProcessing = true;

            SVNLogBridge.LogLine("<b>[Lock]</b> Scanning for modified files (M)...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var modifiedFiles = statusDict
                    .Where(x => x.Value.status == "M")
                    .Select(x => x.Key)
                    .ToList();

                if (modifiedFiles.Count == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>No modified files (M) found to lock.</color>");
                    return;
                }

                var currentServerLocks = await GetDetailedLocks(root);
                var alreadyLockedPaths = new HashSet<string>(
                    currentServerLocks
                        .Where(l => !string.IsNullOrEmpty(l.FullPath))
                        .Select(l => NormalizePath(l.FullPath)),
                    StringComparer.OrdinalIgnoreCase
                );

                var filesToLock = modifiedFiles
                    .Where(f => !alreadyLockedPaths.Contains(NormalizePath(f)))
                    .Select(f => $"\"{f}\"")
                    .ToArray();

                if (filesToLock.Length > 0)
                {
                    SVNLogBridge.LogLine($"Locking {filesToLock.Length} new files...");

                    string allPathsJoined = string.Join(" ", filesToLock);
                    await SvnRunner.RunAsync($"lock {allPathsJoined}", root);

                    SVNLogBridge.LogLine("<color=green>Locking completed successfully.</color>");

                    svnManager._diskChangesDetected = true;
                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true);

                    var statusModule = svnManager.GetModule<SVNStatus>();
                    if (statusModule != null)
                        await statusModule.RefreshAfterAction();
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>All modified files are already locked.</color>");

                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true);
                    svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
                }

                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[Lock] Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("W160035") || ex.Message.Contains("E200009"))
                {
                    SVNLogBridge.LogLine("<color=yellow>Some files are already locked.</color>");
                    SVNStatus.ClearLockCache();
                    await RefreshLockCacheAsync(true);
                    svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
                }
                else
                {
                    SVNLogBridge.LogLine($"<color=#FFAA00>Lock Error:</color> {ex.Message}");
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task UnlockAll()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;

            SVNLogBridge.LogLine("<b>[Unlock]</b> Forcing server to release locks...", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                var allLocks = await GetDetailedLocks(root);
                var myLocksPaths = allLocks
                    .Where(l => l.Owner.Trim().Equals(svnManager.CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(l => $"\"{l.FullPath}\"")
                    .ToList();

                if (myLocksPaths.Count > 0)
                {
                    string allPathsJoined = string.Join(" ", myLocksPaths);
                    await SvnRunner.RunAsync($"unlock --force {allPathsJoined}", root);
                    SVNLogBridge.LogLine("<color=green>Locks released successfully.</color>");

                    svnManager._diskChangesDetected = true;

                    SVNStatus.ClearLockCache();

                    var statusModule = svnManager.GetModule<SVNStatus>();
                    if (statusModule != null)
                        await statusModule.RefreshAfterAction();

                    ShowAllLocksButton();
                }
                else
                {
                    SVNLogBridge.LogLine("You do not own any locked files.");
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task ShowAllLocks()
        {
            if (IsProcessing) return;

            SVNLogBridge.UpdateUIField(svnUI.LocksText,
                "<b><color=orange>Fetching Repository Status...</color></b>", append: false);

            IsProcessing = true;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                var locks = await GetDetailedLocks(svnManager.WorkingDir);
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

                SVNLogBridge.UpdateUIField(svnUI.LocksText, summary, append: false);
            }
            catch (Exception ex)
            {
                SVNLogBridge.UpdateUIField(svnUI.LocksText, $"Error: {ex.Message}", append: true);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task<List<SVNLockDetails>> GetDetailedLocks(string rootPath, CancellationToken token = default)
        {
            List<SVNLockDetails> locks = new List<SVNLockDetails>();
            
            string xmlOutput = await SvnRunner.RunAsync("status --xml -u --no-ignore", rootPath, token: token);

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

                    locks.Add(new SVNLockDetails
                    {
                        Path = svnPath.Replace(rootPath, "").TrimStart('\\', '/'),
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

            string cmd = isLocked
                ? $"unlock --force \"{fullPath}\""
                : $"lock --force \"{fullPath}\"";

            SVNLogBridge.LogLine($"<color=#00E5FF>[Lock]</color> Request: {cmd}");

            try
            {
                await svnManager.CancelBackgroundTasksAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await SvnRunner.RunAsync(cmd, root, token: cts.Token);  // root jest już czysty

                SVNStatus.ClearLockCache();

                if (isLocked)
                {
                    element.LockedByMe = false;
                    element.LockedByOther = false;
                    SVNLogBridge.LogLine($"<color=green>Unlocked:</color> {element.Name}");
                }
                else
                {
                    element.LockedByMe = true;
                    element.LockedByOther = false;
                    SVNLogBridge.LogLine($"<color=green>Locked:</color> {element.Name}");
                }

                _ = RefreshLockCacheAsync(true);
                svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogError("[SVN Lock] Operation timed out or was cancelled.");
                svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN Lock Error]: {ex.Message}");
                svnManager.GetModule<SVNStatus>()?.RefreshVisibleUIOnly();
            }
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            path = path.Replace("\\", "/");
            string root = svnManager.WorkingDir.Replace("\\", "/").TrimEnd('/');

            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(root.Length);
            }
            return path.TrimStart('/');
        }

        public async Task BreakAllLocks()
        {
            string root = svnManager.WorkingDir;
            SVNLogBridge.LogLine("<color=orange><b>[System]</b> Cleaning local database locks...</color>");
            await SvnRunner.RunAsync("cleanup --remove-locks", root);
            SVNLogBridge.LogLine("Local locks removed.");
            SVNStatus.ClearLockCache();
        }

        public async Task RefreshLockCacheAsync(bool force = false, CancellationToken token = default)
        {
            if (_isRefreshingLocks) return;
            _isRefreshingLocks = true;

            try
            {
                if (!force && svnManager.LockCache.IsValid())
                    return;

                string root = svnManager.WorkingDir;
          
                var locks = await GetDetailedLocks(root, token);

                svnManager.LockCache.Clear();
                foreach (var l in locks)
                {
                    token.ThrowIfCancellationRequested();
                    string normalized = NormalizePath(l.FullPath);
                    svnManager.LockCache.Locks[normalized] = l;
                }
                svnManager.LockCache.LastRefreshUtc = DateTime.UtcNow;

                ApplyLocksToTree();
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
                _isRefreshingLocks = false;
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
    }
}