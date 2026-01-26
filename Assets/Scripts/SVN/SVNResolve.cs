using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNResolve : SVNBase
    {
        public SVNResolve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void Button_OpenInEditor()
        {
            if (IsProcessing) return;

            // 1. Pobranie œcie¿ki do edytora z Twojego InputFielda w Settings
            string editorPath = svnUI.SettingsMergeToolPathInput.text;

            if (string.IsNullOrEmpty(editorPath) || !File.Exists(editorPath))
            {
                svnUI.LogText.text += "<color=red>B³¹d:</color> Podaj poprawn¹ œcie¿kê do edytora (.exe) w Settings!\n";
                return;
            }

            // 2. Ustalenie folderu roboczego
            if (string.IsNullOrEmpty(svnManager.WorkingDir) || !Directory.Exists(svnManager.WorkingDir))
            {
                svnUI.LogText.text += "<color=red>B³¹d:</color> Nieprawid³owy folder roboczy (Working Directory)!\n";
                return;
            }

            try
            {
                // 3. Pobieramy aktualny status (zwraca krotki status i rozmiar)
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(svnManager.WorkingDir, false);

                // POPRAWKA: Sprawdzamy pole .status w krotce Value
                // U¿ywamy Any() przed FirstOrDefault(), aby bezpieczniej obs³u¿yæ brak wyników
                var conflictEntry = statusDict.FirstOrDefault(x =>
                    !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"));

                if (!string.IsNullOrEmpty(conflictEntry.Key))
                {
                    // Budujemy pe³n¹ œcie¿kê do pliku
                    string fullFilePath = Path.Combine(svnManager.WorkingDir, conflictEntry.Key);

                    svnUI.LogText.text += $"Otwieranie edytora dla: <color=cyan>{conflictEntry.Key}</color>...\n";

                    // 4. Uruchomienie zewnêtrznego procesu
                    System.Diagnostics.Process.Start(editorPath, $"\"{fullFilePath}\"");

                    svnUI.LogText.text += "<color=yellow>Instrukcja:</color> Napraw konflikt w edytorze, zapisz plik i kliknij <b>Mark as Resolved</b>.\n";
                }
                else
                {
                    svnUI.LogText.text += "<color=yellow>Nie znaleziono plików w stanie konfliktu (C).</color>\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>B³¹d otwierania edytora:</color> {ex.Message}\n";
            }
        }

        public async void Button_MarkAsResolved()
        {
            if (IsProcessing) return;

            // 1. Pobieramy œcie¿kê bezpoœrednio z Managera
            string root = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] MarkAsResolved: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] MarkAsResolved: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;

            try
            {
                if (svnUI.LogText != null)
                    svnUI.LogText.text = "Checking for conflicts to mark as resolved...\n";

                // 2. Pobieramy aktualne statusy
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 3. Szukamy plików ze statusem 'C' (Conflict)
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key)
                    .ToArray();

                if (conflictedPaths.Length > 0)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"Marking {conflictedPaths.Length} items as resolved...\n";

                    // 4. Budujemy argumenty komendy 'svn resolved'
                    // U¿ywamy cudzys³owów dla ka¿dej œcie¿ki, aby obs³u¿yæ spacje
                    string pathsJoined = "\"" + string.Join("\" \"", conflictedPaths) + "\"";
                    string args = $"resolved {pathsJoined}";

                    // 5. Wywo³ujemy SvnRunner
                    await SvnRunner.RunAsync(args, root);

                    if (svnUI.LogText != null)
                    {
                        svnUI.LogText.text += "<color=green><b>Success!</b></color> Conflicts marked as resolved.\n";
                        svnUI.LogText.text += "Local metadata cleaned. You can now commit.\n";
                    }

                    // 6. Odœwie¿amy widok (ukryje to panel konfliktów i odœwie¿y drzewo)
                    svnManager.RefreshStatus();
                }
                else
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += "<color=yellow>No conflicts found to mark as resolved.</color>\n";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] MarkAsResolved Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"<color=red>Resolved Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        // --- ROZWI¥ZYWANIE KONFLIKTÓW ---
        // Przyjmuje wersjê z serwera (nadpisuje Twoje zmiany)
        public async void Button_ResolveTheirs()
        {
            if (IsProcessing) return;

            // 1. Pobieramy œcie¿kê bezpoœrednio z managera (eliminacja b³êdu Input)
            string root = svnManager.WorkingDir;

            // Podstawowe sprawdzenie œcie¿ki i referencji UI
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] ResolveTheirs: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] ResolveTheirs: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;

            if (svnUI.LogText != null)
                svnUI.LogText.text = "Searching for conflicts to resolve using <color=orange>THEIRS</color>...\n";

            try
            {
                // 2. Pobieramy statusy z repozytorium
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 3. Filtrujemy pliki w stanie konfliktu (status 'C')
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key)
                    .ToArray();

                if (conflictedPaths.Length > 0)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"Resolving {conflictedPaths.Length} items using <color=orange>theirs-full</color>...\n";

                    // 4. Wywo³anie komendy SVN Resolve ze strategi¹ 'theirs-full' (false)
                    await SvnRunner.ResolveAsync(root, conflictedPaths, false);

                    if (svnUI.LogText != null)
                    {
                        svnUI.LogText.text += "<color=green><b>Conflicts Resolved!</b></color>\n";
                        svnUI.LogText.text += "<color=yellow>Remember:</color> You MUST <b>Commit</b>i te pliki teraz, aby zakoñczyæ proces.\n";
                    }

                    // 5. Automatyczne odœwie¿enie drzewa plików
                    svnManager.RefreshStatus();
                }
                else
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += "<color=yellow>No conflicts found to resolve.</color>\n";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Resolve Theirs Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"<color=red>Resolve Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async void Button_ResolveMine()
        {
            if (IsProcessing) return;

            // 1. Pobieramy œcie¿kê bezpoœrednio z managera (eliminacja b³êdu Input)
            string root = svnManager.WorkingDir;

            // Podstawowe sprawdzenie œcie¿ki i UI
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] ResolveMine: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] ResolveMine: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;

            if (svnUI.LogText != null)
                svnUI.LogText.text = "Searching for conflicts to resolve using <color=cyan>MINE</color>...\n";

            try
            {
                // 2. Pobieramy statusy
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 3. Szukamy plików ze statusem 'C' (Conflict)
                var conflictedPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("C"))
                    .Select(x => x.Key)
                    .ToArray();

                if (conflictedPaths.Length > 0)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"Resolving {conflictedPaths.Length} conflicts using strategy: <color=cyan>mine-full</color>...\n";

                    // 4. Wywo³anie SvnRunner.ResolveAsync (mine-full)
                    await SvnRunner.ResolveAsync(root, conflictedPaths, true);

                    if (svnUI.LogText != null)
                    {
                        svnUI.LogText.text += $"<color=green><b>Success!</b></color> Resolved {conflictedPaths.Length} conflicts.\n";
                        svnUI.LogText.text += "<color=#AAAAAA>Your local changes preserved.</color>\n";
                    }

                    // 5. Odœwie¿amy widok w managerze
                    svnManager.RefreshStatus();
                }
                else
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += "<color=yellow>No conflicts found to resolve.</color>\n";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Resolve Mine Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"<color=red>Resolve Error (Mine):</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

}