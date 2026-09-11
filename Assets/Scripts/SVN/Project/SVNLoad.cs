using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLoad : SVNBase, IDisposable
    {
        private int _isBusy = 0;
        private int _disposed = 0;
        private CancellationTokenSource _loadCts;
        private ProjectSelectionPanel _cachedSelectionPanel;

        public SVNLoad(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void LoadRepoPathAndRefresh()
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            if (Interlocked.CompareExchange(ref _isBusy, 1, 0) == 1)
            {
                SVNLogBridge.LogLine("<color=#8B0000>>Another operation is running. Please wait.</color>");
                return;
            }

            string path = svnUI.LoadDestFolderInput != null
                ? svnUI.LoadDestFolderInput.text.Trim()
                : string.Empty;

            string manualUrl = svnUI.LoadRepoUrlInput != null
                ? svnUI.LoadRepoUrlInput.text.Trim()
                : string.Empty;

            string keyPath = string.Empty;
            if (svnUI.LoadPrivateKeyInput != null && !string.IsNullOrWhiteSpace(svnUI.LoadPrivateKeyInput.text))
                keyPath = svnUI.LoadPrivateKeyInput.text.Trim();
            else if (svnUI.SettingsSshKeyPathInput != null && !string.IsNullOrWhiteSpace(svnUI.SettingsSshKeyPathInput.text))
                keyPath = svnUI.SettingsSshKeyPathInput.text.Trim();
            else
                keyPath = SvnRunner.KeyPath ?? string.Empty;

            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
            }

            var token = newCts.Token;

            _ = LoadRepoPathAndRefreshAsync(path, manualUrl, keyPath, token).ContinueWith(t =>
            {
                Interlocked.Exchange(ref _isBusy, 0);

                var currentCts = Interlocked.CompareExchange(ref _loadCts, null, newCts);
                if (ReferenceEquals(currentCts, newCts))
                {
                    _ = Task.Delay(1000).ContinueWith(_ => { try { newCts.Dispose(); } catch { } });
                }

                if (t.IsFaulted)
                {
                    var baseEx = t.Exception?.GetBaseException();
                    if (baseEx is not OperationCanceledException)
                    {
                        UnityMainThreadDispatcher.Enqueue(() =>
                            SVNLogBridge.LogError($"[SVNLoad] Operation failed: {baseEx?.Message ?? "Unknown"}"));
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private async Task LoadRepoPathAndRefreshAsync(string path, string manualUrl, string keyPath, CancellationToken token)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid destination path!");
                return;
            }

            if (!string.IsNullOrEmpty(manualUrl) && !IsValidSvnUrl(manualUrl))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid SVN URL. Expected svn://, svn+ssh://, http:// or https://.");
                return;
            }

            SVNLogBridge.LogLine($"<b>Processing path:</b> <color=green>{path}</color>", append: false);

            try
            {
                token.ThrowIfCancellationRequested();

                string normalizedPath = path.Replace("\\", "/").TrimEnd('/');

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (svnUI.LoadDestFolderInput != null)
                        svnUI.LoadDestFolderInput.text = normalizedPath;
                });

                bool hasSvnFolder = Directory.Exists(Path.Combine(normalizedPath, ".svn"));

                if (!hasSvnFolder && string.IsNullOrEmpty(manualUrl))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Path is not a repository and no URL provided!");
                    return;
                }

                if (!hasSvnFolder)
                {
                    bool isFolderEmpty = false;
                    try
                    {
                        isFolderEmpty = !Directory.EnumerateFileSystemEntries(normalizedPath).Any();
                    }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogLine($"<color=#8B0000>>Warning:</color> Cannot read directory: {ex.Message}");
                        isFolderEmpty = false;
                    }

                    string forceFlag = isFolderEmpty ? "" : " --force";
                    if (!isFolderEmpty)
                        SVNLogBridge.LogLine("<color=#8B0000>>Note:</color> Folder not empty. Merging with existing files...");

                    SVNLogBridge.LogLine("<color=yellow>Starting Checkout...</color>");
                    await SvnRunner.RunAsync($"checkout \"{manualUrl}\" .{forceFlag}", normalizedPath, token: token).ConfigureAwait(false);
                    SVNLogBridge.LogLine("<color=green>Checkout completed!</color>");
                }
                else if (string.IsNullOrEmpty(manualUrl))
                {
                    await svnManager.RefreshRepositoryInfo().ConfigureAwait(false);
                }
                else
                {
                    SVNLogBridge.LogLine(
                        $"<color=#88CCFF>[SVNLoad]</color> Using user-provided URL (ignoring working-copy metadata): {manualUrl}");
                }

                token.ThrowIfCancellationRequested();

                // User-typed URL wygrywa z .svn.
                string urlForProject = !string.IsNullOrEmpty(manualUrl)
                    ? manualUrl
                    : (svnManager.RepositoryUrl ?? string.Empty);

                string urlSnapshot = urlForProject;
                string keySnapshot = keyPath;

                // Auto-nazwa: "UrlName (folder)" — rozróżnia wiele checkoutów tego samego repo.
                string displayName = SVNProjectNaming.BuildDisplayName(urlForProject, normalizedPath);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = Path.GetFileName(normalizedPath);

                var project = new SVNProject
                {
                    projectName = displayName,
                    repoUrl = urlForProject,
                    workingDir = normalizedPath,
                    privateKeyPath = keySnapshot
                };

                bool loaded = await svnManager.LoadProject(project).ConfigureAwait(false);
                if (!loaded)
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Project could not be loaded (working copy invalid).");
                    return;
                }

                svnManager.WorkingDir = normalizedPath;
                svnManager.CurrentKey = keyPath;
                SvnRunner.KeyPath = keyPath;

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (svnUI?.TreeDisplay != null)
                        SVNLogBridge.UpdateUIField(svnUI.TreeDisplay, "<i>Loading changes...</i>", "TREE", append: false);
                });

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    RegisterProjectInList(normalizedPath, keySnapshot, urlSnapshot);

                    if (_cachedSelectionPanel == null)
                    {
                        _cachedSelectionPanel = UnityEngine.Object.FindAnyObjectByType<ProjectSelectionPanel>(
                            FindObjectsInactive.Exclude);
                    }

                    _cachedSelectionPanel?.RefreshList();

                    if (svnUI.LoadRepoUrlInput != null)
                        svnUI.LoadRepoUrlInput.text = urlSnapshot;
                });

                SVNLogBridge.LogLine("<color=green>SUCCESS:</color> System synchronized.");

                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    await svnManager.RefreshStatus(force: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception refreshEx)
                {
                    SVNLogBridge.LogError($"[SVNLoad] Post-load refresh failed: {refreshEx.Message}");
                }

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                    UnityMainThreadDispatcher.Enqueue(() => svnManager.PanelHandler?.Button_CloseLoad());
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=#8B0000>>[SVNLoad] Operation canceled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color=#FFAA00>Operation Failed:</color> {ex.Message}");
                SVNLogBridge.LogErrorToOutput($"[SVN] Load Error: {ex}");
            }
        }

        private void RegisterProjectInList(string path, string key, string url)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            ProjectSettings.AddOrUpdateProject(path, (p, created) =>
            {
                // Auto-nazwa TYLKO dla nowego wpisu — ręczny Rename ma priorytet.
                if (created)
                {
                    string auto = SVNProjectNaming.BuildDisplayName(url, path);
                    if (!string.IsNullOrWhiteSpace(auto))
                        p.projectName = auto;
                }

                p.repoUrl = url;
                p.privateKeyPath = key;
                p.lastOpened = DateTime.UtcNow;
            });

            string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
            PlayerPrefs.Save();
        }

        public void UpdateUIFromManager()
        {
            if (svnManager == null) return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI.LoadRepoUrlInput != null)
                    svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl ?? string.Empty;

                if (svnUI.LoadDestFolderInput != null)
                    svnUI.LoadDestFolderInput.text = svnManager.WorkingDir ?? string.Empty;

                if (svnUI.LoadPrivateKeyInput != null)
                    svnUI.LoadPrivateKeyInput.text = svnManager.CurrentKey ?? string.Empty;
            });
        }

        public void ClearSceneReferences()
        {
            _cachedSelectionPanel = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            var cts = Interlocked.Exchange(ref _loadCts, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }
        }
    }
}