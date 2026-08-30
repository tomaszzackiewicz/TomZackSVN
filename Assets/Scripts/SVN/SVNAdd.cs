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

        // === FIX K1: snapshot pól elementu NA MAIN THREAD (tu jesteśmy — wywołanie
        // z UI). Wcześniej async-owy rdzeń czytał element.FullPath/Name PO
        // ConfigureAwait(false) — z thread poolu.
        public void AddSingleItem(SvnTreeElement element)
        {
            if (element == null) return;

            string fullPathSnapshot = element.FullPath;
            string nameSnapshot = element.Name;

            _ = AddSingleItemAsync(fullPathSnapshot, nameSnapshot);
        }

        // === FIX K1: snapshot zaznaczenia NA MAIN THREAD (wejście z przycisku),
        // PRZED pierwszym await. Wcześniej enumeracja _flatTreeData biegła na
        // thread poolu po ConfigureAwait(false) — wyścig z Clear()/podmianą listy
        // na main thread → losowy InvalidOperationException "Collection was modified".
        public async void AddSelected()
        {
            var statusModule = svnManager.GetModule<SVNStatus>();
            List<(string FullPath, string Name)> selectionSnapshot = null;

            var data = statusModule?.GetCurrentData();
            if (data != null)
            {
                selectionSnapshot = data
                    .Where(e => e != null && e.IsChecked && e.Status == "?" && !string.IsNullOrWhiteSpace(e.FullPath))
                    .Select(e => (e.FullPath, e.Name ?? e.FullPath))
                    .ToList();
            }

            try
            {
                await AddSelectedAsync(selectionSnapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[Add] AddSelected failed: {ex.Message}");
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

                // === FIX Ś2: spójnie z Commit — wygaszenie refreshy w tle (dziś
                // serializuje to i tak write-lock na 'add', ale bez zbędnej konkurencji).
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

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
                int total = itemsToAdd.Count;
                int chunkCount = (int)Math.Ceiling(total / (double)chunkSize);

                // === FIX K2: licznik plików (linii 'A' — svn add wypisuje je
                // REKURENCYJNIE, razem z dziećmi katalogów) służy tylko do
                // speed-loga i podsumowania. Postęp paska liczony PER CHUNK —
                // wcześniej processed (dzieci!) dzielone przez total (top-level)
                // dobijało pasek do 100% po pierwszym katalogu.
                int filesAdded = 0;
                DateTime start = DateTime.UtcNow, lastUi = DateTime.MinValue;

                for (int c = 0; c < chunkCount; c++)
                {
                    token.ThrowIfCancellationRequested();
                    var chunk = itemsToAdd.Skip(c * chunkSize).Take(chunkSize).ToList();
                    await Task.Run(() => File.WriteAllLines(tempFile, chunk, new UTF8Encoding(false)), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    await SvnRunner.RunLiveAsync(new[] { "add", "--force", "--parents", "--targets", tempFile }, root, line =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;

                        // === FIX K2 (detekcja): format kolumnowy svn ("A    path"),
                        // nie dowolna linia zaczynająca się od 'A'.
                        string trimmed = line.TrimStart();
                        if (trimmed.Length < 2 || trimmed[0] != 'A' || trimmed[1] != ' ') return;

                        int current = Interlocked.Increment(ref filesAdded);
                        var now = DateTime.UtcNow;
                        if ((now - lastUi).TotalMilliseconds < 200) return;
                        lastUi = now;
                        double elapsed = Math.Max((now - start).TotalSeconds, 0.1);
                        PostToMainThread(() =>
                        {
                            try
                            {
                                SVNLogBridge.LogLine($"<color=yellow>[Adding]</color> chunk {c + 1}/{chunkCount} | {current} file(s) | Speed: <b>{current / elapsed:F0} files/s</b>");
                            }
                            catch { }
                        });
                    }, token).ConfigureAwait(false);

                    // === FIX K2: postęp uczciwie per chunk.
                    float progress = Mathf.Clamp01(0.1f + 0.8f * ((c + 1) / (float)chunkCount));
                    PostToMainThread(() =>
                    {
                        try
                        {
                            if (svnUI?.OperationProgressBar != null)
                                svnUI.OperationProgressBar.value = progress;
                        }
                        catch { }
                    });
                }

                token.ThrowIfCancellationRequested();
                double totalTime = Math.Max((DateTime.UtcNow - start).TotalSeconds, 0.01);
                SVNLogBridge.LogLine(
                    $"<color=green><b>[SUCCESS]</b> {filesAdded} file(s) across {total} top-level target(s) marked as 'Added' in {totalTime:F1}s.</color>");
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

        private async Task AddSingleItemAsync(string fullPath, string name)
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

                if (!TryGetRelativePath(root, fullPath, out string relativePath))
                {
                    SVNLogBridge.LogLine($"<color=#FF4444>Cannot add '{name}'. Path is outside the working copy or invalid.</color>");
                    return;
                }

                string fullPhysicalPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPhysicalPath) && !Directory.Exists(fullPhysicalPath)) return;

                SVNLogBridge.LogLine($"<b>[Add]</b> Adding item: {name}...");
                var paths = ReduceToTopLevelItems(new List<string> { relativePath });
                if (paths.Count == 0) return;

                tempFile = Path.Combine(Path.GetTempPath(), $"svn_add_single_{Guid.NewGuid():N}.txt");
                await Task.Run(() => File.WriteAllLines(tempFile, paths, new UTF8Encoding(false)), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                await SvnRunner.RunAsync(new[] { "add", "--force", "--parents", "--targets", tempFile }, root, false, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                SVNLogBridge.LogLine($"<color=green>Successfully added: {name}</color>");
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

        private async Task AddSelectedAsync(List<(string FullPath, string Name)> selectionSnapshot)
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

                if (selectionSnapshot == null || selectionSnapshot.Count == 0)
                {
                    SVNLogBridge.LogLine("<color=yellow>No unversioned items selected.</color>");
                    return;
                }

                // === FIX K1: pracujemy na SNAPSHOCIE stringów z main threadu —
                // zero enumeracji współdzielonej listy z puli wątków.
                var unversionedSelected = selectionSnapshot
                    .Select(s => { TryGetRelativePath(root, s.FullPath, out string rel); return rel; })
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

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

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

        // UWAGA (Ś3): O(n²) — akceptowalne, bo wejście to top-level '?' z svn status
        // (svn nie rozszerza drzew nieversioned) — lista jest mała.
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
                // === FIX Ś1: delayed dispose (spójny wzorzec) — natychmiastowy
                // dispose ratowało tylko catch ObjectDisposedException w Cancel().
                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    try { localCts.Dispose(); } catch { }
                });
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

            // === FIX: delayed dispose aktywnego CTS (cancel + zwolnienie po sekundzie).
            var localCts = Interlocked.Exchange(ref _activeCTS, null);
            if (localCts != null)
            {
                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    try { localCts.Dispose(); } catch { }
                });
            }

            try { _operationLock.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }
    }
}