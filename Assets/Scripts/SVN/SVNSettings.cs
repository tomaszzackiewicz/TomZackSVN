using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNSettings : SVNBase
    {
        private int _processingFlag;

        public SVNSettings(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private bool TryEnterProcessing()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        private void SafeFireAndForget(Func<Task> operation)
        {
            _ = Task.Run(async () =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>Settings error:</color> {ex.Message}"); }
            });
        }

        public void SaveRepoUrl()
        {
            string newUrl = svnUI?.SettingsRepoUrlInput?.text?.Trim() ?? "";
            if (string.IsNullOrEmpty(newUrl)) return;

            UpdateProjectInJson(svnManager?.WorkingDir, p => p.repoUrl = newUrl);

            PlayerPrefs.SetString(SVNManager.KEY_REPO_URL, newUrl);
            PlayerPrefs.Save();

            if (svnManager != null)
                svnManager.RepositoryUrl = newUrl;

            SVNLogBridge.LogLine($"Saved repo url = '{newUrl}'");
        }

        public void SaveSSHKeyPath()
        {
            string path = svnUI?.SettingsSshKeyPathInput?.text?.Trim() ?? "";

            UpdateProjectInJson(svnManager?.WorkingDir, p => p.privateKeyPath = path);

            PlayerPrefs.SetString(SVNManager.KEY_SSH_PATH, path);
            PlayerPrefs.Save();

            if (svnManager != null)
            {
                svnManager.CurrentKey = path;
                SvnRunner.KeyPath = path;
            }

            SVNLogBridge.LogLine($"Saved ssh key = '{path}'");
        }

        public void SaveMergeEditorPath()
        {
            string newPath = svnUI?.SettingsMergeToolPathInput?.text?.Trim() ?? "";

            UpdateProjectInJson(svnManager?.WorkingDir, p => p.mergeToolPath = newPath);

            PlayerPrefs.SetString(SVNManager.KEY_MERGE_TOOL, newPath);
            PlayerPrefs.Save();

            if (svnManager != null)
                svnManager.MergeToolPath = newPath;

            SVNLogBridge.LogLine($"Saved merge tool = '{newPath}'");
        }

        public void SaveWorkingDir()
        {
            SafeFireAndForget(SaveWorkingDirAsync);
        }

        public void LoadSettings()
        {
            SafeFireAndForget(LoadSettingsAsync);
        }

        public void LoadSettingAsync()
        {
            SafeFireAndForget(LoadSettingsAsync);
        }

        public void UpdateUIFromManager()
        {
            if (svnUI == null || svnManager == null) return;

            svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(svnManager.MergeToolPath ?? "");
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(svnManager.CurrentKey ?? "");
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(svnManager.WorkingDir ?? "");
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(svnManager.RepositoryUrl ?? "");
        }

        private async Task SaveWorkingDirAsync()
        {
            if (!TryEnterProcessing()) return;

            string newPath = svnUI?.SettingsWorkingDirInput?.text?.Trim().Replace("\\", "/") ?? "";
            if (string.IsNullOrWhiteSpace(newPath))
            {
                ExitProcessing();
                return;
            }

            try
            {
                newPath = Path.GetFullPath(newPath).Replace("\\", "/");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> Invalid path: {ex.Message}");
                ExitProcessing();
                return;
            }

            if (!Directory.Exists(newPath))
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> Directory does not exist: {newPath}");
                ExitProcessing();
                return;
            }

            if (!Directory.Exists(Path.Combine(newPath, ".svn")))
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> Not a valid SVN working copy: {newPath}");
                ExitProcessing();
                return;
            }

            try
            {
                List<SVNProject> projects = ProjectSettings.LoadProjects();
                string normalizedPath = newPath.TrimEnd('/');

                var existing = projects.Find(p =>
                    !string.IsNullOrEmpty(p.workingDir) &&
                    NormalizePath(p.workingDir) == normalizedPath);

                if (existing == null)
                {
                    projects.Add(new SVNProject
                    {
                        projectName = Path.GetFileName(newPath),
                        workingDir = normalizedPath,
                        lastOpened = DateTime.Now
                    });
                    ProjectSettings.SaveProjects(projects);
                }

                PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
                PlayerPrefs.Save();

                if (svnManager != null)
                {
                    svnManager.WorkingDir = normalizedPath;
                    await svnManager.RefreshRepositoryInfo().ConfigureAwait(false);
                    await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                    await svnManager.RefreshStatus().ConfigureAwait(false);
                }

                SVNLogBridge.LogLine($"<color=green>Success:</color> Switched to project at {normalizedPath}");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        private async Task LoadSettingsAsync()
        {
            if (svnManager == null) return;

            string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
            if (string.IsNullOrEmpty(lastPath)) return;

            var projects = ProjectSettings.LoadProjects();
            string normalizedLast = NormalizePath(lastPath);

            var current = projects.Find(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                NormalizePath(p.workingDir) == normalizedLast);

            if (current != null)
                await svnManager.LoadProject(current).ConfigureAwait(false);
        }

        private void UpdateProjectInJson(string workingDir, Action<SVNProject> updateAction)
        {
            if (string.IsNullOrEmpty(workingDir) || updateAction == null) return;

            try
            {
                List<SVNProject> projects = ProjectSettings.LoadProjects();
                string normalizedWd = NormalizePath(workingDir);

                var project = projects.Find(p =>
                    !string.IsNullOrEmpty(p.workingDir) &&
                    NormalizePath(p.workingDir) == normalizedWd);

                if (project != null)
                {
                    updateAction(project);

                    project.repoUrl ??= "";
                    project.privateKeyPath ??= "";
                    project.mergeToolPath ??= "";

                    ProjectSettings.SaveProjects(projects);
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Settings save failed:</color> {ex.Message}");
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace("\\", "/").TrimEnd('/');
        }
    }
}