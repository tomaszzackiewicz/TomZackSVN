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

        public async void LockModified()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            IsProcessing = true;
            svnUI.LogText.text = "Searching for modified files to lock...\n";

            try
            {
                // 1. Get status to find files to lock
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                var filesToLock = statusDict
                    .Where(x => x.Value.status == "M" || x.Value.status == "A")
                    .Select(x => x.Key)
                    .ToArray();

                if (filesToLock.Length > 0)
                {
                    svnUI.LogText.text += $"Locking {filesToLock.Length} files on server...\n";
                    string output = await SvnRunner.LockAsync(root, filesToLock);
                    svnUI.LogText.text += $"<color=green>Success!</color>\n{output}\n";

                    // 2. TRIGGER REFRESH via Manager
                    // Manager knows which module handles the tree (SVNStatus)
                    svnManager.Button_RefreshStatus();
                }
                else
                {
                    svnUI.LogText.text += "<color=yellow>No files found to lock.</color>\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Lock Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void UnlockAll()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            IsProcessing = true;
            svnUI.LogText.text = "Releasing your locks (status K)...\n";

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                var filesToUnlock = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("K"))
                    .Select(x => x.Key)
                    .ToArray();

                if (filesToUnlock.Length > 0)
                {
                    await SvnRunner.UnlockAsync(root, filesToUnlock);
                    svnUI.LogText.text += $"<color=green>Locks released.</color>\n";

                    // Trigger Refresh
                    svnManager.Button_RefreshStatus();
                }
                else
                {
                    svnUI.LogText.text += "You have no locked files.\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Unlock Error:</color> {ex.Message}\n";
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

            // Czyœcimy widok i dajemy znaæ, ¿e pracujemy
            svnUI.LocksText.text = "<b><color=orange>Scanning Repository for Locks...</color></b>\n";

            try
            {
                var locks = await GetDetailedLocks(svnManager.WorkingDir);

                // Tutaj czyœcimy komunikat o skanowaniu, ¿eby pokazaæ wyniki
                svnUI.LocksText.text = "<b><color=white>Active Repository Locks:</color></b>\n";
                svnUI.LocksText.text += "----------------------------------\n";

                if (locks == null || locks.Count == 0)
                {
                    // INFORMACJA O BRAKU BLOKAD
                    svnUI.LocksText.text += "<color=yellow>No active locks found in the repository.</color>\n";
                    svnUI.LocksText.text += "<size=80%>(Files are free to be locked and edited)</size>\n";
                }
                else
                {
                    foreach (var lockItem in locks)
                    {
                        // Sprawdzanie w³aœciciela (wymaga CurrentUserName w Managerze)
                        bool isMe = lockItem.Owner.Equals(svnManager.CurrentUserName, StringComparison.OrdinalIgnoreCase);
                        string color = isMe ? "#00FF00" : "#FF4444";
                        string prefix = isMe ? "[MY LOCK]" : "[LOCKED]";

                        svnUI.LocksText.text += $"<color={color}><b>{prefix}</b></color> {lockItem.Path}\n";
                        svnUI.LocksText.text += $"   Owner: <color=yellow>{lockItem.Owner}</color>\n";

                        if (!string.IsNullOrEmpty(lockItem.Comment))
                            svnUI.LocksText.text += $"   Comment: <i>\"{lockItem.Comment}\"</i>\n";

                        svnUI.LocksText.text += "----------------------------------\n";
                    }
                }
            }
            catch (Exception ex)
            {
                svnUI.LocksText.text += $"<color=red>Error while fetching locks:</color>\n{ex.Message}\n";
                Debug.LogError($"[SVN Lock] {ex}");
            }
            finally
            {
                IsProcessing = false;

                // Przewijamy Scroll View na sam¹ górê
                if (svnUI.LogScrollRect != null)
                {
                    Canvas.ForceUpdateCanvases();
                    svnUI.LogScrollRect.verticalNormalizedPosition = 1f;
                }
            }
        }

        private string FormatSvnDate(string rawDate)
        {
            // SVN XML dates look like: 2026-01-22T15:30:00.000000Z
            // Simple cleanup for readability
            if (rawDate.Contains("T"))
            {
                return rawDate.Split('T')[0] + " " + rawDate.Split('T')[1].Substring(0, 5);
            }
            return rawDate;
        }

        public async Task<List<SVNLockDetails>> GetDetailedLocks(string rootPath)
        {
            List<SVNLockDetails> locks = new List<SVNLockDetails>();

            // Command: svn info --xml -R (Recursive info in XML format)
            // We filter for paths that actually contain a <lock> tag
            string args = "info --xml -R";

            try
            {
                string xmlOutput = await SvnRunner.RunAsync(args, rootPath);
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlOutput);

                XmlNodeList entryNodes = doc.SelectNodes("//entry");

                foreach (XmlNode entry in entryNodes)
                {
                    XmlNode lockNode = entry.SelectSingleNode("lock");
                    if (lockNode != null)
                    {
                        SVNLockDetails details = new SVNLockDetails();
                        details.Path = entry.Attributes["path"].Value;
                        details.Owner = lockNode.SelectSingleNode("owner")?.InnerText ?? "Unknown";
                        details.CreationDate = lockNode.SelectSingleNode("created")?.InnerText ?? "";
                        details.Comment = lockNode.SelectSingleNode("comment")?.InnerText ?? "No comment";

                        // Logic: If the owner isn't the current system user, it's 'others'
                        // You can compare this with a 'CurrentUserName' variable if you have one
                        details.IsLockedByOthers = true;

                        locks.Add(details);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Failed to parse SVN Locks: " + ex.Message);
            }

            return locks;
        }

        public async void BreakAllLocks()
        {
            string root = svnManager.WorkingDir;
            svnUI.LogText.text += "<color=orange>Emergency: Breaking local locks...</color>\n";
            string output = await SvnRunner.RunAsync("cleanup --remove-locks", root);
            svnUI.LogText.text += "Local locks removed.\n";
        }
    }
}