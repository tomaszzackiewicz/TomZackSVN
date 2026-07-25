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
    public class SVNAdd : SVNBase
    {
        private CancellationTokenSource _activeCTS;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private const int CleanupTimeoutSeconds = 30;

        private readonly SynchronizationContext _mainThreadContext;
        private readonly int _mainThreadId;

        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        private bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private void RunOnMainThread(Action action)
        {
            if (IsMainThread) { try { action(); } catch (Exception ex) { Debug.LogException(ex); } }
            else if (_mainThreadContext != null)
            {
                _mainThreadContext.Post(_ => { try { action(); } catch (Exception ex) { Debug.LogException(ex); } }, null);
            }
        }

        public async void AddAll() { try { await AddAllAsync(); } catch (Exception ex) { Debug.LogException(ex); } }

        public void AddSingleItem(SvnTreeElement element)
        {
            if (element == null || IsProcessing) return;
            _ = AddSingleItemAsync(element);
        }

        public void Cancel()
        {
            var cts = Interlocked.Exchange(ref _activeCTS, null);
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            IsProcessing = false;

            try { _operationLock.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }

            HideProgressBarAfterDelay(0);
        }

        private async Task AddAllAsync()
        {
            bool hasLock = false;
            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock || IsProcessing) return;

                IsProcessing = true;
                var token = ResetAndGetToken();
                ShowProgressBar();

                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root))
                {
                    SVNLogBridge.LogError("Working directory is null or empty.");
                    return;
                }

                if (!await CleanupWorkingCopyAsync(root, token))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Warning: Cleanup timed out. Proceeding anyway...</color>");
                }

                SVNLogBridge.LogLine("<b>[Add]</b> Scanning for unversioned items...");

                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);

                if (string.IsNullOrWhiteSpace(rawStatus) || !rawStatus.Contains("?"))
                {
                    SVNLogBridge.LogLine("<color=yellow>Nothing to add. All items are already tracked or ignored.</color>");
                    return;
                }

                var unversioned = new List<string>();
                using (var reader = new StringReader(rawStatus))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("?"))
                        {
                            string rawPath = line.Substring(8).TrimStart();
                            string cleanPath = SvnRunner.NormalizeRepositoryPath(rawPath).Replace('\\', '/');
                            if (!string.IsNullOrWhiteSpace(cleanPath)) unversioned.Add(cleanPath);
                        }
                    }
                }

                SVNLogBridge.LogLine($"Found {unversioned.Count} unversioned item(s). Preparing safe add...");

                string normalizedRoot = root.Replace("\\", "/").TrimEnd('/');
                var itemsToAdd = EnsureParentDirectoriesAreIncluded(unversioned, normalizedRoot);

                string tempFile = Path.Combine(Path.GetTempPath(), $"svn_add_all_{Guid.NewGuid():N}.txt");

                try
                {
                    File.WriteAllLines(tempFile, itemsToAdd, new UTF8Encoding(false));
                    await SvnRunner.RunAsync($"add --force --parents --targets \"{tempFile}\"", root, false, token);
                }
                finally { SafeDelete(tempFile); }

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
            catch (OperationCanceledException) { SVNLogBridge.LogLine("<color=orange>Operation cancelled by user.</color>"); }
            catch (Exception ex) { SVNLogBridge.LogError($"\n<color=#FFAA00>Error during AddAll: {ex.Message}</color>"); }
            finally { FinalizeOperation(hasLock); }
        }

        private async Task AddSingleItemAsync(SvnTreeElement element)
        {
            bool hasLock = false;
            try
            {
                hasLock = await _operationLock.WaitAsync(0);
                if (!hasLock || IsProcessing) return;

                IsProcessing = true;
                var token = ResetAndGetToken();
                ShowProgressBar();

                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root)) return;

                if (!await CleanupWorkingCopyAsync(root, token))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Warning: Cleanup timed out. Proceeding anyway...</color>");
                }

                SVNLogBridge.LogLine($"<b>[Add]</b> Adding item: {element.Name}...");

                string normalizedRoot = root.Replace("\\", "/").TrimEnd('/');
                var paths = EnsureParentDirectoriesAreIncluded(new List<string> { element.FullPath }, normalizedRoot);

                string tempFile = Path.Combine(Path.GetTempPath(), $"svn_add_single_{Guid.NewGuid():N}.txt");

                try
                {
                    File.WriteAllLines(tempFile, paths, new UTF8Encoding(false));
                    await SvnRunner.RunAsync($"add --force --parents --targets \"{tempFile}\"", root, false, token);
                }
                finally { SafeDelete(tempFile); }

                SVNLogBridge.LogLine($"<color=green>Successfully added:</color> {element.Name}");
                SVNLogBridge.LogLine("<color=#4FC3F7>Rebuilding tree...</color>");

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null) await statusModule.RefreshModifiedInternal();
            }
            catch (OperationCanceledException) { SVNLogBridge.LogLine("<color=orange>Operation cancelled by user.</color>"); }
            catch (Exception ex) { SVNLogBridge.LogError($"<color=#FFAA00>Add Error: {ex.Message}</color>"); }
            finally { FinalizeOperation(hasLock); }
        }

        private async Task<bool> CleanupWorkingCopyAsync(string root, CancellationToken token)
        {
            SVNLogBridge.LogLine("Checking working copy locks...");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            try
            {
                await SvnRunner.RunAsync("cleanup", root, false, linkedCts.Token);
                SVNLogBridge.LogLine("<color=green>Working copy is clean.</color>");
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                return false;
            }
        }

        private List<string> EnsureParentDirectoriesAreIncluded(List<string> paths, string root)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in paths)
            {
                string current = rawPath?.Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(current))
                    continue;

                result.Add(current);

                string parent = Path.GetDirectoryName(current)?.Replace('\\', '/').Trim('/');
                while (!string.IsNullOrEmpty(parent))
                {
                    result.Add(parent);
                    parent = Path.GetDirectoryName(parent)?.Replace('\\', '/').Trim('/');
                }
            }

            return result.OrderBy(p => p.Count(c => c == '/'))
                          .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        }

        private CancellationToken ResetAndGetToken()
        {
            var oldCts = Interlocked.Exchange(ref _activeCTS, null);
            try { oldCts?.Cancel(); oldCts?.Dispose(); } catch { }
            var newCts = new CancellationTokenSource();
            _activeCTS = newCts;
            return newCts.Token;
        }

        private void FinalizeOperation(bool hasLock)
        {
            _activeCTS?.Dispose();
            _activeCTS = null;

            if (hasLock)
            {
                IsProcessing = false;
                try { _operationLock.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { }
            }
            HideProgressBarAfterDelay(1.5f);
        }

        private void ShowProgressBar()
        {
            RunOnMainThread(() =>
            {
                if (svnUI?.OperationProgressBar != null)
                {
                    svnUI.OperationProgressBar.gameObject.SetActive(true);
                    svnUI.OperationProgressBar.value = 0.1f;
                }
            });
        }

        private void HideProgressBarAfterDelay(float seconds)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay((int)(seconds * 1000)); } catch { }
                RunOnMainThread(() =>
                {
                    if (svnUI?.OperationProgressBar != null && !IsProcessing)
                    {
                        svnUI.OperationProgressBar.gameObject.SetActive(false);
                        svnUI.OperationProgressBar.value = 0f;
                    }
                });
            });
        }

        private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}