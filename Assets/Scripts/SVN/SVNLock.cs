using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLock : SVNBase
    {
        public SVNLock(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager) { }

        // --- COMPILER FIX SECTION (Method Aliases) ---
        public void LockAllModified() => LockModified();
        public void RefreshStealPanel(LockPanel panel) => ShowAllLocks();

        // --- LOCK LOGIC ---
        public async void LockModified()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;
            svnUI.LogText.text = "<b>[Lock]</b> Scanning for modified files (M)...\n";

            try
            {
                // 1. Pobieramy status lokalny (zmodyfikowane pliki)
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                var modifiedFiles = statusDict
                    .Where(x => x.Value.status == "M")
                    .Select(x => x.Key)
                    .ToList();

                if (modifiedFiles.Count == 0)
                {
                    svnUI.LogText.text += "<color=yellow>No modified files (M) found to lock.</color>\n";
                    return;
                }

                // 2. Pobieramy aktualne blokady z serwera
                var currentServerLocks = await GetDetailedLocks(root);

                // Tworzymy zestaw (HashSet) wszystkich ścieżek, które JUŻ mają locka na serwerze
                // (niezależnie od tego, czy to Ty, czy ktoś inny)
                var alreadyLockedPaths = new HashSet<string>(
                    currentServerLocks.Select(l => l.Path.Replace("\\", "/").ToLower())
                );

                // 3. Filtrujemy listę: bierzemy tylko te pliki "M", których NIE MA na liście blokad serwera
                var filesToLock = modifiedFiles
                    .Where(f =>
                    {
                        string normalizedPath = f.Replace("\\", "/").ToLower();
                        // Sprawdzamy, czy którykolwiek zablokowany plik na serwerze pasuje do naszego pliku
                        return !alreadyLockedPaths.Any(lp => normalizedPath.EndsWith(lp));
                    })
                    .Select(f => $"\"{f}\"")
                    .ToArray();

                int alreadyLockedByMeOrOthers = modifiedFiles.Count - filesToLock.Length;

                if (filesToLock.Length > 0)
                {
                    svnUI.LogText.text += $"Locking {filesToLock.Length} new files (Skipped {alreadyLockedByMeOrOthers} already locked)...\n";

                    string allPathsJoined = string.Join(" ", filesToLock);
                    await SvnRunner.RunAsync($"lock {allPathsJoined}", root);

                    svnUI.LogText.text += "<color=green>Locking completed successfully.</color>\n";
                }
                else
                {
                    svnUI.LogText.text += "<color=yellow>All modified files are already locked by you or others.</color>\n";
                }

                await svnManager.RefreshStatus();
            }
            catch (Exception ex)
            {
                // Ignorujemy błąd, jeśli SVN mimo wszystko wypluje ostrzeżenie o istniejącym locku
                if (ex.Message.Contains("W160035"))
                {
                    svnUI.LogText.text += "<color=green>Files were already locked.</color>\n";
                }
                else
                {
                    svnUI.LogText.text += $"<color=red>Lock Error:</color> {ex.Message}\n";
                }
            }
            finally { IsProcessing = false; }
        }

        public async void UnlockAll()
        {
            if (IsProcessing) return;
            string root = svnManager.WorkingDir;
            IsProcessing = true;
            svnUI.LogText.text = "<b>[Unlock]</b> Forcing server to release locks...\n";

            try
            {
                var allLocks = await GetDetailedLocks(root);

                var myLocksPaths = allLocks
                    .Where(l => l.Owner.Trim().Equals(svnManager.CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(l => $"\"{l.FullPath}\"") // Dodajemy cudzysłów na wypadek spacji w nazwie
                    .ToList();

                if (myLocksPaths.Count > 0)
                {
                    // Łączymy wszystkie ścieżki w jeden ciąg znaków oddzielony spacjami
                    string allPathsJoined = string.Join(" ", myLocksPaths);

                    // Wysyłamy jedną komendę: unlock --force plik1 plik2 plik3
                    // Używamy RunAsync tak, jak prawdopodobnie masz go zdefiniowanego: 
                    // (komenda z argumentami, katalog roboczy)
                    await SvnRunner.RunAsync($"unlock --force {allPathsJoined}", root);

                    svnUI.LogText.text += "<color=green>Locks released successfully.</color>\n";

                    await svnManager.RefreshStatus();
                    ShowAllLocks();
                }
                else
                {
                    svnUI.LogText.text += "You do not own any locked files.\n";
                }
            }
            catch (System.Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Error:</color> {ex.Message}\n";
            }
            finally
            {
                IsProcessing = false;
            }
        }

        // --- DISPLAY LOGIC ---
        public async void ShowAllLocks()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            svnUI.LocksText.text = "<b><color=orange>Fetching Repository Status...</color></b>\n";

            try
            {
                var locks = await GetDetailedLocks(svnManager.WorkingDir);
                svnUI.LocksText.text = "<b>Active Repository Locks:</b>\n----------------------------------\n";

                if (locks.Count == 0)
                {
                    svnUI.LocksText.text += "<color=yellow>No active locks found on server.</color>\n";
                }
                else
                {
                    foreach (var lockItem in locks)
                    {
                        // Loguj w konsoli, żebyś widział co kod porównuje
                        Debug.Log($"[SVN DEBUG] Server Owner: '{lockItem.Owner}' | Local User: '{svnManager.CurrentUserName}'");

                        bool isMe = !string.IsNullOrEmpty(svnManager.CurrentUserName) &&
                                    lockItem.Owner.Trim().Equals(svnManager.CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase);

                        string color = isMe ? "#00FF00" : "#FF4444";
                        string prefix = isMe ? "[MINE]" : "[LOCKED]";

                        svnUI.LocksText.text += $"<color={color}><b>{prefix}</b></color> {lockItem.Path}\n";
                        svnUI.LocksText.text += $"   User: <color=yellow>{lockItem.Owner}</color>\n";
                        if (!string.IsNullOrEmpty(lockItem.Comment))
                            svnUI.LocksText.text += $"   Comment: <i>\"{lockItem.Comment}\"</i>\n";
                        svnUI.LocksText.text += "----------------------------------\n";
                    }
                }
            }
            catch (Exception ex) { svnUI.LocksText.text += $"Error: {ex.Message}\n"; }
            finally { IsProcessing = false; }
        }

        public async Task<List<SVNLockDetails>> GetDetailedLocks(string rootPath)
        {
            List<SVNLockDetails> locks = new List<SVNLockDetails>();

            // Dodajemy --no-ignore, aby wykluczyć błędy widoczności
            string xmlOutput = await SvnRunner.RunAsync("status --xml -u --no-ignore", rootPath);

            if (string.IsNullOrEmpty(xmlOutput)) return locks;

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlOutput);

                // KLUCZ: Szukamy locków TYLKO w sekcji repos-status (to co na serwerze)
                // Jeśli szukaliśmy "//lock", mogliśmy łapać stare dane z lokalnego cache'u
                XmlNodeList lockNodes = doc.SelectNodes("//repos-status/lock");

                foreach (XmlNode lockNode in lockNodes)
                {
                    // Przechodzimy w górę do węzła 'entry', aby pobrać ścieżkę
                    XmlNode entryNode = lockNode.ParentNode.ParentNode;
                    if (entryNode == null) continue;

                    string svnPath = entryNode.Attributes["path"]?.Value ?? "";

                    // Sprawdzamy, czy ten plik faktycznie jest zablokowany (czy ma właściciela)
                    string owner = lockNode.SelectSingleNode("owner")?.InnerText;
                    if (string.IsNullOrEmpty(owner)) continue;

                    locks.Add(new SVNLockDetails
                    {
                        Path = svnPath.Replace(rootPath, "").TrimStart('\\', '/'),
                        FullPath = svnPath,
                        Owner = owner,
                        Comment = lockNode.SelectSingleNode("comment")?.InnerText ?? "",
                        CreationDate = lockNode.SelectSingleNode("created")?.InnerText ?? ""
                    });
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("SVN XML Parse Error: " + ex.Message);
            }

            return locks;
        }

        public async void BreakAllLocks()
        {
            string root = svnManager.WorkingDir;
            svnUI.LogText.text += "<color=orange><b>[System]</b> Cleaning local database locks...</color>\n";
            await SvnRunner.RunAsync("cleanup --remove-locks", root);
            svnUI.LogText.text += "Local locks removed.\n";
        }

        public async void OpenStealPanel()
        {
            if (IsProcessing) return;
            IsProcessing = true;

            // 1. Czyścimy listę w UI
            foreach (Transform child in svnUI.LocksContainer)
            {
                GameObject.Destroy(child.gameObject);
            }

            try
            {
                // 2. Pobieramy dane z serwera
                var allLocks = await GetDetailedLocks(svnManager.WorkingDir);

                // --- DIAGNOSTYKA: Sprawdź to w konsoli Unity! ---
                string myName = svnManager.CurrentUserName ?? "NULL";
                foreach (var l in allLocks)
                {
                    Debug.Log($"[SVN Check] Server Owner: '{l.Owner}' | My Name: '{myName}'");
                }
                // -----------------------------------------------

                // 3. FILTRACJA (Pancerna: usuwa spacje i ignoruje wielkość liter)
                string myCleanName = myName.Trim().ToLower();

                var othersLocks = allLocks.Where(l =>
                {
                    if (string.IsNullOrEmpty(l.Owner)) return false;

                    // Czyścimy nazwę właściciela z serwera do porównania
                    string ownerClean = l.Owner.Trim().ToLower();

                    // Zwracamy TRUE tylko jeśli to NIE jesteś Ty
                    return ownerClean != myCleanName;
                }).ToList();

                if (othersLocks.Count == 0)
                {
                    svnUI.LogText.text = "<b>[Steal Panel]</b> No locks found from other users.\n";
                    return;
                }

                // 4. Budowanie listy
                foreach (var lockItem in othersLocks)
                {
                    GameObject go = GameObject.Instantiate(svnUI.LockEntryPrefab, svnUI.LocksContainer);
                    LockUIItem uiScript = go.GetComponent<LockUIItem>();

                    uiScript.Setup(
                        lockItem.Path,
                        lockItem.Owner,
                        lockItem.CreationDate,
                        lockItem.Comment,
                        false,
                        () => ExecuteSteal(lockItem)
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Steal Panel Error: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async void ExecuteSteal(SVNLockDetails lockDetails)
        {
            if (IsProcessing || lockDetails == null) return;
            IsProcessing = true;

            try
            {
                // 1. Wykonujemy kradzież na serwerze
                string command = $"lock --force -m \"Forced takeover by {svnManager.CurrentUserName}\" \"{lockDetails.FullPath}\"";
                await SvnRunner.RunAsync(command, svnManager.WorkingDir);

                svnUI.LogText.text += $"<color=orange>SUCCESS:</color> You took over {lockDetails.Path}\n";

                // 2. Czekamy chwilę, aby serwer SVN zaktualizował rekordy przed ponownym zapytaniem
                await System.Threading.Tasks.Task.Delay(500);

                // 3. Odświeżamy lokalny status SVN
                await svnManager.RefreshStatus();

                // 4. Ważne: Zdejmujemy flagę przed odświeżeniem panelu
                IsProcessing = false;

                // 5. Odświeżamy widok - skradziony plik powinien zniknąć
                OpenStealPanel();
            }
            catch (Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Steal Failed:</color> {ex.Message}\n";
                IsProcessing = false;
            }
        }
    }
}