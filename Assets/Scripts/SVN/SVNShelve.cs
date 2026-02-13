using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNShelve : SVNBase
    {
        public SVNShelve(SVNUI ui, SVNManager manager) : base(ui, manager) { }

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
            await Unshelve(selectedShelf);
            RefreshShelvesUI();
        }

        public async void ExecuteDeleteShelf(string shelfName)
        {
            if (IsProcessing) return;
            IsProcessing = true;

            try
            {
                await SvnRunner.RunAsync($"shelf-drop {shelfName}", svnManager.WorkingDir);
                svnUI.LogText.text += $"<color=green>[Stash]</color> Deleted: {shelfName}\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Delete failed:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
                RefreshShelvesUI();
            }
        }

        public async Task Shelve(string shelfName)
        {
            IsProcessing = true;
            try
            {
                await SvnRunner.RunAsync($"shelf-save {shelfName}", svnManager.WorkingDir);

                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir);

                svnUI.LogText.text += $"<color=green>[Stash]</color> Success: {shelfName}\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Stash failed:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
                await svnManager.RefreshStatus();
            }
        }

        public async Task Unshelve(string shelfName)
        {
            IsProcessing = true;
            try
            {
                await SvnRunner.RunAsync($"shelf-restore {shelfName}", svnManager.WorkingDir);
                await SvnRunner.RunAsync($"shelf-drop {shelfName}", svnManager.WorkingDir);

                svnUI.LogText.text += $"<color=green>[Stash]</color> Restored: {shelfName}\n";
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Restore failed:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
                await svnManager.RefreshStatus();
            }
        }

        public async Task<List<string>> GetShelvesList()
        {
            try
            {
                string output = await SvnRunner.RunAsync("shelf-list", svnManager.WorkingDir);

                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(line => line.Trim().Split(' ')[0])
                             .Where(name => !string.IsNullOrEmpty(name))
                             .ToList();
            }
            catch { return new List<string>(); }
        }

        public async void RefreshShelvesUI()
        {
            List<string> shelfNames = await GetShelvesList();
            if (svnUI.ShelfListContainer == null) return;

            Transform container = svnUI.ShelfListContainer.content;
            foreach (Transform child in container.Cast<Transform>().ToList())
                GameObject.Destroy(child.gameObject);

            foreach (string name in shelfNames)
            {
                if (svnUI.ShelfItemPrefab == null) break;

                GameObject item = GameObject.Instantiate(svnUI.ShelfItemPrefab, container);

                SetLabelText(item, "NameLabel", name);
                SetLabelText(item, "DateLabel", DateTime.Now.ToString("HH:mm"));

                string currentName = name;
                item.transform.Find("RestoreButton")?.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => ExecuteUnshelve(currentName));
                item.transform.Find("DeleteButton")?.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => ExecuteDeleteShelf(currentName));
            }
        }

        private void SetLabelText(GameObject parent, string childName, string value)
        {
            var t = parent.transform.Find(childName);
            if (t != null) t.GetComponent<TMPro.TextMeshProUGUI>().text = value;
        }
    }
}