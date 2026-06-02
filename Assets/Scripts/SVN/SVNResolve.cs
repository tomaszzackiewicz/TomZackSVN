using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using System.Xml.Linq;
using System.Threading;

namespace SVN.Core
{
    public class SVNResolve : SVNBase
    {
        private bool isRefreshing = false;
        private bool _uiRefreshing = false;

        private readonly SemaphoreSlim _resolveLock = new(1, 1);

        public enum SVNConflictType { Text, Manual, Tree }

        public enum SVNConflictState { Pending, ManualEditing, Resolving, Resolved }

        public class SVNConflictData
        {
            public string Path;
            public SVNConflictType Type;
            public SVNConflictState State;
        }

        private readonly Dictionary<string, SVNConflictData> _conflictCache =
    new Dictionary<string, SVNConflictData>();

        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
        }

        public async void AutoRefreshConflictList()
        {
            await AutoRefreshConflictListAsync();
        }

        private async Task AutoRefreshConflictListAsync()
        {
            if (isRefreshing)
                return;

            isRefreshing = true;

            try
            {
                string root = svnManager.WorkingDir;

                if (string.IsNullOrWhiteSpace(root))
                {
                    LogBoth("<color=red>No working directory.</color>");
                    return;
                }

                await Task.Delay(120);

                var conflicts = await GetConflicts(root);

                LogBoth(conflicts.Count > 0
                    ? $"<b>[Resolve]</b> Detected conflicts: {conflicts.Count}"
                    : "<b>[Resolve]</b> No conflicts");

                if (!_uiRefreshing)
                    await RefreshConflictUI();
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Refresh conflict list failed:</color> {ex.Message}");
            }
            finally
            {
                isRefreshing = false;
            }
        }

        private async Task ResolveMany(
    IEnumerable<SVNConflictData> conflicts,
    string strategy)
        {
            if (conflicts == null)
                return;

            var snapshot =
                conflicts
                    .Where(x =>
                        x != null &&
                        !string.IsNullOrWhiteSpace(x.Path))
                    .Select(x => x.Path)
                    .Distinct()
                    .ToList();

            foreach (var path in snapshot)
            {
                await ResolveSingle(
                    path,
                    strategy);
            }

            await Task.Delay(150);

            var latest =
                await GetConflicts(
                    svnManager.WorkingDir);

            _conflictCache.Clear();

            foreach (var c in latest)
            {
                _conflictCache[c.Path] = c;
            }

            if (!_uiRefreshing)
                await RefreshConflictUI();
        }

        private async Task<List<SVNConflictData>> GetConflicts(string root)
        {
            try
            {
                string xml = await SvnRunner.RunAsync("status --xml", root);

                if (string.IsNullOrWhiteSpace(xml))
                {
                    LogBoth("<color=red>[Resolve] Empty SVN XML</color>");
                    return new List<SVNConflictData>();
                }

                XDocument doc = XDocument.Parse(xml);

                var result = new List<SVNConflictData>();
                var seen = new HashSet<string>();

                foreach (var entry in doc.Descendants("entry"))
                {
                    var wc = entry.Element("wc-status");
                    if (wc == null) continue;

                    string item = wc.Attribute("item")?.Value;
                    string props = wc.Attribute("props")?.Value;
                    string tree = wc.Attribute("tree-conflicted")?.Value;

                    bool isConflict =
                        item == "conflicted" ||
                        props == "conflicted" ||
                        tree == "true";

                    if (!isConflict)
                        continue;

                    string path = NormalizePath(entry.Attribute("path")?.Value);
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    if (!seen.Add(path))
                        continue;

                    var type =
                        tree == "true"
                            ? SVNConflictType.Tree
                            : SVNConflictType.Text;

                    if (_conflictCache.TryGetValue(path, out var cached) &&
                        cached.State == SVNConflictState.ManualEditing)
                    {
                        type = SVNConflictType.Manual;
                    }

                    var data = new SVNConflictData
                    {
                        Path = path,
                        Type = type,
                        State = _conflictCache.TryGetValue(path, out var old)
                            ? old.State
                            : SVNConflictState.Pending
                    };

                    _conflictCache[path] = data;
                    result.Add(data);
                }

                var valid = result.Select(x => x.Path).ToHashSet();
                foreach (var key in _conflictCache.Keys.ToList())
                {
                    if (!valid.Contains(key))
                        _conflictCache.Remove(key);
                }

                return result.OrderBy(x => x.Path).ToList();
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>GetConflicts error:</color> {ex.Message}");
                return new List<SVNConflictData>();
            }
        }

