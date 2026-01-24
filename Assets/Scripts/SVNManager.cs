using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SVNManager : MonoBehaviour
    {
        public static SVNManager Instance { get; private set; }

        public const string KEY_WORKING_DIR = "SVN_Persisted_WorkingDir";
        public const string KEY_SSH_PATH = "SVN_Persisted_SSHKeyPath";
        public const string KEY_MERGE_TOOL = "SVN_Persisted_MergeTool";
        public const string KEY_REPO_URL = "SVN_Persisted_RepositoryURL";

        [SerializeField] private SVNUI svnUI = null;
        [SerializeField] private GameObject loadingOverlay; // Przeciągnij tutaj swój obiekt LoadingOverlay
        
        private (string status, string[] files) currentStatus;

        [Header("State")]
        public HashSet<string> expandedPaths = new HashSet<string>();

        private string workingDir = string.Empty;
        private string currentKey = string.Empty;

        private bool isProcessing = false;
        
        public string RepositoryUrl { get; private set; } = string.Empty;

        public string CurrentUserName
        {
            get
            {
                return Environment.UserName;
            }
        }

        public bool IsProcessing
        {
            get => isProcessing;
            set => isProcessing = value;
        }

        public string WorkingDir
        {
            get => workingDir;
            set
            {
                workingDir = value;
                _ = RefreshRepositoryInfo();
            }
        }

        public string CurrentKey
        {
            get => currentKey;
            set => currentKey = value;
        }

        public HashSet<string> ExpandedPaths
        {
            get => expandedPaths;
            set => expandedPaths = value;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // Hide from inspector and disable before destroying to stop UI repaints
                gameObject.hideFlags = HideFlags.HideAndDontSave;
                this.enabled = false;
                DestroyImmediate(gameObject); // Force removal to stop Inspector lag
                return;
            }
            Instance = this;
        }

        private void SetLoading(bool isLoading)
        {
            if (loadingOverlay != null)
            {
                loadingOverlay.SetActive(isLoading);
            }
        }

        public async Task SetWorkingDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            workingDir = path; // pole prywatne używane przez metody wewnętrzne
            WorkingDir = path; // właściwość publiczna dla innych klas

            Debug.Log($"[SVN] Working Directory set to: {path}");

            // Automatycznie odśwież URL i status po zmianie ścieżki
            await RefreshRepositoryInfo();
            UpdateBranchInfo();
        }

        public async Task RefreshRepositoryInfo()
        {
            // Korzystamy z załadowanego już WorkingDir
            if (string.IsNullOrEmpty(WorkingDir) || !System.IO.Directory.Exists(WorkingDir))
                return;

            try
            {
                string url = await SvnRunner.GetRepoUrlAsync(WorkingDir);
                this.RepositoryUrl = url.Trim();
                Debug.Log($"[SVN] URL Synchronized: {RepositoryUrl}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Could not refresh Repo URL: " + ex.Message);
            }
        }

        private async void Start()
        {
            if (svnUI == null) return;

            // 1. Inicjalizacja modułu ustawień
            SVNSettings svnSettings = new SVNSettings(svnUI, this);

            // 2. ŁADOWANIE (Wypełnia pola tekstowe i zmienne Managera)
            svnSettings.LoadSettings();

            // 3. PODPINANIE LISTENERÓW (Dopiero teraz!)
            // Używamy lambdy, by tylko aktualizować zmienne "w locie", 
            // ale NIE zapisywać do PlayerPrefs przy każdym wpisanym znaku.
            if (svnUI.SettingsSshKeyPathInput != null)
            {
                svnUI.SettingsSshKeyPathInput.onValueChanged.AddListener(val => {
                    this.CurrentKey = val;
                    SvnRunner.KeyPath = val;
                });
            }

            if (svnUI.SettingsWorkingDirInput != null)
            {
                svnUI.SettingsWorkingDirInput.onValueChanged.AddListener(val => {
                    this.WorkingDir = val;
                });
            }

            // 4. Reszta inicjalizacji (Diagnostics, Refresh itd.)
            if (!string.IsNullOrEmpty(WorkingDir) && Directory.Exists(WorkingDir))
            {
                await RefreshRepositoryInfo();
                UpdateBranchInfo();
                await Task.Delay(300);
                Button_RefreshStatus();
            }
        }

        public async void UpdateBranchInfo()
        {
            string rootPath = WorkingDir;

            // 1. Validate if directory exists
            if (string.IsNullOrEmpty(rootPath) || !System.IO.Directory.Exists(rootPath))
            {
                if (svnUI?.BranchInfoText != null)
                    svnUI.BranchInfoText.text = "<color=grey>No active project</color>";
                return;
            }

            try
            {
                // 2. Fetch data in XML format (Locale-independent)
                string output = await SvnRunner.RunAsync("info --xml", rootPath);

                if (string.IsNullOrEmpty(output)) return;

                string fullUrl = "Unknown";
                string relativeUrl = "Unknown";
                string revision = "0";

                // 3. XML Parsing
                System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                doc.LoadXml(output);

                System.Xml.XmlNode urlNode = doc.SelectSingleNode("//url");
                System.Xml.XmlNode relativeUrlNode = doc.SelectSingleNode("//relative-url");
                System.Xml.XmlNode entryNode = doc.SelectSingleNode("//entry");

                if (urlNode != null) fullUrl = urlNode.InnerText;
                if (relativeUrlNode != null) relativeUrl = relativeUrlNode.InnerText;
                if (entryNode != null) revision = entryNode.Attributes["revision"]?.Value ?? "0";

                // 4. Sync URL with Manager for other modules (like Checkout/Update)
                this.RepositoryUrl = fullUrl.TrimEnd('/');

                // 5. Clean up Branch Display Name
                // Remove technical SVN prefix "^/"
                string branchDisplayName = relativeUrl.Replace("^/", "");

                // --- IMPROVED LOGIC FOR EMPTY/ROOT BRANCHES ---
                if (string.IsNullOrEmpty(branchDisplayName) || branchDisplayName == "/")
                {
                    // If we are at the root of the repo, it's usually the 'trunk'
                    branchDisplayName = "trunk (root)";
                }
                else if (branchDisplayName.Contains("/"))
                {
                    // If path is "branches/feature-login", extract only "feature-login"
                    string[] parts = branchDisplayName.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        branchDisplayName = parts[parts.Length - 1];
                    }
                }

                // 6. Final UI Update
                if (svnUI?.BranchInfoText != null)
                {
                    svnUI.BranchInfoText.text = $"<color=#00E5FF>Branch:</color> {branchDisplayName} | <color=#FFD700>Rev:</color> {revision}";
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[UpdateBranchInfo] Error: {ex.Message}");
                if (svnUI?.BranchInfoText != null)
                    svnUI.BranchInfoText.text = "<color=red>SVN Info: Metadata Error</color>";
            }
        }

        public async Task RunDiagnostics()
        {
            // 1. Sprawdź czy svnUI i LogText istnieją (potrzebne tylko do wyświetlania wyniku)
            if (svnUI == null || svnUI.LogText == null)
            {
                Debug.LogError("[SVN Manager] SVNUI or LogText reference is missing. Diagnostics cannot be displayed.");
                return;
            }

            // 2. Synchronizacja klucza: Pobieramy dane z Managera (CurrentKey), nie z InputFielda
            // Te dane zostały tam załadowane w metodzie LoadSettings()
            if (!string.IsNullOrEmpty(CurrentKey))
            {
                SvnRunner.KeyPath = CurrentKey;
            }
            else
            {
                Debug.LogWarning("[SVN Manager] No SSH Key path found in Manager. SSH operations may fail.");
            }

            // 3. Rozpoczęcie diagnostyki w UI
            svnUI.LogText.text = "<b>[SYSTEM DIAGNOSTICS]</b>\n";
            svnUI.LogText.text += $"Working Dir: <color=#AAAAAA>{WorkingDir}</color>\n";

            try
            {
                // Test silnika SVN
                bool svnOk = await SvnRunner.CheckIfSvnInstalled();
                svnUI.LogText.text += svnOk
                    ? "<color=green>✔ SVN CLI:</color> Found\n"
                    : "<color=red>✘ SVN CLI:</color> Missing (Add to PATH!)\n";

                // Test silnika SSH
                bool sshOk = await SvnRunner.CheckIfSshInstalled();
                svnUI.LogText.text += sshOk
                    ? "<color=green>✔ OpenSSH:</color> Found\n"
                    : "<color=red>✘ OpenSSH:</color> Missing!\n";

                // Opcjonalnie: Test istnienia pliku klucza
                if (!string.IsNullOrEmpty(CurrentKey))
                {
                    bool keyExists = System.IO.File.Exists(CurrentKey);
                    svnUI.LogText.text += keyExists
                        ? "<color=green>✔ SSH Key:</color> File verified\n"
                        : "<color=orange>⚠ SSH Key:</color> Path set but file not found!\n";
                }

                svnUI.LogText.text += "-----------------------------------\n";
            }
            catch (System.Exception ex)
            {
                svnUI.LogText.text += $"<color=red>Diagnostics Error:</color> {ex.Message}\n";
                Debug.LogError($"[SVN] Diagnostics failed: {ex}");
            }
        }

        public async void Button_RefreshStatus()
        {
            if (isProcessing) return;

            // 1. Sprawdzamy czy UI w ogóle istnieje (do wypisywania logów)
            if (svnUI == null)
            {
                Debug.LogError("[SVN] SVNUI reference is missing in SVNManager!");
                return;
            }

            // 2. Pobieramy ścieżkę bezpośrednio z Managera (pole 'workingDir' lub właściwość 'WorkingDir')
            // Nie dotykamy już svnUI.AddRepoPathInput!
            string root = WorkingDir;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                if (svnUI.LogText != null)
                    svnUI.LogText.text = "<color=red>Error:</color> Valid working directory not found. Please set it in settings.";
                return;
            }

            isProcessing = true;
            if (svnUI.LogText != null) svnUI.LogText.text = "Refreshing status...";

            try
            {
                // 3. Pobranie danych z SVN (używamy zmiennej root)
                var statusDict = await SvnRunner.GetFullStatusDictionaryAsync(root, false);
                CurrentStatusDict = statusDict;

                // 4. Logika konfliktów
                bool hasConflicts = statusDict.Values.Any(v => v.status.Contains("C"));
                if (svnUI.ConflictGroup != null)
                    svnUI.ConflictGroup.SetActive(hasConflicts);

                // 5. Budowanie drzewa plików
                var stats = new SvnStats();
                var sb = new StringBuilder();
                HashSet<string> relevantFolders = MapRelevantFolders(statusDict);

                BuildTreeString(root, root, 0, statusDict, sb, stats, expandedPaths, new bool[64], false, relevantFolders);

                // 6. Wyświetlenie wyników w UI
                if (svnUI.TreeDisplay != null)
                    svnUI.TreeDisplay.text = sb.ToString();

                if (svnUI.LogText != null)
                {
                    string conflictAlert = hasConflicts ? " <color=red><b>(CONFLICTS!)</b></color>" : "";
                    svnUI.LogText.text = $"Last Refresh: {DateTime.Now:HH:mm:ss}\n" +
                                         $"Modified: {stats.ModifiedCount} | Conflicts: {stats.ConflictsCount}{conflictAlert}";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SVN] Refresh Error: {ex}");
                if (svnUI.LogText != null)
                    svnUI.LogText.text = $"<color=red>Refresh Error:</color> {ex.Message}";
            }
            finally
            {
                isProcessing = false;
            }
        }

        // Pomocnicza metoda dla czystości kodu
        private HashSet<string> MapRelevantFolders(Dictionary<string, (string status, string size)> statusDict)
        {
            HashSet<string> folders = new HashSet<string>();
            foreach (var path in statusDict.Keys)
            {
                string[] parts = path.Split('/');
                string currentFolder = "";
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    currentFolder = string.IsNullOrEmpty(currentFolder) ? parts[i] : currentFolder + "/" + parts[i];
                    folders.Add(currentFolder);
                }
            }
            return folders;
        }

        // Helper method to extract revision number from SVN output
        public string ParseRevision(string input)
        {
            // SVN output usually ends with: "Committed revision 123."
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(input, @"revision\s+(\d+)");

            return match.Success ? match.Groups[1].Value : null;
        }

        public string ParseRevisionFromInfo(string infoOutput)
        {
            // Szuka linii "Revision: 123" w komendzie svn info
            var match = System.Text.RegularExpressions.Regex.Match(infoOutput, @"^Revision:\s+(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : "Unknown";
        }

        private void BuildTreeString(
        string currentDir,
        string rootDir,
        int indent,
        Dictionary<string, (string status, string size)> statusDict, // POPRAWIONY TYP ARGUMENTU
        StringBuilder sb,
        SvnStats stats,
        HashSet<string> expandedPaths,
        bool[] parentIsLast,
        bool showIgnored,
        HashSet<string> foldersWithRelevantContent)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(currentDir); }
            catch { return; }

            var sortedEntries = entries
                .Select(e => e.Replace('\\', '/'))
                .OrderBy(e => !Directory.Exists(e))
                .ThenBy(e => e)
                .ToArray();

            for (int i = 0; i < sortedEntries.Length; i++)
            {
                string entry = sortedEntries[i];
                string name = Path.GetFileName(entry);

                if (name == ".svn" || name.EndsWith(".meta")) continue;

                bool isLast = (i == sortedEntries.Length - 1);
                bool isDirectory = Directory.Exists(entry);
                string relPath = entry.Replace(rootDir.Replace('\\', '/'), "").TrimStart('/');

                // --- POPRAWKA: Rozpakowanie krotki ---
                string status = "";
                string sizeInfo = "";
                if (statusDict.TryGetValue(relPath, out var statusData))
                {
                    status = statusData.status; // Pobieramy status
                    sizeInfo = statusData.size;   // Pobieramy rozmiar
                }

                if (showIgnored)
                {
                    if (status != "I" && !foldersWithRelevantContent.Contains(relPath)) continue;
                    if (isDirectory && status != "I") status = "I";
                }
                else
                {
                    if (status == "I") continue;
                    if (isDirectory)
                    {
                        if (string.IsNullOrEmpty(status) && !foldersWithRelevantContent.Contains(relPath)) continue;
                        if (string.IsNullOrEmpty(status) && foldersWithRelevantContent.Contains(relPath)) status = "M";
                    }
                    else if (string.IsNullOrEmpty(status)) continue;
                }

                // --- STATYSTYKI ---
                if (isDirectory) stats.FolderCount++;
                else
                {
                    if (status == "I") stats.IgnoredCount++;
                    else
                    {
                        stats.FileCount++;
                        if (status == "M") stats.ModifiedCount++;
                        if (status == "A" || status == "?") stats.NewFilesCount++;
                        if (status == "C") stats.ConflictsCount++;
                        if (status == "!") stats.FileCount++;
                    }
                }

                // --- RYSOWANIE ---
                // --- RYSOWANIE ---
                StringBuilder indentBuilder = new StringBuilder();
                for (int j = 0; j < indent - 1; j++)
                    indentBuilder.Append(parentIsLast[j] ? "    " : "│   ");

                if (indent > 0)
                    indentBuilder.Append(isLast ? "└── " : "├── ");

                string expandIcon = isDirectory ? (expandedPaths.Contains(relPath) ? "[-] " : "[+] ") : "    ";

                // POPRAWKA: Konwersja char na string za pomocą .ToString()
                // Jeśli status jest pusty, przekazujemy spację jako string
                char statusCode = !string.IsNullOrEmpty(status) ? status[0] : ' ';
                string statusIcon = SvnRunner.GetStatusIcon(statusCode.ToString());

                string typeTag = isDirectory ? "<color=#FFCA28><b><D></b></color>" : "<color=#4FC3F7><F></color>";

                // Wyświetlanie rozmiaru pobranego ze słownika
                string sizeText = (!isDirectory && !string.IsNullOrEmpty(sizeInfo)) ? $" <color=#555555>.... ({sizeInfo})</color>" : "";

                sb.AppendLine($"{indentBuilder}{statusIcon} {expandIcon}{typeTag} {name}{sizeText}");

                if (isDirectory && (expandedPaths.Contains(relPath) || string.IsNullOrEmpty(relPath)))
                {
                    if (indent < parentIsLast.Length) parentIsLast[indent] = isLast;
                    BuildTreeString(entry, rootDir, indent + 1, statusDict, sb, stats, expandedPaths, parentIsLast, showIgnored, foldersWithRelevantContent);
                }
            }
        }

        public Dictionary<string, (string status, string size)> CurrentStatusDict { get; private set; } = new Dictionary<string, (string status, string size)>();

        /// <summary>
        /// Updates the global status dictionary with new data from SVN.
        /// </summary>
        public void UpdateFilesStatus(Dictionary<string, (string status, string size)> newStatus)
        {
            if (newStatus == null) return;

            CurrentStatusDict = newStatus;

            // Opcjonalnie: Tutaj możesz wywołać inne zdarzenia, 
            // które powinny zareagować na zmianę statusu plików.
        }

        /// <summary>
        /// Centralna funkcja aktualizująca statystyki w całym UI (Pasek dolny i Panel Commitu).
        /// </summary>
        /// <param name="stats">Obiekt statystyk pobrany z SvnRunner</param>
        /// <param name="isIgnoredView">Czy aktualnie przeglądamy pliki ignorowane?</param>
        public void UpdateAllStatisticsUI(SvnStats stats, bool isIgnoredView)
        {
            if (svnUI == null) return;

            // --- 1. LOGIKA GŁÓWNEGO PASKA STATUSU (Main Bottom Bar) ---
            if (svnUI.StatsText != null)
            {
                if (isIgnoredView)
                {
                    // Detale dla trybu Ignored
                    svnUI.StatsText.text = $"<color=#AAAAAA><b>VIEW: IGNORED</b></color> | " +
                                           $"Folders: {stats.IgnoredFolderCount} | " +
                                           $"Files: {stats.IgnoredFileCount} | " +
                                           $"Total Ignored: <color=#FFFFFF>{stats.IgnoredCount}</color>";
                }
                else
                {
                    // Pełne detale dla trybu Modified (Working Copy)
                    svnUI.StatsText.text = $"<color=#FFD700><b>VIEW: WORKING COPY</b></color> | " +
                                           $"Folders: {stats.FolderCount} | Files: {stats.FileCount} | " +
                                           $"<color=#FFD700>Modified (M): {stats.ModifiedCount}</color> | " +
                                           $"<color=#00FF00>Added (A): {stats.AddedCount}</color> | " +
                                           $"<color=#00E5FF>New (?): {stats.NewFilesCount}</color> | " +
                                           $"<color=#FF4444>Deleted (D): {stats.DeletedCount}</color> | " +
                                           $"<color=#FF00FF>Conflicted (C): {stats.ConflictsCount}</color>";
                }
            }

            // --- 2. LOGIKA PANELU COMMITU (Commit Panel Summary) ---
            if (svnUI.CommitStatsText != null)
            {
                if (isIgnoredView)
                {
                    // Ostrzeżenie, gdy użytkownik zapomni przełączyć widok
                    svnUI.CommitStatsText.text = "<color=#FFCC00>Switch to 'Modified' view to see commit details.</color>";
                }
                else
                {
                    // Suma wszystkiego, co zostanie wysłane na serwer (łącznie z nowymi plikami, które doda CommitAll)
                    int totalToCommit = stats.ModifiedCount + stats.AddedCount + stats.NewFilesCount + stats.DeletedCount;

                    // Formułujemy ostrzeżenie o konfliktach - w Unrealu to krytyczne!
                    string conflictPart = "";
                    if (stats.ConflictsCount > 0)
                    {
                        conflictPart = $" | <color=#FF0000><b> CONFLICTS (C): {stats.ConflictsCount} (Resolve first!)</b></color>";
                    }

                    // "Full wypas" opis zmian
                    svnUI.CommitStatsText.text = $"<b>Pending Changes:</b> " +
                        $"<color=#FFD700>Modified (M): {stats.ModifiedCount}</color> | " +
                        $"<color=#00FF00>Added (A): {stats.AddedCount}</color> | " +
                        $"<color=#00E5FF>New assets (?): {stats.NewFilesCount}</color> | " +
                        $"<color=#FF4444>To Delete (D): {stats.DeletedCount}</color> | " +
                        $"<color=#FFFFFF><b>Total: {totalToCommit}</b></color>" +
                        conflictPart;
                }
            }
        }

        public string GetRepoRoot()
        {
            if (string.IsNullOrEmpty(RepositoryUrl)) return "";

            // Jeśli jesteśmy w trunk, root jest poziom wyżej
            if (RepositoryUrl.EndsWith("/trunk"))
                return RepositoryUrl.Replace("/trunk", "");

            // Jeśli jesteśmy w branchu, szukamy cięcia przed /branches/
            if (RepositoryUrl.Contains("/branches/"))
                return RepositoryUrl.Substring(0, RepositoryUrl.IndexOf("/branches/"));

            return RepositoryUrl;
        }
        

        private void OnApplicationFocus(bool focus)
        {
            if (focus && !string.IsNullOrEmpty(workingDir))
            {
                // Automatyczne odświeżenie statusu po powrocie do okna Unity
                Button_RefreshStatus();
            }
        }
    }
}