using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Xml;
using System.Xml.Linq;

namespace SVN.Core
{
    public class SVNCheckout : SVNBase
    {
        private CancellationTokenSource _checkoutCTS;
        private long _cachedTotalSizeBytes;
        private bool _canResume;

        private enum OperationState { Idle, Running, Pausing, Paused, Cancelling, Cancelled, Completed, Failed }
        private OperationState _state = OperationState.Idle;
        private readonly object _stateLock = new object();

        private const double BytesInMB = 1024d * 1024d;
        private const double SvnOverheadMultiplier = 2.0d;

        // === FIX: limit czasu sondy rozmiaru repo — 'svn list -R' na dużym repo
        // potrafi trwać MINUTY bez możliwości anulowania (CancellationToken.None!).
        // Po 60 s pomiar jest pomijany (0), operacja główna startuje bez niego.
        private const int RemoteSizeProbeTimeoutSeconds = 60;

        private DateTime _lastStartAttempt = DateTime.MinValue;
        private const double DebounceIntervalMs = 1000d;
        private string _resolvedKeyPath;

        // === PROGRESS (checkout): pasek ważony bajtami — ten sam tracker co update.
        private SvnUpdateProgressUI _progress;

        public SVNCheckout(SVNUI svnUI, SVNManager manager) : base(svnUI, manager)
        {
            UnityMainThreadDispatcher.EnsureExists();
        }

        private string ResolveAndCacheKeyPath()
        {
            string keyPath = ResolveAndValidateKeyPath();
            _resolvedKeyPath = keyPath;
            return keyPath;
        }

        private static string FormatSshConfig(string config) =>
            string.IsNullOrWhiteSpace(config) ? "" : " " + config.TrimStart();

