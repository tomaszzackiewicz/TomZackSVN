using System;
using UnityEngine;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        private bool _isCurrentViewIgnored = false;

        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void ShowOnlyModified()
        {
            _isCurrentViewIgnored = false;
            ExecuteRefreshWithAutoExpand();
        }

        public void ShowOnlyIgnored()
        {
            _isCurrentViewIgnored = true;
            ExecuteRefreshWithAutoExpand();
        }

        public async void ExecuteRefreshWithAutoExpand()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            string root = svnManager.WorkingDir;
            svnUI.LogText.text += $"[{DateTime.Now:HH:mm:ss}] <color=#0FF>Refreshing...</color>\n";

            try
            {
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, true);

                long totalBytes = 0;
                string normalizedRoot = root.Trim().Replace("\\", "/");
                if (!normalizedRoot.EndsWith("/")) normalizedRoot += "/";

                svnManager.ExpandedPaths.Clear();
                svnManager.ExpandedPaths.Add("");

                foreach (var item in statusDict)
                {
                    string path = item.Key;
                    string stat = item.Value.status;

                    bool shouldExpand = _isCurrentViewIgnored ? (stat == "I") : (!string.IsNullOrEmpty(stat) && stat != "I");
                    if (shouldExpand) AddParentFoldersToExpanded(path);

                    if (!_isCurrentViewIgnored && !string.IsNullOrEmpty(stat) && "MA?C".Contains(stat))
                    {
                        string fullPath = System.IO.Path.GetFullPath(normalizedRoot + path);
                        if (System.IO.File.Exists(fullPath))
                        {
                            totalBytes += new System.IO.FileInfo(fullPath).Length;
                        }
                    }
                }

                string sizeStr = FormatBytes(totalBytes);
                svnUI.CommitSizeText.text = $"<color=yellow>Total Size of Changes to Commit: {sizeStr}</color>\n";

                var result = await SvnRunner.GetVisualTreeWithStatsAsync(root, svnManager.ExpandedPaths, _isCurrentViewIgnored);
                svnUI.TreeDisplay.text = result.tree;
                svnUI.CommitTreeDisplay.text = result.tree;
                svnManager.UpdateAllStatisticsUI(result.stats, false);
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Error:</color> {ex.Message}\n";
            }
            finally { IsProcessing = false; }
        }

        private string FormatBytes(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }
            return $"{dblSByte:0.##} {Suffix[i]}";
        }

        private void AddParentFoldersToExpanded(string filePath)
        {
            string[] parts = filePath.Split('/');
            string currentPath = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : $"{currentPath}/{parts[i]}";
                if (!svnManager.ExpandedPaths.Contains(currentPath))
                    svnManager.ExpandedPaths.Add(currentPath);
            }
        }

        public void CollapseAll()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");
            RefreshLocal();
        }

        public async void RefreshLocal()
        {
            // 1. Check if busy
            if (IsProcessing)
            {
                Debug.Log("[SVN] Refresh skipped: System is busy.");
                return;
            }

            IsProcessing = true;

            // 2. Immediate UI Feedback (The "Blink")
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            svnUI.LogText.text += $"[{timestamp}] <color=#0FF>Manual Refresh triggered...</color>\n";

            // Temporarily change text so the user knows the app is working
            svnUI.TreeDisplay.text = "<color=orange>Fetching local status...</color>";

            try
            {
                // 3. Run the visual update
                // Note: This uses the CURRENTly expanded paths. 
                // If you want it to find NEW changes automatically, 
                // call ExecuteRefreshWithAutoExpand() here instead!
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(
                    svnManager.WorkingDir,
                    svnManager.ExpandedPaths,
                    _isCurrentViewIgnored
                );

                // 4. Update Displays
                if (string.IsNullOrEmpty(result.tree))
                {
                    svnUI.TreeDisplay.text = "<i>No changes found in current view.</i>";
                }
                else
                {
                    svnUI.TreeDisplay.text = result.tree;
                }

                svnUI.CommitTreeDisplay.text = svnUI.TreeDisplay.text;
                svnManager.UpdateAllStatisticsUI(result.stats, false);

                svnUI.LogText.text += "<color=green>UI Updated.</color>\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Refresh Error:</color> {ex.Message}\n";
                Debug.LogError($"[SVN] Refresh Error: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public void RefreshView()
        {
            // Odœwie¿a widok zachowuj¹c obecny tryb (Modified lub Ignored)
            // i automatycznie rozwijaj¹c nowo wykryte zmiany
            ExecuteRefreshWithAutoExpand();
        }

        
    }
}