using System;
using System.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNMissing : SVNBase
    {
        public SVNMissing(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async void Button_FixMissingFiles()
        {
            if (IsProcessing) return;

            IsProcessing = true;
            //loadingOverlay?.SetActive(true);
            svnUI.LogText.text = "Szukanie brakuj¹cych plików (status !)...\n";

            try
            {
                string root = svnManager.RepositoryUrl;

                // 1. Pobieramy aktualny status wszystkich plików
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 2. Wybieramy tylko te, które maj¹ status '!' (missing)
                // POPRAWKA: Dodano .status do x.Value
                var missingFiles = statusDict
                    .Where(x => x.Value.status == "!")
                    .Select(x => x.Key)
                    .ToArray();

                if (missingFiles.Length > 0)
                {
                    svnUI.LogText.text += $"Znaleziono {missingFiles.Length} brakuj¹cych plików. Usuwanie z bazy SVN...\n";

                    // 3. Wywo³ujemy svn delete na tych œcie¿kach
                    string output = await SvnRunner.DeleteAsync(root, missingFiles);

                    svnUI.LogText.text += $"<color=green>Naprawiono:</color> {output}\n";

                    // 4. Odœwie¿amy drzewo
                    svnManager.Button_RefreshStatus();
                }
                else
                {
                    svnUI.LogText.text += "Nie znaleziono brakuj¹cych plików do naprawy.\n";
                }
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>B³¹d FixMissing:</color> {ex.Message}\n";
            }
            finally
            {
                //loadingOverlay?.SetActive(false);
                IsProcessing = false;
            }
        }

        public async void Button_DeleteMissing()
        {
            if (IsProcessing) return;

            // 1. Pobieramy œcie¿kê bezpoœrednio z managera
            string root = svnManager.WorkingDir;

            // Podstawowe sprawdzenie œcie¿ki i referencji UI
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[SVN] DeleteMissing: Working directory is empty.");
                return;
            }

            if (svnUI == null)
            {
                Debug.LogError("[SVN] DeleteMissing: svnUI reference is missing!");
                return;
            }

            IsProcessing = true;

            if (svnUI.LogText != null)
                svnUI.LogText.text = "Szukanie plików usuniêtych fizycznie (status !)...\n";

            try
            {
                // 2. Pobieramy status wszystkich plików w folderze root
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);

                // 3. Filtrujemy pliki, które maj¹ status '!' (missing)
                // Oznacza to, ¿e plik nie istnieje na dysku, ale jest w bazie SVN
                var missingPaths = statusDict
                    .Where(x => !string.IsNullOrEmpty(x.Value.status) && x.Value.status.Contains("!"))
                    .Select(x => x.Key)
                    .ToArray();

                if (missingPaths.Length > 0)
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"Usuwanie {missingPaths.Length} brakuj¹cych elementów z bazy SVN...\n";

                    // 4. Wywo³anie SvnRunner.DeleteAsync
                    // Komenda 'svn delete' na brakuj¹cych plikach usuwa je z kontroli wersji
                    string output = await SvnRunner.DeleteAsync(root, missingPaths);

                    if (svnUI.LogText != null)
                        svnUI.LogText.text += $"<color=green>Sukces!</color> Usuniêto meta-dane dla {missingPaths.Length} plików.\n";

                    // 5. Odœwie¿amy drzewo plików
                    svnManager.Button_RefreshStatus();
                }
                else
                {
                    if (svnUI.LogText != null)
                        svnUI.LogText.text += "Nie znaleziono brakuj¹cych plików (status !). Wszystko jest w porz¹dku.\n";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Delete Missing Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text += $"<color=red>B³¹d Delete:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }
}