        public async void MarkAsResolved()
        {
            if (IsProcessing) return;

            IsProcessing = true;

            try
            {
                var conflicts = await GetConflicts(svnManager.WorkingDir);

                if (conflicts.Count == 0)
                {
                    LogBoth("<color=yellow>No conflicts found.</color>");
                    return;
                }

                foreach (var c in conflicts)
                {
                    string full = Path.Combine(svnManager.WorkingDir, c.Path);

                    if (File.Exists(full))
                    {
                        string content = await File.ReadAllTextAsync(full);

                        if (content.Contains("<<<<<<<") ||
                            content.Contains("=======") ||
                            content.Contains(">>>>>>>"))
                        {
                            LogBoth($"<color=red>Abort:</color> {c.Path} still has markers");
                            return;
                        }
                    }
                }

                await ResolveMany(conflicts, "working");

                LogBoth("<color=green>Marked as resolved.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void ResolveTheirs()
        {
            if (IsProcessing) return;

            IsProcessing = true;
            try
            {
                var conflicts = await GetConflicts(svnManager.WorkingDir);

                await ResolveMany(conflicts, "theirs-full");

                LogBoth("<color=green>Theirs-full batch resolved.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void ResolveMine()
        {
            if (IsProcessing) return;

            IsProcessing = true;
            try
            {
                var conflicts = await GetConflicts(svnManager.WorkingDir);

                await ResolveMany(conflicts, "mine-full");

                LogBoth("<color=green>Mine-full batch resolved.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void OpenInEditor()
        {
            if (IsProcessing) return;

            string root = svnManager.WorkingDir;
            string editorPath = svnManager.MergeToolPath;

            if (string.IsNullOrEmpty(editorPath))
            {
                editorPath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                if (string.IsNullOrEmpty(editorPath))
                {
                    LogBoth("<color=red>Error:</color> Merge tool path is not set!");
                    return;
                }
            }

            try
            {
                IsProcessing = true;

                string targetFile = null;

                if (svnUI.ResolveTargetFileInput != null &&
                    !string.IsNullOrWhiteSpace(svnUI.ResolveTargetFileInput.text))
                {
                    targetFile = NormalizePath(svnUI.ResolveTargetFileInput.text.Trim());
                }
                else
                {
                    var first = _conflictCache
                        .Values
                        .OrderBy(x => x.Path)
                        .FirstOrDefault(x => x.State != SVNConflictState.Resolved);

                    if (first != null)
                        targetFile = first.Path;
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    var conflicts = await GetConflicts(root);

                    foreach (var c in conflicts)
                    {
                        if (_conflictCache.TryGetValue(c.Path, out var existing))
                            existing.Type = c.Type;
                        else
                            _conflictCache[c.Path] = c;
                    }

                    targetFile = conflicts.FirstOrDefault()?.Path;
                }

                if (string.IsNullOrEmpty(targetFile))
                {
                    LogBoth("<color=yellow>No conflicted file found to open.</color>");
                    return;
                }

                string fullPath = Path.Combine(root, targetFile);

                LogBoth($"Opening editor: <color=green>{targetFile}</color>");

                System.Diagnostics.Process.Start(editorPath, $"\"{fullPath}\"");

                if (_conflictCache.TryGetValue(targetFile, out var conflict))
                {
                    conflict.Type = SVNConflictType.Manual;
                    conflict.State = SVNConflictState.ManualEditing;
                }
                else
                {
                    _conflictCache[targetFile] = new SVNConflictData
                    {
                        Path = targetFile,
                        Type = SVNConflictType.Manual,
                        State = SVNConflictState.ManualEditing
                    };
                }

                await RefreshConflictUI();
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Exception:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task RefreshConflictUI()
        {
            if (_uiRefreshing)
                return;

            _uiRefreshing = true;

            try
            {
                var root = svnManager.WorkingDir;
                var conflicts = await GetConflicts(root);

                Transform parent = svnUI.ResolveConsoleContent.transform;

                for (int i = parent.childCount - 1; i >= 0; i--)
                {
                    var child = parent.GetChild(i);
                    GameObject.Destroy(child.gameObject);
                }

                await Task.Yield();

                foreach (var c in conflicts)
                {
                    bool markers = await HasConflictMarkers(c.Path);

                    var obj = GameObject.Instantiate(svnUI.ConflictPrefab, parent);
                    obj.SetActive(true);

                    var item = obj.GetComponent<SVNConflictItem>();
                    if (item == null)
                    {
                        LogBoth("<color=red>SVNConflictItem missing!</color>");
                        continue;
                    }

                    item.Setup(c.Path, ConvertConflictType(c.Type), markers);
                }

                Canvas.ForceUpdateCanvases();

                LogBoth($"[Resolve UI] Generated {conflicts.Count} conflict items.");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>RefreshConflictUI failed:</color> {ex.Message}");
            }
            finally
            {
                _uiRefreshing = false;
            }
        }

        private async Task<bool> HasConflictMarkers(string path)
        {
            string fullPath =
                Path.Combine(svnManager.WorkingDir, path);

            if (!File.Exists(fullPath))
                return false;

            string content =
                await File.ReadAllTextAsync(fullPath);

            return content.Contains("<<<<<<<") ||
                   content.Contains("=======") ||
                   content.Contains(">>>>>>>");
        }

        private SVNConflictItem.ConflictType ConvertConflictType(SVNConflictType type)
        {
            switch (type)
            {
                case SVNConflictType.Text:
                    return SVNConflictItem.ConflictType.Text;

                case SVNConflictType.Manual:
                    return SVNConflictItem.ConflictType.Manual;

                case SVNConflictType.Tree:
                    return SVNConflictItem.ConflictType.Tree;

                default:
                    return SVNConflictItem.ConflictType.Text;
            }
        }

        private List<string> GetActiveConflictPaths()
        {
            return _conflictCache.Values
                .Where(x => x != null)
                .Select(x => x.Path)
                .ToList();
        }

        public async void ResolveAllMine()
        {
            if (IsProcessing) return;

            IsProcessing = true;

            try
            {
                var paths = GetActiveConflictPaths();
                await ResolveMany(paths.Select(p => new SVNConflictData { Path = p }), "mine-full");

                LogBoth("<color=green>All conflicts resolved (mine-full).</color>");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void ResolveAllTheirs()
        {
            if (IsProcessing) return;

            IsProcessing = true;

            try
            {
                var paths = GetActiveConflictPaths();
                await ResolveMany(paths.Select(p => new SVNConflictData { Path = p }), "theirs-full");

                LogBoth("<color=green>All conflicts resolved (theirs-full).</color>");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void DeleteAllObstructions()
        {
            if (IsProcessing)
                return;

            IsProcessing = true;

            try
            {
                var conflicts = await GetConflicts(svnManager.WorkingDir);

                if (conflicts == null || conflicts.Count == 0)
                {
                    LogBoth("<color=yellow>No conflicts found.</color>");
                    return;
                }

                var snapshot = conflicts
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                    .Select(x => x.Path)
                    .ToList();

                LogBoth($"[Batch Resolve] {snapshot.Count} items");

                foreach (var path in snapshot)
                {
                    await DeleteObstruction(path, false);
                }

                await SVNManager.Instance.RunSvn("cleanup");
                await svnManager.RefreshStatus();

                await Task.Delay(250);

                if (!_uiRefreshing)
                    await RefreshConflictUI();

                LogBoth("<color=green>All obstructions removed.</color>");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>DeleteAllObstructions error:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task ResolveSingleMine(string path)
        {
            await ResolveSingle(path, "mine-full");
        }

        public async Task ResolveSingleTheirs(string path)
        {
            await ResolveSingle(path, "theirs-full");
        }

        private async Task ResolveSingle(string path, string strategy)
        {
            path = NormalizePath(path);

            await _resolveLock.WaitAsync();

            try
            {
                LogBoth($"[Resolve] {strategy} -> {path}");

                if (_conflictCache.TryGetValue(path, out var data))
                    data.State = SVNConflictState.Resolving;

                string full = Path.Combine(svnManager.WorkingDir, path);

                string parent = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parent))
                    parent = ".";

                if (strategy == "theirs-full")
                {
                    try
                    {
                        if (File.Exists(full))
                            File.Delete(full);

                        if (Directory.Exists(full))
                            Directory.Delete(full, true);
                    }
                    catch { }

                    await SVNManager.Instance.RunSvn(
                        $"resolve --accept theirs-full \"{path}\"");
                }
                else
                {
                    await SVNManager.Instance.RunSvn(
                        $"resolve --accept mine-full \"{path}\"");
                }

                await SVNManager.Instance.RunSvn("cleanup");

                await SVNManager.Instance.RunSvn($"resolve --accept working \"{path}\"");

                await SVNManager.Instance.RunSvn("cleanup --remove-unversioned");

                _conflictCache.Remove(path);

                await svnManager.RefreshStatus();
                await Task.Delay(120);

                if (!_uiRefreshing)
                    await RefreshConflictUI();

                svnManager
                    .GetModule<SVNExternal>()
                    .RefreshWindowsShellIcons(path);

                LogBoth($"<color=green>Resolved ({strategy}):</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Error resolving {path}:</color> {ex.Message}");
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        public async Task OpenSingle(string path)
        {
            if (IsProcessing)
                return;

            string editorPath =
                svnManager.MergeToolPath;

            if (string.IsNullOrEmpty(editorPath))
            {
                editorPath =
                    PlayerPrefs.GetString(
                        SVNManager.KEY_MERGE_TOOL,
                        "");

                if (string.IsNullOrEmpty(editorPath))
                {
                    LogBoth("<color=red>Merge tool path missing!</color>");
                    return;
                }
            }

            try
            {
                string full =
                    Path.Combine(
                        svnManager.WorkingDir,
                        path);

                LogBoth($"Opening editor for: {path}");

                System.Diagnostics.Process.Start(
                    editorPath,
                    $"\"{full}\"");

                if (_conflictCache.TryGetValue(path, out var conflict))
                {
                    conflict.Type =
                        SVNConflictType.Manual;

                    conflict.State =
                        SVNConflictState.ManualEditing;
                }

                await RefreshConflictUI();
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>{ex.Message}</color>");
            }
        }

        public async Task MarkSingleResolved(string path)
        {
            try
            {
                string fullPath =
                    Path.Combine(
                        svnManager.WorkingDir,
                        path);

                if (File.Exists(fullPath))
                {
                    string content =
                        await File.ReadAllTextAsync(fullPath);

                    if (content.Contains("<<<<<<<") ||
                        content.Contains("=======") ||
                        content.Contains(">>>>>>>"))
                    {
                        LogBoth(
                            $"<color=red>Conflict markers still exist:</color> {path}");

                        return;
                    }
                }

                LogBoth($"[Resolve] Finalizing: {path}");

                await SVNManager.Instance.RunSvn($"resolve --accept working \"{path}\"");

                await SVNManager.Instance.RunSvn("cleanup --remove-unversioned");

                _conflictCache.Remove(path);

                await Task.Delay(150);

                await RefreshConflictUI();

                LogBoth(
                    $"<color=green>Resolved manually:</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth(
                    $"<color=red>Error finalizing {path}:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task DeleteObstruction(string path, bool refreshUi = true)
        {
            await _resolveLock.WaitAsync();

            try
            {
                path = NormalizePath(path);

                string fullPath = Path.Combine(svnManager.WorkingDir, path);

                string parent = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parent))
                    parent = ".";

                LogBoth($"[TREE RESOLVE] {path}");

                try
                {
                    await SVNManager.Instance.RunSvn($"revert \"{path}\"");
                }
                catch { }

                try
                {
                    await SVNManager.Instance.RunSvn($"resolve --accept working --force \"{path}\"");
                }
                catch { }

                await SVNManager.Instance.RunSvn("cleanup");

                try
                {
                    if (File.Exists(fullPath))
                    {
                        File.SetAttributes(fullPath, FileAttributes.Normal);
                        File.Delete(fullPath);
                    }
                    else if (Directory.Exists(fullPath))
                    {
                        Directory.Delete(fullPath, true);
                    }
                }
                catch (Exception ex)
                {
                    LogBoth($"<color=yellow>Delete warning:</color> {ex.Message}");
                }

                await SVNManager.Instance.RunSvn("cleanup");

                _conflictCache.Remove(path);

                var keysToRemove = _conflictCache.Keys
                    .Where(k => k.StartsWith(path) || path.StartsWith(k))
                    .ToList();

                foreach (var k in keysToRemove)
                    _conflictCache.Remove(k);

                await svnManager.RefreshStatus();

                await Task.Delay(150);

                if (refreshUi && !_uiRefreshing)
                    await RefreshConflictUI();

                svnManager
                    .GetModule<SVNExternal>()
                    .RefreshWindowsShellIcons(path);

                LogBoth($"<color=green>Tree conflict resolved:</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>DeleteObstruction error:</color> {ex.Message}");
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            return path
                .Replace("\\", "/")
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim();
        }
    }
}