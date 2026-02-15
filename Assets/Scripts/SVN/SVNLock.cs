using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLock : SVNBase
    {
        public SVNLock(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager) { }

        public void LockAllModified() => LockModified();
        public void RefreshStealPanel(LockPanel panel) => ShowAllLocks();

        public async void LockModified()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;

            SVNLogBridge.LogLine("<b>[Lock]</b> Scanning for modified files (M)...", append: false);

            try
            {
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
                    currentServerLocks.Select(l => l.Path.Replace("\\", "/").ToLower())
                );

                var filesToLock = modifiedFiles
                    .Where(f =>
                    {
                        string normalizedPath = f.Replace("\\", "/").ToLower();
                        return !alreadyLockedPaths.Any(lp => normalizedPath.EndsWith(lp));
                    })
                    .Select(f => $"\"{f}\"")
                    .ToArray();

                int alreadyLockedByMeOrOthers = modifiedFiles.Count - filesToLock.Length;

                if (filesToLock.Length > 0)
                {
                    SVNLogBridge.LogLine($"Locking {filesToLock.Length} new files (Skipped {alreadyLockedByMeOrOthers} already locked)...");

                    string allPathsJoined = string.Join(" ", filesToLock);
                    await SvnRunner.RunAsync($"lock {allPathsJoined}", root);

                    SVNLogBridge.LogLine("<color=green>Locking completed successfully.</color>");
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>All modified files are already locked.</color>");
                }

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("W160035"))
                {
                    SVNLogBridge.LogLine("<color=green>Files were already locked.</color>");
                }
                else
                {
                    SVNLogBridge.LogLine($"<color=red>Lock Error:</color> {ex.Message}");
                }
            }
            finally { IsProcessing = false; }
        }

        public async void UnlockAll()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;

            SVNLogBridge.LogLine("<b>[Unlock]</b> Forcing server to release locks...", append: false);

            try
            {
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

                    await svnManager.RefreshStatus();
                    ShowAllLocks();
                }
                else
                {
                    SVNLogBridge.LogLine("You do not own any locked files.");
                }
            }
            catch (System.Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void ShowAllLocks()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            // Specifically updating the LocksText UI field
            SVNLogBridge.UpdateUIField(svnUI.LocksText, "<b><color=orange>Fetching Repository Status...</color></b>", append: false);

            try
            {
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
                                    lockItem.Owner.Trim().Equals(svnManager.CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase);

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
            finally { IsProcessing = false; }
        }

        public async Task<List<SVNLockDetails>> GetDetailedLocks(string rootPath)
        {
            List<SVNLockDetails> locks = new List<SVNLockDetails>();
            string xmlOutput = await SvnRunner.RunAsync("status --xml -u --no-ignore", rootPath);

            if (string.IsNullOrEmpty(xmlOutput)) return locks;

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlOutput);

                XmlNodeList lockNodes = doc.SelectNodes("//repos-status/lock");

                foreach (XmlNode lockNode in lockNodes)
                {
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
            catch (System.Exception ex)
            {
                Debug.LogError("SVN XML Parse Error: " + ex.Message);
            }

            return locks;
        }

        public async void BreakAllLocks()
        {
            string root = svnManager.WorkingDir;
            SVNLogBridge.LogLine("<color=orange><b>[System]</b> Cleaning local database locks...</color>");
            await SvnRunner.RunAsync("cleanup --remove-locks", root);
            SVNLogBridge.LogLine("Local locks removed.");
        }
    }
}