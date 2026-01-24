using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNTree : SVNBase
    {
        private bool _isCurrentViewIgnored = false;

        public SVNTree(SVNUI ui, SVNManager svnManager) : base(ui, svnManager) { }

        /// <summary>
        /// Refreshes the tree view using the currently active filter (Modified or Ignored).
        /// </summary>
        public async void RefreshViewTree()
        {
            await RefreshTreeWithMode(_isCurrentViewIgnored);
        }

        private async Task RefreshTreeWithMode(bool onlyIgnored)
        {
            if (IsProcessing) return;

            string rootPath = svnManager.RepositoryUrl;
            if (string.IsNullOrEmpty(rootPath))
            {
                svnUI.LogText.text = "<color=red>Error: Please provide a valid path!</color>";
                return;
            }

            IsProcessing = true;
            _isCurrentViewIgnored = onlyIgnored;

            try
            {
                // Fetch visual tree data from SvnRunner based on expanded paths and filter
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(rootPath, svnManager.ExpandedPaths, onlyIgnored);

                // Update the main UI text display
                svnUI.TreeDisplay.text = result.tree;

                // Update dynamic statistics bar
                svnManager.UpdateAllStatisticsUI(result.stats, onlyIgnored);
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Tree Refresh Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }
        
        public void CollapseAll()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add(""); // Root only
            RefreshViewTree();
        }

        public void ExpandAll()
        {
            string root = svnManager.RepositoryUrl.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            // Collect all subdirectories recursively
            var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);

            foreach (var dir in directories)
            {
                string cleanPath = dir.Replace('\\', '/');
                string relativePath = cleanPath.Substring(root.Length).TrimStart('/');

                // Blacklist of folders that should never be expanded automatically
                if (IsPathBlacklisted(relativePath))
                    continue;

                svnManager.ExpandedPaths.Add(relativePath);
            }

            RefreshViewTree();
        }

        private bool IsPathBlacklisted(string relativePath)
        {
            return relativePath.Contains(".svn") ||
                   relativePath.StartsWith("Library") ||
                   relativePath.StartsWith("Intermediate") ||
                   relativePath.StartsWith("Saved") ||
                   relativePath.StartsWith("DerivedDataCache");
        }

        public async void ShowOnlyModified()
        {
            await FilterAndExpandTree(onlyIgnored: false);
        }

        public async void ShowOnlyIgnored()
        {
            await FilterAndExpandTree(onlyIgnored: true);
        }

        private async Task FilterAndExpandTree(bool onlyIgnored)
        {
            if (IsProcessing) return;

            // 1. Pobieramy œcie¿kê z managera zamiast z UI
            string root = svnManager.WorkingDir;

            // Podstawowe sprawdzenie œcie¿ki i referencji UI
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] FilterAndExpandTree: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] FilterAndExpandTree: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;
            _isCurrentViewIgnored = onlyIgnored;

            if (svnUI.LogText != null)
                svnUI.LogText.text = onlyIgnored ? "Filtering: Showing Ignored files..." : "Filtering: Showing Modified/New files...";

            try
            {
                // 2. Pobieramy s³ownik plików pasuj¹cych do filtra (Ignored lub Modified)
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, onlyIgnored);

                // 3. Czyœcimy i inicjalizujemy œcie¿ki rozwijane w managerze
                svnManager.ExpandedPaths.Clear();
                svnManager.ExpandedPaths.Add(""); // Root zawsze rozwiniêty

                // 4. Automatyczne rozwijanie folderów prowadz¹cych do znalezionych plików
                foreach (var relPath in statusDict.Keys)
                {
                    // Zamieniamy \ na / dla spójnoœci i dzielimy œcie¿kê
                    string normalizedPath = relPath.Replace('\\', '/');
                    string[] parts = normalizedPath.Split('/');

                    string currentPath = "";
                    for (int i = 0; i < parts.Length - 1; i++) // parts.Length - 1 bo nie rozwijamy samego pliku, tylko foldery
                    {
                        currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : currentPath + "/" + parts[i];

                        if (!svnManager.ExpandedPaths.Contains(currentPath))
                            svnManager.ExpandedPaths.Add(currentPath);
                    }
                }

                // 5. Generowanie wizualnego drzewa z nowymi zasadami rozwijania
                var result = await SvnRunner.GetVisualTreeWithStatsAsync(root, svnManager.ExpandedPaths, onlyIgnored);

                if (svnUI.TreeDisplay != null)
                    svnUI.TreeDisplay.text = result.tree;

                // 6. Podsumowanie statusu w logu
                LogStatusSummary(result.stats);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Filter Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"\n<color=red>Filter Error:</color> {ex.Message}";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void LogStatusSummary(SvnStats stats)
        {
            string summary = "<b>Current Stats:</b> ";

            if (stats.ModifiedCount > 0) summary += $"<color=yellow>{stats.ModifiedCount} Modified</color> | ";
            if (stats.NewFilesCount > 0) summary += $"<color=#00E5FF>{stats.NewFilesCount} New</color> | ";

            if (stats.ModifiedCount == 0 && stats.NewFilesCount == 0)
                summary += "<color=green>Working copy is clean.</color>";

            svnUI.LogText.text += summary + "\n";
        }
    }
}