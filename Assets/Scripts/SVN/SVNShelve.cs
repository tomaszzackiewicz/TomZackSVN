using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private int _processingFlag;
        private readonly SynchronizationContext _mainThreadContext;
        private static readonly Regex PatchFileRegex = new(@"^---\s\S+", RegexOptions.Compiled | RegexOptions.Multiline);

        public SVNShelve(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
            _shelfFolder = Path.Combine(Application.persistentDataPath, "SVN_Shelves");
            Directory.CreateDirectory(_shelfFolder);
        }

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

        private void PostUI(Action action)
        {
            if (_mainThreadContext != null)
                _mainThreadContext.Post(_ => action(), null);
            else
                action();
        }

        private void SafeFireAndForget(Func<Task> operation)
        {
            _ = Task.Run(async () =>
            {
                try { await operation().ConfigureAwait(false); }
                catch (Exception ex) { PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash] Unhandled:</color> {ex.Message}")); }
            });
        }

        public void Cancel() => _cts?.Cancel();

        public void ExecuteShelve()
        {
            SafeFireAndForget(async () =>
            {
                string name = svnUI?.ShelfNameInput?.text;
                if (string.IsNullOrWhiteSpace(name))
                    name = "Stash_" + DateTime.Now.ToString("HHmm");

                await Shelve(name).ConfigureAwait(false);

                PostUI(() =>
                {
                    RefreshShelvesUI();
                    if (svnUI?.ShelfNameInput != null)
                        svnUI.ShelfNameInput.text = "";
                });
            });
        }

        public void ExecuteUnshelve(string selectedShelf)
        {
            SafeFireAndForget(async () =>
            {
                PostUI(() => RemoveShelfUI(selectedShelf));
                await Unshelve(selectedShelf).ConfigureAwait(false);
                PostUI(() => RefreshShelvesUI());
            });
        }

        public void ExecuteDeleteShelf(string shelfName)
        {
            SafeFireAndForget(async () =>
            {
                if (!TryEnterProcessing()) return;

                PostUI(() => RemoveShelfUI(shelfName));

                try
                {
                    string filePath = GetShelfFilePath(shelfName);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        PostUI(() => SVNLogBridge.LogLine($"<color=green>[Stash]</color> Deleted: {shelfName}"));
                    }
                    else
                    {
                        PostUI(() => SVNLogBridge.LogLine($"<color=yellow>[Stash]</color> Shelf '{shelfName}' not found."));
                    }
                }
                catch (Exception ex)
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>Delete failed:</color> {ex.Message}"));
                }
                finally
                {
                    ExitProcessing();
                    PostUI(() => RefreshShelvesUI());
                }
            });
        }

        public void Button_RefreshShelvesUI() => RefreshShelvesUI();

        public async Task Shelve(string shelfName)
        {
            if (!TryEnterProcessing()) return;

            using var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root)) return;

                string diff = await SvnRunner.RunAsync("diff", root, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(diff))
                {
                    PostUI(() => SVNLogBridge.LogLine("<color=yellow>[Stash]</color> No changes to shelve."));
                    return;
                }

                await SvnRunner.RunAsync("revert -R .", root, true, token).ConfigureAwait(false);

                string patchFile = GetShelfFilePath(shelfName);
                await File.WriteAllTextAsync(patchFile, diff, token).ConfigureAwait(false);

                var statusModule = svnManager?.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    PostUI(() =>
                    {
                        statusModule.ClearSVNTreeView();
                        statusModule.ClearCurrentData();
                    });
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);
                }

                PostUI(() => SVNLogBridge.LogLine($"<color=green>[Stash]</color> Saved as: {shelfName}"));
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Stash] Cancelled.</color>"));
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>Stash failed:</color> {ex.Message}"));
            }
            finally
            {
                _cts = null;
                ExitProcessing();
            }
        }

        public async Task Unshelve(string shelfName)
        {
            if (!TryEnterProcessing()) return;

            using var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                string root = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(root)) return;

                string patchFile = GetShelfFilePath(shelfName);
                if (!File.Exists(patchFile))
                {
                    PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>[Stash]</color> Shelf '{shelfName}' not found."));
                    return;
                }

                await SvnRunner.RunAsync($"patch \"{EscapeSvnArg(patchFile)}\"", root, true, token).ConfigureAwait(false);
                File.Delete(patchFile);

                PostUI(() => SVNLogBridge.LogLine($"<color=green>[Stash]</color> Restored: {shelfName}"));
                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostUI(() => SVNLogBridge.LogLine("<color=orange>[Stash] Unshelve cancelled.</color>"));
            }
            catch (Exception ex)
            {
                PostUI(() => SVNLogBridge.LogLine($"<color=#FFAA00>Restore failed:</color> {ex.Message}"));
            }
            finally
            {
                _cts = null;
                ExitProcessing();
            }
        }

        private void RemoveShelfUI(string shelfName)
        {
            if (svnUI?.ShelfListContainer == null) return;
            Transform container = svnUI.ShelfListContainer.content;

            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                var ui = child.GetComponent<ShelfItemUI>();
                if (ui != null && ui.NameText.text == shelfName)
                {
                    GameObject.Destroy(child.gameObject);
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
                            int fileCount = 0;
                            using var reader = new StreamReader(fileInfo.FullName);
                            for (int i = 0; i < 50 && !reader.EndOfStream; i++)
                            {
                                if (PatchFileRegex.IsMatch(reader.ReadLine()))
                                    fileCount++;
                            }
                            info.FileCount = fileCount;
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
            }).ConfigureAwait(false);
        }

        public async void RefreshShelvesUI()
        {
            List<ShelfInfo> shelfInfos = await GetShelvesList().ConfigureAwait(false);

            PostUI(() => RefreshShelvesUIInternal(shelfInfos));
        }

        private void RefreshShelvesUIInternal(List<ShelfInfo> shelfInfos)
        {
            if (svnUI?.ShelfListContainer == null) return;

            Transform container = svnUI.ShelfListContainer.content;

            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i);
                if (child != null) GameObject.Destroy(child.gameObject);  // <-- poprawione
            }

            if (shelfInfos.Count == 0)
            {
                if (svnUI.ShelfItemPrefab != null)
                {
                    GameObject emptyItem = GameObject.Instantiate(svnUI.ShelfItemPrefab, container);  // <-- poprawione
                    var ui = emptyItem.GetComponent<ShelfItemUI>();
                    if (ui != null)
                    {
                        ui.NameText.text = "<color=#888888>No shelves found.</color>";
                        if (ui.DateText != null) ui.DateText.text = "";
                        if (ui.FilesLabel != null) ui.FilesLabel.text = "";
                        if (ui.SizeLabel != null) ui.SizeLabel.text = "";
                        ui.RestoreButton.gameObject.SetActive(false);
                        ui.DeleteButton.gameObject.SetActive(false);
                    }
                }
                else
                {
                    SVNLogBridge.LogLine("<color=#888888>No shelves found.</color>");
                }
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

                    GameObject item = GameObject.Instantiate(svnUI.ShelfItemPrefab, container);  // <-- poprawione
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

            if (container is RectTransform rect)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        private string GetShelfFilePath(string name)
        {
            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_shelfFolder, safeName + ".patch");
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            int digit = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), units.Length - 1);
            double value = bytes / Math.Pow(1024, digit);
            return value.ToString("F1") + " " + units[digit];
        }

        private static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            return arg.Replace("\"", "\\\"");
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