        public async void UpdateProjectInfo()
        {
            try { await UpdateProjectInfoAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"UpdateProjectInfo failed: {ex}");
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, $"<color=#FFAA00>Error: {ex.Message}</color>", "Info");
            }
        }

        private async Task UpdateProjectInfoAsync()
        {
            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string destPath = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(url)) return;
            if (string.IsNullOrWhiteSpace(destPath))
            {
                PostToMainThread(() =>
                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        "<color=yellow><b>Info:</b> Enter destination path to check disk space.</color>", "Info"));
                return;
            }

            PostToMainThread(() =>
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Analyzing repository...", "Info"));

            string keyPath = ResolveAndCacheKeyPath();
            string sshConfig = BuildSshConfigOption(keyPath);
            _cachedTotalSizeBytes = await GetRemoteRepositorySizeAsync(url, sshConfig).ConfigureAwait(false);
            string structure = await GetRepositoryStructureAsync(url, sshConfig).ConfigureAwait(false);

            string driveLabel;
            long freeSpaceBytes = 0;

            try
            {
                string fullPath = Path.GetFullPath(destPath);
                driveLabel = Path.GetPathRoot(fullPath);
                DriveInfo drive = new DriveInfo(driveLabel);
                freeSpaceBytes = drive.AvailableFreeSpace;
            }
            catch { driveLabel = "?"; freeSpaceBytes = 0; }

            string repoSizeStr = FormatSize(_cachedTotalSizeBytes);
            string requiredStr = FormatSize((long)(_cachedTotalSizeBytes * SvnOverheadMultiplier));
            string freeSpaceStr = FormatSize(freeSpaceBytes);

            string spaceColor = freeSpaceBytes < (_cachedTotalSizeBytes * SvnOverheadMultiplier) && _cachedTotalSizeBytes > 0 ? "red" : "green";
            var sb = new StringBuilder(512);
            sb.Append("<b>Repository Size:</b> ").Append(repoSizeStr).Append('\n')
              .Append("<b>Required Space:</b> ").Append(requiredStr).Append('\n')
              .Append("<b>Available Space (").Append(driveLabel).Append("):</b> <color=")
              .Append(spaceColor).Append(">").Append(freeSpaceStr).Append("</color>\n\n")
              .Append("<b>Repository Structure:</b>\n").Append(structure).Append("\n\n");

            if (_cachedTotalSizeBytes > 0 && freeSpaceBytes < (_cachedTotalSizeBytes * SvnOverheadMultiplier))
                sb.Append("<color=#FFAA00><b>ERROR:</b> Not enough disk space. SVN needs approximately ")
                  .Append(requiredStr).Append(".</color>");
            else if (_cachedTotalSizeBytes == 0)
                sb.Append("<color=yellow>Could not determine repository size. The repository may be empty or unreachable.</color>");
            else
                sb.Append("<color=green>Ready to checkout.</color>");

            string finalText = sb.ToString();
            PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, finalText, "Info"));
        }

        private async Task<string> GetRepositoryStructureAsync(string baseUrl, string sshConfig = "")
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
            baseUrl = baseUrl.TrimEnd('/');

            try
            {
                string output = await SvnRunner.RunAsync(
                    $"list \"{baseUrl}\" --non-interactive --trust-server-cert" + FormatSshConfig(sshConfig),
                    Path.GetTempPath(), false, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output))
                    return "<color=yellow>Repository is empty or unreachable.</color>";

                var entries = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimEnd('/'))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                var directoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entry in entries)
                    if (!directoryMap.ContainsKey(entry)) directoryMap.Add(entry, entry);

                var result = new List<string>(3);
                if (directoryMap.TryGetValue("trunk", out string trunk)) result.Add($"{trunk}");
                if (directoryMap.TryGetValue("branches", out string branches))
                {
                    int count = await GetDirectoryCountAsync($"{baseUrl}/{branches}", sshConfig).ConfigureAwait(false);
                    result.Add($"{branches} ({count} branches)");
                }
                if (directoryMap.TryGetValue("tags", out string tags))
                {
                    int count = await GetDirectoryCountAsync($"{baseUrl}/{tags}", sshConfig).ConfigureAwait(false);
                    result.Add($"{tags} ({count} tags)");
                }

                if (result.Count == 0) return "<color=yellow>No standard SVN structure found (flat repository).</color>";
                return string.Join("\n", result);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Error loading repository structure: {ex.Message}");
                return "<color=#FFAA00>Error loading repository structure.</color>";
            }
        }

        private async Task<int> GetDirectoryCountAsync(string targetUrl, string sshConfig = "")
        {
            try
            {
                string output = await SvnRunner.RunAsync(
                    $"list \"{targetUrl}\" --xml --non-interactive --trust-server-cert" + FormatSshConfig(sshConfig),
                    Path.GetTempPath(), false, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output)) return 0;
                XDocument document = XDocument.Parse(output);
                return document.Descendants("entry")
                    .Count(x => string.Equals((string)x.Attribute("kind"), "dir", StringComparison.OrdinalIgnoreCase));
            }
            catch { return 0; }
        }

        public async void StartCheckout()
        {
            try { await StartCheckoutAsync().ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task StartCheckoutAsync()
        {
            if (!CanStartOperation()) return;

            ClearPausedState();

            string url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(path))
            {
                ShowError("Repository URL and destination path cannot be empty.");
                return;
            }

            if (!IsValidSvnUrl(url))
            {
                ShowError("Invalid SVN URL. Expected svn://, svn+ssh://, http:// or https://.");
                return;
            }

            if (!TryValidatePath(path, out string fullPath)) return;

            // === FIX: normalizacja trailing slasha — Path.GetDirectoryName("D:\Repo\")
            // zwraca "D:\Repo" (nie parenta!), co psuło wybór CWD dla procesu svn.
            // (root dysku "C:\" — długość 3 — pozostaje nietknięty)
            if (fullPath.Length > 3)
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (Directory.Exists(fullPath) && Directory.GetFileSystemEntries(fullPath).Length > 0)
            {
                if (Directory.Exists(Path.Combine(fullPath, ".svn")))
                    ShowError("Destination already contains an SVN working copy. Use Resume instead.");
                else
                    ShowError("Destination folder is not empty.");
                return;
            }

            string keyPath = ResolveAndCacheKeyPath();
            if (url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(keyPath))
            {
                ShowError("SSH repository requires a valid private key.");
                return;
            }

            lock (_stateLock)
            {
                _state = OperationState.Idle;
                _canResume = false;
            }

            string sshConfig = BuildSshConfigOption(keyPath);
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Calculating repository size...", "SVN");

            // === PROGRESS: sonda zwraca MAPĘ plików (cache) — total liczony z niej;
            // ExecuteSvnOperationAsync weźmie tę samą mapę z cache (zero dodatkowego
            // roundtripu) jako wagi bajtowe paska.
            var sizeMap = await GetRemoteFileSizesAsync(url, sshConfig).ConfigureAwait(false);
            _cachedTotalSizeBytes = sizeMap.Values.Sum();

            string checkoutArgs = $"checkout \"{url}\" \"{fullPath}\" --non-interactive --trust-server-cert" + FormatSshConfig(sshConfig);
            await ExecuteSvnOperationAsync(url, fullPath, checkoutArgs, false, keyPath, "Downloading").ConfigureAwait(false);
        }

        public async void ResumeCheckout()
        {
            try { await ResumeCheckoutAsync().ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task ResumeCheckoutAsync()
        {
            if (!CanStartOperation()) return;

            string url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                ShowError("Destination path cannot be empty.");
                return;
            }

            if (!TryValidatePath(path, out string fullPath)) return;

            lock (_stateLock)
            {
                if (!_canResume)
                {
                    if (TryRestorePausedState(fullPath, url))
                    {
                        _canResume = true;
                        string savedKey = PlayerPrefs.GetString("SVN_CheckoutPaused_KeyPath", "");
                        if (!string.IsNullOrEmpty(savedKey) && File.Exists(savedKey))
                            _resolvedKeyPath = savedKey;
                    }
                    // === FIX: fallback dla cancelu+restart — projekt jest w
                    // liście (rejestracja przy przerwaniu), .svn istnieje,
                    // svn update dokończy transfer. Wcześniej tylko brama
                    // paused-state → "explicitly cancelled" zamykało drogę.
                    else if (IsRegisteredProject(fullPath))
                    {
                        _canResume = true;
                        SVNLogBridge.LogToOutput(
                            "<color=yellow>[SVN] Brak zapisanej pauzy — wznawiam zarejestrowany (przerwany) checkout. " +
                            "svn update dokończy pobieranie.</color>");
                    }
                    else
                    {
                        ShowError("Cannot resume. No paused state or registered project found for this path.");
                        return;
                    }
                }
            }

            if (!Directory.Exists(Path.Combine(fullPath, ".svn")))
            {
                ShowError("No .svn metadata found. Start a new checkout.");
                return;
            }

            string keyPath = ResolveAndCacheKeyPath();
            string sshConfig = BuildSshConfigOption(keyPath);

            lock (_stateLock) { _state = OperationState.Running; }
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow><b>Resuming checkout...</b></color>", "SVN");

            if (_cachedTotalSizeBytes <= 0)
            {
                var sizeMap = await GetRemoteFileSizesAsync(url, sshConfig).ConfigureAwait(false);
                _cachedTotalSizeBytes = sizeMap.Values.Sum();
            }

            string updateArgs = "update --non-interactive --trust-server-cert" + FormatSshConfig(sshConfig);
            await ExecuteSvnOperationAsync(url, fullPath, updateArgs, true, keyPath, "Resuming").ConfigureAwait(false);
        }

        /// <summary>Czy pod tą ścieżką istnieje zarejestrowany projekt (lista projektów).</summary>
        private static bool IsRegisteredProject(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return false;

            string norm = fullPath.Replace("\\", "/").TrimEnd('/');
            var projects = ProjectSettings.LoadProjects();
            return projects.Any(p =>
                !string.IsNullOrEmpty(p.workingDir) &&
                string.Equals(p.workingDir.Replace("\\", "/").TrimEnd('/'), norm,
                    StringComparison.OrdinalIgnoreCase));
        }

        public void PauseCheckout()
        {
            lock (_stateLock)
            {
                if (!IsProcessing) return;
                // === FIX: pauzować można tylko faktycznie działającą operację
                // (wcześniej można było nadpisać stan Completed/Failed).
                if (_state != OperationState.Running) return;
                _canResume = true;
                _state = OperationState.Pausing;
            }

            string path = svnUI.CheckoutDestFolderInput.text.Trim();
            string url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            SavePausedState(path, url, _resolvedKeyPath);

            SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Pausing checkout...");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow>Pausing...</color>", "SVN");

            // === FIX: Volatile.Read + guard na disposed (spójnie ze wzorcem z SVNCommit).
            try
            {
                var cts = Volatile.Read(ref _checkoutCTS);
                cts?.Cancel();
            }
            catch (ObjectDisposedException) { }

            // === FIX (pause-race): operacja mogła zakończyć się naturalnie między
            // ustawieniem Pausing a Cancel — wtedy jej ścieżka sukcesu/OCE oznaczy
            // stan Completed/Cancelled. Jeśli zdążyła się zakończyć sukcesem,
            // usuwamy zbędny zapisany stan pauzy, żeby Resume nie oferowało
            // wznawiania już zakończonej operacji.
            lock (_stateLock)
            {
                if (_state == OperationState.Completed || _state == OperationState.Idle)
                    ClearPausedState();
            }
        }

        public void CancelCheckout()
        {
            lock (_stateLock)
            {
                if (!IsProcessing) return;
                _canResume = false;
                if (_state == OperationState.Cancelling) return;
                _state = OperationState.Cancelling;
            }

            ClearPausedState();

            SVNLogBridge.LogToOutput("<color=#FFAA00>[SVN]</color> Cancelling checkout...");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=#FFAA00>Cancelling...</color>", "SVN");

            // === FIX: Volatile.Read + guard na disposed.
            try
            {
                var cts = Volatile.Read(ref _checkoutCTS);
                cts?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        // === PROGRESS: cache MAPY rozmiarów (path → bytes). Zastępuje dawne pola
        // sumy — suma liczona z mapy, mapa służy jako wagi bajtowe paska.
        private Dictionary<string, long> _cachedFileSizesMap;
        private string _cachedFileSizesUrl;
        private DateTime _cachedFileSizesTime = DateTime.MinValue;
        private static readonly TimeSpan RepoSizeCacheTtl = TimeSpan.FromMinutes(5);
        private readonly object _repoSizeLock = new object();

        /// <summary>
        /// Rozmiary WSZYSTKICH plików repo (svn list --xml -R) — mapa ścieżka→bajty.
        /// Klucze = ścieżki względne URL-a (format zgodny z wyjściem checkout/update).
        /// Cache 5 min (TTL); pomiary nieudane NIE są cache'owane. Timeout 60 s.
        /// </summary>
        private async Task<Dictionary<string, long>> GetRemoteFileSizesAsync(
            string url, string sshConfig = "", CancellationToken token = default)
        {
            var empty = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(url)) return empty;
            url = url.TrimEnd('/');

            lock (_repoSizeLock)
            {
                if (_cachedFileSizesMap != null &&
                    string.Equals(_cachedFileSizesUrl, url, StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow - _cachedFileSizesTime < RepoSizeCacheTtl)
                {
                    return _cachedFileSizesMap;
                }
            }

            try
            {
                string args = $"list --xml -R \"{url}\" --non-interactive --trust-server-cert" + FormatSshConfig(sshConfig);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(RemoteSizeProbeTimeoutSeconds));

                string output = await SvnRunner.RunAsync(args, Path.GetTempPath(), false, timeoutCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return empty;

                var sizes = ParseSvnListSizes(output, url);

                if (sizes.Count > 0)
                {
                    long total = sizes.Values.Sum();
                    SVNLogBridge.LogToOutput($"[SVN] Repository size: {total / BytesInMB:F2} MB ({sizes.Count} files)");

                    lock (_repoSizeLock)
                    {
                        _cachedFileSizesMap = sizes;
                        _cachedFileSizesUrl = url;
                        _cachedFileSizesTime = DateTime.UtcNow;
                    }
                }

                return sizes;
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogToOutput("<color=yellow>[SVN] Repository size probe timed out — skipping size check.</color>");
                return empty;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Failed to calculate repository size: {ex.Message}");
                return empty;
            }
        }

        /// <summary>Suma bajtów (kompatybilność starych wywołań) — z tej samej sondy/cache.</summary>
        private async Task<long> GetRemoteRepositorySizeAsync(string url, string sshConfig = "", CancellationToken token = default)
        {
            var map = await GetRemoteFileSizesAsync(url, sshConfig, token).ConfigureAwait(false);
            return map.Values.Sum();
        }

        /// <summary>Parsowanie list --xml -R: list path (URL lub relatywna) + entry/name/size.</summary>
        private static Dictionary<string, long> ParseSvnListSizes(string xml, string url)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // odporność na śmieci przed/za XML
                int start = xml.IndexOf('<');
                int end = xml.LastIndexOf('>');
                if (start < 0 || end <= start) return sizes;

                var doc = new XmlDocument();
                doc.LoadXml(xml.Substring(start, end - start + 1));

                XmlNodeList lists = doc.SelectNodes("//list");
                if (lists == null) return sizes;

                foreach (XmlNode listNode in lists)
                {
                    string listPath = (listNode.Attributes?["path"]?.Value ?? "")
                        .Trim().Replace('\\', '/').TrimEnd('/');

                    // list path może być pełnym URL-em — zdejmij prefiks
                    int idx = listPath.IndexOf(url, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) listPath = listPath.Substring(idx + url.Length);
                    listPath = listPath.Trim('/');

                    foreach (XmlNode entry in listNode.SelectNodes("entry"))
                    {
                        if (!string.Equals(entry.Attributes?["kind"]?.Value, "file",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        string name = entry.SelectSingleNode("name")?.InnerText;
                        XmlNode sizeNode = entry.SelectSingleNode("size");
                        if (string.IsNullOrEmpty(name) || sizeNode == null) continue;
                        if (!long.TryParse(sizeNode.InnerText, out long size)) continue;

                        string rel = string.IsNullOrEmpty(listPath) ? name : listPath + "/" + name;
                        rel = rel.Replace('\\', '/').TrimStart('/');
                        if (rel.Length > 0)
                            sizes[rel] = size;
                    }
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"[SVN] Size map parse failed: {ex.Message}");
            }

            return sizes;
        }

        /// <summary>Ścieżki pozycji out-of-date ('*' w svn status -u) — podzbiór do wag przy resume.</summary>
        private static async Task<List<string>> GetOutOfDatePathsAsync(string path, CancellationToken token)
        {
            var result = new List<string>();
            try
            {
                string output = await SvnRunner.RunAsync("status -u", path, token: token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(output)) return result;

                using var reader = new StringReader(output);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 8 && line[8] == '*')
                    {
                        string rest = line.Substring(9).TrimStart();
                        // po '*': [remote revision] ścieżka — zjedz wiodującą liczbę
                        int sp = rest.IndexOf(' ');
                        if (sp > 0 && long.TryParse(rest.Substring(0, sp), out _))
                            rest = rest.Substring(sp + 1).TrimStart();
                        string clean = rest.Replace('\\', '/').Trim();
                        if (clean.Length > 0) result.Add(clean);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            return result;
        }

        private async Task ExecuteSvnOperationAsync(string url, string path, string command, bool isResume, string keyPath, string operationType)
        {
            // === FIX (race): atomowe wejście przez SVNBase.TryStart() (Interlocked).
            if (!TryStart())
            {
                SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Operation already running.");
                return;
            }

            int addedCount = 0;
            int updatedCount = 0;
            int conflictCount = 0;

            DateTime startTime = DateTime.Now;

            // === FIX (CS0103): isExport/token/sizeBeforeSession/monitorTask/barModule
            // hoistowane PRZED try — catche używają barModule (dolny pasek przy
            // pauzie/cancelu/błędzie), wnętrze try przypisuje wartości.
            bool isExport = operationType == "Exporting";
            CancellationTokenSource cts = null;
            CancellationToken token = default;
            long sizeBeforeSession = 0;
            SVNBar barModule = null;

            Task logFlushTask = null;
            Task monitorTask = null;
            var logBuffer = new ConcurrentQueue<string>();

            try
            {
                cts = new CancellationTokenSource();

                // === FIX (pauza-race): przypisanie _checkoutCTS w tym SAMYM locku
                // co przejście na Running.
                lock (_stateLock)
                {
                    _state = OperationState.Running;
                    _checkoutCTS = cts;
                }

                token = cts.Token;

                sizeBeforeSession = Directory.Exists(path) ? GetDirectorySizeFast(path) : 0;

                if (!isExport && !Directory.Exists(path))
                    Directory.CreateDirectory(path);

                // === DOLNY PASEK (SVNBar): stan Updating na czas checkoutu — monitor
                // rozmiarów odświeża (x GB / y GB) na żywo, jak przy update.
                // Nazwa projektu = folder docelowy. Export pomijamy (nie tworzy WC).
                barModule = svnManager.GetModule<SVNBar>();
                if (!isExport && barModule != null)
                {
                    string barProjectName = Path.GetFileName(path.TrimEnd('/', '\\'));
                    barModule.BeginCheckout(barProjectName, path);
                }

                PostToMainThread(() =>
                {
                    SVNLogBridge.LogCheckoutConsole($"<b>[{operationType}]</b> Starting...\n");
                    SVNLogBridge.LogCheckoutConsole($"<b>[Target]</b> {url}\n");
                    SVNLogBridge.LogCheckoutConsole($"<b>[Dest]</b> {path}\n\n");
                });

                if (isResume)
                {
                    PostToMainThread(() =>
                    {
                        SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow>Cleaning working copy...</color>", "SVN");
                        SVNLogBridge.LogCheckoutConsole($"<color=yellow>[Cleanup]</color> Cleaning working copy...\n");
                    });

                    string sshConfigCleanup = BuildSshConfigOption(keyPath);
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, cleanupTimeout.Token);

                    try
                    {
                        await SvnRunner.RunAsync(
                            $"cleanup --non-interactive --trust-server-cert" + FormatSshConfig(sshConfigCleanup), path, false, linkedCts.Token).ConfigureAwait(false);
                        PostToMainThread(() =>
                            SVNLogBridge.LogCheckoutConsole($"<color=green>[Cleanup]</color> Complete.\n"));
                    }
                    catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        PostToMainThread(() =>
                            SVNLogBridge.LogCheckoutConsole($"<color=#FFAA00>[Cleanup]</color> Timed out (30s), proceeding...\n"));
                    }

                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                }

                logFlushTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await Task.Delay(200, token).ConfigureAwait(false);
                            FlushLogBuffer(logBuffer);
                        }
                    }
                    catch (OperationCanceledException) { FlushLogBuffer(logBuffer); }
                }, token);

                // === MONITOR STATUSU (WIDOK 1 — CheckoutStatusInfoText): Status /
                // Time Elapsed / Speed / Items odświeżane co 1 s. Pasek+pliki żyją
                // osobno w CheckoutedFilesText (widok 2) — brak kolizji.
                monitorTask = Task.Run(async () =>
                {
                    try
                    {
                        var sb = new StringBuilder(256);
                        while (!token.IsCancellationRequested)
                        {
                            double elapsedSeconds = Math.Max((DateTime.Now - startTime).TotalSeconds, 1);

                            int curAdded = Volatile.Read(ref addedCount);
                            int curUpdated = Volatile.Read(ref updatedCount);
                            int curConflicts = Volatile.Read(ref conflictCount);

                            double speedFiles = curAdded / elapsedSeconds;

                            string stateText;
                            string statusColor;
                            lock (_stateLock)
                            {
                                // === FIX (report-overwrite): monitor publikuje UI TYLKO
                                // dopóki operacja realnie trwa — ostatni tick nie nadpisze
                                // raportu końcowego.
                                if (_state != OperationState.Running && _state != OperationState.Pausing)
                                    break;

                                stateText = _state == OperationState.Pausing ? "Pausing" : operationType;
                                statusColor = _state == OperationState.Pausing ? "yellow" : "green";
                            }

                            sb.Clear();
                            sb.Append("<b>Status:</b> <color=").Append(statusColor).Append('>').Append(stateText).Append("</color>\n")
                              .Append("<b>Time Elapsed:</b> ").AppendFormat("{0:F1}s", elapsedSeconds).Append('\n')
                              .Append("<b>Speed:</b> ").AppendFormat("{0:F1}", speedFiles).Append(" files/sec\n")
                              .Append("<b>Items Added:</b> ").Append(curAdded);

                            if (curUpdated > 0)
                                sb.Append(" | <b>Updated:</b> ").Append(curUpdated);
                            if (curConflicts > 0)
                                sb.Append(" | <b><color=#FFAA00>Conflicts: ").Append(curConflicts).Append("</color></b>");

                            string currentText = sb.ToString();
                            PostToMainThread(() =>
                            {
                                // === FIX (domknięcie report-overwrite): re-check stanu
                                // W MOMENCIE wykonania na main — zamyka okno wyścigu.
                                lock (_stateLock)
                                {
                                    if (_state != OperationState.Running && _state != OperationState.Pausing) return;
                                }
                                if (svnUI != null && svnUI.CheckoutStatusInfoText != null)
                                    svnUI.CheckoutStatusInfoText.text = currentText;
                            });

                            await Task.Delay(1000, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);

                // === FIX (CWD): CWD procesu svn musi istnieć.
                string workingDirectory = isResume ? path : Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    workingDirectory = Path.GetTempPath();
                else if (!Directory.Exists(workingDirectory))
                    Directory.CreateDirectory(workingDirectory);

                // === PROGRESS (wagi bajtowe): mapa z sondy (cache — przy StartCheckout
                // hit; przy Export jedyny koszt). RESUME: mapa filtrowana do pozycji
                // out-of-date ('*' z status -u), inaczej total objąłby CAŁE repo.
                string sshConfigForSizes = BuildSshConfigOption(keyPath);
                var sizeMap = await GetRemoteFileSizesAsync(url, sshConfigForSizes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                int fallbackTotal = 0;
                if (isResume)
                {
                    var outOfDate = await GetOutOfDatePathsAsync(path, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    fallbackTotal = outOfDate.Count;

                    if (sizeMap.Count > 0 && outOfDate.Count > 0)
                    {
                        var incomingOnly = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in outOfDate)
                            if (sizeMap.TryGetValue(p, out long s))
                                incomingOnly[p] = s;

                        // brak dopasowań → pusta mapa → pasek per-plik na fallbackTotal
                        sizeMap = incomingOnly;
                    }
                }

                long totalBytes = sizeMap.Values.Sum();
                int itemTotal = sizeMap.Count > 0 ? sizeMap.Count : fallbackTotal;
                var weights = sizeMap;   // po tym punkcie niezmienne

                // === PROGRESS UI (WIDOK 2 — CheckoutedFilesText): dwie linie
                // podmieniane in-place — pasek ważony bajtami + % + GB, pod spodem
                // aktualny plik na niebiesko + (x/y). Jak przy update.
                _progress = new SvnUpdateProgressUI(svnUI.CheckoutedFilesText);
                _progress.SetTotal(itemTotal, totalBytes, sizeMap);

                int itemsProcessed = 0;

                PostToMainThread(() =>
                    SVNLogBridge.LogCheckoutConsole($"<color=blue><b>[Download]</b> In progress...\n</color>"));

                // === FIX (hardening): svn: E w strumieniu → flaga błędu.
                int sawError = 0;

                string result = await SvnRunner.RunLiveAsync(command, workingDirectory, line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;

                    if (line.Contains("svn: E", StringComparison.Ordinal))
                        Interlocked.Exchange(ref sawError, 1);

                    string cleanLine = line.Replace("\r", "").Replace("\\", "/").Trim();
                    if (string.IsNullOrWhiteSpace(cleanLine)) return;
                    if (cleanLine.All(c => c == '@' || c == '*')) return;
                    if (cleanLine.StartsWith("*****") || cleanLine.StartsWith("@@@@@")) return;

                    cleanLine = cleanLine.Replace("[SVN ERROR]", "").Trim();

                    // === FIX (parser): status + DOWOLNA liczba spacji + ścieżka.
                    // Linie narracyjne ("Updating...", "Checked out...") odpadają: [1] != spacja.
                    if (cleanLine.Length >= 2 &&
                        "UAGDCR".Contains(cleanLine[0]) &&
                        cleanLine[1] == ' ')
                    {
                        char status = cleanLine[0];

                        int i = 1;
                        while (i < cleanLine.Length && cleanLine[i] == ' ') i++;
                        string rawPath = i < cleanLine.Length ? cleanLine.Substring(i).Trim() : "";

                        switch (status)
                        {
                            case 'A': Interlocked.Increment(ref addedCount); break;
                            case 'U':
                            case 'G':
                            case 'R':
                            case 'D': Interlocked.Increment(ref updatedCount); break;
                            case 'C': Interlocked.Increment(ref conflictCount); break;
                        }

                        // === FIX (WAGI): zdejmij prefiks dest — bez tego klucze mapy
                        // ("Assets/x.cs") nie matchowały ścieżek z svn ("MyProject/Assets/x.cs").
                        string itemPath = StripCheckoutItemPath(rawPath, isResume ? "" : path);

                        // (x/y): przy znanej mapie liczymy tylko pliki z mapy —
                        // katalogi ("A    Assets") nie nadymają licznika ponad total.
                        bool fileKnown = weights.Count > 0 &&
                                         !string.IsNullOrEmpty(itemPath) &&
                                         weights.ContainsKey(itemPath);
                        if (weights.Count == 0 || fileKnown)
                            itemsProcessed++;

                        string prefix = status switch
                        {
                            'U' => "= Updated",
                            'A' => "+ Added",
                            'D' => "- Deleted",
                            'C' => "x Conflict",
                            'G' => "~ Merged",
                            'R' => "~ Replaced",
                            _ => $"  {status}"
                        };
                        string displayLine = $"{prefix}: {itemPath}";

                        string progressStr = "";
                        if (itemTotal > 0)
                        {
                            if (itemsProcessed > itemTotal)
                                progressStr = $" ({itemTotal}/{itemTotal}, +{itemsProcessed - itemTotal} extra)";
                            else
                                progressStr = $" ({itemsProcessed}/{itemTotal})";
                        }

                        // === plik raportowany TYLKO przez tracker (linia 2 widoku 2,
                        // format identyczny z update) — koniec dublowania ścieżek.
                        _progress?.OnItem(itemPath, displayLine, progressStr, status == 'C');
                    }
                    else
                    {
                        // linie nie-plikowe (komunikaty svn) — białym strumieniem jak dotychczas
                        logBuffer.Enqueue(cleanLine);
                    }

                }, token).ConfigureAwait(false);

                if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                bool hasWorkingCopy = Directory.Exists(Path.Combine(path, ".svn"));

                bool hasError = Interlocked.CompareExchange(ref sawError, 0, 0) == 1 ||
                                (!string.IsNullOrWhiteSpace(result) &&
                                 result.Contains("svn: E", StringComparison.OrdinalIgnoreCase));

                if (isExport)
                {
                    if (hasError)
                    {
                        _progress?.Clear();
                        lock (_stateLock) { _state = OperationState.Failed; }
                        PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                            "<color=#FFAA00><b>Export Failed</b></color>\nCheck console for details.", "SVN"));
                        return;
                    }
                }
                else if (!hasWorkingCopy || hasError)
                {
                    _progress?.Clear();
                    lock (_stateLock) { _state = OperationState.Failed; }

                    // === DOLNY PASEK: porażka → Idle z danym snapshotem
                    barModule?.EndUpdateFailed(svnManager.CurrentSnapshot);

                    PostToMainThread(() => SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        "<color=#FFAA00><b>Operation Failed</b></color>\nCheck console for details.", "SVN"));
                    return;
                }

                lock (_stateLock) { _state = OperationState.Completed; }
                ClearPausedState();

                _progress?.Finish();   // === sukces: snap do 100%

                // === DOLNY PASEK: koniec — świeży snapshot (resetuje też incomplete).
                if (!isExport && barModule != null)
                {
                    var snap = svnManager.CurrentSnapshot ?? new SVNProjectInfoSnapshot
                    {
                        ProjectName = Path.GetFileName(path.TrimEnd('/', '\\')),
                        IsValid = true
                    };
                    snap.IsValid = true;
                    svnManager.CurrentSnapshot = snap;
                    await barModule.EndCheckout(snap).ConfigureAwait(false);
                }

                var elapsed = DateTime.Now - startTime;
                long finalSize = GetDirectorySizeFast(path);
                long downloadedBytes = Math.Max(0, finalSize - sizeBeforeSession);
                double avgSpeedMB = (downloadedBytes / BytesInMB) / Math.Max(elapsed.TotalSeconds, 1);

                int finalAdded = addedCount;
                int finalUpdated = updatedCount;
                int finalConflicts = conflictCount;

                PostToMainThread(() =>
                {
                    var report = new StringBuilder(512);
                    report.AppendLine();
                    report.AppendLine($"<color=green><b>=========================================</b></color>");
                    report.AppendLine($"<color=green><b>     {operationType.ToUpper()} COMPLETED</b></color>");
                    report.AppendLine($"<color=green><b>=========================================</b></color>");
                    report.AppendLine($"Items added:  <b>{finalAdded}</b>");
                    if (finalUpdated > 0)
                        report.AppendLine($"Updated:      <b>{finalUpdated}</b>");
                    report.AppendLine($"Disk usage:   <b>{FormatSize(finalSize)}</b>");
                    report.AppendLine($"Downloaded:   <b>{FormatSize(downloadedBytes)}</b>");
                    report.AppendLine($"Duration:     <b>{elapsed.TotalSeconds:F1}s</b>");
                    report.AppendLine($"Avg speed:    <b>{avgSpeedMB:F2} MB/s</b>");
                    if (finalConflicts > 0)
                        report.AppendLine($"<color=#FFAA00><b>Conflicts: {finalConflicts}</b></color>");
                    report.AppendLine($"<color=green><b>=========================================</b></color>");

                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, report.ToString(), "SVN");

                    SVNLogBridge.LogCheckoutConsole($"<color=green><b>[{operationType}]</b> Finished. {finalAdded} items, {elapsed.TotalSeconds:F1}s</color>\n");

                    if (operationType != "Exporting")
                        SVNManager.Instance?.ProjectSelectionPanel?.RefreshList();
                });

                SVNLogBridge.LogLine($"<color=green><b>[{operationType}]</b> Finished. {finalAdded} items, {elapsed.TotalSeconds:F1}s</color>");

                if (SVNManager.Instance != null)
                {
                    var pollingService = SVNManager.Instance.GetComponent<SVNPollingService>();
                    if (pollingService != null) pollingService.ResetRevisionTracking();
                }

                if (!isExport)
                {
                    var activeProject = new SVNProject
                    {
                        projectName = Path.GetFileName(path.TrimEnd('/', '\\')),
                        repoUrl = url,
                        workingDir = path,
                        privateKeyPath = keyPath ?? _resolvedKeyPath,
                        lastOpened = DateTime.Now
                    };
                    SVNManager.Instance?.SetActiveProject(activeProject);
                    RegisterProjectInList(path, url, keyPath ?? _resolvedKeyPath);
                }
            }
            catch (OperationCanceledException)
            {
                var elapsed = DateTime.Now - startTime;
                int finalAdded = addedCount;
                long diskSize = GetDirectorySizeFast(path);

                bool isPaused;
                lock (_stateLock)
                {
                    isPaused = (_state == OperationState.Pausing);
                    _state = isPaused ? OperationState.Paused : OperationState.Cancelled;
                }

                // === FIX (przerwany checkout ginął): rejestracja projektu PRZY
                // PRZERWANIU, nie tylko po sukcesie.
                if (!isExport && Directory.Exists(Path.Combine(path, ".svn")))
                {
                    RegisterProjectInList(path, url, keyPath ?? _resolvedKeyPath);

                    SVNLogBridge.LogToOutput(
                        $"<color=yellow>[SVN] Interrupted checkout saved as project: <b>{Path.GetFileName(path.TrimEnd('/', '\\'))}</b>. " +
                        "Use Resume in the Checkout panel to finish downloading.</color>");
                }

                if (!isPaused)
                {
                    ClearPausedState();
                }

                // === WIDOK 2, ostatnia linia: jawny "incomplete — Resume", ale tylko
                // gdy .svn istnieje (jest co wznawiać); wczesne przerwanie → Clear.
                if (Directory.Exists(Path.Combine(path, ".svn")))
                    _progress?.FinishIncomplete(isPaused ? "PAUSED" : "CANCELLED");
                else
                    _progress?.Clear();

                // === DOLNY PASEK: przerwany checkout → Idle; przy następnym
                // BuildSnapshot check '!' doda badge "Incomplete checkout".
                if (!isExport)
                {
                    barModule?.EndUpdateFailed(svnManager.CurrentSnapshot);
                }

                string statusMsg = isPaused ? "PAUSED" : "CANCELLED";

                PostToMainThread(() =>
                {
                    var report = new StringBuilder(256);
                    report.AppendLine();
                    report.AppendLine($"<color=#FFAA00><b>=========================================</b></color>");
                    report.AppendLine($"<color=#FFAA00><b>     OPERATION {statusMsg}</b></color>");
                    report.AppendLine($"<color=#FFAA00><b>=========================================</b></color>");
                    report.AppendLine($"Items downloaded: <b>{finalAdded}</b>");
                    report.AppendLine($"Duration:         <b>{elapsed.TotalSeconds:F1}s</b>");
                    report.AppendLine($"Disk preserved:   <b>{FormatSize(diskSize)}</b>");
                    report.AppendLine($"<color=#FFAA00><b>=========================================</b></color>");

                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, report.ToString(), "SVN");

                    SVNLogBridge.LogCheckoutConsole($"<color=#FFAA00><b>[{operationType}]</b> {statusMsg}. {finalAdded} items, {elapsed.TotalSeconds:F1}s</color>\n");
                });
            }
            catch (Exception ex)
            {
                lock (_stateLock) { _state = OperationState.Failed; }

                // === WIDOK 2: linia niekompletności tylko gdy .svn istnieje (resumable).
                if (Directory.Exists(Path.Combine(path, ".svn")))
                    _progress?.FinishIncomplete("FAILED");
                else
                    _progress?.Clear();

                // === DOLNY PASEK: błąd → Idle z danym snapshotem.
                if (!isExport)
                {
                    barModule?.EndUpdateFailed(svnManager.CurrentSnapshot);
                }

                PostToMainThread(() =>
                {
                    SVNLogBridge.LogCheckoutConsole($"\n<color=#FF4444><b>ERROR:</b> {ex.Message}</color>\n\n");
                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                        $"<color=#FFAA00>Error: {ex.Message}</color>", "SVN");
                });

                SVNLogBridge.LogErrorToOutput($"[SVN] Operation failed:\n{ex}");
            }
            finally
            {
                try { cts?.Cancel(); } catch { }
                try { if (logFlushTask != null) await logFlushTask.ConfigureAwait(false); } catch { }
                try { if (monitorTask != null) await monitorTask.ConfigureAwait(false); } catch { }
                FlushLogBuffer(logBuffer);

                // === FIX (kolejność teardown): najpierw ODPIĘCIE _checkoutCTS i
                // zwolnienie guardu (End), dopiero potem Dispose.
                _checkoutCTS = null;
                End();
                cts?.Dispose();

                lock (_stateLock) { if (_state != OperationState.Paused) _state = OperationState.Idle; }
            }
        }

        public async void ExportRepository()
        {
            try { await ExportRepositoryAsync().ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        private async Task ExportRepositoryAsync()
        {
            // === FIX: Export nie miał debounce ani sprawdzenia IsProcessing.
            if (!CanStartOperation()) return;

            if (!TryValidateExportCommon(out string url, out string fullPath, out string keyPath, out string errorMsg))
            {
                if (!string.IsNullOrEmpty(errorMsg)) ShowError(errorMsg);
                return;
            }

            lock (_stateLock) { _canResume = false; }

            string sshConfig = BuildSshConfigOption(keyPath);
            string exportArgs = $"export \"{url}\" \"{fullPath}\" --force --non-interactive --trust-server-cert" + FormatSshConfig(sshConfig);
            await ExecuteSvnOperationAsync(url, fullPath, exportArgs, false, keyPath, "Exporting").ConfigureAwait(false);
        }

        public async void ExportRevision(string revision)
        {
            try { await ExportRevisionAsync(revision).ConfigureAwait(false); }
            catch (Exception ex) { HandleOperationException(ex); }
        }

        public async Task ExportRevisionAsync(string revision)
        {
            // === FIX: jw. — debounce dla exportu rewizji.
            if (!CanStartOperation()) return;

            if (!TryValidateExportCommon(out string url, out string fullPath, out string keyPath, out string errorMsg))
            {
                if (!string.IsNullOrEmpty(errorMsg)) ShowError(errorMsg);
                return;
            }

            lock (_stateLock) { _canResume = false; }

            string revArg = string.IsNullOrWhiteSpace(revision) ? "" : $" -r {revision}";
            string sshConfig = BuildSshConfigOption(keyPath);
            string exportArgs = $"export{revArg} \"{url}\" \"{fullPath}\" --force --non-interactive --trust-server-cert" + FormatSshConfig(sshConfig);
            await ExecuteSvnOperationAsync(url, fullPath, exportArgs, false, keyPath, "Exporting").ConfigureAwait(false);
        }

        private bool TryValidateExportCommon(out string url, out string fullPath, out string keyPath, out string errorMsg)
        {
            url = null;
            fullPath = null;
            keyPath = null;
            errorMsg = null;

            url = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(path))
            {
                errorMsg = "Please enter both Repository URL and Destination Folder in the Checkout panel.";
                SVNLogBridge.LogLine("<color=#FFAA00>Export: Both URL and destination folder must be provided.</color>");
                return false;
            }

            if (!IsValidSvnUrl(url))
            {
                errorMsg = "Invalid SVN URL.";
                SVNLogBridge.LogLine("<color=#FFAA00>Export: Invalid SVN URL.</color>");
                return false;
            }

            if (!TryValidatePath(path, out fullPath)) return false;

            if (Directory.Exists(fullPath))
            {
                if (Directory.GetFileSystemEntries(fullPath).Length > 0)
                {
                    errorMsg = $"Destination folder is not empty: {fullPath}\nPlease choose an empty or non-existent folder.";
                    SVNLogBridge.LogLine($"<color=#FFAA00>{errorMsg}</color>");
                    return false;
                }
            }

            keyPath = ResolveAndCacheKeyPath();
            if (url.StartsWith("svn+ssh://", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(keyPath))
            {
                errorMsg = "SSH repository requires a valid private key.";
                SVNLogBridge.LogLine("<color=#FFAA00>Export: SSH key required but not provided.</color>");
                return false;
            }

            return true;
        }

        private bool CanStartOperation()
        {
            lock (_stateLock)
            {
                double elapsed = (DateTime.Now - _lastStartAttempt).TotalMilliseconds;
                if (elapsed < DebounceIntervalMs)
                {
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Please wait...");
                    return false;
                }
                _lastStartAttempt = DateTime.Now;

                if (IsProcessing)
                {
                    SVNLogBridge.LogToOutput("<color=yellow>[SVN]</color> Another operation is already running.");
                    return false;
                }
                return true;
            }
        }

        private void FlushLogBuffer(ConcurrentQueue<string> logBuffer)
        {
            if (logBuffer == null || logBuffer.IsEmpty) return;
            var lines = new List<string>();
            while (logBuffer.TryDequeue(out string line))
                lines.Add(line); // === FIX: usunięta bezsensowna interpolacja $"{line}"
            if (lines.Count == 0) return;
            string text = string.Join("\n", lines) + "\n";
            PostToMainThread(() => SVNLogBridge.LogCheckoutConsole(text));
        }

        private long GetDirectorySizeFast(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return 0;
            return CalculateDirectorySizeSafe(new DirectoryInfo(folderPath));
        }

        private long CalculateDirectorySizeSafe(DirectoryInfo directory)
        {
            long size = 0;
            try
            {
                // === FIX: EnumerationOptions.IgnoreInaccessible.
                var options = new EnumerationOptions { IgnoreInaccessible = true };

                foreach (FileInfo file in directory.EnumerateFiles("*", options))
                {
                    try { size += file.Length; }
                    catch { }
                }

                foreach (DirectoryInfo subDir in directory.EnumerateDirectories("*", options))
                {
                    size += CalculateDirectorySizeSafe(subDir);
                }
            }
            catch { }

            return size;
        }

        private void RegisterProjectInList(string path, string url, string keyPath)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // === S1: atomowe AddOrUpdate (koniec Load→Find→Save)
            ProjectSettings.AddOrUpdateProject(path, (p, created) =>
            {
                if (created)
                    p.projectName = GetRepoNameFromUrl(url);

                p.repoUrl = url;
                p.privateKeyPath = keyPath;
                p.lastOpened = DateTime.Now;
            });

            string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
            PlayerPrefs.Save();
        }

        private string GetRepoNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Repository";
            url = url.TrimEnd('/');
            if (url.EndsWith("/trunk", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - "/trunk".Length);
            if (url.EndsWith("/branches", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - "/branches".Length);
            if (url.EndsWith("/tags", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - "/tags".Length);
            int slash = url.LastIndexOf('/');
            return slash >= 0 && slash < url.Length - 1 ? url.Substring(slash + 1) : url;
        }

        // === FIX: wołane po awaitach (pula wątków) — PlayerPrefs przez SVNPrefs.
        private void SavePausedState(string path, string url, string keyPath)
        {
            SVNPrefs.SetString("SVN_CheckoutPaused_Path", path ?? "");
            SVNPrefs.SetString("SVN_CheckoutPaused_Url", url ?? "");
            SVNPrefs.SetString("SVN_CheckoutPaused_KeyPath", keyPath ?? "");
        }

        private void ClearPausedState()
        {
            SVNPrefs.DeleteKey("SVN_CheckoutPaused_Path");
            SVNPrefs.DeleteKey("SVN_CheckoutPaused_Url");
            SVNPrefs.DeleteKey("SVN_CheckoutPaused_KeyPath");
        }

        private bool TryRestorePausedState(string currentPath, string currentUrl)
        {
            string savedPath = PlayerPrefs.GetString("SVN_CheckoutPaused_Path", "");
            string savedUrl = PlayerPrefs.GetString("SVN_CheckoutPaused_Url", "");

            if (string.IsNullOrEmpty(savedPath) || string.IsNullOrEmpty(savedUrl))
                return false;

            string normSavedPath = savedPath.Replace("\\", "/").TrimEnd('/');
            string normCurrentPath = currentPath.Replace("\\", "/").TrimEnd('/');
            string normSavedUrl = savedUrl.TrimEnd('/');
            string normCurrentUrl = currentUrl.TrimEnd('/');

            return string.Equals(normSavedPath, normCurrentPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(normSavedUrl, normCurrentUrl, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// === FIX (WAGI): svn checkout/export wypisuje ścieżki z prefiksem —
        /// nazwą folderu docelowego (relatywnie do CWD) lub pełną ścieżką.
        /// Klucze mapy wag są relatywne do URL — bez zdjęcia prefiksu pasek
        /// stał na 0%. Resume (update w WC) ma ścieżki już relatywne — nic nie zdejmujemy.
        /// </summary>
        private static string StripCheckoutItemPath(string rawPath, string destFullPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;

            string p = rawPath.Replace('\\', '/').Trim().Trim('"');
            if (p.Length == 0) return p;

            string full = (destFullPath ?? "").Replace('\\', '/').TrimEnd('/');

            // 1) pełna ścieżka bezwzględna (CWD != parent dest, np. temp)
            if (full.Length > 0 && p.StartsWith(full + "/", StringComparison.OrdinalIgnoreCase))
                return p.Substring(full.Length + 1);
            if (full.Length > 0 && string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
                return "";

            // 2) prefiks nazwą folderu dest (typowe: CWD = parent)
            string name = Path.GetFileName(full);
            if (!string.IsNullOrEmpty(name) && p.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase))
                return p.Substring(name.Length + 1);

            // 3) już relatywna
            return p;
        }

    }
}