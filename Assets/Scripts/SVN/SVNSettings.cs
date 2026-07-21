using System;
using System.IO;
using UnityEngine;
using System.Collections.Generic;

namespace SVN.Core
{
    public class SVNSettings : SVNBase
    {
        public SVNSettings(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void UpdateProjectInJson(string workingDir, Action<SVNProject> updateAction)
        {
            if (string.IsNullOrEmpty(workingDir)) return;

            List<SVNProject> projects = ProjectSettings.LoadProjects();
            var project = projects.Find(p => p.workingDir == workingDir);

            if (project != null)
            {
                updateAction(project);

                project.repoUrl ??= "";
                project.privateKeyPath ??= "";
                project.mergeToolPath ??= "";

                ProjectSettings.SaveProjects(projects);
            }
        }

        public void SaveRepoUrl()
        {
            string newUrl = svnUI.SettingsRepoUrlInput.text.Trim();

            UpdateProjectInJson(svnManager.WorkingDir, p => p.repoUrl = newUrl);

            PlayerPrefs.SetString(SVNManager.KEY_REPO_URL, newUrl);
            PlayerPrefs.Save();

            svnManager.RepositoryUrl = newUrl;

            SVNLogBridge.LogLine($"Saved repo url = '{newUrl}'");
        }

        public void SaveSSHKeyPath()
        {
            string path = svnUI.SettingsSshKeyPathInput.text.Trim();

            UpdateProjectInJson(svnManager.WorkingDir, p => p.privateKeyPath = path);

            PlayerPrefs.SetString(SVNManager.KEY_SSH_PATH, path);
            PlayerPrefs.Save();

            svnManager.CurrentKey = path;
            SvnRunner.KeyPath = path;

            SVNLogBridge.LogLine($"Saved ssh key = '{path}'");
        }

        public void SaveMergeEditorPath()
        {
            string newPath = svnUI.SettingsMergeToolPathInput.text.Trim();

            UpdateProjectInJson(svnManager.WorkingDir, p => p.mergeToolPath = newPath);

            PlayerPrefs.SetString(SVNManager.KEY_MERGE_TOOL, newPath);
            PlayerPrefs.Save();

            svnManager.MergeToolPath = newPath;

            SVNLogBridge.LogLine($"Saved merge tool = '{newPath}'");
        }

        public async void SaveWorkingDir()
        {
            if (IsProcessing) return;
            string newPath = svnUI.SettingsWorkingDirInput.text.Trim().Replace("\\", "/");

            if (Directory.Exists(newPath))
            {
                IsProcessing = true;
                try
                {
                    List<SVNProject> projects = ProjectSettings.LoadProjects();
                    var existing = projects.Find(p => p.workingDir == newPath);

                    if (existing == null)
                    {
                        var newProj = new SVNProject
                        {
                            projectName = Path.GetFileName(newPath),
                            workingDir = newPath,
                            lastOpened = DateTime.Now
                        };
                        projects.Add(newProj);
                        ProjectSettings.SaveProjects(projects);
                    }

                    PlayerPrefs.SetString("SVN_LastOpenedProjectPath", newPath);
                    PlayerPrefs.Save();

                    svnManager.WorkingDir = newPath;
                    await svnManager.RefreshRepositoryInfo();
                    await svnManager.CancelBackgroundTasksAsync();
                    await svnManager.RefreshStatus();

                    SVNLogBridge.LogLine($"<color=green>Success:</color> Switched to project at {newPath}");
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> {ex.Message}");
                }
                finally { IsProcessing = false; }
            }
        }

        public void LoadSettings()
        {
            if (svnManager == null) return;

            string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
            if (string.IsNullOrEmpty(lastPath)) return;

            var projects = ProjectSettings.LoadProjects();
            var current = projects.Find(p => p.workingDir == lastPath);
            if (current != null)
                _ = svnManager.LoadProject(current);
        }

        public async void LoadSettingAsync()
        {
            if (svnManager == null) return;

            string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
            var projects = ProjectSettings.LoadProjects();
            var current = projects.Find(p => p.workingDir == lastPath);

            if (current == null) return;

            await svnManager.LoadProject(current);
        }

        public void UpdateUIFromManager()
        {
            if (svnUI == null || svnManager == null) return;

            string merge = svnManager.MergeToolPath ?? "";
            svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(merge);

            string key = svnManager.CurrentKey ?? "";
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(key);

            string wd = svnManager.WorkingDir ?? "";
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(wd);

            string url = svnManager.RepositoryUrl ?? "";
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(url);
        }
    }
}