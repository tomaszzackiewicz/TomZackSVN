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
            svnUI.LogText.text = "<b>[Lock]</b> Scanning for modified files (M)...\n";

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var modifiedFiles = statusDict
                    .Where(x => x.Value.status == "M")
                    .Select(x => x.Key)
                    .ToList();

                if (modifiedFiles.Count == 0)
                {
                    svnUI.LogText.text += "<color=yellow>No modified files (M) found to lock.</color>\n";
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
                    svnUI.LogText.text += $"Locking {filesToLock.Length} new files (Skipped {alreadyLockedByMeOrOthers} already locked)...\n";

                    string allPathsJoined = string.Join(" ", filesToLock);
                    await SvnRunner.RunAsync($"lock {allPathsJoined}", root);

                    svnUI.LogText.text += "<color=green>Locking completed successfully.</color>\n";
                }
                else
                {
                    svnUI.LogText.text += "<color=yellow>All modified files are already locked.</color>\n";
                }

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("W160035"))
                {
                    svnUI.LogText.text += "<color=green>Files were already locked.</color>\n";
                }
                else
                {
                    svnUI.LogText.text += $"<color=red>Lock Error:</color> {ex.Message}\n";
                }
            }
            finally { IsProcessing = false; }
        }

        public async void UnlockAll()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;
            svnUI.LogText.text = "<b>[Unlock]</b> Forcing server to release locks...\n";

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

                    svnUI.LogText.text += "<color=green>Locks released successfully.</color>\n";

                    await svnManager.RefreshStatus();
                    ShowAllLocks();
                }
                else
                {
                    svnUI.LogText.text += "You do not own any locked files.\n";
                }
            }
            catch (System.Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Error:</color> {ex.Message}\n";
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
            svnUI.LocksText.text = "<b><color=orange>Fetching Repository Status...</color></b>\n";

            try
            {
                var locks = await GetDetailedLocks(svnManager.WorkingDir);
                svnUI.LocksText.text = "<b>Active Repository Locks:</b>\n----------------------------------\n";

                if (locks.Count == 0)
                {
                    svnUI.LocksText.text += "<color=yellow>No active locks found on server.</color>\n";
                }
                else
                {
                    foreach (var lockItem in locks)
                    {
                        bool isMe = !string.IsNullOrEmpty(svnManager.CurrentUserName) &&
                                    lockItem.Owner.Trim().Equals(svnManager.CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase);

                        string color = isMe ? "#00FF00" : "#FF4444";
                        string prefix = isMe ? "[MINE]" : "[LOCKED]";

                        svnUI.LocksText.text += $"<color={color}><b>{prefix}</b></color> {lockItem.Path}\n";
                        svnUI.LocksText.text += $"   User: <color=yellow>{lockItem.Owner}</color>\n";
                        if (!string.IsNullOrEmpty(lockItem.Comment))
                            svnUI.LocksText.text += $"   Comment: <i>\"{lockItem.Comment}\"</i>\n";
                        svnUI.LocksText.text += "----------------------------------\n";
                    }
                }
            }
            catch (Exception ex) { svnUI.LocksText.text += $"Error: {ex.Message}\n"; }
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
            svnUI.LogText.text += "<color=orange><b>[System]</b> Cleaning local database locks...</color>\n";
            await SvnRunner.RunAsync("cleanup --remove-locks", root);
            svnUI.LogText.text += "Local locks removed.\n";
        }

        
    }
}