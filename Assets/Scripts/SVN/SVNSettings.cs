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

        // Metoda pomocnicza: Aktualizuje dane projektu w pliku JSON
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

            // 1. Update JSON
            UpdateProjectInJson(svnManager.WorkingDir, p => p.repoUrl = newUrl);

            // 2. Global Sync
            PlayerPrefs.SetString(SVNManager.KEY_REPO_URL, newUrl);
            PlayerPrefs.Save();
            svnManager.RepositoryUrl = newUrl;

            svnUI.LogText.text += $"<color=green>Saved:</color> Repository URL updated to: {newUrl}\n";
        }

        public void SaveSSHKeyPath()
        {
            string path = svnUI.SettingsSshKeyPathInput.text.Trim();

            // 1. Update JSON
            UpdateProjectInJson(svnManager.WorkingDir, p => p.privateKeyPath = path);

            // 2. Global Sync
            PlayerPrefs.SetString(SVNManager.KEY_SSH_PATH, path);
            PlayerPrefs.Save();
            svnManager.CurrentKey = path;
            SvnRunner.KeyPath = path;

            svnUI.LogText.text += $"<color=green>Saved:</color> SSH Key path updated.\n";
        }

        public void SaveMergeEditorPath()
        {
            if (svnUI.SettingsMergeToolPathInput == null) return;
            string newPath = svnUI.SettingsMergeToolPathInput.text.Trim();

            if (string.IsNullOrEmpty(newPath)) return;

            // 1. Global Sync (Merge tool jest zazwyczaj globalny dla aplikacji, nie projektu)
            PlayerPrefs.SetString(SVNManager.KEY_MERGE_TOOL, newPath);
            PlayerPrefs.Save();
            svnManager.MergeToolPath = newPath;

            svnUI.LogText.text += $"<color=green>Saved:</color> Merge Tool updated.\n";
        }

        // Ta metoda jest wywo³ywana, gdy rêcznie zmieniamy œcie¿kê w ustawieniach
        public async void SaveWorkingDir()
        {
            if (IsProcessing) return;
            string newPath = svnUI.SettingsWorkingDirInput.text.Trim().Replace("\\", "/");

            if (Directory.Exists(newPath))
            {
                IsProcessing = true;
                try
                {
                    // 1. Sprawdzamy, czy ten folder jest ju¿ projektem, czy nowym
                    List<SVNProject> projects = ProjectSettings.LoadProjects();
                    var existing = projects.Find(p => p.workingDir == newPath);

                    if (existing == null)
                    {
                        // Jeœli zmieniliœmy na folder, którego nie ma na liœcie - dodaj go
                        var newProj = new SVNProject
                        {
                            projectName = Path.GetFileName(newPath),
                            workingDir = newPath,
                            lastOpened = DateTime.Now
                        };
                        projects.Add(newProj);
                        ProjectSettings.SaveProjects(projects);
                    }

                    // 2. Global Sync
                    PlayerPrefs.SetString("SVN_LastOpenedProjectPath", newPath);
                    PlayerPrefs.Save();

                    svnManager.WorkingDir = newPath;
                    await svnManager.RefreshRepositoryInfo();
                    svnManager.UpdateBranchInfo();
                    svnManager.RefreshStatus();

                    svnUI.LogText.text += $"<color=green>Success:</color> Switched to project at {newPath}\n";
                }
                catch (Exception ex) { svnUI.LogText.text += $"<color=red>Error:</color> {ex.Message}\n"; }
                finally { IsProcessing = false; }
            }
        }

        // Metoda wczytuj¹ca ustawienia przy starcie aplikacji
        public async void LoadSettings()
        {
            svnManager.IsProcessing = true;

            // 1. Pobierz œcie¿kê ostatniego projektu
            string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");
            string globalMergeTool = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
            
            svnManager.MergeToolPath = globalMergeTool;

            // 2. ZnajdŸ dane projektu w JSON
            var projects = ProjectSettings.LoadProjects();
            var currentProj = projects.Find(p => p.workingDir == lastPath);

            if (currentProj != null)
            {
                svnManager.WorkingDir = currentProj.workingDir;
                svnManager.RepositoryUrl = currentProj.repoUrl;
                svnManager.CurrentKey = currentProj.privateKeyPath;
                SvnRunner.KeyPath = currentProj.privateKeyPath;

                // Aktualizuj UI
                UpdateUIFromManager();

                // Odœwie¿ SVN
                if (Directory.Exists(currentProj.workingDir))
                {
                    await svnManager.SetWorkingDirectory(currentProj.workingDir);
                    svnManager.RefreshStatus();
                }
            }

            svnManager.IsProcessing = false;
        }

        public void UpdateUIFromManager()
        {
            if (svnUI == null) return;

            // POBIERZ ZAPIS (Ostateczne zabezpieczenie)
            string savedPath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");

            if (!string.IsNullOrEmpty(savedPath))
            {
                svnManager.MergeToolPath = savedPath; // Wymuœ wartoœæ w Managerze

                if (svnUI.SettingsMergeToolPathInput != null)
                {
                    // Wpisz do UI, ale NIE wywo³uj ¿adnych eventów
                    svnUI.SettingsMergeToolPathInput.SetTextWithoutNotify(savedPath);
                }
            }

            // Pozosta³e pola
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(svnManager.WorkingDir);
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(svnManager.RepositoryUrl);
            svnUI.SettingsSshKeyPathInput?.SetTextWithoutNotify(svnManager.CurrentKey);
        }
    }
}