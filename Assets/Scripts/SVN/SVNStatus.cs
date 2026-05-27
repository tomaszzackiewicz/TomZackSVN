using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using System.Threading;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        public event Action OnSelectionChanged;

        private List<SvnTreeElement> _flatTreeData = new List<SvnTreeElement>();

        private List<SvnTreeElement> _commitTreeData;

        private bool _isCurrentViewIgnored = false;
        long totalCommitBytes = 0;
        private CancellationTokenSource _cts;
        private const bool ENABLE_FILE_SIZES = true;

        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            manager.OnProjectChanged += HandleProjectChanged;
        }

        private async void HandleProjectChanged(SVNProject project)
        {
            if (project == null) return;

            try
            {
                ClearCurrentData();
                ClearSVNTreeView();

                svnManager.WorkingDir = project.workingDir;
                svnManager.RepositoryUrl = project.repoUrl;
                svnManager.CurrentKey = project.privateKeyPath;

                await RefreshModifiedInternal();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVNStatus] Project switch failed: {ex}");
            }
        }

        public void ToggleFolderVisibility(SvnTreeElement folder)
        {
            List<SvnTreeElement> targetData = folder.IsCommitDelegate ? _commitTreeData : _flatTreeData;

            if (targetData == null || targetData.Count == 0) return;

            int startIndex = targetData.IndexOf(folder);
            if (startIndex == -1) return;

            string folderPath = folder.FullPath;
            string prefix = folderPath.EndsWith("/") ? folderPath : folderPath + "/";

            var localLookup = new Dictionary<string, SvnTreeElement>(32)
            {
                [folderPath] = folder
            };

            for (int i = startIndex + 1; i < targetData.Count; i++)
            {
                var e = targetData[i];

                if (!e.FullPath.StartsWith(prefix))
                {
                    break;
                }

                localLookup[e.FullPath] = e;

                string parentPath = GetParentPath(e.FullPath);
                if (string.IsNullOrEmpty(parentPath))
                {
                    continue;
                }

                if (localLookup.TryGetValue(parentPath, out var parent))
                {
                    e.IsVisible = parent.IsVisible && parent.IsExpanded;
                }
            }

            if (folder.IsCommitDelegate)
            {
                if (svnUI.SVNCommitTreeDisplay != null)
                    svnUI.SVNCommitTreeDisplay.RefreshUI(targetData, this);
            }
            else
            {
                if (svnUI.SvnTreeView != null)
                    svnUI.SvnTreeView.RefreshUI(targetData, this);
            }
        }

        private string GetParentPath(string path)
        {
            int lastSlash = path.LastIndexOf('/');
            return lastSlash > 0 ? path.Substring(0, lastSlash) : "";
        }

        public async Task RefreshAfterAction()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");
            ClearSVNTreeView();

            await ExecuteRefreshWithAutoExpand(force: true);
        }

        public async void ShowOnlyModified()
        {
            await RefreshModifiedInternal();
        }

        public async Task RefreshModifiedInternal()
        {
            svnManager.ExpandedPaths.Clear();
            svnManager.ExpandedPaths.Add("");

            ClearSVNTreeView();

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Refreshing...", "TREE", append: false);

            if (svnUI.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing...", "COMMIT_TREE", append: false);

            _isCurrentViewIgnored = false;

            await ExecuteRefreshWithAutoExpand(force: true);
        }


        public async Task ExecuteRefreshWithAutoExpand(bool force = false)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            if (IsProcessing && !force) return;
            if (!force) IsProcessing = true;

            try
            {
                if (svnUI != null)
                {
                    if (svnUI.TreeDisplay != null)
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.TreeDisplay,
                            "",
                            "TREE",
                            append: false
                        );

                        SVNLogBridge.UpdateUIField(
                            svnUI.TreeDisplay,
                            "Scanning local changes...",
                            "TREE",
                            append: false
                        );
                    }

                    if (svnUI.CommitTreeDisplay != null &&
                        svnUI.CommitTreeDisplay.gameObject.activeInHierarchy)
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.CommitTreeDisplay,
                            "",
                            "COMMIT_TREE",
                            append: false
                        );

                        SVNLogBridge.UpdateUIField(
                            svnUI.CommitTreeDisplay,
                            "Refreshing commit list...",
                            "COMMIT_TREE",
                            append: false
                        );
                    }
                }

                await WaitForNextFrame();

                string root = svnManager.WorkingDir;

                token.ThrowIfCancellationRequested();

                var statusDict = await GetChangesDictionaryAsync(root, token);

                token.ThrowIfCancellationRequested();

                if (svnUI != null)
                {
                    if (svnUI.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "", "TREE", append: false);

                    if (svnUI.CommitTreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "", "COMMIT_TREE", append: false);
                }

                if (statusDict == null || statusDict.Count == 0)
                {
                    ShowEmptyState();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await GetLocksDictionaryAsync(root, token);
                            // var statusModule = svnManager.GetModule<SVNStatus>();
                            // await statusModule.SetLockAsync();
                        }
                        catch
                        {
                            // ignore
                        }
                    });

                    return;
                }

                var (processedFlatData, finalBytes) = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    return BuildFlatTreeStructureText(root, statusDict);
                }, token);

                token.ThrowIfCancellationRequested();

                _flatTreeData = processedFlatData;
                totalCommitBytes = finalBytes;

                if (svnUI.SvnTreeView != null &&
                    svnUI.SvnTreeView.gameObject.activeInHierarchy)
                {
                    foreach (var e in _flatTreeData)
                        e.IsCommitDelegate = false;

                    svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
                }

                if (svnUI.SVNCommitTreeDisplay != null &&
                    svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy)
                {
                    _commitTreeData = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        return BuildCommitView(_flatTreeData);
                    }, token);

                    token.ThrowIfCancellationRequested();

                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);

                    if (svnUI.CommitSizeText != null)
                    {
                        svnUI.CommitSizeText.text =
                            $"Total Commit Size: <color=#FFFF00>{FormatSize(totalCommitBytes)}</color>";
                        UpdateSelectedSizeDisplay();
                    }
                }

                UpdateAllStatisticsUI(
                    CalculateStats(statusDict),
                    _isCurrentViewIgnored
                );

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var lockDict = await GetLocksDictionaryAsync(root, token);

                        token.ThrowIfCancellationRequested();

                        if (lockDict == null || lockDict.Count == 0)
                            return;

                        ApplyLockColors(_flatTreeData, lockDict);
                        // var statusModule = svnManager.GetModule<SVNStatus>();
                        // await statusModule.SetLockAsync();

                        token.ThrowIfCancellationRequested();

                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            if (token.IsCancellationRequested)
                                return;

                            RefreshAllUIComponents();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        // ignored
                    }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogError($"Background lock refresh failed: {ex.Message}");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("SVN Refresh operation canceled safely.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Refresh Error: {ex.Message}");
            }
            finally
            {
                if (!force)
                    IsProcessing = false;
            }
        }

        public async Task SetLockAsync()
        {
            string root = svnManager.WorkingDir;

            var lockDict = await GetLocksDictionaryAsync(root);

            if (lockDict == null || lockDict.Count == 0)
                return;

            ApplyLockColors(_flatTreeData, lockDict);
        }

        private async Task WaitForNextFrame()
        {
            await Task.Yield();
            await Task.Delay(1);
        }

        private void ShowEmptyState()
        {
            ResetTreeView();
            _flatTreeData.Clear();
            _commitTreeData?.Clear();

            if (svnUI.TreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected.</i>", "TREE", append: false);
            if (svnUI.CommitTreeDisplay != null)
                SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "<i>Nothing to commit.</i>", "COMMIT_TREE", append: false);

            UpdateAllStatisticsUI(new SvnStats(), _isCurrentViewIgnored);
        }

        private void RefreshAllUIComponents()
        {
            if (svnUI.SvnTreeView != null && svnUI.SvnTreeView.gameObject.activeInHierarchy)
            {
                foreach (var e in _flatTreeData) e.IsCommitDelegate = false;
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
            }

            bool isCommitActive = (svnUI.SVNCommitTreeDisplay != null && svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy);
            if (isCommitActive)
            {
                _commitTreeData = BuildCommitView(_flatTreeData);
                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);

                UpdateSelectedSizeDisplay();
            }
        }

        private void ApplyLockColors(List<SvnTreeElement> data, Dictionary<string, string> locks)
        {
            foreach (var e in data)
            {
                if (locks.TryGetValue(e.FullPath, out string lockStatus))
                {
                    string baseColor = "#FFFFFF";
                    if (e.Status.Contains("M")) baseColor = "#FFD700";
                    else if (e.Status.Contains("A")) baseColor = "#00FF00";
                    else if (e.Status.Contains("?")) baseColor = "#00E5FF";
                    else if (e.Status.Contains("D") || e.Status.Contains("!")) baseColor = "#FF4444";

                    string lockColor = lockStatus == "K" ? "#00FF00" : "#FF4444";
                    string cleanBaseStatus = e.Status.Trim();

                    if (string.IsNullOrEmpty(cleanBaseStatus) || cleanBaseStatus == "DIR")
                        e.Status = $"<color={lockColor}>{lockStatus}</color>";
                    else if (!cleanBaseStatus.Contains(lockStatus))
                        e.Status = $"<color={baseColor}>{cleanBaseStatus}</color><color={lockColor}>{lockStatus}</color>";
                }
            }
        }

        public void ClearCurrentData()
        {
            _flatTreeData?.Clear();
            _commitTreeData?.Clear();

            if (svnManager != null && svnManager.CurrentStatusDict != null)
                svnManager.CurrentStatusDict.Clear();

            totalCommitBytes = 0;
        }

        public void ClearSVNTreeView()
        {
            foreach (var svnTreeView in svnUI.SVNTreeViews)
            {
                svnTreeView.ClearView();
            }
        }

        public void ResetTreeView()
        {
            SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes detected. (Everything up to date)</i>", "TREE", append: false);
        }

        private SvnStats CalculateStats(Dictionary<string, SvnChangeInfo> statusDict)
        {
            SvnStats stats = new SvnStats();
            if (statusDict == null) return stats;

            foreach (var item in statusDict.Values)
            {
                bool isFolder = item.Size == "DIR";
                string s = item.Status;

                if (isFolder)
                {
                    stats.FolderCount++;
                    continue;
                }

                stats.FileCount++;

                if (s.Contains("M")) stats.ModifiedCount++;
                else if (s.Contains("A")) stats.AddedCount++;
                else if (s.Contains("?")) stats.NewFilesCount++;
                else if (s.Contains("D") || s.Contains("!")) stats.DeletedCount++;
                else if (s.Contains("C")) stats.ConflictsCount++;
            }

            return stats;
        }

        private List<SvnTreeElement> BuildCommitView(List<SvnTreeElement> source)
        {
            var visible = new HashSet<string>();

            foreach (var element in source)
            {
                bool isCommitable =
                    !string.IsNullOrWhiteSpace(element.Status) &&
                    element.Status != " " &&
                    element.Status != "DIR";

                if (!isCommitable)
                    continue;

                string current = element.FullPath;

                while (!string.IsNullOrEmpty(current))
                {
                    visible.Add(current);

                    current = GetParentPath(current);
                }
            }

            return source
                .Where(e => visible.Contains(e.FullPath))
                .ToList();
        }

        private void ShowElementAndParents(SvnTreeElement element, Dictionary<string, SvnTreeElement> lookup)
        {
            element.IsVisible = true;

            string parentPath = GetParentPath(element.FullPath);
            if (string.IsNullOrEmpty(parentPath)) return;

            if (lookup.TryGetValue(parentPath, out var parent))
            {
                if (!parent.IsVisible)
                {
                    ShowElementAndParents(parent, lookup);
                }
            }
        }

        private (List<SvnTreeElement> Elements, long TotalBytes) BuildFlatTreeStructureText(
    string root,
    Dictionary<string, SvnChangeInfo> statusDict)
        {
            var previousSelectionStates = new Dictionary<string, bool>();

            if (_flatTreeData != null)
            {
                foreach (var e in _flatTreeData)
                {
                    if (!string.IsNullOrEmpty(e.FullPath))
                    {
                        previousSelectionStates[e.FullPath] = e.IsChecked;
                    }
                }
            }

            var elements = new List<SvnTreeElement>(statusDict.Count * 2);
            var existingPaths = new HashSet<string>(statusDict.Count * 2);

            var sortedPaths = new List<string>(statusDict.Keys);
            sortedPaths.Sort(StringComparer.Ordinal);

            long localTotalBytes = 0;

            foreach (var relPath in sortedPaths)
            {
                string normalizedPath = relPath.Replace('\\', '/');

                if (normalizedPath.Contains(":/"))
                {
                    int trunkIdx = normalizedPath.LastIndexOf("trunk/");

                    if (trunkIdx != -1)
                    {
                        normalizedPath = normalizedPath.Substring(trunkIdx);
                    }
                }

                string[] parts = normalizedPath.Split('/');
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string partName = parts[i];

                    if (string.IsNullOrEmpty(partName))
                        continue;

                    currentPath = string.IsNullOrEmpty(currentPath)
                        ? partName
                        : currentPath + "/" + partName;

                    if (!existingPaths.Add(currentPath))
                        continue;

                    bool isLastPart = (i == parts.Length - 1);
                    bool isActuallyFolder;

                    if (!isLastPart)
                    {
                        isActuallyFolder = true;
                    }
                    else
                    {
                        isActuallyFolder =
                            statusDict.TryGetValue(currentPath, out var info) &&
                            info.Size == "DIR";
                    }

                    string displayStatus = " ";

                    if (isLastPart)
                    {
                        if (statusDict.TryGetValue(relPath, out var finalInfo))
                        {
                            displayStatus = finalInfo.Status;
                        }
                    }
                    else if (isActuallyFolder)
                    {
                        if (statusDict.TryGetValue(currentPath, out var folderInfo))
                        {
                            displayStatus = folderInfo.Status;
                        }
                        else
                        {
                            displayStatus = "DIR";
                        }
                    }

                    string fileSize = "";
                    long bytes = 0; // <-- ROZWIĄZANIE: Deklaracja wyciągnięta przed blok try

                    if (ENABLE_FILE_SIZES && !isActuallyFolder && isLastPart)
                    {
                        try
                        {
                            string physicalPath = Path.Combine(root, currentPath).Replace('\\', '/');
                            var fileInfo = new FileInfo(physicalPath);

                            if (fileInfo.Exists)
                            {
                                bytes = fileInfo.Length;
                                fileSize = FormatSize(bytes);

                                if (
                                    displayStatus != " " &&
                                    displayStatus != "DIR" &&
                                    displayStatus != "!" &&
                                    displayStatus != "D"
                                )
                                {
                                    localTotalBytes += bytes;
                                }
                            }
                            else
                            {
                                fileSize = "---";
                            }
                        }
                        catch
                        {
                            fileSize = "---";
                        }
                    }

                    bool isChecked =
                        !string.IsNullOrWhiteSpace(displayStatus) &&
                        displayStatus != " " &&
                        displayStatus != "?" &&
                        displayStatus != "I";

                    if (previousSelectionStates.TryGetValue(currentPath, out bool prev))
                    {
                        isChecked = prev;
                    }

                    elements.Add(new SvnTreeElement
                    {
                        FullPath = currentPath,
                        Name = partName,
                        Depth = i,
                        Status = displayStatus,
                        IsFolder = isActuallyFolder,
                        IsChecked = isChecked,
                        IsExpanded = true,
                        IsVisible = true,
                        Size = fileSize,
                        Bytes = (isActuallyFolder || !ENABLE_FILE_SIZES || !isLastPart) ? 0 : bytes
                    });
                }
            }

            var reversedFolders = new List<(SvnTreeElement Element, int Index)>();

            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i].IsFolder)
                {
                    reversedFolders.Add((elements[i], i));
                }
            }

            reversedFolders.Sort((a, b) =>
                b.Element.Depth.CompareTo(a.Element.Depth));

            foreach (var folderData in reversedFolders)
            {
                var folder = folderData.Element;
                int startIndex = folderData.Index;

                string prefix = folder.FullPath + "/";

                for (int j = startIndex + 1; j < elements.Count; j++)
                {
                    var child = elements[j];

                    if (!child.FullPath.StartsWith(prefix))
                        break;

                    if (child.IsChecked)
                    {
                        folder.IsChecked = true;
                        break;
                    }
                }
            }

            return (elements, localTotalBytes);
        }

        // Dokończenie uciętej metody na samym dole klasy SVNStatus:
        // W pliku SVNStatus.cs
        public void UpdateSelectedSizeDisplay()
        {
            if (svnUI == null || svnUI.CommitSizeText == null)
                return;

            if (_flatTreeData == null || _flatTreeData.Count == 0)
            {
                svnUI.CommitSizeText.text =
                    "Total Commit Size: <color=#FFFF00>0 B</color>";
                return;
            }

            long selectedBytes = 0;

            foreach (var element in _flatTreeData)
            {
                if (!element.IsChecked)
                    continue;

                if (element.IsFolder)
                    continue;

                // deleted/missing nie mają payloadu
                if (element.Status == "!" || element.Status == "D")
                    continue;

                selectedBytes += element.Bytes;
            }

            totalCommitBytes = selectedBytes;

            svnUI.CommitSizeText.text =
                $"Total Commit Size: <color=#FFFF00>{FormatSize(selectedBytes)}</color>";
        }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "0 B";

            string[] units = { "B", "KB", "MB", "GB", "TB" };

            int digit = (int)Math.Floor(Math.Log(bytes, 1024));

            digit = Mathf.Clamp(digit, 0, units.Length - 1);

            double value = bytes / Math.Pow(1024, digit);

            return value.ToString("F2") + " " + units[digit];
        }

        public void UpdateAllStatisticsUI(SvnStats stats, bool isIgnoredView)
        {
            if (svnUI == null) return;

            if (svnUI.StatsText != null)
            {
                string statsContent = isIgnoredView
                    ? $"<color=#444444><b>VIEW: IGNORED</b></color> | Folders: {stats.IgnoredFolderCount} | Files: {stats.IgnoredFileCount} | Total Ignored: <color=#FFFFFF>{stats.IgnoredCount}</color>"
                    : $"Folders: {stats.FolderCount} | Files: {stats.FileCount} | <color=#FFD700>Mod (M): {stats.ModifiedCount}</color> | <color=#00FF00>Add (A): {stats.AddedCount}</color> | <color=#00E5FF>New (?): {stats.NewFilesCount}</color> | <color=#FF4444>Del (D/!): {stats.DeletedCount}</color> | <color=#FF00FF>Conf (C): {stats.ConflictsCount}</color>";

                SVNLogBridge.UpdateUIField(svnUI.StatsText, statsContent, "STATS", append: false);
            }

            if (svnUI.CommitStatsText != null)
            {
                if (isIgnoredView)
                {
                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText, "<color=#FFCC00>Switch to 'Modified' view to see commit details.</color>", "STATS", append: false);
                }
                else
                {
                    int totalToCommit = stats.ModifiedCount + stats.AddedCount + stats.NewFilesCount + stats.DeletedCount;
                    string conflictPart = stats.ConflictsCount > 0 ? $" | <color=#FF0000><b> CONFLICTS (C): {stats.ConflictsCount} (Resolve first!)</b></color>" : "";
                    string commitStats = $"<b>Pending Changes:</b> <color=#FFD700>M: {stats.ModifiedCount}</color> | <color=#00FF00>A: {stats.AddedCount}</color> | <color=#00E5FF>?: {stats.NewFilesCount}</color> | <color=#FF4444>D/!: {stats.DeletedCount}</color> | <color=#FFFFFF><b>Total: {totalToCommit}</b></color>{conflictPart}";

                    SVNLogBridge.UpdateUIField(svnUI.CommitStatsText, commitStats, "STATS", append: false);
                }
            }
        }

        public static async Task<Dictionary<string, SvnChangeInfo>> GetChangesDictionaryAsync(string workingDir, CancellationToken cancellationToken = default)
        {
            const int statusCharIndex = 0;
            const int svnStatusPrefixLength = 8;
            const string allowedSvnStatuses = "MA?!DC~R";
            const string directoryLabel = "DIR";
            const string fileLabel = "FILE";

            workingDir = workingDir.Replace("\\", "/").TrimEnd('/');

            string output = await SvnRunner.RunAsync("status", workingDir, token: cancellationToken);

            var statusDict = new Dictionary<string, SvnChangeInfo>();

            if (string.IsNullOrWhiteSpace(output))
                return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (line.Length < svnStatusPrefixLength)
                    continue;

                string stat = line[statusCharIndex].ToString().ToUpper();

                if (!allowedSvnStatuses.Contains(stat))
                    continue;

                string rawPath = line.Substring(svnStatusPrefixLength).Trim();
                string cleanPath = SvnRunner.CleanSvnPath(rawPath).Replace("\\", "/");

                string fullPath = Path.Combine(workingDir, cleanPath).Replace("\\", "/");

                bool isDir = Directory.Exists(fullPath);
                bool isFile = File.Exists(fullPath);

                statusDict[cleanPath] = new SvnChangeInfo
                {
                    Status = stat,
                    Size = isDir ? directoryLabel : fileLabel,
                    Bytes = isFile ? new FileInfo(fullPath).Length : 0,
                    Exists = isDir || isFile
                };
            }

            return statusDict;
        }

        public struct SvnChangeInfo
        {
            public string Status;
            public string Size;
            public long Bytes;
            public bool Exists;
        }

        public static async Task<Dictionary<string, string>> GetLocksDictionaryAsync(string workingDir, CancellationToken cancellationToken = default)
        {
            const int lockCharIndex = 5;
            const int svnStatusPrefixLength = 8;

            var lockDict = new Dictionary<string, string>();

            try
            {
                string output = await SvnRunner.RunAsync("status -u", workingDir, token: cancellationToken);

                if (string.IsNullOrEmpty(output)) return lockDict;

                string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (line.Length < svnStatusPrefixLength) continue;

                    char lockCode = line[lockCharIndex];
                    if (lockCode == 'K' || lockCode == 'O')
                    {
                        string pathPart = line.Substring(svnStatusPrefixLength).Trim();
                        string cleanPath = SvnRunner.CleanSvnPath(pathPart).Replace("\\", "/");

                        if (cleanPath.Length > 0 && char.IsDigit(cleanPath[0]))
                        {
                            int firstSpace = cleanPath.IndexOf(' ');
                            if (firstSpace != -1) cleanPath = cleanPath.Substring(firstSpace).Trim();
                        }

                        if (!string.IsNullOrEmpty(cleanPath))
                        {
                            lockDict[cleanPath] = lockCode.ToString();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"SVN Lock check failed (probably offline): {ex.Message}");
            }

            return lockDict;
        }

        public void ToggleChildrenSelection(SvnTreeElement parentFolder, bool isChecked)
        {
            UpdateListSelection(_flatTreeData, parentFolder.FullPath, isChecked);

            NotifySelectionChanged();
        }

        private void UpdateListSelection(List<SvnTreeElement> list, string path, bool isChecked)
        {
            if (list == null || list.Count == 0) return;

            int startIndex = list.FindIndex(e => e.FullPath == path);
            if (startIndex == -1) return;

            list[startIndex].IsChecked = isChecked;
            string prefix = path + "/";
            for (int i = startIndex + 1; i < list.Count; i++)
            {
                if (list[i].FullPath.StartsWith(prefix))
                {
                    list[i].IsChecked = isChecked;
                }
                else
                {
                    break;
                }
            }
        }

        public List<SvnTreeElement> GetCurrentData()
        {
            return _flatTreeData;
        }

        // Nowa metoda dedykowana wyłącznie aktualizacji wyświetlanego rozmiaru
        // public void UpdateSelectedSizeDisplay()
        // {
        //     if (svnUI.CommitSizeText == null || _flatTreeData == null) return;

        //     totalCommitBytes = 0;

        //     foreach (var element in _flatTreeData)
        //     {
        //         if (element.IsChecked && !element.IsFolder)
        //         {
        //             // Wymaga dodania pola 'public long Bytes;' do klasy SvnTreeElement
        //             // i przypisania go podczas tworzenia elementu w BuildFlatTreeStructureText
        //             totalCommitBytes += element.Bytes;
        //         }
        //     }

        //     svnUI.CommitSizeText.text = $"Total Commit Size: <color=#FFFF00>{FormatSize(totalCommitBytes)}</color>";
        // }

        public void NotifySelectionChanged()
        {
            if (svnUI.SvnTreeView != null)
                svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);

            bool commitPanelVisible =
                svnUI.SVNCommitTreeDisplay != null &&
                svnUI.SVNCommitTreeDisplay.gameObject.activeInHierarchy;

            if (commitPanelVisible)
            {
                _commitTreeData = BuildCommitView(_flatTreeData);

                svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);
            }

            UpdateSelectedSizeDisplay();

            OnSelectionChanged?.Invoke();
        }
    }
}