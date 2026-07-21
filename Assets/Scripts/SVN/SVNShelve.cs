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
        private CancellationTokenSource _cts;

        public SVNShelve(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _shelfFolder = Path.Combine(Application.persistentDataPath, "SVN_Shelves");
            if (!Directory.Exists(_shelfFolder))
                Directory.CreateDirectory(_shelfFolder);
        }

        public void Cancel()
        {
            _cts?.Cancel();
        }

        public async void ExecuteShelve()
        {
            if (IsProcessing) return;

            string name = svnUI.ShelfNameInput?.text;
            if (string.IsNullOrWhiteSpace(name))
                name = "Stash_" + DateTime.Now.ToString("HHmm");

            await Shelve(name);
            RefreshShelvesUI();

            if (svnUI.ShelfNameInput != null) svnUI.ShelfNameInput.text = "";
        }

        public async void ExecuteUnshelve(string selectedShelf)
        {
            RemoveShelfUI(selectedShelf);
            await Unshelve(selectedShelf);
            RefreshShelvesUI();
        }

        public async void ExecuteDeleteShelf(string shelfName)
        {
            if (IsProcessing) return;
            IsProcessing = true;

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
                SVNLogBridge.LogLine($"<color=#FFAA00>Delete failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                RefreshShelvesUI();
            }
        }

        public void Button_RefreshShelvesUI() => RefreshShelvesUI();

        public async Task Shelve(string shelfName)
        {
            if (IsProcessing) return;
            IsProcessing = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                string root = svnManager.WorkingDir;

                string diff = await SvnRunner.RunAsync("diff", root, token: token);
                if (string.IsNullOrWhiteSpace(diff))
                {
                    SVNLogBridge.LogLine("<color=yellow>[Stash]</color> No changes to shelve.");
                    return;
                }

                await SvnRunner.RunAsync("revert -R .", root, token: token);

                string patchFile = GetShelfFilePath(shelfName);
                File.WriteAllText(patchFile, diff);

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearSVNTreeView();
                    statusModule.ClearCurrentData();
                    await statusModule.RefreshAfterAction();
                }

                SVNLogBridge.LogLine($"<color=green>[Stash]</color> Saved as: {shelfName}");
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[Stash] Cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Stash failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        public async Task Unshelve(string shelfName)
        {
            if (IsProcessing) return;
            IsProcessing = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync();
                string root = svnManager.WorkingDir;
                string patchFile = GetShelfFilePath(shelfName);

                if (!File.Exists(patchFile))
                {
                    SVNLogBridge.LogLine($"<color=#FFAA00>[Stash]</color> Shelf '{shelfName}' not found.");
                    return;
                }

                await SvnRunner.RunAsync($"patch \"{patchFile}\"", root, token: token);
                File.Delete(patchFile);

                SVNLogBridge.LogLine($"<color=green>[Stash]</color> Restored: {shelfName}");
                await svnManager.RefreshStatus();
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[Stash] Unshelve cancelled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Restore failed:</color> {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void RemoveShelfUI(string shelfName)
        {
            if (svnUI.ShelfListContainer == null) return;
            Transform container = svnUI.ShelfListContainer.content;

            foreach (Transform child in container)
            {
                var ui = child.GetComponent<ShelfItemUI>();
                if (ui != null && ui.NameText.text == shelfName)
                {
                    if (Application.isPlaying)
                        GameObject.Destroy(child.gameObject);
                    else
                        GameObject.DestroyImmediate(child.gameObject);
                    return;
                }
            }
        }

        public async Task<List<ShelfInfo>> GetShelvesList()
        {
            return await Task.Run(() =>
            {
                var result = new List<ShelfInfo>();
                try
                {
                    var dirInfo = new DirectoryInfo(_shelfFolder);
                    if (!dirInfo.Exists) return result;

                    foreach (var fileInfo in dirInfo.GetFiles("*.patch"))
                    {
                        var info = new ShelfInfo
                        {
                            Name = Path.GetFileNameWithoutExtension(fileInfo.Name),
                            Date = fileInfo.LastWriteTime,
                            SizeBytes = fileInfo.Length
                        };

                        try
                        {
                            string content = File.ReadAllText(fileInfo.FullName);
                            info.FileCount = System.Text.RegularExpressions.Regex.Matches(
                                content, @"^---\s\S+", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
                        }
                        catch
                        {
                            info.FileCount = 0;
                        }

                        result.Add(info);
                    }
                }
                catch { }

                return result.OrderByDescending(i => i.Date).ToList();
            });
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

            foreach (Transform child in container.Cast<Transform>().ToList())
            {
                if (child == null || child.gameObject == null) continue;
                if (Application.isPlaying)
                    GameObject.Destroy(child.gameObject);
                else
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