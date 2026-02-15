using System;
using System.IO;
using UnityEngine;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNLoad : SVNBase
    {
        public SVNLoad(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void LoadRepoPathAndRefresh()
        {
            if (IsProcessing) return;

            string path = svnUI.LoadDestFolderInput.text.Trim();
            string manualUrl = svnUI.LoadRepoUrlInput != null ? svnUI.LoadRepoUrlInput.text.Trim() : "";

            string keyPath = "";
            if (svnUI.LoadPrivateKeyInput != null && !string.IsNullOrWhiteSpace(svnUI.LoadPrivateKeyInput.text))
            {
                keyPath = svnUI.LoadPrivateKeyInput.text.Trim();
            }
            else if (svnUI.SettingsSshKeyPathInput != null && !string.IsNullOrWhiteSpace(svnUI.SettingsSshKeyPathInput.text))
            {
                keyPath = svnUI.SettingsSshKeyPathInput.text.Trim();
            }
            else
            {
                keyPath = SvnRunner.KeyPath;
            }

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                SVNLogBridge.LogLine("<color=red>Error:</color> Invalid destination path!");
                return;
            }

            IsProcessing = true;
            SvnRunner.KeyPath = keyPath;

            SVNLogBridge.LogLine($"<b>Processing path:</b> <color=green>{path}</color>", append: false);

            try
            {
                string normalizedPath = path.Replace("\\", "/");
                svnManager.WorkingDir = normalizedPath;
                svnUI.LoadDestFolderInput.text = normalizedPath;

                if (!string.IsNullOrEmpty(keyPath))
                {
                    svnManager.CurrentKey = keyPath;
                }

                bool hasSvnFolder = Directory.Exists(Path.Combine(normalizedPath, ".svn"));

                if (hasSvnFolder)
                {
                    SVNLogBridge.LogLine("<b>Existing repository detected.</b> Linking files...");
                    await svnManager.RefreshRepositoryInfo();
                }
                else
                {
                    if (!string.IsNullOrEmpty(manualUrl))
                    {
                        bool isFolderEmpty = Directory.GetFileSystemEntries(normalizedPath).Length == 0;
                        string forceFlag = isFolderEmpty ? "" : " --force";

                        if (!isFolderEmpty)
                            SVNLogBridge.LogLine("<color=orange>Note:</color> Folder not empty. Merging with existing files...");

                        SVNLogBridge.LogLine("<color=yellow>Starting Checkout...</color>");

                        await SvnRunner.RunAsync($"checkout \"{manualUrl}\" .{forceFlag}", normalizedPath);

                        SVNLogBridge.LogLine("<color=green>Checkout completed!</color>");
                        await svnManager.RefreshRepositoryInfo();
                    }
                    else
                    {
                        SVNLogBridge.LogLine("<color=red>Error:</color> Path is not a repository and no URL provided!");
                        IsProcessing = false;
                        return;
                    }
                }

                if (string.IsNullOrEmpty(svnManager.RepositoryUrl) && !string.IsNullOrEmpty(manualUrl))
                    svnManager.RepositoryUrl = manualUrl;

                if (svnUI.LoadRepoUrlInput != null)
                    svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl;

                RegisterProjectInList(normalizedPath, keyPath, svnManager.RepositoryUrl);

                var selectionPanel = UnityEngine.Object.FindAnyObjectByType<ProjectSelectionPanel>();
                if (selectionPanel != null) selectionPanel.RefreshList();

                await svnManager.RefreshStatus();

                SVNLogBridge.LogLine("<color=green>SUCCESS:</color> System synchronized.");

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(500);
                    svnManager.PanelHandler.Button_CloseLoad();
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Operation Failed:</color> {ex.Message}");
                Debug.LogError($"[SVN] Load Error: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void RegisterProjectInList(string path, string key, string url)
        {
            var newProj = new SVNProject
            {
                projectName = System.IO.Path.GetFileName(path.TrimEnd('/')),
                repoUrl = url,
                workingDir = path,
                privateKeyPath = key,
                lastOpened = DateTime.Now
            };

            var projects = ProjectSettings.LoadProjects();
            int existingIndex = projects.FindIndex(p => p.workingDir == path);

            if (existingIndex != -1)
            {
                projects[existingIndex].repoUrl = url;
                projects[existingIndex].privateKeyPath = key;
                projects[existingIndex].lastOpened = DateTime.Now;
                SVNLogBridge.LogLine("<color=#888888>Existing project entry updated in list.</color>");
            }
            else
            {
                projects.Add(newProj);
                SVNLogBridge.LogLine($"<color=green>New project '{newProj.projectName}' added to Selection List.</color>");
            }

            ProjectSettings.SaveProjects(projects);

            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", path);
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