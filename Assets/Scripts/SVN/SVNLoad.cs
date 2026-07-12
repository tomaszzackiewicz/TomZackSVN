using System;
using System.IO;
using UnityEngine;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNLoad : SVNBase
    {
        private bool _isBusy = false;

        public SVNLoad(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void LoadRepoPathAndRefresh()
        {
            if (_isBusy || svnManager.IsProcessing)
            {
                SVNLogBridge.LogLine("<color=orange>Another operation is running. Please wait.</color>");
                return;
            }

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
                SVNLogBridge.LogLine("<color=red>Error:</color> Invalid destination path!");
                return;
            }

            _isBusy = true;
            svnManager.CurrentKey = keyPath;
            SvnRunner.KeyPath = keyPath;

            SVNLogBridge.LogLine($"<b>Processing path:</b> <color=green>{path}</color>", append: false);

            try
            {
                string normalizedPath = path.Replace("\\", "/");
                svnManager.WorkingDir = normalizedPath;
                svnUI.LoadDestFolderInput.text = normalizedPath;

                bool hasSvnFolder = Directory.Exists(Path.Combine(normalizedPath, ".svn"));

                if (!hasSvnFolder && string.IsNullOrEmpty(manualUrl))
                {
                    SVNLogBridge.LogLine("<color=red>Error:</color> Path is not a repository and no URL provided!");
                    return;
                }

                if (!hasSvnFolder)
                {
                    bool isFolderEmpty = Directory.GetFileSystemEntries(normalizedPath).Length == 0;
                    string forceFlag = isFolderEmpty ? "" : " --force";
                    if (!isFolderEmpty)
                        SVNLogBridge.LogLine("<color=orange>Note:</color> Folder not empty. Merging with existing files...");

                    SVNLogBridge.LogLine("<color=yellow>Starting Checkout...</color>");
                    await SvnRunner.RunAsync($"checkout \"{manualUrl}\" .{forceFlag}", normalizedPath);
                    SVNLogBridge.LogLine("<color=green>Checkout completed!</color>");
                }
                else
                {
                    await svnManager.RefreshRepositoryInfo();
                }

                if (string.IsNullOrEmpty(svnManager.RepositoryUrl) && !string.IsNullOrEmpty(manualUrl))
                    svnManager.RepositoryUrl = manualUrl;

                if (svnUI.LoadRepoUrlInput != null)
                    svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl;

                RegisterProjectInList(normalizedPath, keyPath, svnManager.RepositoryUrl);

                var selectionPanel = UnityEngine.Object.FindAnyObjectByType<ProjectSelectionPanel>();
                selectionPanel?.RefreshList();

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
                    await Task.Delay(300);
                    svnManager.PanelHandler.Button_CloseLoad();
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Operation Failed:</color> {ex.Message}");
                SVNLogBridge.LogError($"[SVN] Load Error: {ex}");
            }
            finally
            {
                _isBusy = false;
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