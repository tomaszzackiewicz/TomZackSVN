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
            if (Interlocked.CompareExchange(ref _isBusy, 1, 0) == 1)
            {
                SVNLogBridge.LogLine("<color=orange>Another operation is running. Please wait.</color>");
                return;
            }

            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _loadCts, newCts);

            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                try { oldCts.Dispose(); } catch { }
            }

            var token = newCts.Token;

            _ = LoadRepoPathAndRefreshAsync(token).ContinueWith(t =>
            {
                Interlocked.Exchange(ref _isBusy, 0);

                var currentCts = Interlocked.CompareExchange(ref _loadCts, null, newCts);
                if (ReferenceEquals(currentCts, newCts))
                {
                    try { newCts.Dispose(); } catch { }
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

        private async Task LoadRepoPathAndRefreshAsync(CancellationToken token)
        {
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

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid destination path!");
                return;
            }

            if (!string.IsNullOrEmpty(manualUrl) && !Uri.TryCreate(manualUrl, UriKind.Absolute, out _))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid repository URL!");
                return;
            }

            SVNLogBridge.LogLine($"<b>Processing path:</b> <color=green>{path}</color>", append: false);

            try
            {
                token.ThrowIfCancellationRequested();

                string normalizedPath = path.Replace("\\", "/");

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
                        SVNLogBridge.LogLine($"<color=orange>Warning:</color> Cannot read directory: {ex.Message}");
                        isFolderEmpty = false;
                    }

                    string forceFlag = isFolderEmpty ? "" : " --force";
                    if (!isFolderEmpty)
                        SVNLogBridge.LogLine("<color=orange>Note:</color> Folder not empty. Merging with existing files...");

                    SVNLogBridge.LogLine("<color=yellow>Starting Checkout...</color>");
                    await SvnRunner.RunAsync($"checkout \"{manualUrl}\" .{forceFlag}", normalizedPath, token: token).ConfigureAwait(false);
                    SVNLogBridge.LogLine("<color=green>Checkout completed!</color>");
                }
                else
                {
                    await svnManager.RefreshRepositoryInfo().ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(svnManager.RepositoryUrl) && !string.IsNullOrEmpty(manualUrl))
                    svnManager.RepositoryUrl = manualUrl;

                string urlSnapshot = svnManager.RepositoryUrl ?? string.Empty;
                string keySnapshot = keyPath;

                var project = new SVNProject
                {
                    projectName = Path.GetFileName(normalizedPath),
                    repoUrl = urlSnapshot,
                    workingDir = normalizedPath,
                    privateKeyPath = keySnapshot
                };

                await svnManager.LoadProject(project).ConfigureAwait(false);

                svnManager.WorkingDir = normalizedPath;
                svnManager.CurrentKey = keyPath;
                SvnRunner.KeyPath = keyPath;

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

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                    UnityMainThreadDispatcher.Enqueue(() => svnManager.PanelHandler?.Button_CloseLoad());
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[SVNLoad] Operation canceled.</color>");
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

            string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
            var projects = ProjectSettings.LoadProjects();

            int index = projects.FindIndex(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                p.workingDir.Replace("\\", "/").TrimEnd('/') == normalizedPath);

            if (index != -1)
            {
                projects[index].repoUrl = url ?? string.Empty;
                projects[index].privateKeyPath = key ?? string.Empty;
                projects[index].lastOpened = DateTime.UtcNow;
            }
            else
            {
                projects.Add(new SVNProject
                {
                    projectName = Path.GetFileName(normalizedPath),
                    repoUrl = url ?? string.Empty,
                    workingDir = normalizedPath,
                    privateKeyPath = key ?? string.Empty,
                    lastOpened = DateTime.UtcNow
                });
            }

            ProjectSettings.SaveProjects(projects);
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
            PlayerPrefs.Save();
        }

        public void UpdateUIFromManager()
        {
            if (svnManager == null) return;

            if (svnUI.LoadRepoUrlInput != null)
                svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl ?? string.Empty;

            if (svnUI.LoadDestFolderInput != null)
                svnUI.LoadDestFolderInput.text = svnManager.WorkingDir ?? string.Empty;

            if (svnUI.LoadPrivateKeyInput != null)
                svnUI.LoadPrivateKeyInput.text = svnManager.CurrentKey ?? string.Empty;
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
                try { cts.Dispose(); } catch { }
            }
        }
    }
}