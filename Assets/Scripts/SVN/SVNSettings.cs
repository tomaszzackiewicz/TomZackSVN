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

        // Ta metoda jest wywo�ywana, gdy r�cznie zmieniamy �cie�k� w ustawieniach
        public async void SaveWorkingDir()
        {
            if (IsProcessing) return;
            string newPath = svnUI.SettingsWorkingDirInput.text.Trim().Replace("\\", "/");

            if (Directory.Exists(newPath))
            {
                IsProcessing = true;
                try
                {
                    // 1. Sprawdzamy, czy ten folder jest ju� projektem, czy nowym
                    List<SVNProject> projects = ProjectSettings.LoadProjects();
                    var existing = projects.Find(p => p.workingDir == newPath);

                    if (existing == null)
                    {
                        // Je�li zmienili�my na folder, kt�rego nie ma na li�cie - dodaj go
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
                    await svnManager.RefreshStatus();

                    svnUI.LogText.text += $"<color=green>Success:</color> Switched to project at {newPath}\n";
                }
                catch (Exception ex) { svnUI.LogText.text += $"<color=red>Error:</color> {ex.Message}\n"; }
                finally { IsProcessing = false; }
            }
        }

        // Metoda wczytuj�ca ustawienia przy starcie aplikacji
        public async void LoadSettings()
        {
            if (svnManager == null) return;

            svnManager.IsProcessing = true;
            svnUI.LogText.text += "<b>[Settings]</b> Loading environment...\n";

            try
            {
                // 1. Pobierz ścieżki globalne (Merge Tool)
                string globalMergeTool = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
                svnManager.MergeToolPath = globalMergeTool;

                // 2. Pobierz ścieżkę ostatnio otwartego projektu
                string lastPath = PlayerPrefs.GetString("SVN_LastOpenedProjectPath", "");

                if (string.IsNullOrEmpty(lastPath))
                {
                    svnUI.LogText.text += "<color=orange>Note:</color> No last project found. Please load or checkout a repository.\n";
                    svnManager.IsProcessing = false;
                    return;
                }

                // 3. Znajdź dane konkretnego projektu w bazie JSON
                var projects = ProjectSettings.LoadProjects();
                var currentProj = projects.Find(p => p.workingDir == lastPath);

                if (currentProj != null)
                {
                    // Podstawowe dane projektu
                    svnManager.WorkingDir = currentProj.workingDir;
                    svnManager.RepositoryUrl = currentProj.repoUrl;

                    // --- LOGIKA KLUCZA SSH ---
                    // Najpierw sprawdź klucz przypisany do projektu
                    string keyToUse = currentProj.privateKeyPath;

                    // Jeśli projekt nie ma klucza, pobierz ostatnio używany klucz globalny
                    if (string.IsNullOrEmpty(keyToUse))
                    {
                        keyToUse = PlayerPrefs.GetString(SVNManager.KEY_SSH_PATH, "");
                        if (!string.IsNullOrEmpty(keyToUse))
                        {
                            svnUI.LogText.text += "<color=#888888>Project has no specific key. Using global SSH key.</color>\n";
                        }
                    }

                    // Aplikuj klucz do systemu
                    svnManager.CurrentKey = keyToUse;
                    SvnRunner.KeyPath = keyToUse;

                    // 4. Aktualizuj pola w UI (używając metody z hierarchią sprawdzania)
                    UpdateUIFromManager();

                    // 5. Inicjalizacja repozytorium w Managerze
                    if (Directory.Exists(currentProj.workingDir))
                    {
                        await svnManager.SetWorkingDirectory(currentProj.workingDir);
                        await svnManager.RefreshStatus();
                        svnUI.LogText.text += $"<color=green>Loaded:</color> {currentProj.projectName}\n";
                    }
                    else
                    {
                        svnUI.LogText.text += $"<color=red>Warning:</color> Working directory not found at {currentProj.workingDir}\n";
                    }
                }
                else
                {
                    svnUI.LogText.text += "<color=orange>Note:</color> Last project not found in Selection List. It might have been deleted.\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Load Error:</color> {ex.Message}\n";
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

            // 1. Obsługa Merge Tool (Globalny)
            string savedMergePath = PlayerPrefs.GetString(SVNManager.KEY_MERGE_TOOL, "");
            if (!string.IsNullOrEmpty(savedMergePath))
            {
                svnManager.MergeToolPath = savedMergePath;
                svnUI.SettingsMergeToolPathInput?.SetTextWithoutNotify(savedMergePath);
            }

            // 2. Obsługa Klucza SSH (Hierarchia: Manager -> Runner -> PlayerPrefs)
            string effectiveKey = svnManager.CurrentKey;

            if (string.IsNullOrEmpty(effectiveKey))
                effectiveKey = SvnRunner.KeyPath;

            if (string.IsNullOrEmpty(effectiveKey))
                effectiveKey = PlayerPrefs.GetString(SVNManager.KEY_SSH_PATH, "");

            // Jeśli znaleźliśmy jakikolwiek klucz, upewnij się, że Runner i UI o nim wiedzą
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

            // 3. Pozostałe pola (Working Dir i Repo URL)
            svnUI.SettingsWorkingDirInput?.SetTextWithoutNotify(svnManager.WorkingDir);
            svnUI.SettingsRepoUrlInput?.SetTextWithoutNotify(svnManager.RepositoryUrl);
        }
    }
}