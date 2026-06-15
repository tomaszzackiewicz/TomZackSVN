using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SVNShelve : SVNBase
    {
        private readonly string _shelfFolder;

        public SVNShelve(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _shelfFolder = Path.Combine(Application.persistentDataPath, "SVN_Shelves");
            if (!Directory.Exists(_shelfFolder))
                Directory.CreateDirectory(_shelfFolder);
        }

        // ----- PUBLICZNE METODY DLA PRZYCISKÓW -----

        public async void ExecuteShelve()
        {
            string name = svnUI.ShelfNameInput?.text;
            if (string.IsNullOrWhiteSpace(name))
                name = "Stash_" + DateTime.Now.ToString("HHmm");

            await Shelve(name);
            RefreshShelvesUI();

            if (svnUI.ShelfNameInput != null) svnUI.ShelfNameInput.text = "";
        }

        public async void ExecuteUnshelve(string selectedShelf)
        {
            // 🔥 Natychmiastowo usuwamy wpis z listy
            RemoveShelfUI(selectedShelf);

            // Wykonujemy właściwą operację (może chwilę potrwać)
            await Unshelve(selectedShelf);

            // Odświeżamy listę, aby upewnić się, że wpis nie wróci (np. gdy unshelve nie usunął pliku)
            RefreshShelvesUI();
        }

        public async void ExecuteDeleteShelf(string shelfName)
        {
            if (IsProcessing) return;
            IsProcessing = true;

            // 🔥 Usuwamy wpis natychmiastowo
            RemoveShelfUI(shelfName);

            try
            {
                string filePath = GetShelfFilePath(shelfName);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    SVNLogBridge.LogLine($"<color=green>[Stash]</color> Deleted: {shelfName}");
                }
                else
                {
                    SVNLogBridge.LogLine($"<color=yellow>[Stash]</color> Shelf '{shelfName}' not found.");
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Delete failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                RefreshShelvesUI();   // dla bezpieczeństwa odświeżamy
            }
        }

        /// <summary>
        /// Ręczne odświeżenie listy półek (opcjonalny przycisk Refresh).
        /// </summary>
        public void Button_RefreshShelvesUI()
        {
            RefreshShelvesUI();
        }

        // ----- LOGIKA SHELVE / UNSHELVE -----

        public async Task Shelve(string shelfName)
        {
            IsProcessing = true;
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                string root = svnManager.WorkingDir;
                string patchFile = GetShelfFilePath(shelfName);

                string diff = await SvnRunner.RunAsync("diff", root);
                if (string.IsNullOrWhiteSpace(diff))
                {
                    SVNLogBridge.LogLine("<color=yellow>[Stash]</color> No changes to shelve.");
                    return;
                }

                File.WriteAllText(patchFile, diff);
                await SvnRunner.RunAsync("revert -R .", root);

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearSVNTreeView();
                    statusModule.ClearCurrentData();
                    await statusModule.RefreshAfterAction();
                }

                SVNLogBridge.LogLine($"<color=green>[Stash]</color> Saved as: {shelfName}");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Stash failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task Unshelve(string shelfName)
        {
            IsProcessing = true;
            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                string root = svnManager.WorkingDir;
                string patchFile = GetShelfFilePath(shelfName);

                if (!File.Exists(patchFile))
                {
                    SVNLogBridge.LogLine($"<color=red>[Stash]</color> Shelf '{shelfName}' not found.");
                    return;
                }

                // Nałożenie diffa
                await SvnRunner.RunAsync($"patch \"{patchFile}\"", root);
                File.Delete(patchFile);

                SVNLogBridge.LogLine($"<color=green>[Stash]</color> Restored: {shelfName}");
                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=red>Restore failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        // ----- POMOCNICZE -----

        /// <summary>
        /// Natychmiastowo usuwa z UI element odpowiadający podanej nazwie półki.
        /// </summary>
        private void RemoveShelfUI(string shelfName)
        {
            if (svnUI.ShelfListContainer == null) return;
            Transform container = svnUI.ShelfListContainer.content;

            foreach (Transform child in container)
            {
                var ui = child.GetComponent<ShelfItemUI>();
                if (ui != null && ui.NameText.text == shelfName)
                {
                    GameObject.DestroyImmediate(child.gameObject);
                    return; // usuwamy tylko jeden pasujący element
                }
            }
        }

        public Task<List<ShelfInfo>> GetShelvesList()
        {
            try
            {
                var dirInfo = new DirectoryInfo(_shelfFolder);
                dirInfo.Refresh();

                var shelfInfos = dirInfo.GetFiles("*.patch")
                    .Select(fileInfo =>
                    {
                        var info = new ShelfInfo
                        {
                            Name = Path.GetFileNameWithoutExtension(fileInfo.Name),
                            Date = fileInfo.LastWriteTime,
                            SizeBytes = fileInfo.Length
                        };

                        // Parsuj plik .patch, aby policzyć liczbę zmienionych plików
                        try
                        {
                            string content = File.ReadAllText(fileInfo.FullName);
                            // Każdy plik w patchu zaczyna się od "Index: " lub "--- "
                            info.FileCount = System.Text.RegularExpressions.Regex.Matches(
                                content, @"^---\s\S+", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
                        }
                        catch
                        {
                            info.FileCount = 0;
                        }

                        return info;
                    })
                    .Where(info => !string.IsNullOrEmpty(info.Name))
                    .OrderByDescending(info => info.Date)  // najnowsze na górze
                    .ToList();

                return Task.FromResult(shelfInfos);
            }
            catch
            {
                return Task.FromResult(new List<ShelfInfo>());
            }
        }

        private string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            int digit = (int)Math.Floor(Math.Log(bytes, 1024));
            digit = Mathf.Clamp(digit, 0, units.Length - 1);
            double value = bytes / Math.Pow(1024, digit);
            return value.ToString("F1") + " " + units[digit];
        }

        public async void RefreshShelvesUI()
        {
            List<ShelfInfo> shelfInfos = await GetShelvesList();
            if (svnUI.ShelfListContainer == null) return;

            Transform container = svnUI.ShelfListContainer.content;

            // Natychmiastowe usunięcie starych obiektów
            foreach (Transform child in container.Cast<Transform>().ToList())
            {
                if (child != null && child.gameObject != null)
                    GameObject.DestroyImmediate(child.gameObject);
            }

            if (shelfInfos.Count == 0)
            {
                var msg = new GameObject("EmptyMessage");
                msg.transform.SetParent(container, false);
                var txt = msg.AddComponent<TMPro.TextMeshProUGUI>();
                txt.text = "<color=#888888>No shelves found.</color>";
                txt.fontSize = 12;
                txt.alignment = TMPro.TextAlignmentOptions.Center;
            }
            else
            {
                foreach (var info in shelfInfos)
                {
                    if (svnUI.ShelfItemPrefab == null) break;

                    // Sprawdź, czy plik faktycznie istnieje
                    string filePath = GetShelfFilePath(info.Name);
                    if (!File.Exists(filePath))
                    {
                        SVNLogBridge.LogLine($"<color=yellow>[Stash]</color> Stale entry '{info.Name}' ignored.");
                        continue;
                    }

                    GameObject item = GameObject.Instantiate(svnUI.ShelfItemPrefab, container);
                    var ui = item.GetComponent<ShelfItemUI>();
                    if (ui != null)
                    {
                        ui.RestoreButton.onClick.RemoveAllListeners();
                        ui.DeleteButton.onClick.RemoveAllListeners();

                        // Ustawiamy dane
                        ui.NameText.text = info.Name;
                        ui.DateText.text = info.Date.ToString("yyyy-MM-dd HH:mm");

                        if (ui.FilesLabel != null)
                            ui.FilesLabel.text = $"Files: {info.FileCount}";

                        if (ui.SizeLabel != null)
                            ui.SizeLabel.text = $"Size: {FormatSize(info.SizeBytes)}";

                        string currentName = info.Name;
                        ui.RestoreButton.onClick.AddListener(() => ExecuteUnshelve(currentName));
                        ui.DeleteButton.onClick.AddListener(() => ExecuteDeleteShelf(currentName));
                    }
                }
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(container as RectTransform);
        }

        private string GetShelfFilePath(string name)
        {
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_shelfFolder, safeName + ".patch");
        }

        public class ShelfInfo
        {
            public string Name;
            public DateTime Date;
            public int FileCount;
            public long SizeBytes;
        }
    }
}