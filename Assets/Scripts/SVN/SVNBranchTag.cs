using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBranchTag : SVNBase, IDisposable
    {
        private DateTime _lastDeleteBranchClickTime = DateTime.MinValue;
        private DateTime _lastDeleteTagClickTime = DateTime.MinValue;
        private string _cachedRepoRoot;
        private CancellationTokenSource _refreshCts;
        private CancellationTokenSource _operationCts;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private int _disposed;

        private static readonly HashSet<string> PlaceholderTexts = new(StringComparer.OrdinalIgnoreCase)
        {
            "Loading...", "Not ready", "No branches", "No tags", "Error", "None"
        };

        public SVNBranchTag(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            manager.OnProjectChanged += OnProjectChangedHandler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            CancelOperation();

            var refreshCts = Interlocked.Exchange(ref _refreshCts, null);
            if (refreshCts != null)
            {
                try { refreshCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { refreshCts.Dispose(); } catch { } });
            }

            if (svnManager != null)
                svnManager.OnProjectChanged -= OnProjectChangedHandler;

            _ = Task.Delay(1500).ContinueWith(_ => { try { _operationLock.Dispose(); } catch { } });
            GC.SuppressFinalize(this);
        }

        public void CancelOperation()
        {
            try
            {
                var cts = Volatile.Read(ref _operationCts);
                if (cts == null || cts.IsCancellationRequested) return;
                cts.Cancel();
                LogInfo("<color=orange>Cancellation requested...</color>");
            }
            catch (ObjectDisposedException) { }
        }

        private async Task RunWithOperationLockAsync(Func<CancellationToken, Task> action)
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            bool hasLock;
            try { hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }

            if (!hasLock)
            {
                LogInfo("<color=orange>Another branch/tag operation is already in progress.</color>");
                return;
            }

            var localCts = new CancellationTokenSource();
            Volatile.Write(ref _operationCts, localCts);
            try
            {
                await action(localCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogInfo("<color=orange>Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogErrorLocal($"[Operation Error] {ex.Message}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _operationCts, null, localCts);
                _ = Task.Delay(1000).ContinueWith(_ => { try { localCts.Dispose(); } catch { } });
                try { _operationLock.Release(); } catch { }
            }
        }

        private void OnProjectChangedHandler(SVNProject project)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            _cachedRepoRoot = null;
            _ = RefreshOnProjectLoadedAsync();
        }

        private async Task RefreshOnProjectLoadedAsync()
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) == 1) return;
                await RefreshUnifiedList().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarning($"[BranchTag] Auto-refresh failed: {ex.Message}");
            }
        }

        // UWAGA: wywoływane z puli wątków — UI czytane tylko w snapshotach przez callerów.
        private async Task<string[]> GetRepoListAsync(string url, CancellationToken token = default)
        {
            try
            {
                string command = $"list \"{url}\" --non-interactive";
                string output = await SvnRunner.RunAsync(command, svnManager.WorkingDir, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();

                return output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimEnd('/'))
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("*"))
                    .Where(x => x.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) < 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogWarning($"[BranchTag] Failed to list repository: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private string EnsureRepoRoot()
        {
            if (!string.IsNullOrWhiteSpace(_cachedRepoRoot)) return _cachedRepoRoot;
            if (svnManager == null || string.IsNullOrWhiteSpace(svnManager.WorkingDir)) return null;

            try
            {
                _cachedRepoRoot = svnManager.GetRepoRoot()?.Trim().TrimEnd('/');
            }
            catch (Exception ex)
            {
                LogWarning($"[SVNBranchTag] GetRepoRoot failed: {ex.Message}");
            }
            return _cachedRepoRoot;
        }

        public async Task RefreshIfEmpty()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (!IsReady() || svnUI?.BranchesDropdown == null) return;
            var options = svnUI.BranchesDropdown.options;
            if (options.Count == 0 || options.All(o => IsPlaceholder(o.text)))
                await RefreshUnifiedList().ConfigureAwait(false);
        }

        private bool IsReady()
        {
            if (svnManager == null) return false;
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir)) return false;
            if (!Directory.Exists(svnManager.WorkingDir)) return false;
            if (string.IsNullOrWhiteSpace(SvnRunner.KeyPath)) return false;
            return true;
        }

        public async Task CreateBranchFromTrunk() => await RunWithOperationLockAsync(CreateBranchFromTrunkCore);
        public async Task CreateBranchFromSelected() => await RunWithOperationLockAsync(CreateBranchFromSelectedCore);

        private async Task CreateBranchFromTrunkCore(CancellationToken token)
        {
            // Snapshot UI na starcie (main thread — entry z RunWithOperationLockAsync z puli,
            // ale kontynuacja pierwszego awaitu wraca na pulę; UI czytamy przez Enqueue lub zakładamy main z buttona).
            // Bezpiecznie: czytamy przez dispatcher.
            string name = null, subFolder = null, sourceRelativePath = null, revision = null;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                name = svnUI?.BranchNameInput?.text?.Trim();
                subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";
                sourceRelativePath = svnUI?.BranchSourcePathInput?.text?.Trim();
                revision = svnUI.RevisionInput?.text?.Trim();
            });

            // Enqueue jest async — czekamy chwilę na snapshot.
            await Task.Delay(50, token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(name))
            {
                LogErrorLocal("[Error] Branch/Tag name cannot be empty.");
                return;
            }
            if (string.IsNullOrWhiteSpace(sourceRelativePath))
                sourceRelativePath = "trunk";

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            bool hasRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

            string sanitizedSource, sanitizedName;
            try
            {
                sanitizedSource = SanitizeSvnPath(sourceRelativePath);
                sanitizedName = SanitizeSvnName(name);
            }
            catch (ArgumentException)
            {
                LogErrorLocal("[Error] Name contains no valid characters.");
                return;
            }

            string sourceUrl = $"{repoRoot}/{sanitizedSource}";
            string targetUrl = $"{repoRoot}/{subFolder}/{sanitizedName}";

            string cmd = hasRevision
                ? $"copy \"{sourceUrl}@{revision}\" \"{targetUrl}\" -m \"Created {subFolder}/{sanitizedName} from {sourceRelativePath}@{revision}\" --parents"
                : $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{sanitizedName} from {sourceRelativePath}\" --parents";

            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, token).ConfigureAwait(false);
            LogSuccess(hasRevision
                ? $"Created: {name} from {sourceRelativePath} at revision {revision}"
                : $"Created: {name} from {sourceRelativePath}");
            await RefreshUnifiedList().ConfigureAwait(false);
        }

        private async Task CreateBranchFromSelectedCore(CancellationToken token)
        {
            string newName = null, revision = null;
            TMP_Dropdown sourceDropdown = null;
            string selected = null;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                newName = svnUI?.BranchNameInput?.text?.Trim();
                revision = svnUI.RevisionInput?.text?.Trim();

                string subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";
                sourceDropdown = subFolder == "branches" ? svnUI?.BranchesDropdown : svnUI?.TagsDropdown;

                if (sourceDropdown != null && sourceDropdown.options.Count > 0 &&
                    sourceDropdown.value >= 0 && sourceDropdown.value < sourceDropdown.options.Count)
                {
                    selected = sourceDropdown.options[sourceDropdown.value].text;
                }
            });

            await Task.Delay(50, token).ConfigureAwait(false);

            string subFolderFinal = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";

            if (string.IsNullOrWhiteSpace(newName))
            {
                LogErrorLocal("[Error] Branch/Tag name cannot be empty.");
                return;
            }

            if (sourceDropdown == null || string.IsNullOrEmpty(selected))
            { LogErrorLocal($"[Error] No {subFolderFinal} available."); return; }

            if (IsPlaceholder(selected) || string.IsNullOrEmpty(selected))
            { LogErrorLocal($"[Error] Invalid source {subFolderFinal}."); return; }

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            string sanitizedName;
            try { sanitizedName = SanitizeSvnName(newName); }
            catch (ArgumentException)
            {
                LogErrorLocal("[Error] Name contains no valid characters.");
                return;
            }

            string suggestedPath = selected.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                ? "trunk"
                : $"{subFolderFinal}/{SanitizeSvnName(selected)}";

            PostToMainThread(() =>
            {
                if (svnUI?.BranchSourcePathInput != null)
                    svnUI.BranchSourcePathInput.SetTextWithoutNotify(suggestedPath);
            });

            string sourceRelativePath = suggestedPath;
            bool hasRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

            string sourceUrl = $"{repoRoot}/{SanitizeSvnPath(sourceRelativePath)}";
            if (hasRevision) sourceUrl = $"{sourceUrl}@{revision}";

            string targetUrl = $"{repoRoot}/{subFolderFinal}/{sanitizedName}";
            string message = hasRevision
                ? $"Created {subFolderFinal}/{newName} from {sourceRelativePath}@{revision}"
                : $"Created {subFolderFinal}/{newName} from {sourceRelativePath}";

            string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"{message}\" --parents";
            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, token).ConfigureAwait(false);
            LogSuccess(hasRevision
                ? $"Created: {newName} from {sourceRelativePath} at revision {revision}"
                : $"Created: {newName} from {sourceRelativePath}");
            await RefreshUnifiedList().ConfigureAwait(false);
        }

        public async Task RefreshUnifiedList()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (svnUI?.BranchesDropdown == null && svnUI?.TagsDropdown == null) return;

            if (!IsReady())
            {
                PostToMainThread(() =>
                {
                    UpdateDropdown(svnUI?.BranchesDropdown, Array.Empty<string>(), "Loading...", true);
                    UpdateDropdown(svnUI?.TagsDropdown, Array.Empty<string>(), "Loading...", false);
                });
                return;
            }

            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _refreshCts, newCts);
            if (oldCts != null)
            {
                oldCts.Cancel();
                _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
            }
            CancellationToken token = newCts.Token;

            try
            {
                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    PostToMainThread(() =>
                    {
                        UpdateDropdown(svnUI?.BranchesDropdown, Array.Empty<string>(), "Not ready", true);
                        UpdateDropdown(svnUI?.TagsDropdown, Array.Empty<string>(), "Not ready", false);
                    });
                    return;
                }

                LogInfo("[Refresh] Syncing lists with server...");
                string branchesUrl = $"{repoRoot}/branches";
                string tagsUrl = $"{repoRoot}/tags";

                var branchesTask = GetRepoListAsync(branchesUrl, token);
                var tagsTask = GetRepoListAsync(tagsUrl, token);
                await Task.WhenAll(branchesTask, tagsTask).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                string actualCurrentBranch = null;
                string currentUrl = "";
                try
                {
                    currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(currentUrl))
                    {
                        actualCurrentBranch = GetBranchNameFromUrl(currentUrl, repoRoot);
                    }
                }
                catch { }

                string[] branchesResult = branchesTask.Result;
                string[] tagsResult = tagsTask.Result;
                string currentUrlSnapshot = currentUrl;
                string currentBranchSnapshot = actualCurrentBranch;
                string repoRootSnapshot = repoRoot;

                PostToMainThread(() =>
                {
                    UpdateDropdown(svnUI?.BranchesDropdown, branchesResult, "No branches", true, currentBranchSnapshot);
                    UpdateDropdown(svnUI?.TagsDropdown, tagsResult, "No tags", false, currentBranchSnapshot);

                    if (svnUI?.BranchSourcePathInput != null)
                    {
                        string currentRelativePath = GetRelativePathFromUrl(currentUrlSnapshot, repoRootSnapshot);
                        svnUI.BranchSourcePathInput.SetTextWithoutNotify(currentRelativePath);
                    }
                });

                LogSuccess("[Refresh Complete] UI synchronized.");
            }
            catch (OperationCanceledException) { LogWarning("[Refresh] Cancelled."); }
            catch (Exception ex)
            {
                string msg = ex.Message ?? "";
                if (msg.Contains("Permission denied") || msg.Contains("publickey") || msg.Contains("E170013"))
                    LogWarning("[Refresh] SSH connection failed. Check your key.");
                else LogErrorLocal($"[Refresh Error] {msg}");

                PostToMainThread(() =>
                {
                    UpdateDropdown(svnUI?.BranchesDropdown, Array.Empty<string>(), "Error", true);
                    UpdateDropdown(svnUI?.TagsDropdown, Array.Empty<string>(), "Error", false);
                });
            }
        }

        public async Task DiffWithCurrent(bool isTag) => await RunWithOperationLockAsync(token => DiffWithCurrentCore(isTag, token));

        private async Task DiffWithCurrentCore(bool isTag, CancellationToken token)
        {
            string selected = null;
            TMP_Dropdown dropdown = null;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                dropdown = isTag ? svnUI?.TagsDropdown : svnUI?.BranchesDropdown;
                if (dropdown != null && dropdown.options.Count > 0 &&
                    dropdown.value >= 0 && dropdown.value < dropdown.options.Count)
                {
                    selected = dropdown.options[dropdown.value].text;
                }
            });

            await Task.Delay(50, token).ConfigureAwait(false);

            if (dropdown == null || string.IsNullOrEmpty(selected)) { LogErrorLocal("[Diff] No items available."); return; }
            if (IsPlaceholder(selected)) { LogErrorLocal("[Diff] Please select a valid branch/tag."); return; }

            if (!IsReady()) { LogWarning("[Diff] SVN not ready."); return; }

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string subFolder = isTag ? "tags" : "branches";
            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) { LogErrorLocal("[Diff] Repo root missing."); return; }

            string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(currentUrl)) { LogErrorLocal("[Diff] Could not determine current URL."); return; }

            string selectedUrl = BuildSvnUrl(repoRoot, subFolder, selected);

            if (NormalizeUrl(currentUrl) == NormalizeUrl(selectedUrl)) { LogWarning($"[Diff] You are already on '{selected}'."); return; }

            string currentName = GetBranchNameFromUrl(currentUrl, repoRoot);
            LogInfo($"[Diff] {currentName} vs {selected}");

            string args = $"diff --summarize \"{currentUrl}\" \"{selectedUrl}\"";
            string output = await SvnRunner.RunAsync(args, svnManager.WorkingDir, false, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output)) { LogSuccess("[Diff] No differences found."); return; }

            var sb = new StringBuilder(4096);
            sb.AppendLine("=== SVN DIFF SUMMARY ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Current : {currentUrl}");
            sb.AppendLine($"Selected: {selectedUrl}");
            sb.AppendLine(new string('-', 60));

            int added = 0, modified = 0, deleted = 0;
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 2) continue;
                char status = line[0];
                string path = line.Substring(2).Trim();
                if (string.IsNullOrEmpty(path)) continue;
                switch (status) { case 'A': added++; break; case 'M': modified++; break; case 'D': deleted++; break; }
                sb.AppendLine($"[{status}] {Uri.UnescapeDataString(path)}");
            }

            sb.AppendLine(new string('-', 60));
            sb.AppendLine($"Added: {added} | Modified: {modified} | Deleted: {deleted}");
            sb.AppendLine($"Total: {added + modified + deleted}");

            string fileName = $"Diff_{GetSafeFileName(currentName)}_vs_{GetSafeFileName(selected)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string tempFilePath = Path.Combine(SVNPrefs.TemporaryCachePath, fileName);

            await File.WriteAllTextAsync(tempFilePath, sb.ToString(), token).ConfigureAwait(false);

            try { Process.Start(new ProcessStartInfo(tempFilePath) { UseShellExecute = true }); }
            catch (Exception ex) { LogWarning($"[Diff] Could not open file: {ex.Message}"); }

            LogSuccess($"[Diff] Exported: {fileName}");
            LogInfo($"<color=#55FF55>+{added}</color>  <color=#FFFF55>~{modified}</color>  <color=#FF9900>-{deleted}</color>");
        }

        public async Task ShowDetailsForSelected() => await RunWithOperationLockAsync(ShowDetailsForSelectedCore);

        private async Task ShowDetailsForSelectedCore(CancellationToken token)
        {
            string subFolder = null, selected = null;
            TMP_Dropdown dropdown = null;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";
                dropdown = subFolder == "branches" ? svnUI?.BranchesDropdown : svnUI?.TagsDropdown;
                if (dropdown != null && dropdown.options.Count > 0 &&
                    dropdown.value >= 0 && dropdown.value < dropdown.options.Count)
                {
                    selected = dropdown.options[dropdown.value].text;
                }
            });

            await Task.Delay(50, token).ConfigureAwait(false);

            if (dropdown == null || string.IsNullOrEmpty(selected)) { LogErrorLocal("[Details] No items available."); return; }
            if (IsPlaceholder(selected)) { LogErrorLocal("[Details] Please select a valid branch/tag."); return; }

            if (!IsReady()) { LogWarning("[Details] SVN not ready."); return; }

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            string branchUrl = BuildSvnUrl(repoRoot, subFolder, selected);

            LogInfo($"[Details] {selected} @ {branchUrl}");

            string logOutput = await SvnRunner.RunAsync($"log \"{branchUrl}\" --stop-on-copy --limit 1 --verbose --xml", svnManager.WorkingDir, false, token).ConfigureAwait(false);

            string firstAuthor = "unknown", firstDate = "unknown", sourceBranch = "trunk (default)";
            string sourceRevision = "N/A", creationRev = "N/A", commitMsg = "N/A";

            if (!string.IsNullOrWhiteSpace(logOutput))
            {
                try
                {
                    using var sr = new StringReader(logOutput);
                    using var reader = System.Xml.XmlReader.Create(sr);
                    while (reader.Read())
                    {
                        if (reader.NodeType == System.Xml.XmlNodeType.Element)
                        {
                            switch (reader.Name)
                            {
                                case "logentry": creationRev = $"r{reader.GetAttribute("revision")}"; break;
                                case "author": firstAuthor = reader.ReadElementContentAsString(); break;
                                case "date": firstDate = reader.ReadElementContentAsString(); break;
                                case "msg": commitMsg = reader.ReadElementContentAsString().Replace("\n", " ").Trim(); break;
                                case "path":
                                    string copyFrom = reader.GetAttribute("copyfrom-path");
                                    string copyRev = reader.GetAttribute("copyfrom-rev");
                                    if (!string.IsNullOrEmpty(copyFrom)) { sourceBranch = ExtractBranchName(copyFrom); if (!string.IsNullOrEmpty(copyRev)) sourceRevision = $"r{copyRev}"; }
                                    break;
                            }
                        }
                    }
                }
                catch { }
            }

            LogSuccess($"Name       : {selected}");
            LogInfo($"Created in : {creationRev}");
            LogInfo($"Created by : {firstAuthor}");

            if (DateTime.TryParse(firstDate, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                LogInfo($"Created on : {parsed.ToLocalTime():yyyy-MM-dd HH:mm:ss} (local)");
            else LogInfo($"Created on : {firstDate}");

            LogInfo($"Source     : {sourceBranch} (at {sourceRevision})");

            if (commitMsg != "N/A" && commitMsg.Length > 0)
            {
                if (commitMsg.Length > 100) commitMsg = commitMsg.Substring(0, 100) + "...";
                LogInfo($"Message    : {commitMsg}");
            }
        }

        public async Task SwitchToSelectedBranch() => await RunWithOperationLockAsync(SwitchToSelectedBranchCore);
        public async Task SwitchToSelectedTag() => await RunWithOperationLockAsync(SwitchToSelectedTagCore);

        private async Task SwitchToSelectedBranchCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Switch] SVN not ready."); return; }

            string selected = null;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.BranchesDropdown != null && svnUI.BranchesDropdown.options.Count > 0 &&
                    svnUI.BranchesDropdown.value >= 0 && svnUI.BranchesDropdown.value < svnUI.BranchesDropdown.options.Count)
                {
                    selected = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
                }
            });
            await Task.Delay(50, token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(selected) || IsPlaceholder(selected)) return;
            await ExecuteUnifiedSwitch(selected, "branches", token).ConfigureAwait(false);
        }

        private async Task SwitchToSelectedTagCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Switch] SVN not ready."); return; }

            string selected = null;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.TagsDropdown != null && svnUI.TagsDropdown.options.Count > 0 &&
                    svnUI.TagsDropdown.value >= 0 && svnUI.TagsDropdown.value < svnUI.TagsDropdown.options.Count)
                {
                    selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                }
            });
            await Task.Delay(50, token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(selected) || IsPlaceholder(selected)) return;
            await ExecuteUnifiedSwitch(selected, "tags", token).ConfigureAwait(false);
        }

        public async Task DeleteSelectedBranch() => await RunWithOperationLockAsync(DeleteSelectedBranchCore);
        public async Task DeleteSelectedTag() => await RunWithOperationLockAsync(DeleteSelectedTagCore);

        private async Task DeleteSelectedBranchCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Delete] SVN not ready."); return; }

            string selectedBranch = null;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.BranchesDropdown != null && svnUI.BranchesDropdown.options.Count > 0 &&
                    svnUI.BranchesDropdown.value >= 0 && svnUI.BranchesDropdown.value < svnUI.BranchesDropdown.options.Count)
                {
                    selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text?.Trim();
                }
            });
            await Task.Delay(50, token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(selectedBranch)) { LogErrorLocal("Delete aborted."); return; }
            if (IsProtectedBranch(selectedBranch)) { LogErrorLocal("SECURITY BLOCK: 'trunk' is protected."); return; }
            if (!ConfirmDelete(ref _lastDeleteBranchClickTime, selectedBranch)) return;
            await ExecuteRemoteDeleteTask(selectedBranch, "branches", token).ConfigureAwait(false);
        }

        private async Task DeleteSelectedTagCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Delete] SVN not ready."); return; }

            string selected = null;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.TagsDropdown != null && svnUI.TagsDropdown.options.Count > 0 &&
                    svnUI.TagsDropdown.value >= 0 && svnUI.TagsDropdown.value < svnUI.TagsDropdown.options.Count)
                {
                    selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
                }
            });
            await Task.Delay(50, token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(selected) || IsPlaceholder(selected)) return;
            if (IsProtectedBranch(selected))
            {
                LogErrorLocal("SECURITY BLOCK: 'trunk' is protected.");
                return;
            }
            if (!ConfirmDelete(ref _lastDeleteTagClickTime, selected)) return;
            await ExecuteRemoteDeleteTask(selected, "tags", token).ConfigureAwait(false);
        }

        // ===================================================================
        //  SMART SWITCH — niezawodny dla KAŻDEGO brancha:
        //  1) Próba normalna (ancestry-preserving) — standard dla svn copy branchy.
        //  2) Gdy E195012: diagnoza + AUTO-RETRY z --ignore-ancestry + WARNING
        //     o konsekwencjach merge. Użytkownik nie musi znać toggle.
        // ===================================================================
        private async Task ExecuteUnifiedSwitch(string targetName, string subFolder, CancellationToken token)
        {
            bool userRequestedIgnoreAncestry = false;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                userRequestedIgnoreAncestry = svnUI?.IgnoreAncestryToggle != null && svnUI.IgnoreAncestryToggle.isOn;
            });
            await Task.Delay(20, token).ConfigureAwait(false);

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string workingDir = svnManager?.WorkingDir;
            if (string.IsNullOrWhiteSpace(workingDir)) { LogErrorLocal("[Switch] Working directory is empty."); return; }
            if (!SVNAssetLocator.IsWorkingCopy(workingDir)) { LogErrorLocal("[Switch] Not a valid SVN working copy."); return; }

            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) { LogErrorLocal("[Switch] Repository root is missing."); return; }

            string currentUrl = await SvnRunner.GetRepoUrlAsync(workingDir).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(currentUrl)) { LogErrorLocal("[Switch] Could not determine current SVN URL."); return; }

            string cleanTarget = targetName;
            if (cleanTarget.StartsWith("branches/", StringComparison.OrdinalIgnoreCase) ||
                cleanTarget.StartsWith("tags/", StringComparison.OrdinalIgnoreCase))
            {
                cleanTarget = cleanTarget.Substring(cleanTarget.IndexOf('/') + 1);
            }

            string targetUrl = BuildSvnUrl(repoRoot, subFolder, cleanTarget);

            LogInfo($"[Switch] Name: '{targetName}' | SubFolder: {subFolder} | FinalURL: '{targetUrl}'");

            if (NormalizeUrl(currentUrl) == NormalizeUrl(targetUrl)) { LogWarning($"[Switch] You are already on '{targetName}'."); return; }

            LogInfo($"[Switch] Current URL: {currentUrl}");
            LogInfo($"[Switch] Target URL:  {targetUrl}");

            // Snapshot unversioned PRZED switchem (ochrona plików użytkownika — fix K1).
            HashSet<string> preSwitchUnversioned = await SnapshotUnversionedAsync(workingDir, token).ConfigureAwait(false);

            // Auto-shelve zmian wersjonowanych.
            SvnStats stats = await GetStatsAsync(workingDir, token).ConfigureAwait(false);
            bool hasLocalChanges = stats.ModifiedCount > 0 || stats.AddedCount > 0 || stats.DeletedCount > 0 || stats.NewFilesCount > 0;

            string shelfName = null;
            if (hasLocalChanges)
            {
                string currentBranchName = GetBranchNameFromUrl(currentUrl, repoRoot);
                string safeCurrent = GetSafeShelfName(currentBranchName);
                string safeTarget = GetSafeShelfName(targetName);
                shelfName = $"AutoSwitch_{safeCurrent}_To_{safeTarget}_{DateTime.Now:yyyyMMdd_HHmmss}";

                LogWarning("[Switch] Local changes detected – creating automatic shelf...");
                var shelve = svnManager.GetModule<SVNShelve>();
                if (shelve == null) { LogErrorLocal("[Switch] Shelve module unavailable."); return; }

                bool shelveOk = await shelve.Shelve(shelfName, requireCleanWorkingCopy: false).ConfigureAwait(false);
                if (!shelveOk) { LogErrorLocal("[Switch] Failed to shelve local changes."); return; }

                LogSuccess($"[Switch] Local changes saved as: {shelfName}");

                SvnStats afterShelve = await GetStatsAsync(workingDir, token).ConfigureAwait(false);
                if (afterShelve.ConflictsCount > 0) { LogErrorLocal("[Switch] Conflicts detected after shelve. Aborting."); return; }
            }

            LogInfo($"[Switch] Switching to '{targetName}'...");

            try
            {
                await SwitchAsync(workingDir, targetUrl, userRequestedIgnoreAncestry, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string msg = ex.Message ?? "";

                // E195012 → SMART RETRY z --ignore-ancestry.
                if (msg.Contains("E195012") && !userRequestedIgnoreAncestry)
                {
                    string diagMsg = $"[Switch] Ancestry mismatch (E195012) — analyzing branch '{cleanTarget}'...";
                    LogWarning(diagMsg);
                    SVNLogBridge.LogWarning(diagMsg);

                    string ancestryInfo = await DiagnoseAncestryAsync(targetUrl, workingDir, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ancestryInfo))
                        SVNLogBridge.LogWarning($"[Switch] Ancestry: {ancestryInfo}");

                    string retryMsg = "[Switch] Retrying with --ignore-ancestry (safe for switch; merge-tracking limited)...";
                    LogWarning(retryMsg);
                    SVNLogBridge.LogWarning(retryMsg);

                    try
                    {
                        await SwitchAsync(workingDir, targetUrl, true, token).ConfigureAwait(false);

                        string warnMsg = "[Switch] Switched with --ignore-ancestry. WARNING: future merges between " +
                                         $"'{cleanTarget}' and trunk may not auto-detect common revisions — " +
                                         "consider recreating this branch via 'svn copy' for full merge support.";
                        LogWarning($"<color=#FFAA00>{warnMsg}</color>");
                        SVNLogBridge.LogWarning(warnMsg);
                    }
                    catch (Exception retryEx)
                    {
                        LogErrorLocal($"[Switch] FAILED even with --ignore-ancestry: {retryEx.Message}");
                        ReportSwitchFailureWithShelfInfo(shelfName, retryEx.Message);
                        await SafeCleanupAfterFailedSwitch(workingDir, token).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    LogErrorLocal($"[Switch] FAILED: {msg}");
                    ReportSwitchFailureWithShelfInfo(shelfName, msg);
                    await SafeCleanupAfterFailedSwitch(workingDir, token).ConfigureAwait(false);
                    return;
                }
            }

            await CleanupOrphanedFilesAsync(workingDir, token, preSwitchUnversioned).ConfigureAwait(false);

            SVNStatus.ClearLockCache();
            LogSuccess($"[Switch] Successfully switched to '{targetName}'.");

            if (!string.IsNullOrWhiteSpace(shelfName))
            {
                LogInfo($"[Switch] Previous changes were shelved as: {shelfName}");
                LogInfo("[Switch] Restore them manually from the Shelves panel.");
            }

            var bar = svnManager.GetModule<SVNBar>();
            if (bar != null) await bar.ShowProjectInfo(null, workingDir).ConfigureAwait(false);
            await svnManager.RefreshStatus().ConfigureAwait(false);
            await RefreshUnifiedList().ConfigureAwait(false);
        }

        private async Task<string> DiagnoseAncestryAsync(string branchUrl, string workingDir, CancellationToken token)
        {
            try
            {
                string xml = await SvnRunner.RunAsync(
                    $"log \"{branchUrl}\" --stop-on-copy --limit 1 --verbose --xml",
                    workingDir, false, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(xml)) return null;

                var doc = System.Xml.Linq.XDocument.Parse(xml);
                var entry = doc.Descendants("logentry").FirstOrDefault();
                if (entry == null) return "no log entries found (empty branch?)";

                var pathEl = entry.Descendants("path").FirstOrDefault();
                string action = pathEl?.Attribute("action")?.Value ?? "?";
                string copyFrom = pathEl?.Attribute("copyfrom-path")?.Value;
                string copyRev = pathEl?.Attribute("copyfrom-rev")?.Value;

                if (!string.IsNullOrEmpty(copyFrom))
                    return $"created via {action} from '{copyFrom}' @ r{copyRev} — branch HAS ancestry, but current working-copy line may have been recreated (check trunk for D+A entries).";

                return $"created via {action} WITHOUT copyfrom — manual creation (svn mkdir + add), not svn copy. No ancestry link.";
            }
            catch
            {
                return "ancestry analysis unavailable (log query failed).";
            }
        }

        private void ReportSwitchFailureWithShelfInfo(string shelfName, string errorMessage)
        {
            if (!string.IsNullOrWhiteSpace(shelfName))
            {
                string safeMsg = $"[Switch] Your local changes are SAFE in shelf: {shelfName}";
                LogWarning($"<color=#FFFF00><b>{safeMsg}</b></color>");
                SVNLogBridge.LogWarning(safeMsg);
                SVNLogBridge.LogWarning("[Switch] Restore them from the Shelves panel (unshelve) and try switching again.");
            }
            if (!string.IsNullOrEmpty(errorMessage) && errorMessage.Contains("E195012"))
            {
                SVNLogBridge.LogWarning("[Switch] E195012: branch has no common ancestry with working copy. " +
                                        "For production branches, always create via: svn copy ^/trunk ^/branches/<name>");
            }
        }

        private async Task SafeCleanupAfterFailedSwitch(string workingDir, CancellationToken token)
        {
            try { await SvnRunner.RunAsync("cleanup", workingDir, true, token).ConfigureAwait(false); } catch { }
            try { await svnManager.RefreshStatus().ConfigureAwait(false); } catch { }
        }

        private async Task ExecuteRemoteDeleteTask(string targetName, string subFolder, CancellationToken token)
        {
            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            string currentUrl = await SvnRunner.GetRepoUrlAsync(svnManager.WorkingDir).ConfigureAwait(false);
            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) { LogErrorLocal("[Delete] Repo root missing."); return; }

            string targetUrl = BuildSvnUrl(repoRoot, subFolder, targetName);
            if (NormalizeUrl(currentUrl) == NormalizeUrl(targetUrl)) { LogErrorLocal("ABORTED: Active branch/tag cannot be deleted."); return; }

            string msg = $"Deleted {subFolder}: {targetName} via Unity SVN Tool";
            await DeleteRemotePathAsync(svnManager.WorkingDir, targetUrl, msg, token).ConfigureAwait(false);
            LogSuccess($"Deleted: {targetName}");
            await RefreshUnifiedList().ConfigureAwait(false);
        }

        private static async Task<HashSet<string>> SnapshotUnversionedAsync(string workingDir, CancellationToken token)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string status = await SvnRunner.RunAsync("status", workingDir, false, token).ConfigureAwait(false);
                foreach (string line in (status ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length < 8 || line[0] != '?') continue;
                    string rel = NormalizeRelativeForCompare(line.Substring(8).Trim());
                    if (!string.IsNullOrWhiteSpace(rel)) set.Add(rel);
                }
                return set;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return null;
            }
        }

        private static string NormalizeRelativeForCompare(string rel) =>
            string.IsNullOrWhiteSpace(rel) ? "" : rel.Replace('\\', '/').Trim().TrimEnd('/');

        private async Task CleanupOrphanedFilesAsync(string workingDir, CancellationToken token, HashSet<string> preSwitchUnversioned)
        {
            try
            {
                if (preSwitchUnversioned == null)
                {
                    LogInfo("[Switch] Unversioned snapshot unavailable – orphan cleanup skipped (safety).");
                    return;
                }

                string statusOutput = await SvnRunner.RunAsync("status", workingDir, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(statusOutput)) return;

                string workingRoot = Path.GetFullPath(workingDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var orphans = new List<string>();
                foreach (string rawLine in statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;
                    string line = rawLine.TrimEnd();
                    if (line.Length < 8 || line[0] != '?') continue;

                    string relativePath = line.Substring(8).Trim();
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;

                    if (preSwitchUnversioned.Contains(NormalizeRelativeForCompare(relativePath)))
                        continue;

                    string fullPath = Path.GetFullPath(Path.Combine(workingRoot, relativePath));

                    if (!fullPath.StartsWith(workingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        LogWarning($"[Switch] Skipping suspicious path outside working copy: {relativePath}");
                        continue;
                    }

                    orphans.Add(fullPath);
                }

                if (orphans.Count == 0) return;

                LogInfo($"[Switch] Cleaning up {orphans.Count} orphaned item(s) left from previous branch...");
                int removed = 0;

                foreach (string path in orphans.OrderByDescending(p => p.Length))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            removed++;
                            string metaPath = path + ".meta";
                            if (File.Exists(metaPath)) File.Delete(metaPath);
                        }
                        else if (Directory.Exists(path))
                        {
                            Directory.Delete(path, true);
                            removed++;
                            string metaPath = path + ".meta";
                            if (File.Exists(metaPath)) File.Delete(metaPath);
                        }
                    }
                    catch (Exception ex) { LogWarning($"[Switch] Could not remove '{path}': {ex.Message}"); }
                }

                if (removed > 0)
                    LogSuccess($"[Switch] Removed {removed} orphaned item(s). Working copy is clean.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LogWarning($"[Switch] Cleanup warning: {ex.Message}"); }
        }

        private static string BuildSvnUrl(string repoRoot, string subFolder, string name)
        {
            if (name.Equals("trunk", StringComparison.OrdinalIgnoreCase))
                return $"{repoRoot}/trunk";
            return $"{repoRoot}/{subFolder}/{SanitizeSvnName(name)}";
        }

        private static string SanitizeSvnName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty.");
            var allowedChars = new HashSet<char>("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (allowedChars.Contains(c)) sb.Append(c);
                else sb.Append('_');
            }
            string clean = sb.ToString().Trim('_', ' ', '.');
            if (clean.Length == 0) throw new ArgumentException("Name contains no valid characters.");
            return clean;
        }

        private static string SanitizeSvnPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            int protocolIdx = path.IndexOf("://", StringComparison.OrdinalIgnoreCase);
            if (protocolIdx >= 0)
            {
                path = path.Substring(protocolIdx + 3);
                int firstSlash = path.IndexOf('/');
                if (firstSlash >= 0) path = path.Substring(firstSlash + 1);
            }

            var segments = path.Split('/');
            var sb = new StringBuilder();
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0) sb.Append('/');
                string seg = segments[i].Trim();
                if (!string.IsNullOrEmpty(seg))
                {
                    var allowedChars = new HashSet<char>("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");
                    foreach (char c in seg)
                    {
                        sb.Append(allowedChars.Contains(c) ? c : '_');
                    }
                }
            }
            return sb.ToString().Trim('/');
        }

        private static bool IsProtectedBranch(string name) => string.Equals(name?.Trim(), "trunk", StringComparison.OrdinalIgnoreCase);

        private static bool IsPlaceholder(string text) =>
            PlaceholderTexts.Contains((text ?? "").Trim());

        // === FIX Ś1: odczyt currentSelection PRZENIESIONY do środka dispatchowanego
        // callbacku — wcześniej czytany z thread poolu (race z ClearOptions/AddOptions).
        private static void UpdateDropdown(TMP_Dropdown dropdown, string[] items, string emptyMsg, bool includeTrunk, string currentBranchName = null)
        {
            if (dropdown == null) return;

            dropdown.ClearOptions();
            var options = new List<string>(capacity: (items?.Length ?? 0) + 2);
            if (includeTrunk) options.Add("trunk");

            if (items != null)
            {
                foreach (var item in items)
                {
                    string clean = item?.Trim().TrimEnd('/');
                    if (!string.IsNullOrEmpty(clean) && !clean.Equals("trunk", StringComparison.OrdinalIgnoreCase))
                        options.Add(clean);
                }
            }

            if (options.Count == 0) options.Add(emptyMsg);

            dropdown.AddOptions(options);

            // Odczyt aktualnego wyboru — TUTAJ (main thread, po AddOptions).
            string currentSelection = null;
            if (dropdown.options.Count > 0 && dropdown.value >= 0 && dropdown.value < dropdown.options.Count)
            {
                string current = dropdown.options[dropdown.value].text;
                if (!IsPlaceholder(current)) currentSelection = current;
            }

            string targetSelection = currentSelection ?? currentBranchName;
            int indexToSelect = 0;
            if (!string.IsNullOrEmpty(targetSelection))
            {
                int foundIndex = options.FindIndex(o => string.Equals(o, targetSelection, StringComparison.OrdinalIgnoreCase));
                if (foundIndex >= 0) indexToSelect = foundIndex;
            }

            dropdown.value = indexToSelect;
            dropdown.RefreshShownValue();
        }

        private static string NormalizeUrl(string url) => (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();

        private static string GetBranchNameFromUrl(string url, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(url)) return "unknown";
            string cleanUrl = url.Trim().TrimEnd('/');

            if (cleanUrl.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase)) return "trunk";

            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                string cleanRoot = repoRoot.Trim().TrimEnd('/');
                if (cleanUrl.StartsWith(cleanRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string relative = cleanUrl.Substring(cleanRoot.Length).TrimStart('/');
                    string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && (parts[0].Equals("branches", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("tags", StringComparison.OrdinalIgnoreCase)))
                    {
                        return parts[1];
                    }
                }
            }

            string[] allParts = cleanUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return allParts.Length > 0 ? allParts[^1] : "unknown";
        }

        private static string GetRelativePathFromUrl(string url, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(repoRoot)) return "trunk";
            string cleanUrl = url.Trim().TrimEnd('/');
            string cleanRoot = repoRoot.Trim().TrimEnd('/');

            if (cleanUrl.Equals(cleanRoot, StringComparison.OrdinalIgnoreCase))
                return "trunk";

            if (cleanUrl.StartsWith(cleanRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return cleanUrl.Substring(cleanRoot.Length + 1);
            }

            return cleanUrl;
        }

        private static string ExtractBranchName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "trunk";
            string clean = path.Trim('/');
            if (clean.StartsWith("trunk", StringComparison.OrdinalIgnoreCase)) return "trunk";

            string[] parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && (parts[0].Equals("branches", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("tags", StringComparison.OrdinalIgnoreCase)))
            {
                return $"{parts[0]}/{parts[1]}";
            }

            return parts.Length > 0 ? parts[^1] : path;
        }

        private static string GetSafeShelfName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            try { return SanitizeSvnName(name); }
            catch { return "unnamed"; }
        }

        private static string GetSafeFileName(string name) => GetSafeShelfName(name);

        private bool ConfirmDelete(ref DateTime lastClickTime, string name)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastClickTime).TotalSeconds < 3.0)
            {
                lastClickTime = DateTime.MinValue;
                return true;
            }
            lastClickTime = now;
            LogWarning($"<b>[Double Click Required]</b> Click 'Delete' again within 3s to permanently delete: <color=red>{name}</color>");
            return false;
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir, CancellationToken token = default)
        {
            string output = await SvnRunner.RunAsync("status", workingDir, false, token).ConfigureAwait(false);
            var stats = new SvnStats();
            foreach (string rawLine in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.TrimEnd();
                if (line.Length == 0) continue;

                char col0 = line[0];
                char col1 = line.Length > 1 ? line[1] : ' ';

                if (col0 == 'M' || col1 == 'M') stats.ModifiedCount++;
                if (col0 == 'A') stats.AddedCount++;
                if (col0 == 'D' || col1 == 'D') stats.DeletedCount++;
                if (col0 == 'C' || col1 == 'C') stats.ConflictsCount++;
                if (col0 == '?') stats.NewFilesCount++;
                if (col0 == 'I') stats.IgnoredCount++;
            }
            return stats;
        }

        public static async Task<string> SwitchAsync(string workingDir, string targetUrl, bool ignoreAncestry, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(workingDir)) throw new ArgumentException("Working directory is empty.", nameof(workingDir));
            if (string.IsNullOrWhiteSpace(targetUrl)) throw new ArgumentException("Target URL is empty.", nameof(targetUrl));

            string command = $"switch \"{targetUrl}\" \"{workingDir}\"";
            if (ignoreAncestry) command += " --ignore-ancestry";
            command += " --non-interactive";

            return await SvnRunner.RunAsync(command, workingDir, true, token).ConfigureAwait(false);
        }

        public static async Task<string> CopyAsync(string workingDir, string sourceUrl, string destUrl, string message, CancellationToken token = default)
        {
            string cmd = $"copy \"{sourceUrl}\" \"{destUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(cmd, workingDir, false, token).ConfigureAwait(false);
        }

        public static async Task<string> DeleteRemotePathAsync(string workingDir, string remoteUrl, string message, CancellationToken token = default)
        {
            string args = $"rm \"{remoteUrl}\" -m \"{message}\"";
            return await SvnRunner.RunAsync(args, workingDir, false, token).ConfigureAwait(false);
        }

        protected override TMP_Text GetConsole() => svnUI?.BranchTagConsoleText;
    }
}