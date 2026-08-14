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
                try { refreshCts.Cancel(); refreshCts.Dispose(); } catch { }
            }

            if (svnManager != null)
                svnManager.OnProjectChanged -= OnProjectChangedHandler;

            try { _operationLock.Dispose(); } catch { }
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
            bool hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
            if (!hasLock) return;

            CancellationTokenSource localCts = new();
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
                try { localCts.Dispose(); } catch { }
                try { _operationLock.Release(); } catch { }
            }
        }

        private void OnProjectChangedHandler(SVNProject project)
        {
            _cachedRepoRoot = null;
            _ = RefreshOnProjectLoadedAsync();
        }

        private async Task RefreshOnProjectLoadedAsync()
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
                await RefreshUnifiedList().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarning($"[BranchTag] Auto-refresh failed: {ex.Message}");
            }
        }

        private async Task<string[]> GetRepoListAsync(string url, CancellationToken token = default)
        {
            try
            {
                string currentKey = SvnRunner.KeyPath;
                string sshArgs = "-o BatchMode=yes -o StrictHostKeyChecking=no";
                if (!string.IsNullOrEmpty(currentKey))
                    sshArgs = $"-i \"{currentKey}\" {sshArgs}";

                string command = $"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" list \"{url}\" --non-interactive";
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

                LogWarning($"[DEBUG REPO ROOT] GetRepoRoot zwrocil: '{_cachedRepoRoot}'");
            }
            catch (Exception ex)
            {
                LogWarning($"[SVNBranchTag] GetRepoRoot failed: {ex.Message}");
            }
            return _cachedRepoRoot;
        }

        public async Task RefreshIfEmpty()
        {
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
            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            if (!ValidateCreateInputs(out string name, out string subFolder)) return;

            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            string sourceRelativePath = svnUI?.BranchSourcePathInput?.text?.Trim();
            if (string.IsNullOrWhiteSpace(sourceRelativePath))
                sourceRelativePath = "trunk";

            string revision = svnUI.RevisionInput?.text?.Trim();
            bool hasRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

            string sourceUrl = $"{repoRoot}/{SanitizeSvnPath(sourceRelativePath)}";
            string targetUrl = $"{repoRoot}/{subFolder}/{SanitizeSvnName(name)}";

            string cmd = hasRevision
                ? $"copy \"{sourceUrl}@{revision}\" \"{targetUrl}\" -m \"Created {subFolder}/{SanitizeSvnName(name)} from {sourceRelativePath}@{revision}\" --parents"
                : $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"Created {subFolder}/{SanitizeSvnName(name)} from {sourceRelativePath}\" --parents";

            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, token).ConfigureAwait(false);
            LogSuccess(hasRevision
                ? $"Created: {name} from {sourceRelativePath} at revision {revision}"
                : $"Created: {name} from {sourceRelativePath}");
            await RefreshUnifiedList().ConfigureAwait(false);
        }

        private async Task CreateBranchFromSelectedCore(CancellationToken token)
        {
            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            if (!ValidateCreateInputs(out string newName, out string subFolder)) return;

            TMP_Dropdown sourceDropdown = subFolder == "branches" ? svnUI?.BranchesDropdown : svnUI?.TagsDropdown;
            if (sourceDropdown == null || sourceDropdown.options.Count == 0)
            { LogErrorLocal($"[Error] No {subFolder} available."); return; }
            if (sourceDropdown.value < 0 || sourceDropdown.value >= sourceDropdown.options.Count)
            { LogErrorLocal($"[Error] Invalid {subFolder} selection."); return; }

            string selected = sourceDropdown.options[sourceDropdown.value].text;
            if (IsPlaceholder(selected) || string.IsNullOrEmpty(selected))
            { LogErrorLocal($"[Error] Invalid source {subFolder}."); return; }

            string repoRoot = EnsureRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot)) return;

            string suggestedPath = selected.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                ? "trunk"
                : $"{subFolder}/{SanitizeSvnName(selected)}";

            if (svnUI?.BranchSourcePathInput != null)
            {
                svnUI.BranchSourcePathInput.SetTextWithoutNotify(suggestedPath);
            }

            string sourceRelativePath = svnUI?.BranchSourcePathInput?.text?.Trim();
            if (string.IsNullOrWhiteSpace(sourceRelativePath))
                sourceRelativePath = suggestedPath;

            string revision = svnUI.RevisionInput?.text?.Trim();
            bool hasRevision = !string.IsNullOrEmpty(revision) && long.TryParse(revision, out _);

            string sourceUrl = $"{repoRoot}/{SanitizeSvnPath(sourceRelativePath)}";
            if (hasRevision) sourceUrl = $"{sourceUrl}@{revision}";

            string targetUrl = $"{repoRoot}/{subFolder}/{SanitizeSvnName(newName)}";
            string message = hasRevision
                ? $"Created {subFolder}/{newName} from {sourceRelativePath}@{revision}"
                : $"Created {subFolder}/{newName} from {sourceRelativePath}";

            string cmd = $"copy \"{sourceUrl}\" \"{targetUrl}\" -m \"{message}\" --parents";
            await SvnRunner.RunAsync(cmd, svnManager.WorkingDir, false, token).ConfigureAwait(false);
            LogSuccess(hasRevision
                ? $"Created: {newName} from {sourceRelativePath} at revision {revision}"
                : $"Created: {newName} from {sourceRelativePath}");
            await RefreshUnifiedList().ConfigureAwait(false);
        }

        public async Task RefreshUnifiedList()
        {
            if (svnUI?.BranchesDropdown == null && svnUI?.TagsDropdown == null) return;

            if (!IsReady())
            {
                PostToMainThread(() =>
                {
                    UpdateDropdown(svnUI.BranchesDropdown, Array.Empty<string>(), "Loading...", true);
                    UpdateDropdown(svnUI.TagsDropdown, Array.Empty<string>(), "Loading...", false);
                });
                return;
            }

            var oldCts = Interlocked.Exchange(ref _refreshCts, new CancellationTokenSource());
            oldCts?.Cancel();
            try { oldCts?.Dispose(); } catch { }

            var token = _refreshCts.Token;

            try
            {
                string repoRoot = EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    PostToMainThread(() =>
                    {
                        UpdateDropdown(svnUI.BranchesDropdown, Array.Empty<string>(), "Not ready", true);
                        UpdateDropdown(svnUI.TagsDropdown, Array.Empty<string>(), "Not ready", false);
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

                PostToMainThread(() =>
                {
                    UpdateDropdown(svnUI.BranchesDropdown, branchesTask.Result, "No branches", true, actualCurrentBranch);
                    UpdateDropdown(svnUI.TagsDropdown, tagsTask.Result, "No tags", false, actualCurrentBranch);

                    if (svnUI?.BranchSourcePathInput != null)
                    {
                        string currentRelativePath = GetRelativePathFromUrl(currentUrl, repoRoot);
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
                    UpdateDropdown(svnUI.BranchesDropdown, Array.Empty<string>(), "Error", true);
                    UpdateDropdown(svnUI.TagsDropdown, Array.Empty<string>(), "Error", false);
                });
            }
        }

        public async Task DiffWithCurrent(bool isTag) => await RunWithOperationLockAsync(token => DiffWithCurrentCore(isTag, token));

        private async Task DiffWithCurrentCore(bool isTag, CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Diff] SVN not ready."); return; }

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            TMP_Dropdown dropdown = isTag ? svnUI?.TagsDropdown : svnUI?.BranchesDropdown;
            if (dropdown == null || dropdown.options.Count == 0) { LogErrorLocal("[Diff] No items available."); return; }
            if (dropdown.value < 0 || dropdown.value >= dropdown.options.Count) { LogErrorLocal("[Diff] Invalid selection."); return; }

            string selected = dropdown.options[dropdown.value].text;
            if (string.IsNullOrEmpty(selected) || IsPlaceholder(selected)) { LogErrorLocal("[Diff] Please select a valid branch/tag."); return; }

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
            if (!string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith("svn: E")) { LogErrorLocal($"[Diff Error] {output}"); return; }
            if (string.IsNullOrWhiteSpace(output)) { LogSuccess("[Diff] No differences found."); return; }

            var sb = new StringBuilder(4096);
            sb.AppendLine("=== SVN DIFF SUMMARY ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Current : {currentUrl}");
            sb.AppendLine($"Selected: {selectedUrl}");
            sb.AppendLine(new string('-', 60));

            int added = 0, modified = 0, deleted = 0;
            foreach (string line in (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
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

            string fileName = $"Diff_{currentName}_vs_{selected}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string tempFilePath = Path.Combine(Application.temporaryCachePath, fileName);

            await File.WriteAllTextAsync(tempFilePath, sb.ToString(), token).ConfigureAwait(false);

            try { Process.Start(new ProcessStartInfo(tempFilePath) { UseShellExecute = true }); }
            catch (Exception ex) { LogWarning($"[Diff] Could not open file: {ex.Message}"); }

            LogSuccess($"[Diff] Exported: {fileName}");
            LogInfo($"<color=#55FF55>+{added}</color>  <color=#FFFF55>~{modified}</color>  <color=#FF9900>-{deleted}</color>");
        }

        public async Task ShowDetailsForSelected() => await RunWithOperationLockAsync(ShowDetailsForSelectedCore);

        private async Task ShowDetailsForSelectedCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Details] SVN not ready."); return; }

            await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
            string subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";
            TMP_Dropdown dropdown = subFolder == "branches" ? svnUI?.BranchesDropdown : svnUI?.TagsDropdown;
            if (dropdown == null || dropdown.options.Count == 0) { LogErrorLocal("[Details] No items available."); return; }
            if (dropdown.value < 0 || dropdown.value >= dropdown.options.Count) { LogErrorLocal("[Details] Invalid selection."); return; }

            string selected = dropdown.options[dropdown.value].text;
            if (string.IsNullOrEmpty(selected) || IsPlaceholder(selected)) { LogErrorLocal("[Details] Please select a valid branch/tag."); return; }

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

            if (DateTime.TryParseExact(firstDate, "yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed))
                LogInfo($"Created on : {parsed.ToLocalTime():yyyy-MM-dd HH:mm:ss} (local)");
            else LogInfo($"Created on : {firstDate}");

            LogInfo($"Source     : {sourceBranch} (at {sourceRevision})");

            if (commitMsg != "N/A" && commitMsg.Length > 0)
            {
                if (commitMsg.Length > 100) commitMsg = commitMsg.Substring(0, 100) + "...";
                LogInfo($"Message    : {commitMsg}");
            }
        }

        public async Task SwitchToSelectedBranch() => await RunWithOperationLockAsync(token => SwitchToSelectedBranchCore(token));
        public async Task SwitchToSelectedTag() => await RunWithOperationLockAsync(token => SwitchToSelectedTagCore(token));

        private async Task SwitchToSelectedBranchCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Switch] SVN not ready."); return; }
            if (svnUI?.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) return;
            if (svnUI.BranchesDropdown.value < 0 || svnUI.BranchesDropdown.value >= svnUI.BranchesDropdown.options.Count) return;
            string selected = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text;
            if (IsPlaceholder(selected)) return;
            await ExecuteUnifiedSwitch(selected, "branches", token).ConfigureAwait(false);
        }

        private async Task SwitchToSelectedTagCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Switch] SVN not ready."); return; }
            if (svnUI?.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;
            if (svnUI.TagsDropdown.value < 0 || svnUI.TagsDropdown.value >= svnUI.TagsDropdown.options.Count) return;
            string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
            if (IsPlaceholder(selected)) return;
            await ExecuteUnifiedSwitch(selected, "tags", token).ConfigureAwait(false);
        }

        public async Task DeleteSelectedBranch() => await RunWithOperationLockAsync(DeleteSelectedBranchCore);
        public async Task DeleteSelectedTag() => await RunWithOperationLockAsync(DeleteSelectedTagCore);

        private async Task DeleteSelectedBranchCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Delete] SVN not ready."); return; }
            if (svnUI?.BranchesDropdown == null || svnUI.BranchesDropdown.options.Count == 0) { LogErrorLocal("Delete aborted."); return; }
            if (svnUI.BranchesDropdown.value < 0 || svnUI.BranchesDropdown.value >= svnUI.BranchesDropdown.options.Count) { LogErrorLocal("Delete aborted."); return; }

            string selectedBranch = svnUI.BranchesDropdown.options[svnUI.BranchesDropdown.value].text?.Trim();
            if (string.IsNullOrEmpty(selectedBranch) || IsProtectedBranch(selectedBranch)) { LogErrorLocal("SECURITY BLOCK: 'trunk' is protected."); return; }
            if (!ConfirmDelete(ref _lastDeleteBranchClickTime, selectedBranch)) return;
            await ExecuteRemoteDeleteTask(selectedBranch, "branches", token).ConfigureAwait(false);
        }

        private async Task DeleteSelectedTagCore(CancellationToken token)
        {
            if (!IsReady()) { LogWarning("[Delete] SVN not ready."); return; }
            if (svnUI?.TagsDropdown == null || svnUI.TagsDropdown.options.Count == 0) return;
            if (svnUI.TagsDropdown.value < 0 || svnUI.TagsDropdown.value >= svnUI.TagsDropdown.options.Count) { LogErrorLocal("Delete aborted."); return; }
            string selected = svnUI.TagsDropdown.options[svnUI.TagsDropdown.value].text;
            if (IsPlaceholder(selected)) return;
            if (string.IsNullOrEmpty(selected) || IsProtectedBranch(selected))
            {
                LogErrorLocal("SECURITY BLOCK: 'trunk' is protected.");
                return;
            }
            if (!ConfirmDelete(ref _lastDeleteTagClickTime, selected)) return;
            await ExecuteRemoteDeleteTask(selected, "tags", token).ConfigureAwait(false);
        }

        private async Task ExecuteUnifiedSwitch(string targetName, string subFolder, CancellationToken token)
        {
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

            LogWarning($"[Switch Debug] RawName: '{targetName}' | CleanName: '{cleanTarget}' | SubFolder: '{subFolder}' | FinalURL: '{targetUrl}'");

            if (NormalizeUrl(currentUrl) == NormalizeUrl(targetUrl)) { LogWarning($"[Switch] You are already on '{targetName}'."); return; }

            LogInfo($"[Switch] Current URL: {currentUrl}");
            LogInfo($"[Switch] Target URL:  {targetUrl}");

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

            bool ignoreAncestry = svnUI?.IgnoreAncestryToggle != null && svnUI.IgnoreAncestryToggle.isOn;

            string switchResult = await SwitchAsync(workingDir, targetUrl, ignoreAncestry, token).ConfigureAwait(false);
            if (switchResult?.Contains("svn: E", StringComparison.OrdinalIgnoreCase) == true)
                throw new Exception(switchResult);

            await CleanupOrphanedFilesAsync(workingDir, token).ConfigureAwait(false);

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
        private static bool IsPlaceholder(string text) => text?.Contains("Loading") == true || text?.Contains("No ") == true || text?.Contains("None") == true;

        private static void UpdateDropdown(TMP_Dropdown dropdown, string[] items, string emptyMsg, bool includeTrunk, string currentBranchName = null)
        {
            if (dropdown == null) return;

            string currentSelection = null;
            if (dropdown.options.Count > 0 && dropdown.value >= 0 && dropdown.value < dropdown.options.Count)
            {
                currentSelection = dropdown.options[dropdown.value].text;
            }

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
                if (cleanUrl.StartsWith(cleanRoot, StringComparison.OrdinalIgnoreCase))
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

        private bool ValidateCreateInputs(out string name, out string subFolder)
        {
            name = svnUI?.BranchNameInput?.text?.Trim();
            subFolder = (svnUI?.TypeSelector?.value == 0) ? "branches" : "tags";

            if (string.IsNullOrWhiteSpace(name))
            {
                LogErrorLocal("[Error] Branch/Tag name cannot be empty.");
                return false;
            }
            return true;
        }

        public static async Task<SvnStats> GetStatsAsync(string workingDir, CancellationToken token = default)
        {
            string output = await SvnRunner.RunAsync("status", workingDir, false, token).ConfigureAwait(false);
            var stats = new SvnStats();
            foreach (string rawLine in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.TrimEnd();
                if (line.Length == 0) continue;
                switch (line[0])
                {
                    case 'M': stats.ModifiedCount++; break;
                    case 'A': stats.AddedCount++; break;
                    case 'D': stats.DeletedCount++; break;
                    case 'C': stats.ConflictsCount++; break;
                    case '?': stats.NewFilesCount++; break;
                    case 'I': stats.IgnoredCount++; break;
                }
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

        private async Task CleanupOrphanedFilesAsync(string workingDir, CancellationToken token)
        {
            try
            {
                string statusOutput = await SvnRunner.RunAsync("status", workingDir, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(statusOutput)) return;

                var orphans = new List<string>();
                foreach (string rawLine in statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;
                    string line = rawLine.TrimEnd();
                    if (line.Length < 8 || line[0] != '?') continue;

                    string relativePath = line.Substring(8).Trim();
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;

                    string fullPath = Path.GetFullPath(Path.Combine(workingDir, relativePath));
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

        protected override TMP_Text GetConsole() => svnUI?.BranchTagConsoleText;
    }
}