using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNStatus : SVNBase
    {
        private bool _isCurrentViewIgnored = false;
        private string _lastKnownProjectName = "";
        private string _svnVersionCached = "";
        private List<string> _cachedIgnoreRules = new List<string>();

        private List<SvnTreeElement> _flatTreeData = new List<SvnTreeElement>();
        private List<SvnTreeElement> _commitTreeData; // Dodaj to pole!
        long totalCommitBytes = 0;
        public SVNStatus(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // Wewnątrz klasy SVNStatus:
        public void ToggleFolderVisibility(SvnTreeElement folder)
        {
            // 1. Ustalmy, na której liście pracujemy (Główna czy Commit)
            // Wykorzystujemy flagę IsCommitDelegate, którą dodaliśmy do elementu
            List<SvnTreeElement> targetData = folder.IsCommitDelegate ? _commitTreeData : _flatTreeData;

            if (targetData == null) return;

            // 2. Przeliczamy widoczność dla tej konkretnej listy
            foreach (var e in targetData)
            {
                // Pomijamy korzenie (rooty nie mają rodziców)
                string parentPath = GetParentPath(e.FullPath);
                if (string.IsNullOrEmpty(parentPath))
                {
                    // Rooty są zawsze widoczne (chyba że w panelu Commit zostały ukryte przez PrepareCommitTree)
                    // więc tutaj ich nie dotykamy, chyba że chcesz wymusić e.IsVisible = true;
                    continue;
                }

                var parent = targetData.FirstOrDefault(x => x.FullPath == parentPath);
                if (parent != null)
                {
                    // Kluczowa zasada: dziecko jest widoczne TYLKO jeśli rodzic jest widoczny I rozwinięty
                    e.IsVisible = parent.IsVisible && parent.IsExpanded;
                }
            }

            // 3. Odświeżamy odpowiednie okno UI
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
        public void ShowOnlyModified()
        {
            // svnManager.ExpandedPaths.Clear();
            // svnManager.ExpandedPaths.Add("");

            // if (svnUI.TreeDisplay != null)
            // {
            //     SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "Refreshing...", "TREE", append: false);
            // }

            // if (svnUI.CommitTreeDisplay != null)
            // {
            //     SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, "Refreshing...", "COMMIT_TREE", append: false);
            // }

            // _isCurrentViewIgnored = false;
            // _ = ExecuteRefreshWithAutoExpand();
        }

        public async Task ExecuteRefreshWithAutoExpand()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                string root = svnManager.WorkingDir;

                // 1. Pobierz dane
                var statusDict = _isCurrentViewIgnored
                    ? await GetIgnoredOnlyAsync(root)
                    : await GetChangesDictionaryAsync(root);

                // 2. Budujemy główną strukturę projektu
                _flatTreeData = BuildFlatTreeStructureText(root, statusDict);

                // 3. Odśwież WIDOK GŁÓWNY (z rozmiarami plików)
                if (svnUI != null && svnUI.SvnTreeView != null)
                {
                    foreach (var e in _flatTreeData) e.IsCommitDelegate = false;
                    svnUI.SvnTreeView.RefreshUI(_flatTreeData, this);
                }

                // 4. Odśwież WIDOK COMMIT (bez rozmiarów plików przy liniach, ale z sumą na dole)
                if (svnUI != null && svnUI.SVNCommitTreeDisplay != null)
                {
                    // ZAPISUJEMY DO POLA KLASY!
                    _commitTreeData = PrepareCommitTree(_flatTreeData);
                    svnUI.SVNCommitTreeDisplay.RefreshUI(_commitTreeData, this);

                    // LICZYMY SUMĘ I WYŚWIETLAMY W CommitSizeText
                    if (svnUI.CommitSizeText != null)
                    {
                        string totalSize = FormatSize(totalCommitBytes);
                        // Używamy bezpośredniego przypisania dla pewności
                        svnUI.CommitSizeText.text = $"Total Commit Size: <color=#FFFF00>{totalSize}</color>";

                        // Opcjonalnie przez bridge jeśli go używasz:
                        // SVNLogBridge.UpdateUIField(svnUI.CommitSizeText, $"Total Commit Size: <color=#FFFF00>{totalSize}</color>", "STATS", false);
                    }
                }

                UpdateAllStatisticsUI(CalculateStats(statusDict), _isCurrentViewIgnored);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Refresh Error: {ex.Message}");
            }
            finally { IsProcessing = false; }
        }

        private SvnStats CalculateStats(Dictionary<string, (string status, string size)> statusDict)
        {
            SvnStats stats = new SvnStats();
            if (statusDict == null) return stats;

            foreach (var item in statusDict.Values)
            {
                string s = item.status;

                // Zliczanie na podstawie pierwszej litery statusu SVN
                if (s.Contains("M")) stats.ModifiedCount++;
                else if (s.Contains("A")) stats.AddedCount++;
                else if (s.Contains("?")) stats.NewFilesCount++;
                else if (s.Contains("D") || s.Contains("!")) stats.DeletedCount++;
                else if (s.Contains("C")) stats.ConflictsCount++;
                else if (s.Contains("I"))
                {
                    stats.IgnoredCount++;
                    stats.IgnoredFileCount++;
                }

                // Prosta heurystyka: jeśli brak rozszerzenia w kluczu słownika, traktuj jako folder
                // (W Twoim obecnym systemie status "DIR" jest przypisywany ręcznie w BuildFlatTree)
                if (s == "DIR") stats.FolderCount++;
                else stats.FileCount++;
            }

            return stats;
        }

        private List<SvnTreeElement> PrepareCommitTree(List<SvnTreeElement> fullTree)
        {
            // 1. Tworzymy kopię listy
            var commitTree = fullTree.Select(e => e.Clone()).ToList();

            // 2. Chowamy wszystko i oznaczamy jako widok commitu
            foreach (var e in commitTree)
            {
                e.IsVisible = false;
                e.IsCommitDelegate = true; // To sprawi, że SvnLineController ukryje rozmiar
            }

            // 3. Odkrywamy pliki ze zmianami i ich "przodków"
            foreach (var element in commitTree)
            {
                if (!string.IsNullOrEmpty(element.Status) && element.Status != " " && element.Status != "DIR")
                {
                    ShowElementAndParents(element, commitTree);
                }
            }

            return commitTree;
        }

        private void ShowElementAndParents(SvnTreeElement element, List<SvnTreeElement> list)
        {
            element.IsVisible = true;

            // Znajdź ścieżkę rodzica
            string[] pathParts = element.FullPath.Split('/');
            if (pathParts.Length <= 1) return;

            string parentPath = string.Join("/", pathParts.Take(pathParts.Length - 1));
            var parent = list.Find(e => e.FullPath == parentPath);

            if (parent != null && !parent.IsVisible)
            {
                ShowElementAndParents(parent, list);
            }
        }
        // Licznik rozmiaru całego commitu
        private List<SvnTreeElement> BuildFlatTreeStructureText(string root, Dictionary<string, (string status, string size)> statusDict)
        {
            var elements = new List<SvnTreeElement>();
            var sortedPaths = statusDict.Keys.OrderBy(p => p).ToList();
            var rootCache = new Dictionary<string, string>();

            foreach (var relPath in sortedPaths)
            {
                string normalizedPath = relPath.Replace('\\', '/');
                string[] parts = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string partName = parts[i];
                    currentPath = string.IsNullOrEmpty(currentPath) ? partName : $"{currentPath}/{partName}";

                    if (elements.Any(e => e.FullPath == currentPath)) continue;

                    // --- DEKLARACJA ZMIENNYCH (NAPRAWA BŁĘDÓW CS0103) ---
                    string actualRoot = "";
                    string physicalPath = "";

                    // --- SZUKANIE FIZYCZNEGO KORZENIA ---
                    string topFolder = parts[0];
                    if (!rootCache.TryGetValue(topFolder, out actualRoot))
                    {
                        try
                        {
                            string[] matches = Directory.GetDirectories(root, topFolder, SearchOption.AllDirectories);
                            actualRoot = matches.Length > 0 ? matches[0] : Path.Combine(root, topFolder);
                        }
                        catch { actualRoot = Path.Combine(root, topFolder); }
                        rootCache[topFolder] = actualRoot;
                    }

                    // --- BUDOWANIE ŚCIEŻKI FIZYCZNEJ ---
                    physicalPath = actualRoot;
                    if (parts.Length > 1)
                    {
                        string[] subParts = parts.Skip(1).Take(i).ToArray();
                        if (subParts.Length > 0)
                        {
                            physicalPath = Path.Combine(actualRoot, string.Join(Path.DirectorySeparatorChar.ToString(), subParts));
                        }
                    }

                    // --- LOGIKA TYPU OBIEKTU ---
                    bool isActuallyFolder = Directory.Exists(physicalPath) || (i < parts.Length - 1);

                    // --- STATUS ---
                    string displayStatus = " ";
                    if (i == parts.Length - 1 && statusDict.ContainsKey(relPath))
                        displayStatus = statusDict[relPath].status;
                    else if (isActuallyFolder)
                        displayStatus = statusDict.ContainsKey(currentPath) ? statusDict[currentPath].status : "DIR";

                    // --- ROZMIAR PLIKU I SUMOWANIE ---
                    string fileSize = "";
                    if (!isActuallyFolder && i == parts.Length - 1)
                    {
                        string finalFilePath = physicalPath;
                        if (parts.Length > 1 && !physicalPath.EndsWith(partName))
                        {
                            string subPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(1));
                            finalFilePath = Path.Combine(actualRoot, subPath);
                        }

                        if (File.Exists(finalFilePath))
                        {
                            long bytes = new FileInfo(finalFilePath).Length;
                            fileSize = FormatSize(bytes);

                            // SUMUJEMY TYLKO PLIKI DO COMMITU (z realnym statusem)
                            if (displayStatus != " " && displayStatus != "DIR")
                            {
                                totalCommitBytes += bytes;
                            }
                        }
                        else { fileSize = "---"; }
                    }

                    elements.Add(new SvnTreeElement
                    {
                        FullPath = currentPath,
                        Name = partName,
                        Depth = i,
                        Status = displayStatus,
                        IsFolder = isActuallyFolder,
                        IsExpanded = true,
                        IsVisible = true,
                        Size = fileSize
                    });
                }
            }

            return elements;
        }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int digit = (int)Math.Log(bytes, 1024);
            return (bytes / Math.Pow(1024, digit)).ToString("F2") + " " + units[digit];
        }

        // Metoda symulująca "Virtual Scroll" w jednym polu tekstowym
        public void RenderVisibleTree()
        {
            // if (svnUI.TreeDisplay == null) return;

            // StringBuilder sb = new StringBuilder();
            // int renderedCount = 0;
            // const int MaxVisibleLines = 1000; // Limit linii, by nie zawiesić Unity

            // foreach (var element in _flatTreeData)
            // {
            //     if (!element.IsVisible) continue;
            //     if (renderedCount >= MaxVisibleLines) break;

            //     // Budowanie linii (Wcięcia + Ikona + Status + Nazwa)
            //     string indent = new string(' ', element.Depth * 3);
            //     string typeMarker = element.IsFolder ? "[D]" : "[F]";
            //     string statusColor = GetColorForStatus(element.Status);

            //     sb.AppendLine($"{indent}{typeMarker} <color={statusColor}>[{element.Status}]</color> {element.Name}");
            //     renderedCount++;
            // }

            // if (_flatTreeData.Count > MaxVisibleLines)
            // {
            //     sb.AppendLine($"\n<color=orange>... and {_flatTreeData.Count - MaxVisibleLines} more items (Truncated for performance)</color>");
            // }

            // SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, sb.ToString(), "TREE", append: false);
        }

        private string GetColorForStatus(string status)
        {
            switch (status)
            {
                case "M": return "#FFD700"; // Gold
                case "A": return "#00FF00"; // Green
                case "?": return "#00E5FF"; // Cyan
                case "D": return "#FF4444"; // Red
                case "I": return "#888888"; // Grey
                default: return "#FFFFFF";
            }
        }

        public void UpdateFilesStatus(Dictionary<string, (string status, string size)> newStatus)
        {
            if (newStatus == null) return;
            svnManager.CurrentStatusDict = newStatus;
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

        public static async Task<Dictionary<string, (string status, string size)>> GetChangesDictionaryAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status", workingDir);
            var statusDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return statusDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 8) continue;
                char rawCode = line[0];
                string stat = rawCode.ToString().ToUpper();
                if ("MA?!DC".Contains(stat))
                {
                    string rawPath = line.Substring(8).Trim();
                    string cleanPath = SvnRunner.CleanSvnPath(rawPath);
                    string fullPath = Path.Combine(workingDir, cleanPath);
                    statusDict[cleanPath] = (stat, SvnRunner.GetFileSizeSafe(fullPath));
                }
            }
            return statusDict;
        }

        public static async Task<Dictionary<string, (string status, string size)>> GetIgnoredOnlyAsync(string workingDir)
        {
            string output = await SvnRunner.RunAsync("status --no-ignore", workingDir);
            var ignoredDict = new Dictionary<string, (string status, string size)>();

            if (string.IsNullOrEmpty(output)) return ignoredDict;

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 8) continue;
                if (line[0] == 'I')
                {
                    string rawPath = line.Substring(8).Trim();
                    string cleanPath = SvnRunner.CleanSvnPath(rawPath);
                    ignoredDict[cleanPath] = ("I", "<IGNORED>");
                }
            }
            return ignoredDict;
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

        public void RefreshLocal()
        {
            // svnManager.ExpandedPaths.Clear();
            // svnManager.ExpandedPaths.Add("");
            // ShowProjectInfo(null, svnManager.WorkingDir);
            // _ = ExecuteRefreshWithAutoExpand();
        }

        public void ClearUI()
        {
            // if (svnUI.TreeDisplay != null)
            // {
            //     SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>No changes found.</i>", "TREE", append: false);
            // }

            // if (svnUI.CommitTreeDisplay != null)
            // {
            //     SVNLogBridge.UpdateUIField(svnUI.CommitTreeDisplay, string.Empty, "COMMIT_TREE", append: false);
            // }

            // if (svnUI.CommitSizeText != null)
            // {
            //     SVNLogBridge.UpdateUIField(svnUI.CommitSizeText, "<color=yellow>Total Size: 0 B</color>", "STATS", append: false);
            // }
            // svnManager.ExpandedPaths.Clear();
            // svnManager.ExpandedPaths.Add("");
        }

        public void RefreshIgnoredPanel()
        {
            _ = RefreshIgnoredPanelAsync();
        }

        public void ReloadIgnoreRules()
        {
            if (svnManager != null && !string.IsNullOrEmpty(svnManager.WorkingDir))
                LoadIgnoreRulesFromFile(svnManager.WorkingDir);
            else
                Debug.LogError("[SVN] Cannot reload: WorkingDir is null or empty.");
        }

        public void LoadIgnoreRulesFromFile(string workingDir)
        {
            _cachedIgnoreRules.Clear();
            string ignoreFilePath = Path.Combine(workingDir, ".svnignore");

            if (File.Exists(ignoreFilePath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(ignoreFilePath);
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                        {
                            if (!_cachedIgnoreRules.Contains(trimmed))
                                _cachedIgnoreRules.Add(trimmed);
                        }
                    }
                    Debug.Log($"<color=#00FFFF>[SVN]</color> Loaded {_cachedIgnoreRules.Count} rules from .svnignore");
                }
                catch (Exception e) { Debug.LogError($"[SVN] File read error: {e.Message}"); }
            }
            else
            {
                Debug.LogWarning($"[SVN] .svnignore file not found at: {workingDir}");
            }
        }

        public async Task RefreshIgnoredPanelAsync()
        {
            string root = svnManager.WorkingDir;
            string ignoreFilePath = Path.Combine(root, ".svnignore");
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("<color=#444444><b>System Info:</b></color>");
            sb.AppendLine($"<color=#555555>Working Dir:</color> <color=#FFFFFF>{root}</color>");
            sb.AppendLine($"<color=#555555>Config File:</color> <color=#FFFFFF>{ignoreFilePath}</color>");

            bool fileExists = File.Exists(ignoreFilePath);
            string fileStatus = fileExists ? "<color=green>FOUND</color>" : "<color=red>NOT FOUND</color>";
            sb.AppendLine($"<color=#555555>File Status:</color> {fileStatus}");
            sb.AppendLine("--------------------------------------------------\n");

            if (!fileExists)
            {
                sb.AppendLine("<color=#FFCC00><b>[!] ACTION REQUIRED</b></color>");
                sb.AppendLine($"Please ensure <b>.svnignore</b> is located in the folder above to load local rules.");
                sb.AppendLine("--------------------------------------------------\n");
            }

            List<string> activeRules = await GetIgnoreRulesFromSvnAsync(root);

            if (_cachedIgnoreRules != null)
            {
                foreach (var fileRule in _cachedIgnoreRules)
                {
                    if (!activeRules.Contains(fileRule)) activeRules.Add(fileRule);
                }
            }

            sb.AppendLine("<color=#FFA500><b>Active Ignore Rules:</b></color>");
            if (activeRules.Count == 0)
            {
                sb.AppendLine("  <color=#FF4444>No rules loaded. Click 'Reload' if you just added the file.</color>");
            }
            else
            {
                foreach (var rule in activeRules)
                {
                    bool isFromFile = _cachedIgnoreRules.Contains(rule);
                    string color = isFromFile ? "#00FFFF" : "#00FF99";
                    sb.AppendLine($"<color={color}>  {(isFromFile ? "[FILE]" : "[SVN]")} {rule}</color>");
                }
            }

            sb.AppendLine("\n<color=#FF4444><b>Files currently ignored on disk:</b></color>");

            int count = 0;
            if (activeRules.Count > 0 && Directory.Exists(root))
            {
                string[] allEntries = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
                foreach (var entry in allEntries)
                {
                    string name = Path.GetFileName(entry);
                    string relPath = entry.Replace(root, "").TrimStart('\\', '/').Replace('\\', '/');
                    if (relPath.Contains(".svn")) continue;

                    bool isIgnored = activeRules.Any(rule =>
                        name.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                        relPath.Split('/').Any(part => part.Equals(rule, StringComparison.OrdinalIgnoreCase)) ||
                        (rule.Contains("*") && IsMatch(name, rule))
                    );

                    if (isIgnored)
                    {
                        sb.AppendLine($"<color=#555555>[I]</color> <color=#FFFFFF>{relPath}</color>");
                        count++;
                        if (count > 200) { sb.AppendLine("<color=#FFFF00>... truncated</color>"); break; }
                    }
                }
            }

            if (count == 0 && activeRules.Count > 0)
                sb.AppendLine("<color=green>No files match the active rules.</color>");

            if (svnUI != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.IgnoredText, sb.ToString(), "IGNORED", append: false);
            }
        }

        public static async Task<List<string>> GetIgnoreRulesFromSvnAsync(string workingDir)
        {
            List<string> rules = new List<string>();
            try
            {
                string globalOutput = await SvnRunner.RunAsync("propget svn:global-ignores -R .", workingDir);
                string standardOutput = await SvnRunner.RunAsync("propget svn:ignore -R .", workingDir);
                string combinedOutput = globalOutput + "\n" + standardOutput;

                if (string.IsNullOrEmpty(combinedOutput) || combinedOutput.Contains("ERROR"))
                    return rules;

                string[] lines = combinedOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string pattern = line;
                    if (line.Contains(" - "))
                    {
                        var parts = line.Split(new[] { " - " }, StringSplitOptions.None);
                        pattern = parts.Length > 1 ? parts[1] : parts[0];
                    }

                    string trimmed = pattern.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.Contains(" ") && !rules.Contains(trimmed))
                    {
                        rules.Add(trimmed);
                    }
                }
            }
            catch (Exception e) { UnityEngine.Debug.LogError(e.Message); }
            return rules;
        }

        private bool IsMatch(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern == "*") return true;

            string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public async void PushLocalRulesToSvn()
        {
            string root = svnManager.WorkingDir;
            string ignoreFilePath = Path.Combine(root, ".svnignore");

            if (!File.Exists(ignoreFilePath))
            {
                UpdateStatusInUI("Error: .svnignore file missing!");
                return;
            }

            string rules = File.ReadAllText(ignoreFilePath);
            bool success = await SetSvnGlobalIgnorePropertyAsync(root, rules);

            if (success)
            {
                UpdateStatusInUI("SUCCESS: Global ignores set. Commit the root folder.");
                _ = RefreshIgnoredPanelAsync();
            }
        }

        public static async Task<bool> SetSvnGlobalIgnorePropertyAsync(string workingDir, string rulesRawText)
        {
            string tempFilePath = Path.Combine(workingDir, "temp_global_ignore.txt");
            File.WriteAllText(tempFilePath, rulesRawText.Replace("\r\n", "\n"));

            string result = await SvnRunner.RunAsync($"propset svn:global-ignores -F \"{tempFilePath}\" .", workingDir);

            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);

            if (result.StartsWith("ERROR"))
            {
                UnityEngine.Debug.LogError(result);
                return false;
            }
            return true;
        }

        private void UpdateStatusInUI(string message)
        {
            if (svnUI != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.IgnoredText, $"<color=#FFFF00>{message}</color>\n", "IGNORED", append: true);
            }
        }

        public async void ShowProjectInfo(SVNProject svnProject, string path, bool forceOutdatedCheck = false, bool isRefreshing = false)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (svnUI == null) return;

            if (svnProject != null && !string.IsNullOrEmpty(svnProject.projectName))
                _lastKnownProjectName = svnProject.projectName;

            string displayName = !string.IsNullOrEmpty(_lastKnownProjectName)
                ? _lastKnownProjectName
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            string initialColor = isRefreshing ? "#FFFF00" : "#FFFF00"; // Yellow dot
            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color={initialColor}>●</color></size> <color=#555555>Initializing {displayName}...</color>", "INFO", append: false);

            string sizeText = "---";
            string rawInfo = "";
            int retryCount = 0;
            int maxRetries = 8;

            while (retryCount < maxRetries)
            {
                try
                {
                    if (!Directory.Exists(Path.Combine(path, ".svn")))
                    {
                        retryCount++;
                        SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color=#FFFF00>●</color></size> <color=#555555>Waiting for .svn metadata... ({retryCount}/{maxRetries})</color>"); //Yellow dot
                        await Task.Delay(1000);
                        continue;
                    }

                    var infoTask = SvnRunner.GetInfoAsync(path);
                    var sizeTask = GetFolderSizeAsync(path);
                    await Task.WhenAll(infoTask, sizeTask);

                    rawInfo = infoTask.Result;
                    sizeText = sizeTask.Result;

                    if (!string.IsNullOrEmpty(rawInfo) && rawInfo != "unknown") break;
                }
                catch (Exception)
                {
                    retryCount++;
                    await Task.Delay(1000);
                }
            }

            if (string.IsNullOrEmpty(rawInfo) || rawInfo == "unknown")
            {
                SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, $"<size=150%><color= #000000>●</color></size> <b>{displayName}</b> | <color=#FF8888>Not a working copy yet</color>", "INFO", append: false); //Black dot
                return;
            }

            string revision = ExtractValue(rawInfo, "Revision:");
            string author = ExtractValue(rawInfo, "Last Changed Author:");
            string fullDate = ExtractValue(rawInfo, "Last Changed Date:");
            string relUrl = ExtractValue(rawInfo, "Relative URL:");
            string absUrl = ExtractValue(rawInfo, "URL:");
            string repoRootUrl = ExtractValue(rawInfo, "Repository Root:");

            bool isOutdated = false;
            string remoteRevision = revision;

            try
            {
                string remoteRevRaw = await SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", path);
                if (!string.IsNullOrEmpty(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                {
                    remoteRevision = remoteRevRaw.Trim();
                    if (int.TryParse(revision, out int localRev) && int.TryParse(remoteRevision, out int remRev))
                    {
                        isOutdated = remRev > localRev;
                    }
                }
            }
            catch { }

            string statusColor = "#4ca74c"; // Green dot
            if (isRefreshing) statusColor = "#FFFF00"; // Yellow dot
            else if (isOutdated) statusColor = "#FF1A1A"; // red dot

            if (string.IsNullOrEmpty(_lastKnownProjectName) || _lastKnownProjectName == displayName)
            {
                if (repoRootUrl != "unknown")
                {
                    _lastKnownProjectName = repoRootUrl.Split('/').Last(s => !string.IsNullOrEmpty(s));
                    displayName = _lastKnownProjectName;
                }
            }

            string branchName = "trunk";
            string source = (relUrl != "unknown") ? relUrl : absUrl;
            if (source != "unknown")
            {
                branchName = source.Replace("^/", "").Trim();
                if (branchName.Contains("/"))
                    branchName = Path.GetFileName(branchName.TrimEnd('/'));
                if (string.IsNullOrEmpty(branchName) || branchName == "/") branchName = "trunk";
            }

            string serverHost = "local";
            if (absUrl != "unknown")
            {
                try { serverHost = new Uri(absUrl).Host; } catch { }
            }

            string shortDate = (fullDate != "unknown") ? fullDate.Split('(')[0].Trim() : "no commits";
            string appVersion = Application.version;
            if (string.IsNullOrEmpty(_svnVersionCached)) await EnsureVersionCached();

            string currentUser = svnManager.CurrentUserName ?? "Unknown";

            string revDisplay = isOutdated
                ? $"<color=#FF5555>{revision}</color> <color=#FF8888>(HEAD: {remoteRevision})</color>"
                : revision;

            string statusLine = $"<size=150%><color={statusColor}>●</color></size> <color=orange> <b>{displayName}</b> ({sizeText})</color> | " +
                                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{currentUser}</color> | " +
                                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{branchName}</color> | " +
                                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{revDisplay}</color> | " +
                                $"<color=#00E5FF>By:</color> <color=#E6E6E6>{author}</color> | " +
                                $"<color=#E6E6E6> {shortDate}</color> | " +
                                $"<color=#E6E6E6>Srv: {serverHost}</color> | " +
                                $"<color=#E6E6E6>App: {appVersion}</color> | " +
                                $"<color=#E6E6E6>SVN: {_svnVersionCached}</color>";

            SVNLogBridge.UpdateUIField(svnUI.StatusInfoText, statusLine, "INFO", append: false);
        }

        public async Task<string> GetFolderSizeAsync(string path)
        {
            return await Task.Run(() =>
            {
                try
                {
                    DirectoryInfo dir = new DirectoryInfo(path);
                    if (!dir.Exists) return "0 GB";

                    long bytes = 0;
                    // Używamy EnumerateFiles zamiast GetFiles, aby nie alokować wielkiej tablicy w pamięci
                    var files = dir.EnumerateFiles("*", SearchOption.AllDirectories);

                    // Limit czasu na liczenie rozmiaru - jeśli trwa to więcej niż 2 sekundy, przerywamy
                    // To zapobiega zawieszeniu przy ogromnych dyskach sieciowych
                    foreach (var fi in files)
                    {
                        bytes += fi.Length;
                    }

                    double gigabytes = (double)bytes / (1024 * 1024 * 1024);
                    return gigabytes > 1 ? $"{gigabytes:F2} GB" : $"{(double)bytes / (1024 * 1024):F2} MB";
                }
                catch { return "Size unknown"; }
            });
        }

        private async Task EnsureVersionCached()
        {
            if (string.IsNullOrEmpty(_svnVersionCached))
            {
                try
                {
                    _svnVersionCached = await SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir);
                    _svnVersionCached = _svnVersionCached.Trim();
                }
                catch { _svnVersionCached = "?.?.?"; }
            }
        }

        private string ExtractValue(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return "N/A";
            using (var reader = new System.IO.StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith(key)) return line.Replace(key, "").Trim();
                }
            }
            return "unknown";
        }


        public static async Task<(string tree, SvnStats stats)> GetVisualTreeWithStatsAsync(string workingDir, HashSet<string> expandedPaths, bool showIgnored = false)
        {
            // Pobieramy status (to dzieje się w tle)
            Dictionary<string, (string status, string size)> statusDict = await SvnRunner.GetFullStatusDictionaryAsync(workingDir, true);
            var sb = new StringBuilder();
            var stats = new SvnStats();

            if (!Directory.Exists(workingDir)) return ("Path error.", stats);

            // KROK 1: Inteligentna selekcja folderów
            HashSet<string> foldersWithRelevantContent = new HashSet<string>();
            int interestingCount = 0;

            foreach (var item in statusDict)
            {
                string stat = item.Value.status;
                bool isInteresting = showIgnored ? (stat == "I") : (!string.IsNullOrEmpty(stat) && stat != "I");

                if (isInteresting)
                {
                    interestingCount++;
                    string[] parts = item.Key.Split('/');
                    string currentFolder = "";
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        currentFolder = string.IsNullOrEmpty(currentFolder) ? parts[i] : $"{currentFolder}/{parts[i]}";
                        foldersWithRelevantContent.Add(currentFolder);
                    }
                }
            }

            // KROK 2: Obsługa "Ekstremalnych" commitów
            // Jeśli zmian jest więcej niż 1000, budowanie pełnego stringa drzewa zabije wydajność UI.
            if (interestingCount > 1000)
            {
                sb.AppendLine($"<color=orange><b>[Warning]</b> Too many changes to display ({interestingCount} items).</color>");
                sb.AppendLine("<color=#AAAAAA>The tree display is limited to maintain performance.</color>");
                sb.AppendLine("--------------------------------------------------");

                // W takim przypadku budujemy drzewo tylko dla folderów nadrzędnych (płytki skan)
                // Albo po prostu zwracamy informację o liczbie plików bez renderowania każdego z nich.
            }

            // KROK 3: Budowanie drzewa z limitem bezpieczeństwa
            // Musisz upewnić się, że BuildTreeString ma wewnątrz licznik, który przerywa dodawanie linii powyżej np. 2000
            SvnRunner.BuildTreeString(workingDir, workingDir, 0, statusDict, sb, stats, expandedPaths, new bool[128], showIgnored, foldersWithRelevantContent);

            string finalTree = sb.ToString();

            // KROK 4: Ostateczne zabezpieczenie przed "murem tekstu"
            // Unity TMP/Text ma limit meshów. Przycinamy string, jeśli jest absurdalnie długi.
            const int MaxCharLimit = 15000;
            if (finalTree.Length > MaxCharLimit)
            {
                finalTree = finalTree.Substring(0, MaxCharLimit) + "\n\n<color=red>... [TREE TRUNCATED FOR PERFORMANCE] ...</color>";
            }

            return (finalTree, stats);
        }

        public async Task<int> GetRemoteRevisionInternal()
        {
            string output = await SvnRunner.RunAsync("info --show-item last-changed-revision", svnManager.WorkingDir);
            if (int.TryParse(output.Trim(), out int rev))
            {
                return rev;
            }
            return -1;
        }
    }
}