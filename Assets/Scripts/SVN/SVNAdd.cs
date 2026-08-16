using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNAdd : SVNBase, IDisposable
    {
        private CancellationTokenSource _activeCTS;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private const int CleanupTimeoutSeconds = 30;
        private int _disposed;

        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        public async void AddAll()
        {
            try
            {
                await AddAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Add] AddAll failed: {ex.Message}");
            }
        }

        public void AddSingleItem(SvnTreeElement element)
        {
            if (element != null)
            {
                _ = AddSingleItemAsync(element);
            }
        }

        public void Cancel()
        {
            try
            {
                var cts = Volatile.Read(ref _activeCTS);
                if (cts == null || cts.IsCancellationRequested) return;

                cts.Cancel();
                SVNLogBridge.LogLine("<color=orange><b>[Add]</b> Cancellation requested...</color>");
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Add] Error during cancel: {ex.Message}");
            }
        }

        private async Task AddAllAsync()
        {
            bool hasLock = false;
            bool ownsProcessing = false;
            CancellationTokenSource localCts = null;
            string tempFile = null;

            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock || IsProcessing) return;

                string root = NormalizeRoot(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(root)) return;

                ownsProcessing = true;
                IsProcessing = true;

                localCts = new CancellationTokenSource();
                Volatile.Write(ref _activeCTS, localCts);
                CancellationToken token = localCts.Token;

                ShowProgressBar();

                SVNLogBridge.LogLine("<b>[Add]</b> Checking working copy locks...");
                bool cleanupOk = await CleanupWorkingCopyAsync(root, token).ConfigureAwait(false);
                if (!cleanupOk) return;

                SVNLogBridge.LogLine("<b>[Add]</b> Scanning for unversioned items...");
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var unversioned = statusDict
                    .Where(x => x.Value.status == "?")
                    .Select(x => NormalizeRelativePath(x.Key))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (unversioned.Count == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>Nothing to add. All items are already tracked or ignored.</color>");
                    return;
                }

                var itemsToAdd = ReduceToTopLevelItems(unversioned);
                if (itemsToAdd.Count == 0) return;

                SVNLogBridge.LogLine($"Found <b>{itemsToAdd.Count}</b> unversioned target(s). Adding...");

                tempFile = Path.Combine(Path.GetTempPath(), $"svn_add_all_{Guid.NewGuid():N}.txt");
                const int chunkSize = 500;
                int processed = 0, total = itemsToAdd.Count;
                DateTime start = DateTime.UtcNow, lastUi = DateTime.MinValue;

                for (int i = 0; i < itemsToAdd.Count; i += chunkSize)
                {
                    token.ThrowIfCancellationRequested();
                    var chunk = itemsToAdd.Skip(i).Take(chunkSize).ToList();
                    await Task.Run(() => File.WriteAllLines(tempFile, chunk, new UTF8Encoding(false)), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    await SvnRunner.RunLiveAsync(new[] { "add", "--force", "--parents", "--targets", tempFile }, root, line =>
                    {
                        if (string.IsNullOrWhiteSpace(line) || !line.Trim().StartsWith("A", StringComparison.Ordinal)) return;
                        int current = Interlocked.Increment(ref processed);
                        var now = DateTime.UtcNow;
                        if ((now - lastUi).TotalMilliseconds < 200) return;
                        lastUi = now;
                        double elapsed = Math.Max((now - start).TotalSeconds, 0.1);
                        float progress = Mathf.Clamp01(0.1f + (total > 0 ? current / (float)total : 0f) * 0.8f);
                        PostToMainThread(() =>
                        {
                            try
                            {
                                if (svnUI?.OperationProgressBar != null) svnUI.OperationProgressBar.value = progress;
                                SVNLogBridge.LogLine($"<color=yellow>[Adding]</color> {current}/{total} items | Speed: <b>{current / elapsed:F0} items/s</b>");
                            }
                            catch { }
                        });
                    }, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                double totalTime = Math.Max((DateTime.UtcNow - start).TotalSeconds, 0.01);
                SVNLogBridge.LogLine($"<color=green><b>[SUCCESS]</b> {processed} of {total} top-level targets marked as 'Added' in {totalTime:F1}s.</color>");
                SVNLogBridge.LogLine("<color=white>Note: You still need to <b>Commit</b> to upload them to the server.</color>");
                await RefreshStatusTreeAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange><b>[ABORTED]</b> Add operation cancelled by user.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Add] Error during AddAll: {ex.Message}");
            }
            finally
            {
                SafeDelete(tempFile);
                FinalizeOperation(hasLock, localCts, ownsProcessing);
            }
        }

        private async Task AddSingleItemAsync(SvnTreeElement element)
        {
            bool hasLock = false, ownsProcessing = false;
            CancellationTokenSource localCts = null;
            string tempFile = null;

            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock || IsProcessing) return;

                string root = NormalizeRoot(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(root)) return;

                ownsProcessing = true;
                IsProcessing = true;
                localCts = new CancellationTokenSource();
                Volatile.Write(ref _activeCTS, localCts);
                CancellationToken token = localCts.Token;

                ShowProgressBar();

                bool cleanupOk = await CleanupWorkingCopyAsync(root, token).ConfigureAwait(false);
                if (!cleanupOk) return;

                if (!TryGetRelativePath(root, element.FullPath, out string relativePath))
                {
                    SVNLogBridge.LogLine($"<color=#FF4444>Cannot add '{element.Name}'. Path is outside the working copy or invalid.</color>");
                    return;
                }

                string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return;

                SVNLogBridge.LogLine($"<b>[Add]</b> Adding item: {element.Name}...");
                var paths = ReduceToTopLevelItems(new List<string> { relativePath });
                if (paths.Count == 0) return;

                tempFile = Path.Combine(Path.GetTempPath(), $"svn_add_single_{Guid.NewGuid():N}.txt");
                await Task.Run(() => File.WriteAllLines(tempFile, paths, new UTF8Encoding(false)), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                await SvnRunner.RunAsync(new[] { "add", "--force", "--parents", "--targets", tempFile }, root, false, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                SVNLogBridge.LogLine($"<color=green>Successfully added: {element.Name}</color>");
                await RefreshStatusTreeAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange><b>[ABORTED]</b> Add operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"<color=#FFAA00>Add Error: {ex.Message}</color>");
            }
            finally
            {
                SafeDelete(tempFile);
                FinalizeOperation(hasLock, localCts, ownsProcessing);
            }
        }

        private async Task<bool> CleanupWorkingCopyAsync(string root, CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            try
            {
                await SvnRunner.RunAsync("cleanup", root, false, linkedCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                SVNLogBridge.LogLine("<color=#FF4444><b>Cleanup timed out.</b></color>");
                return false;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Add] Cleanup failed: {ex.Message}");
                return false;
            }
        }

        private async Task RefreshStatusTreeAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var statusModule = svnManager.GetModule<SVNStatus>();
            if (statusModule == null) return;

            try
            {
                await statusModule.RefreshModifiedInternal().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Add] Failed to refresh status tree: {ex.Message}");
            }
        }

        private string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return string.Empty;
            try { return Path.GetFullPath(root.Trim()).Replace('\\', '/').TrimEnd('/'); }
            catch { return root.Replace('\\', '/').TrimEnd('/'); }
        }

        private bool TryGetRelativePath(string root, string path, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
            string normRoot = NormalizeRoot(root);
            string normInput = path.Replace('\\', '/').Trim();
            try
            {
                string candidate = Path.IsPathRooted(normInput)
                    ? Path.GetFullPath(normInput)
                    : Path.GetFullPath(Path.Combine(normRoot, normInput));
                candidate = candidate.Replace('\\', '/').TrimEnd('/');
                if (candidate.Equals(normRoot, StringComparison.OrdinalIgnoreCase)) { relativePath = ""; return true; }
                string rootWithSlash = normRoot + "/";
                if (!candidate.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) return false;
                relativePath = candidate.Substring(rootWithSlash.Length).Replace('\\', '/').Trim('/');
                return !string.IsNullOrWhiteSpace(relativePath);
            }
            catch { return false; }
        }

        private string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string value = path.Replace('\\', '/').Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(p => p == "..")) return null;
            return string.Join("/", value.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        private List<string> ReduceToTopLevelItems(IEnumerable<string> paths)
        {
            var normalized = paths.Select(NormalizeRelativePath).Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return normalized.Where(p => !normalized.Any(parent => parent != p && p.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private void FinalizeOperation(bool hasLock, CancellationTokenSource localCts, bool ownsProcessing)
        {
            if (localCts != null)
            {
                Interlocked.CompareExchange(ref _activeCTS, null, localCts);
                try { localCts.Dispose(); } catch { }
            }
            if (hasLock)
            {
                if (ownsProcessing) IsProcessing = false;
                try { _operationLock.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { }
            }
            HideProgressBarAfterDelay(1.0f);
        }

        private void ShowProgressBar() => PostToMainThread(() =>
        {
            try
            {
                if (svnUI?.OperationProgressBar == null) return;
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.1f;
            }
            catch { }
        });

        private void HideProgressBarAfterDelay(float seconds) => _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)(seconds * 1000)).ConfigureAwait(false);
                PostToMainThread(() =>
                {
                    try
                    {
                        if (svnUI?.OperationProgressBar == null || IsProcessing) return;
                        svnUI.OperationProgressBar.gameObject.SetActive(false);
                        svnUI.OperationProgressBar.value = 0f;
                    }
                    catch { }
                });
            }
            catch { }
        });

        private static void SafeDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            Cancel();
            try { _operationLock.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }

        public async void AddSelected()
        {
            try { await AddSelectedAsync().ConfigureAwait(false); }
            catch (Exception ex) { SVNLogBridge.LogError($"[Add] AddSelected failed: {ex.Message}"); }
        }

        private async Task AddSelectedAsync()
        {
            bool hasLock = false, ownsProcessing = false;
            CancellationTokenSource localCts = null;
            string tempFile = null;

            try
            {
                hasLock = await _operationLock.WaitAsync(0).ConfigureAwait(false);
                if (!hasLock || IsProcessing) return;

                string root = NormalizeRoot(svnManager.WorkingDir);
                if (string.IsNullOrWhiteSpace(root)) return;

                var statusModule = svnManager.GetModule<SVNStatus>();
                var selectedItems = statusModule?.GetCurrentData().Where(e => e.IsChecked).ToList() ?? new List<SvnTreeElement>();

                var unversionedSelected = selectedItems
                    .Where(e => e.Status == "?" && !string.IsNullOrWhiteSpace(e.FullPath))
                    .Select(e =>
                    {
                        TryGetRelativePath(root, e.FullPath, out string rel);
                        return rel;
                    })
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (unversionedSelected.Count == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>No unversioned items selected.</color>");
                    return;
                }

                var itemsToAdd = ReduceToTopLevelItems(unversionedSelected);
                if (itemsToAdd.Count == 0) return;

                ownsProcessing = true;
                IsProcessing = true;
                localCts = new CancellationTokenSource();
                Volatile.Write(ref _activeCTS, localCts);
                CancellationToken token = localCts.Token;

                ShowProgressBar();

                bool cleanupOk = await CleanupWorkingCopyAsync(root, token).ConfigureAwait(false);
                if (!cleanupOk) return;

                SVNLogBridge.LogLine($"<b>[Add Selected]</b> Adding {itemsToAdd.Count} item(s)...");

                tempFile = Path.Combine(Path.GetTempPath(), $"svn_add_selected_{Guid.NewGuid():N}.txt");
                await Task.Run(() => File.WriteAllLines(tempFile, itemsToAdd, new UTF8Encoding(false)), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                await SvnRunner.RunAsync(new[] { "add", "--force", "--parents", "--targets", tempFile }, root, false, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                SVNLogBridge.LogLine($"<color=green><b>[SUCCESS]</b> {itemsToAdd.Count} item(s) scheduled for addition. You can now Commit Selected.</color>");
                await RefreshStatusTreeAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange><b>[ABORTED]</b> Add Selected cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"<color=#FFAA00>Add Selected Error: {ex.Message}</color>");
            }
            finally
            {
                SafeDelete(tempFile);
                FinalizeOperation(hasLock, localCts, ownsProcessing);
            }
        }
    }
}