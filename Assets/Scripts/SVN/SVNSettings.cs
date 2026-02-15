using System;
using System.IO;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;

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
                ProjectSettings.SaveProjects(projects);
                Debug.Log($"[Settings] Updated JSON for project: {project.projectName}");
            }
        }

        public void SaveRepoUrl()
        {
            if (svnUI.SettingsRepoUrlInput == null) return;
            string newUrl = svnUI.SettingsRepoUrlInput.text.Trim();

            UpdateProjectInJson(svnManager.WorkingDir, p => p.repoUrl = newUrl);

            PlayerPrefs.SetString(SVNManager.KEY_REPO_URL, newUrl);
            PlayerPrefs.Save();
            svnManager.RepositoryUrl = newUrl;

            SVNLogBridge.LogLine($"<color=green>Saved:</color> Repository URL updated to: {newUrl}");
        }

        public void SaveSSHKeyPath()
        {
            string path = svnUI.SettingsSshKeyPathInput.text.Trim();

            UpdateProjectInJson(svnManager.WorkingDir, p => p.privateKeyPath = path);

            PlayerPrefs.SetString(SVNManager.KEY_SSH_PATH, path);
            PlayerPrefs.Save();
            svnManager.CurrentKey = path;
            SvnRunner.KeyPath = path;

            SVNLogBridge.LogLine("<color=green>Saved:</color> SSH Key path updated.");
        }

        public void SaveMergeEditorPath()
        {
            if (svnUI.SettingsMergeToolPathInput == null) return;
            string newPath = svnUI.SettingsMergeToolPathInput.text.Trim();

            if (string.IsNullOrEmpty(newPath)) return;

            PlayerPrefs.SetString(SVNManager.KEY_MERGE_TOOL, newPath);
            PlayerPrefs.Save();
            svnManager.MergeToolPath = newPath;

            SVNLogBridge.LogLine("<color=green>Saved:</color> Merge Tool updated.");
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
                    await svnManager.RefreshStatus();

                    SVNLogBridge.LogLine($"<color=green>Success:</color> Switched to project at {newPath}");
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=red>Error:</color> {ex.Message}");
                }
                finally { IsProcessing = false; }
            }
        }

        public async void LoadSettings()
        {
            if (svnManager == null) return;

            svnManager.IsProcessing = true;
            SVNLogBridge.LogLine("<b>[Settings]</b> Loading environment...", append: false);

            try
            {
                string globalMergeTool = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                svnManager.MergeToolPath = globalMergeTool;

                string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");

                if (string.IsNullOrEmpty(lastPath))
                {
                    SVNLogBridge.LogLine("<color=orange>Note:</color> No last project found. Please load or checkout a repository.");
                    svnManager.IsProcessing = false;
                    return;
                }

                var projects = ProjectSettings.LoadProjects();
                var currentProj = projects.Find(p => p.workingDir == lastPath);

                if (currentProj != null)
                {
                    svnManager.WorkingDir = currentProj.workingDir;
                    svnManager.RepositoryUrl = currentProj.repoUrl;

                    string keyToUse = currentProj.privateKeyPath;

                    if (string.IsNullOrEmpty(keyToUse))
                    {
                        keyToUse = PlayerPrefs.GetString(SVNManager.KEY_SSH_PATH, "");
                        if (!string.IsNullOrEmpty(keyToUse))
                        {
                            SVNLogBridge.LogLine("<color=#888888>Project has no specific key. Using global SSH key.</color>");
                        }
                    }

                    svnManager.CurrentKey = keyToUse;
                    SvnRunner.KeyPath = keyToUse;

                    UpdateUIFromManager();

                    if (Directory.Exists(currentProj.workingDir))
                    {
                        await svnManager.SetWorkingDirectory(currentProj.workingDir);
                        await svnManager.RefreshStatus();
                        SVNLogBridge.LogLine($"<color=green>Loaded:</color> {currentProj.projectName}");
                    }
                    else
                    {
                        SVNLogBridge.LogLine($"<color=red>Warning:</color> Working directory not found at {currentProj.workingDir}");
                    }
                }
                else
                {
                    SVNLogBridge.LogLine("<color=orange>Note:</color> Last project not found in Selection List. It might have been deleted.");
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Load Error:</color> {ex.Message}");
                Debug.LogError($"[SVNSettings] LoadSettings failed: {ex}");
            }
            finally
            {
                svnManager.IsProcessing = false;
            }
        }

        public void UpdateUIFromManager()
        {
            if (svnUI == null) return;

            string savedMergePath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
            if (!string.IsNullOrEmpty(savedMergePath))
            {
                svnManager.MergeToolPath = savedMergePath;
                svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(savedMergePath);
            }

            string effectiveKey = svnManager.CurrentKey;

            if (string.IsNullOrEmpty(effectiveKey))
                effectiveKey = SvnRunner.KeyPath;

            if (string.IsNullOrEmpty(effectiveKey))
                effectiveKey = PlayerPrefs.GetString(SVNManager.KEY_SSH_PATH, "");

            if (!string.IsNullOrEmpty(effectiveKey))
            {
                svnManager.CurrentKey = effectiveKey;
                SvnRunner.KeyPath = effectiveKey;
                svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(effectiveKey);
            }
            else
            {
                svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify("");
            }

            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(svnManager.WorkingDir);
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(svnManager.RepositoryUrl);
        }
    }
}