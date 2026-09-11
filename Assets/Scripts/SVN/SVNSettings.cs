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

        public void SafeFireAndForget(Func<Task> operation)
        {
            _ = FireAndForget(operation);
        }

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>Settings error:</color> {ex.Message}"); }
        }

        public void SaveRepoUrl() => SafeFireAndForget(SaveRepoUrlAsync);
        public void SaveSSHKeyPath() => SafeFireAndForget(SaveSSHKeyPathAsync);
        public void SaveMergeEditorPath() => SafeFireAndForget(SaveMergeEditorPathAsync);
        public void SaveWorkingDir() => SafeFireAndForget(SaveWorkingDirAsync);
        public void LoadSettings() => SafeFireAndForget(LoadSettingsAsync);
        public void SaveDiffToolPath() => SafeFireAndForget(SaveDiffToolPathAsync);
        public void SaveResolveToolPath() => SafeFireAndForget(SaveResolveToolPathAsync);
        public void SaveBlameToolPath() => SafeFireAndForget(SaveBlameToolPathAsync);

        public void UpdateUIFromManager()
        {
            if (svnUI == null || svnManager == null) return;

            svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(svnManager.MergeToolPath ?? "");
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(svnManager.CurrentKey ?? "");
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(svnManager.WorkingDir ?? "");
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(svnManager.RepositoryUrl ?? "");

            svnUI.SettingsDiffToolPathInput?.SetTextWithoutNotify(svnManager.DiffToolPath ?? "");
            svnUI.SettingsResolveToolPathInput?.SetTextWithoutNotify(svnManager.ResolveToolPath ?? "");
            svnUI.SettingsBlameToolPathInput?.SetTextWithoutNotify(svnManager.BlameToolPath ?? "");
        }

        private async Task SaveRepoUrlAsync()
        {
            string newUrl = svnUI?.SettingsRepoUrlInput?.text?.Trim() ?? "";
            if (string.IsNullOrEmpty(newUrl)) return;

            await UpdateProjectInJsonAsync(svnManager?.WorkingDir, p => p.repoUrl = newUrl).ConfigureAwait(false);
            SVNPrefs.SetString(SVNManager.KEY_REPO_URL, newUrl);

            if (svnManager != null)
            {
                // === FIX: przez centrum — unieważnia cache filtrów RepoBrowsera
                // i synchronizuje wszystkie pola UI (Load/Checkout/Settings).
                UnityMainThreadDispatcher.Enqueue(() =>
                    svnManager.SynchronizeConnectionSettings(SVNManager.SettingsSource.None, url: newUrl));
            }

            SVNLogBridge.LogLine($"Saved repo url = '{newUrl}'");
        }

        private async Task SaveSSHKeyPathAsync()
        {
            string path = svnUI?.SettingsSshKeyPathInput?.text?.Trim() ?? "";

            await UpdateProjectInJsonAsync(svnManager?.WorkingDir, p => p.privateKeyPath = path).ConfigureAwait(false);
            SVNPrefs.SetString(SVNManager.KEY_SSH_PATH, path);

            if (svnManager != null)
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                    svnManager.SynchronizeConnectionSettings(SVNManager.SettingsSource.None, key: path));
            }

            SVNLogBridge.LogLine($"Saved ssh key = '{path}'");
        }

        private async Task SaveMergeEditorPathAsync()
        {
            string newPath = svnUI?.SettingsMergeToolPathInput?.text?.Trim() ?? "";

            await UpdateProjectInJsonAsync(svnManager?.WorkingDir, p => p.mergeToolPath = newPath).ConfigureAwait(false);
            SVNPrefs.SetString(SVNManager.KEY_TEXTEDITOR_TOOL, newPath);

            if (svnManager != null)
                svnManager.MergeToolPath = newPath;

            SVNLogBridge.LogLine($"Saved merge tool = '{newPath}'");
        }

        private async Task SaveDiffToolPathAsync()
        {
            string newPath = svnUI?.SettingsDiffToolPathInput?.text?.Trim() ?? "";

            await UpdateProjectInJsonAsync(svnManager?.WorkingDir, p => p.diffToolPath = newPath).ConfigureAwait(false);
            SVNPrefs.SetString(SVNManager.KEY_DIFF_TOOL, newPath);

            if (svnManager != null)
                svnManager.DiffToolPath = newPath;

            SVNLogBridge.LogLine($"Saved diff tool = '{newPath}'");
        }

        private async Task SaveResolveToolPathAsync()
        {
            string newPath = svnUI?.SettingsResolveToolPathInput?.text?.Trim() ?? "";

            await UpdateProjectInJsonAsync(svnManager?.WorkingDir, p => p.resolveToolPath = newPath).ConfigureAwait(false);
            SVNPrefs.SetString(SVNManager.KEY_RESOLVE_TOOL, newPath);

            if (svnManager != null)
                svnManager.ResolveToolPath = newPath;

            SVNLogBridge.LogLine($"Saved resolve tool = '{newPath}'");
        }

        private async Task SaveBlameToolPathAsync()
        {
            string newPath = svnUI?.SettingsBlameToolPathInput?.text?.Trim() ?? "";

            await UpdateProjectInJsonAsync(svnManager?.WorkingDir, p => p.blameToolPath = newPath).ConfigureAwait(false);
            SVNPrefs.SetString(SVNManager.KEY_BLAME_TOOL, newPath);

            if (svnManager != null)
                svnManager.BlameToolPath = newPath;

            SVNLogBridge.LogLine($"Saved blame tool = '{newPath}'");
        }

        private async Task SaveWorkingDirAsync()
        {
            if (!TryEnterProcessing()) return;

            try
            {
                string newPath = svnUI?.SettingsWorkingDirInput?.text?.Trim().Replace("\\", "/") ?? "";
                if (string.IsNullOrWhiteSpace(newPath))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Path is empty.");
                    return;
                }

                try { newPath = Path.GetFullPath(newPath).Replace("\\", "/"); }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> Invalid path: {ex.Message}");
                    return;
                }

                if (!Directory.Exists(newPath))
                {
                    SVNLogBridge.LogLine($"<color=#FFAA00>Error:</color> Directory does not exist: {newPath}");
                    return;
                }

                if (!Directory.Exists(Path.Combine(newPath, ".svn")))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Not a valid SVN working copy.");
                    return;
                }

                string normalizedPath = newPath.TrimEnd('/');

                // === FIX (spójność nazewnictwa): nie generujemy tu nazwy na sztywno
                // z folderu — URL poznamy dopiero po LoadProject (jest w .svn).
                // Tworzymy wpis z fallbackiem; po udanym LoadProject zaktualizujemy
                // nazwę do "UrlName (folder)" — tym samym formatem co SVNLoad.
                SVNProject project = ProjectSettings.AddOrUpdateProject(normalizedPath, (p, created) =>
                {
                    if (created && string.IsNullOrWhiteSpace(p.projectName))
                        p.projectName = Path.GetFileName(newPath);   // tymczasowy fallback
                    p.lastOpened = DateTime.UtcNow;
                });

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                bool loaded = await svnManager.LoadProject(project).ConfigureAwait(false);
                if (!loaded)
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Project could not be loaded (working copy invalid).");
                    return;
                }

                // === FIX (spójność nazewnictwa): po LoadProject svnManager.RepositoryUrl
                // zawiera URL z .svn. Ustawiamy auto-nazwę w tym samym formacie, co
                // SVNLoad.RegisterProjectInList. Tylko gdy user nie zrobił ręcznego Rename
                // (tzn. nazwa nadal == fallback z folderu).
                try
                {
                    string finalUrl = svnManager.RepositoryUrl ?? "";
                    string folderFallback = Path.GetFileName(newPath);

                    ProjectSettings.UpdateProject(normalizedPath, p =>
                    {
                        bool untouchedByUser =
                            string.Equals(p.projectName, folderFallback, StringComparison.Ordinal) ||
                            string.IsNullOrWhiteSpace(p.projectName);

                        if (untouchedByUser)
                        {
                            string auto = SVNProjectNaming.BuildDisplayName(finalUrl, normalizedPath);
                            if (!string.IsNullOrWhiteSpace(auto))
                                p.projectName = auto;
                        }
                    });
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=yellow>[Settings]</color> Auto-name update skipped: {ex.Message}");
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

            List<SVNProject> projects = await Task.Run(() => ProjectSettings.LoadProjects()).ConfigureAwait(false);
            string normalizedLast = NormalizePath(lastPath);

            var current = projects.Find(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                string.Equals(NormalizePath(p.workingDir), normalizedLast, StringComparison.OrdinalIgnoreCase));

            if (current != null)
            {
                bool loaded = await svnManager.LoadProject(current).ConfigureAwait(false);
                if (!loaded)
                    SVNLogBridge.LogLine($"<color=#FFAA00>[Settings]</color> Last project '{current.projectName}' has no valid working copy — not loaded.");
            }
        }

        private static Task UpdateProjectInJsonAsync(string workingDir, Action<SVNProject> updateAction)
        {
            if (string.IsNullOrEmpty(workingDir) || updateAction == null) return Task.CompletedTask;

            return Task.Run(() =>
            {
                try
                {
                    if (!ProjectSettings.UpdateProject(workingDir, updateAction))
                        SVNLogBridge.LogLine("<color=yellow>[Settings] Project not found in list — change kept for this session only.</color>");
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=#FFAA00>Settings save failed:</color> {ex.Message}");
                }
            });
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace("\\", "/").TrimEnd('/');
        }
    }
}