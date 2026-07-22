using System;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNAdd : SVNBase
    {
        private CancellationTokenSource _activeCTS;

        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async Task AddAll()
        {
            if (IsProcessing) return;

            CancellationToken token = PrepareNewOperation();

            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root))
                {
                    SVNLogBridge.LogError("Working directory is null or empty.");
                    return;
                }

                SVNLogBridge.LogLine("<b>[Recursive Add]</b> Scanning for unversioned items...");

                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);

                if (string.IsNullOrWhiteSpace(rawStatus) || !rawStatus.Contains("?"))
                {
                    SVNLogBridge.LogLine("<color=yellow>Nothing to add. All items are already tracked or ignored.</color>");
                    return;
                }

                SVNLogBridge.LogLine("Adding files to SVN (Local operation)...");

                await SvnRunner.RunAsync("add * --force --parents --depth infinity", root, true, token);

                SVNLogBridge.LogLine("<color=#4FC3F7>Rebuilding tree...</color>");

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearSVNTreeView();
                    statusModule.ClearCurrentData();
                    await statusModule.RefreshModifiedInternal();
                }

                SVNLogBridge.LogLine("\n<color=green><b>[SUCCESS]</b> Items marked as 'Added'.</color>");
                SVNLogBridge.LogLine("<color=white>Note: You still need to <b>Commit</b> to upload them to the server.</color>");
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>Operation cancelled by user.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"\n<color=#FFAA00>Error during AddAll: {ex.Message}</color>");
            }
            finally
            {
                CleanUpOperation(token);
            }
        }

        public void AddSingleItem(SvnTreeElement element)
        {
            if (IsProcessing || element == null) return;

            _ = AddSingleItemAsync(element).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[SVNAdd] AddSingleItem failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        private async Task AddSingleItemAsync(SvnTreeElement element)
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            CancellationToken token = PrepareNewOperation();
            SVNLogBridge.LogLine($"<b>[Add]</b> Adding item: {element.Name}...");

            try
            {
                await SvnRunner.RunAsync($"add \"{element.FullPath}\"", root, true, token);

                SVNLogBridge.LogLine($"<color=green>Successfully added:</color> {element.Name}");

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    SVNLogBridge.LogLine("<color=#4FC3F7>Rebuilding tree...</color>");
                    await statusModule.RefreshModifiedInternal();
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>Operation cancelled by user.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"<color=#FFAA00>Add Error: {ex.Message}</color>");
            }
            finally
            {
                CleanUpOperation(token);
            }
        }

        private CancellationToken PrepareNewOperation()
        {
            var oldCts = Interlocked.Exchange(ref _activeCTS, new CancellationTokenSource());
            oldCts?.Cancel();
            try { oldCts?.Dispose(); } catch { }

            IsProcessing = true;

            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.1f;
            }

            return _activeCTS.Token;
        }

        // POPRAWKA: Przechwytujemy _activeCTS do zmiennej lokalnej PRZED uruchomieniem ContinueWith.
        // Bez tego, jeśli w międzyczasie nowa operacja podmieni _activeCTS, Dispose() zabije nowy token.
        private void CleanUpOperation(CancellationToken token)
        {
            var capturedCts = _activeCTS;
            if (capturedCts != null && capturedCts.Token == token)
            {
                IsProcessing = false;

                _ = HideProgressAsync(1.5f, capturedCts.Token).ContinueWith(t =>
                {
                    try { capturedCts?.Dispose(); } catch { }
                }, TaskScheduler.Default);
            }
        }

        private async Task HideProgressAsync(float delay, CancellationToken token)
        {
            try
            {
                await Task.Delay((int)(delay * 1000), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;

            if (!IsProcessing && svnUI?.OperationProgressBar != null)
                svnUI.OperationProgressBar.gameObject.SetActive(false);
        }
    }
}