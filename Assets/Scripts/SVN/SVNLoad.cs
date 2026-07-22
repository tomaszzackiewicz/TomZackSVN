using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLoad : SVNBase
    {
        // OPT: Thread-safe flaga zamiast bool
        private int _isBusy = 0;
        private CancellationTokenSource _loadCts;

        // OPT: Cache panelu zamiast FindAnyObjectByType za każdym razem
        private ProjectSelectionPanel _cachedSelectionPanel;

        public SVNLoad(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void LoadRepoPathAndRefresh()
        {
            if (Interlocked.CompareExchange(ref _isBusy, 1, 0) == 1)
            {
                SVNLogBridge.LogLine("<color=orange>Another operation is running. Please wait.</color>");
                return;
            }

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();

            _ = LoadRepoPathAndRefreshAsync(_loadCts.Token).ContinueWith(t =>
            {
                Interlocked.Exchange(ref _isBusy, 0);
                if (t.IsFaulted)
                    SVNLogBridge.LogError($"[SVNLoad] Operation failed: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }

        private async Task LoadRepoPathAndRefreshAsync(CancellationToken token)
        {
            string path = svnUI.LoadDestFolderInput.text.Trim();
            string manualUrl = svnUI.LoadRepoUrlInput != null ? svnUI.LoadRepoUrlInput.text.Trim() : "";

            string keyPath = "";
            if (svnUI.LoadPrivateKeyInput != null && !string.IsNullOrWhiteSpace(svnUI.LoadPrivateKeyInput.text))
                keyPath = svnUI.LoadPrivateKeyInput.text.Trim();
            else if (svnUI.SettingsSshKeyPathInput != null && !string.IsNullOrWhiteSpace(svnUI.SettingsSshKeyPathInput.text))
                keyPath = svnUI.SettingsSshKeyPathInput.text.Trim();
            else
                keyPath = SvnRunner.KeyPath;

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid destination path!");
                return;
            }

            svnManager.CurrentKey = keyPath;
            SvnRunner.KeyPath = keyPath;

            SVNLogBridge.LogLine($"<b>Processing path:</b> <color=green>{path}</color>", append: false);

            try
            {
                token.ThrowIfCancellationRequested();
                string normalizedPath = path.Replace("\\", "/");
                svnManager.WorkingDir = normalizedPath;
                svnUI.LoadDestFolderInput.text = normalizedPath;

                bool hasSvnFolder = Directory.Exists(Path.Combine(normalizedPath, ".svn"));

                if (!hasSvnFolder && string.IsNullOrEmpty(manualUrl))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Path is not a repository and no URL provided!");
                    return;
                }

                if (!hasSvnFolder)
                {
                    // OPT: Enumerate zamiast GetFileSystemEntries (nie alokuje tablicy)
                    bool isFolderEmpty = !Directory.EnumerateFileSystemEntries(normalizedPath).Any();
                    string forceFlag = isFolderEmpty ? "" : " --force";
                    if (!isFolderEmpty)
                        SVNLogBridge.LogLine("<color=orange>Note:</color> Folder not empty. Merging with existing files...");

                    SVNLogBridge.LogLine("<color=yellow>Starting Checkout...</color>");
                    await SvnRunner.RunAsync($"checkout \"{manualUrl}\" .{forceFlag}", normalizedPath, token: token);
                    SVNLogBridge.LogLine("<color=green>Checkout completed!</color>");
                }
                else
                {
                    await svnManager.RefreshRepositoryInfo();
                }

                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(svnManager.RepositoryUrl) && !string.IsNullOrEmpty(manualUrl))
                    svnManager.RepositoryUrl = manualUrl;

                if (svnUI.LoadRepoUrlInput != null)
                    svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl;

                RegisterProjectInList(normalizedPath, keyPath, svnManager.RepositoryUrl);

                // OPT: Cache panelu
                if (_cachedSelectionPanel == null)
                    _cachedSelectionPanel = UnityEngine.Object.FindAnyObjectByType<ProjectSelectionPanel>();
                _cachedSelectionPanel?.RefreshList();

                var project = new SVNProject
                {
                    projectName = Path.GetFileName(normalizedPath),
                    repoUrl = svnManager.RepositoryUrl,
                    workingDir = normalizedPath,
                    privateKeyPath = keyPath
                };

                await svnManager.LoadProject(project);

                SVNLogBridge.LogLine("<color=green>SUCCESS:</color> System synchronized.");

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(300, token);
                    svnManager.PanelHandler.Button_CloseLoad();
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[SVNLoad] Operation canceled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Operation Failed:</color> {ex.Message}");
                SVNLogBridge.LogError($"[SVN] Load Error: {ex}");
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
                projects[index].lastOpened = DateTime.Now;
            }
            else
            {
                projects.Add(new SVNProject
                {
                    projectName = Path.GetFileName(normalizedPath),
                    repoUrl = url ?? string.Empty,
                    workingDir = normalizedPath,
                    privateKeyPath = key ?? string.Empty,
                    lastOpened = DateTime.Now
                });
            }

            ProjectSettings.SaveProjects(projects);
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
            PlayerPrefs.Save();
        }

        public void UpdateUIFromManager()
        {
            if (svnUI.LoadRepoUrlInput != null) svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl;
            if (svnUI.LoadDestFolderInput != null) svnUI.LoadDestFolderInput.text = svnManager.WorkingDir;
            if (svnUI.LoadPrivateKeyInput != null) svnUI.LoadPrivateKeyInput.text = svnManager.CurrentKey;
        }
    }
}