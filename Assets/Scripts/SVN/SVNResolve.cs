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

        private HashSet<string> _lastRenderedConflicts =
            new HashSet<string>();

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

                await svnManager.RefreshStatus();

                await Task.Delay(150);

                string[] conflicts =
                    await GetTargetPaths(root);

                if (conflicts.Length > 0)
                {
                    LogBoth(
                        $"<b>[Resolve]</b> Detected conflicts: {conflicts.Length}");
                }

                await RefreshConflictUI(conflicts);
            }
            catch (Exception ex)
            {
                LogBoth(
                    $"<color=red>Refresh conflict list failed:</color> {ex.Message}");
            }
            finally
            {
                isRefreshing = false;
            }
        }

        private async Task<string[]> GetTargetPaths(string root)
        {
            try
            {
                if (svnUI.ResolveTargetFileInput != null &&
                    !string.IsNullOrWhiteSpace(svnUI.ResolveTargetFileInput.text))
                {
                    return new[]
                    {
                NormalizePath(
                    svnUI.ResolveTargetFileInput.text.Trim())
            };
                }

                string xml =
                    await SvnRunner.RunAsync(
                        "status --xml",
                        root);

                if (string.IsNullOrWhiteSpace(xml))
                {
                    LogBoth("<color=red>[Resolve] Empty SVN XML</color>");
                    return Array.Empty<string>();
                }

                HashSet<string> conflicts =
                    new HashSet<string>();

                XDocument doc = XDocument.Parse(xml);

                foreach (XElement entry in doc.Descendants("entry"))
                {
                    XElement wcStatus =
                        entry.Element("wc-status");

                    if (wcStatus == null)
                        continue;

                    string itemStatus =
                        wcStatus.Attribute("item")?.Value;

                    string propsStatus =
                        wcStatus.Attribute("props")?.Value;

                    string treeConflicted =
                        wcStatus.Attribute("tree-conflicted")?.Value;

                    bool isConflict =
                        itemStatus == "conflicted" ||
                        propsStatus == "conflicted" ||
                        treeConflicted == "true";

                    if (!isConflict)
                        continue;

                    string path =
                        entry.Attribute("path")?.Value;

                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    conflicts.Add(
                        NormalizePath(path));
                }

                return conflicts.ToArray();
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>GetTargetPaths XML error:</color> {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private async Task RefreshAll()
        {
            await svnManager.RefreshStatus();

            await Task.Delay(150);

            await RefreshConflictUI();
        }

        public async void MarkAsResolved()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;

            try
            {
                string[] targetPaths = await GetTargetPaths(root);
                if (targetPaths.Length == 0)
                {
                    LogBoth("<color=yellow>No conflicts found.</color>");
                    return;
                }

                foreach (var path in targetPaths)
                {
                    string fullPath = Path.Combine(root, path);
                    if (File.Exists(fullPath))
                    {
                        string content = await File.ReadAllTextAsync(fullPath);
                        if (content.Contains("<<<<<<<") || content.Contains("=======") || content.Contains(">>>>>>>"))
                        {
                            LogBoth($"<color=red>Abort:</color> File {path} still has conflict markers!");
                            return;
                        }
                    }
                }

                await RunSvnResolve(root, targetPaths, "working");
                LogBoth("<color=green>Marked as resolved successfully.</color>");
                await RefreshAll();
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        public async void ResolveTheirs() => await RunResolveStrategy("theirs-full");
        public async void ResolveMine() => await RunResolveStrategy("mine-full");

        private async Task RunResolveStrategy(string strategy)
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;

            try
            {
                string[] targetPaths = await GetTargetPaths(root);
                if (targetPaths.Length > 0)
                {
                    LogBoth($"Applying strategy <color=orange>{strategy}</color> to {targetPaths.Length} items...");
                    await RunSvnResolve(root, targetPaths, strategy);
                    LogBoth("<color=green>Resolved!</color>");
                    await RefreshAll();
                }
                else LogBoth("<color=yellow>Nothing to resolve.</color>");
            }
            catch (Exception ex) { LogBoth($"<color=red>Error:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        private async Task RunSvnResolve(string root, string[] paths, string acceptStrategy)
        {
            string pathsArg = string.Join(" ", paths.Select(p => $"\"{p}\""));
            await SvnRunner.RunAsync($"resolve --accept {acceptStrategy} {pathsArg}", root);
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
                string targetFile = "";

                if (svnUI.ResolveTargetFileInput != null && !string.IsNullOrEmpty(svnUI.ResolveTargetFileInput.text))
                {
                    targetFile = svnUI.ResolveTargetFileInput.text.Trim();
                }
                else
                {
                    var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                    var conflict = statusDict?.FirstOrDefault(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"));
                    if (conflict.HasValue && !string.IsNullOrEmpty(conflict.Value.Key))
                        targetFile = conflict.Value.Key;
                }

                if (!string.IsNullOrEmpty(targetFile))
                {
                    string fullPath = Path.Combine(root, targetFile);
                    LogBoth($"Opening editor: <color=green>{targetFile}</color>");
                    System.Diagnostics.Process.Start(editorPath, $"\"{fullPath}\"");
                }
                else LogBoth("<color=yellow>No conflicted file found to open.</color>");
            }
            catch (Exception ex) { LogBoth($"<color=red>Exception:</color> {ex.Message}"); }
            finally { IsProcessing = false; }
        }

        private async Task<string[]> GetTreeConflictFiles()
        {
            string status =
                await SVNManager.Instance.RunSvn("status");

            if (string.IsNullOrWhiteSpace(status))
                return Array.Empty<string>();

            var lines = status.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            return lines
                .Where(x => x.StartsWith("C"))
                .Select(x =>
                {
                    int index = x.IndexOf(' ');

                    if (index < 0)
                        return null;

                    return x.Substring(index).Trim();
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToArray();
        }

        public async Task RefreshConflictUI(
    string[] preFetchedConflicts = null)
        {
            // 🔒 blokada równoległego renderowania
            if (_uiRefreshing)
                return;

            _uiRefreshing = true;

            try
            {
                string root = svnManager.WorkingDir;

                var conflicts =
                    (preFetchedConflicts ??
                     await GetTargetPaths(root))
                    .Select(NormalizePath)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();

                // =====================================================
                // 🔥 CACHE CHECK
                // =====================================================

                HashSet<string> currentSet =
                    new HashSet<string>(conflicts);

                if (currentSet.SetEquals(_lastRenderedConflicts))
                {
                    return;
                }

                _lastRenderedConflicts = currentSet;

                // =====================================================
                // 🔥 VALIDATION
                // =====================================================

                if (svnUI.ResolveConsoleContent == null)
                {
                    LogBoth("<color=red>ResolveConsoleContent NULL</color>");
                    return;
                }

                if (svnUI.ConflictPrefab == null)
                {
                    LogBoth("<color=red>ConflictPrefab NULL</color>");
                    return;
                }

                // =====================================================
                // 🔥 CLEAR OLD UI
                // =====================================================

                Transform parent =
                    svnUI.ResolveConsoleContent.transform;

                for (int i = parent.childCount - 1; i >= 0; i--)
                {
                    Transform child =
                        parent.GetChild(i);

                    child.SetParent(null);

                    if (Application.isPlaying)
                    {
                        GameObject.Destroy(child.gameObject);
                    }
                    else
                    {
                        GameObject.DestroyImmediate(child.gameObject);
                    }
                }

                await Task.Yield();

                // =====================================================
                // 🔥 BUILD NEW UI
                // =====================================================

                foreach (string conflict in conflicts)
                {
                    GameObject obj =
                        GameObject.Instantiate(
                            svnUI.ConflictPrefab,
                            parent);

                    obj.SetActive(true);

                    SVNConflictItem item =
                        obj.GetComponent<SVNConflictItem>();

                    if (item == null)
                    {
                        LogBoth("<color=red>SVNConflictItem missing on prefab!</color>");
                        continue;
                    }

                    item.Setup(conflict);
                }

                Canvas.ForceUpdateCanvases();

                LogBoth(
                    $"[Resolve UI] Generated {conflicts.Length} conflict items.");
            }
            catch (Exception ex)
            {
                LogBoth(
                    $"<color=red>RefreshConflictUI failed:</color> {ex.Message}");
            }
            finally
            {
                _uiRefreshing = false;
            }
        }

        public async void ResolveSingleMine(string path)
        {
            await ResolveSingle(path, "mine-full");
        }

        public async void ResolveSingleTheirs(string path)
        {
            await ResolveSingle(path, "theirs-full");
        }

        private readonly SemaphoreSlim _resolveLock = new(1, 1);

        private async Task ResolveSingle(
            string path,
            string strategy)
        {
            await _resolveLock.WaitAsync();

            try
            {
                if (IsProcessing)
                    return;

                IsProcessing = true;

                LogBoth($"[Resolve] {strategy} -> {path}");

                string full =
                    Path.Combine(
                        svnManager.WorkingDir,
                        path);

                string parent =
                    Path.GetDirectoryName(path);

                if (string.IsNullOrWhiteSpace(parent))
                    parent = ".";

                // =====================================================
                // 🔥 THEIRS
                // =====================================================

                if (strategy == "theirs-full")
                {
                    try
                    {
                        if (File.Exists(full))
                            File.Delete(full);

                        if (Directory.Exists(full))
                            Directory.Delete(full, true);
                    }
                    catch
                    {
                        // intentionally ignored
                    }

                    await SVNManager.Instance.RunSvn(
                        $"update \"{parent}\"");

                    await SVNManager.Instance.RunSvn(
                        $"resolve --accept theirs-full \"{path}\"");
                }

                // =====================================================
                // 🔥 MINE
                // =====================================================

                else
                {
                    await SVNManager.Instance.RunSvn(
                        $"resolve --accept mine-full \"{path}\"");

                    await SVNManager.Instance.RunSvn(
                        $"update \"{parent}\"");
                }

                // =====================================================
                // 🔥 FINALIZE ONLY IF STILL CONFLICTED
                // =====================================================

                string status =
                    await SVNManager.Instance.RunSvn(
                        $"status \"{path}\"");

                bool stillConflicted =
                    !string.IsNullOrWhiteSpace(status) &&
                    status.TrimStart().StartsWith("C");

                if (stillConflicted)
                {
                    await SVNManager.Instance.RunSvn(
                        $"resolve --accept working \"{path}\"");
                }

                // =====================================================
                // 🔥 CLEANUP
                // =====================================================

                await SVNManager.Instance.RunSvn("cleanup");

                // =====================================================
                // 🔥 REFRESH
                // =====================================================

                await svnManager.RefreshStatus();

                await Task.Delay(150);

                string[] conflicts =
                    await GetTargetPaths(
                        svnManager.WorkingDir);

                await RefreshConflictUI(conflicts);

                svnManager
                    .GetModule<SVNExternal>()
                    .RefreshWindowsShellIcons(path);

                LogBoth(
                    $"<color=green>Resolved ({strategy}):</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth(
                    $"<color=red>Error resolving {path}:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _resolveLock.Release();
            }
        }

        public void OpenSingle(string path)
        {
            if (IsProcessing) return;

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
                string full = System.IO.Path.Combine(svnManager.WorkingDir, path);
                LogBoth($"Opening editor for: <color=green>{path}</color>");

                System.Diagnostics.Process.Start(editorPath, $"\"{full}\"");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Exception:</color> {ex.Message}");
            }
        }

        public async void MarkSingleResolved(string path)
        {
            if (IsProcessing)
                return;

            IsProcessing = true;

            try
            {
                LogBoth($"[Resolve] Finalizing: {path}");

                string fullPath = Path.Combine(svnManager.WorkingDir, path);

                // Prevent marking as resolved if conflict markers are still present
                if (File.Exists(fullPath))
                {
                    string content = await File.ReadAllTextAsync(fullPath);
                    if (content.Contains("<<<<<<<") || content.Contains("=======") || content.Contains(">>>>>>>"))
                    {
                        LogBoth($"<color=red>Abort:</color> File {path} still has conflict markers! Merge changes first.");
                        return;
                    }
                }

                string parent = Path.GetDirectoryName(path);

                if (string.IsNullOrWhiteSpace(parent))
                    parent = ".";

                await SVNManager.Instance.RunSvn("cleanup");

                await SVNManager.Instance.RunSvn($"update \"{parent}\"");

                await SVNManager.Instance.RunSvn($"resolve --accept working \"{path}\"");

                LogBoth($"<color=green>Resolved (working state accepted):</color> {path}");

                // Refresh UI and system status after successful resolve
                await svnManager.RefreshStatus();

                await Task.Delay(150);

                string[] conflicts =
                    await GetTargetPaths(
                        svnManager.WorkingDir);

                await RefreshConflictUI(conflicts);
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>Error finalizing {path}:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void DeleteObstruction(string path)
        {
            if (IsProcessing)
                return;

            IsProcessing = true;

            try
            {
                string full = Path.Combine(svnManager.WorkingDir, path);
                string parent = Path.GetDirectoryName(path);

                if (string.IsNullOrWhiteSpace(parent))
                    parent = ".";

                LogBoth($"[TREE CONFLICT] Resolving obstruction: {path}");

                if (File.Exists(full))
                {
                    File.Delete(full);
                    LogBoth($"Deleted obstruction file: {path}");
                }
                else if (Directory.Exists(full))
                {
                    Directory.Delete(full, true);
                    LogBoth($"Deleted obstruction dir: {path}");
                }

                await SVNManager.Instance.RunSvn("cleanup");

                await SVNManager.Instance.RunSvn($"update \"{parent}\"");

                await SVNManager.Instance.RunSvn($"resolve --accept working \"{path}\"");

                await SVNManager.Instance.RunSvn("cleanup");

                await DebugConflictState(path);

                await svnManager.RefreshStatus();

                await Task.Delay(150);

                string[] conflicts =
                    await GetTargetPaths(
                        svnManager.WorkingDir);

                await RefreshConflictUI(conflicts);

                svnManager.GetModule<SVNExternal>().RefreshWindowsShellIcons(path);

                // Updated log to indicate obstruction deletion resolution
                LogBoth($"<color=green>Resolved (obstruction deleted):</color> {path}");
            }
            catch (Exception ex)
            {
                LogBoth($"<color=red>{ex.Message}</color>");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async Task DebugConflictState(string path)
        {
            try
            {
                string status = await SVNManager.Instance.RunSvn($"status \"{path}\"");
                LogBoth($"[DEBUG STATUS] {path}");
                LogBoth(status);

                string info = await SVNManager.Instance.RunSvn($"info \"{path}\"");
                LogBoth($"[DEBUG INFO] {path}");
                LogBoth(info);
            }
            catch (Exception ex)
            {
                LogBoth($"[DEBUG ERROR] {ex.Message}");
            }
        }

        private string NormalizePath(string path)
        {
            return (path ?? "")
                .Replace("\\", "/")
                .Trim()
                .ToLowerInvariant();
        }
    }
}