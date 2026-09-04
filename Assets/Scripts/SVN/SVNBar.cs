using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public enum BarState
    {
        Idle,
        Updating,
        PostUpdate
    }

    public class SVNBar : SVNBase
    {
        private string _svnVersionCached = "";
        private Task<string> _svnVersionTask;
        private readonly Dictionary<string, (DateTime time, string working, string total)> _sizeCache = new();
        private readonly object _sizeCacheLock = new object();

        // === FIX NESTED: cache korzenia working copy (klucz = ścieżka wejściowa,
        // więc zmiana projektu sama unieważnia wpis)
        private (string path, string root) _wcRootCache;

        // === CHECKOUT NIEKOMPLETNY: true, gdy wc ma pozycje '!' (missing —
        // przerwany checkout). Czyszczone po udanym update (nowy snapshot
        // i tak przelicza flagę).
        private volatile bool _checkoutIncomplete;

        private CancellationTokenSource _snapshotCts;

        private volatile BarState _state = BarState.Idle;
        private volatile string _desiredContent = "";
        private volatile string _lastRenderedContent = "";
        private readonly object _contentLock = new object();
        private CancellationTokenSource _monitorCts;

        public BarState CurrentState => _state;

        public SVNBar(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
        }

        public void Tick()
        {
            if (svnUI?.StatusInfoText == null) return;

            string content;
            lock (_contentLock)
            {
                content = _desiredContent;
            }

            if (content != _lastRenderedContent)
            {
                svnUI.StatusInfoText.text = content;
                _lastRenderedContent = content;
            }
        }

        private void SetContent(string content, string source)
        {
            lock (_contentLock)
            {
                if (_desiredContent != content)
                {
                    _desiredContent = content;
                }
            }
        }

        public void SetLoadingContent(string projectName)
        {
            string content =
                $"<size=150%><color=#FFFF00>●</color></size> " +
                $"<color=orange><b>{projectName}</b></color> | " +
                $"<color=#FFFF00>Loading project...</color>";

            SetContent(content, "SetLoadingContent");
        }

        public void ShowNoWorkingCopy(string projectName = null)
        {
            string name = string.IsNullOrWhiteSpace(projectName)
                ? ""
                : $"<color=#888888><b>{projectName}</b></color> | ";
            string content =
                $"<size=150%><color=black>●</color></size> " +
                $"{name}<color=#888888>No working copy</color>";

            SetContent(content, "ShowNoWorkingCopy");
        }

        public async Task ShowProjectInfo(
            SVNProject svnProject,
            string path,
            bool forceOutdatedCheck = false,
            bool isRefreshing = false)
        {
            if (_state != BarState.Idle) return;

            _snapshotCts?.Cancel();
            _snapshotCts?.Dispose();
            _snapshotCts = new CancellationTokenSource();
            var localToken = _snapshotCts.Token;

            try
            {
                var snapshot = await BuildSnapshotAsync(svnProject, path, localToken);

                if (localToken.IsCancellationRequested) return;
                if (_state != BarState.Idle) return;

                if (snapshot == null || !snapshot.IsValid)
                {
                    ShowNoWorkingCopy(svnProject?.projectName ?? Path.GetFileName(path));
                    return;
                }

                svnManager.CurrentSnapshot = snapshot;
                SetContent(BuildNormalContent(snapshot, isRefreshing), "ShowProjectInfo");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogError($"[SVNBar] ShowProjectInfo failed: {ex.Message}");
                ShowNoWorkingCopy(svnProject?.projectName ?? Path.GetFileName(path));
            }
        }

        public void RenderSnapshot(SVNProjectInfoSnapshot snapshot, bool isRefreshing = false)
        {
            if (_state != BarState.Idle) return;
            if (snapshot == null || !snapshot.IsValid) return;

            SetContent(BuildNormalContent(snapshot, isRefreshing), "RenderSnapshot");
        }

        public void RenderFromSnapshot(SVNProjectInfoSnapshot snapshot)
        {
            RenderSnapshot(snapshot);
        }

        public void BeginUpdate(string projectName)
        {
            _state = BarState.Updating;

            if (svnManager.CurrentSnapshot == null)
            {
                svnManager.CurrentSnapshot = new SVNProjectInfoSnapshot
                {
                    ProjectName = projectName,
                    WorkingCopySize = "...",
                    RepoTotalSize = "...",
                    IsValid = true
                };
            }

            string size = svnManager.CurrentSnapshot?.WorkingCopySize ?? "...";
            string total = svnManager.CurrentSnapshot?.RepoTotalSize ?? "...";
            SetContent(BuildUpdatingContent(projectName, size, total), "BeginUpdate");

            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();

            _ = RunSizeMonitor(svnManager.WorkingDir, _monitorCts.Token);
        }

        public async Task EndUpdate(SVNProjectInfoSnapshot fallbackSnapshot)
        {
            _monitorCts?.Cancel();

            _state = BarState.Idle;

            // Rozmiary na dysku mogły się zmienić po update — wymuś świeży pomiar
            // (bez tego cache 10s zwróciłby stare wartości).
            ClearSizeCache();

            // === CHECKOUT NIEKOMPLETNY: update doszedł do końca — reset flagi
            // (nowy snapshot poniżej i tak przeliczy ją z 'svn status').
            _checkoutIncomplete = false;

            bool renderedFresh = false;
            try
            {
                var freshSnapshot = await BuildSnapshotAsync(null, svnManager.WorkingDir);

                if (freshSnapshot != null && freshSnapshot.IsValid)
                {
                    // === FIX: nazwa projektu z fallbacku ZAWSZE, nie warunkowo.
                    if (fallbackSnapshot != null && !string.IsNullOrEmpty(fallbackSnapshot.ProjectName))
                        freshSnapshot.ProjectName = fallbackSnapshot.ProjectName;

                    svnManager.CurrentSnapshot = freshSnapshot;

                    _lastRenderedContent = "";
                    SetContent(BuildNormalContent(freshSnapshot, isRefreshing: false), "EndUpdate-Fresh");
                    renderedFresh = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SVNBar] Pobranie świeżych danych po update nie powiodło się (używam fallbacku): {ex.Message}");
            }

            if (!renderedFresh && fallbackSnapshot != null)
            {
                fallbackSnapshot.IsValid = true;
                svnManager.CurrentSnapshot = fallbackSnapshot;
                try
                {
                    string wcRoot = await GetWorkingCopyRootAsync(svnManager.WorkingDir);
                    var sizes = await GetSizesForNestedAsync(svnManager.WorkingDir, wcRoot);
                    fallbackSnapshot.WorkingCopySize = sizes.WorkingSize;
                    fallbackSnapshot.RepoTotalSize = sizes.TotalSize;
                }
                catch { }

                _lastRenderedContent = "";
                SetContent(BuildNormalContent(fallbackSnapshot, isRefreshing: false), "EndUpdate-Fallback");
            }
        }

        public void EndUpdateFailed(SVNProjectInfoSnapshot oldSnapshot)
        {
            _monitorCts?.Cancel();
            _state = BarState.Idle;

            if (oldSnapshot != null)
            {
                svnManager.CurrentSnapshot = oldSnapshot;
                _lastRenderedContent = "";
                SetContent(BuildNormalContent(oldSnapshot, isRefreshing: false), "EndUpdateFailed");
            }
        }

        private async Task RunSizeMonitor(string path, CancellationToken token)
        {
            try
            {
                await Task.Delay(400, token);

                // === FIX NESTED: total liczymy z korzenia WC (cache wewnętrzny
                // GetWorkingCopyRootAsync — tylko pierwsze wywołanie spawnuje proces svn)
                string wcRoot = await GetWorkingCopyRootAsync(path, token);

                while (!token.IsCancellationRequested && _state == BarState.Updating)
                {
                    try
                    {
                        var sizes = await GetSizesForNestedAsync(path, wcRoot, token);

                        if (token.IsCancellationRequested || _state != BarState.Updating) break;

                        var snapshot = svnManager.CurrentSnapshot;
                        if (snapshot != null && snapshot.IsValid)
                        {
                            bool changed = snapshot.WorkingCopySize != sizes.WorkingSize ||
                                           snapshot.RepoTotalSize != sizes.TotalSize;
                            snapshot.WorkingCopySize = sizes.WorkingSize;
                            snapshot.RepoTotalSize = sizes.TotalSize;

                            if (changed)
                            {
                                SetContent(BuildUpdatingContent(snapshot.ProjectName, sizes.WorkingSize, sizes.TotalSize), "Monitor");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    try { await Task.Delay(5000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private string BuildUpdatingContent(string projectName, string workingSize, string totalSize)
        {
            var snapshot = svnManager.CurrentSnapshot;
            string user = snapshot?.CurrentUser ?? "...";
            string branch = snapshot?.Branch ?? "...";
            string rev = snapshot?.Revision ?? "...";
            string svnVer = snapshot?.SvnVersion ?? "...";

            return
                $"<size=150%><color=#FFFF00>●</color></size> " +
                $"<color=orange><b>{projectName}</b> ({FormatSizeDisplay(workingSize, totalSize)})</color> | " +
                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{user}</color> | " +
                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{branch}</color> | " +
                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{rev}</color> | " +
                $"<color=#E6E6E6>SVN: {svnVer}</color>" +
                " | <color=#FFFF00>Updating...</color>";
        }

        private string BuildNormalContent(SVNProjectInfoSnapshot snapshot, bool isRefreshing)
        {
            var state = svnManager.OperationInfo;
            bool isCanceled = state.State == SVNOperationState.Canceled;
            bool isFailed = state.State == SVNOperationState.Failed;

            string statusColor = "#4ca74c";
            if (isRefreshing) statusColor = "#FFFF00";
            else if (isCanceled) statusColor = "#FFAA00";
            else if (isFailed) statusColor = "#FF1A1A";
            else if (snapshot.IsOutdated) statusColor = "#FF1A1A";

            string shortDate = snapshot.Date != "unknown"
                ? snapshot.Date.Split('(')[0].Trim()
                : "no dates";

            string revDisplay = snapshot.IsOutdated
                ? $"<color=#FF4444>{snapshot.Revision}</color> <color=#FF8888>(HEAD: {snapshot.RemoteRevision})</color>"
                : snapshot.Revision;

            string statusSuffix = "";
            if (isCanceled) statusSuffix = " | Update Canceled";
            else if (isFailed) statusSuffix = " | Update Interrupted";

            return
                $"<size=150%><color={statusColor}>●</color></size> " +
                $"<color=orange><b>{snapshot.ProjectName}</b> ({FormatSizeDisplay(snapshot.WorkingCopySize, snapshot.RepoTotalSize)})</color> | " +
                $"<color=#00E5FF>User:</color> <color=#E6E6E6>{snapshot.CurrentUser}</color> | " +
                $"<color=#00E5FF>Branch:</color> <color=#E6E6E6>{snapshot.Branch}</color> | " +
                $"<color=#00E5FF>Rev:</color> <color=#E6E6E6>{revDisplay}</color> | " +
                $"<color=#00E5FF>By:</color> <color=#E6E6E6>{snapshot.Author}</color> | " +
                $"<color=#E6E6E6>{shortDate}</color> | " +
                $"<color=#E6E6E6>Srv: {snapshot.Server}</color> | " +
                $"<color=#E6E6E6>App: {snapshot.AppVersion}</color> | " +
                $"<color=#E6E6E6>SVN: {snapshot.SvnVersion}</color>" +
                statusSuffix;
        }

        public async Task StartLightSizeMonitor(string path, CancellationToken token)
        {
            await Task.CompletedTask;
        }

        public async Task StartLiveSizeMonitor(string path, CancellationToken token)
        {
            await Task.CompletedTask;
        }

        public void ShowUpdatingStatus(string projectName)
        {
        }

        public void ResetMonitorState()
        {
        }

        public void ForceRenderAfterUpdate(SVNProjectInfoSnapshot snapshot)
        {
        }

        public void ClearSizeCache()
        {
            lock (_sizeCacheLock) _sizeCache.Clear();
        }

        // ==================== ROZMIARY ====================

        /// <summary>
        /// Liczy oba rozmiary w JEDNYM przebiegu po dysku:
        /// - WorkingSize: pliki projektu (bez katalogów .svn)
        /// - TotalSize:   całość na dysku (razem z .svn/pristine — zwykle ~2x więcej)
        /// </summary>
        public async Task<(string WorkingSize, string TotalSize)> GetFolderSizesAsync(
            string path, CancellationToken token = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(path)) return ("0 MB", "0 MB");

                    long workingBytes = 0; // pliki projektu (bez .svn)
                    long totalBytes = 0;   // całość na dysku (razem z .svn)
                    int fileCount = 0;

                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.System
                    };

                    foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", options))
                    {
                        try
                        {
                            totalBytes += fi.Length;
                            if (!SvnPathUtils.IsInsideSvnAdminDir(fi.FullName))
                                workingBytes += fi.Length;
                        }
                        catch { }

                        if ((++fileCount & 0x1F) == 0)
                            token.ThrowIfCancellationRequested();
                    }

                    return (SvnPathUtils.FormatBytes(workingBytes), SvnPathUtils.FormatBytes(totalBytes));
                }
                catch (OperationCanceledException) { throw; }
                catch { return ("Size unknown", "Size unknown"); }
            }, token);
        }

        // Zachowana stara sygnatura — includeSvnTemp teraz faktycznie działa
        public async Task<string> GetFolderSizeAsync(string path, CancellationToken token = default, bool includeSvnTemp = false)
        {
            var sizes = await GetFolderSizesAsync(path, token);
            return includeSvnTemp ? sizes.TotalSize : sizes.WorkingSize;
        }

        /// <summary>
        /// Korzeń working copy (svn info --show-item wc-root, wymaga SVN 1.8+).
        /// Fallback dla starszych SVN: szukanie .svn w górę drzewa.
        /// Cache per ścieżka wejściowa (pojedynczy wpis).
        /// </summary>
        public async Task<string> GetWorkingCopyRootAsync(string path, CancellationToken token = default)
        {
            if (!string.IsNullOrEmpty(_wcRootCache.path) &&
                string.Equals(_wcRootCache.path, path, StringComparison.OrdinalIgnoreCase) &&
                _wcRootCache.root != null)
                return _wcRootCache.root;

            string root = null;
            try
            {
                string raw = await SvnRunner.RunAsync("info --show-item wc-root", path, token: token);
                if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("Error"))
                    root = raw.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (root == null)
                root = SvnPathUtils.FindWorkingCopyRoot(path) ?? path;

            _wcRootCache = (path, root);
            return root;
        }

        /// <summary>
        /// Rozmiary dla projektu ZAGNIEŻDŻONEGO w większym checkoucie:
        /// - WorkingSize: pliki folderu projektu
        /// - TotalSize:   cały korzeń WC (koszt dyskowy repo — obejmuje też
        ///                inne projekty z tego checkoutu — zamierzone)
        /// Projekt == korzeń: jeden skan zwraca oba.
        /// </summary>
        public async Task<(string WorkingSize, string TotalSize)> GetSizesForNestedAsync(
            string projectPath, string wcRoot, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(wcRoot) ||
                string.Equals(projectPath, wcRoot, StringComparison.OrdinalIgnoreCase))
            {
                return await GetFolderSizesAsync(projectPath, token);
            }

            // Skany równoległe — dysk i tak jest wąskim gardłem
            var workingTask = GetFolderSizesAsync(projectPath, token);
            var totalTask = GetFolderSizesAsync(wcRoot, token);

            try { await Task.WhenAll(workingTask, totalTask); }
            catch { /* OCE — poniżej degradujemy do "Size unknown" */ }

            string working = workingTask.Status == TaskStatus.RanToCompletion
                ? workingTask.Result.WorkingSize : "Size unknown";
            string total = totalTask.Status == TaskStatus.RanToCompletion
                ? totalTask.Result.TotalSize : "Size unknown";

            return (working, total);
        }

        /// <summary>
        /// Rozmiary z cache (10 s) — dla wywołań zewnętrznych (SVNManager.RefreshStatus),
        /// które wcześniej robiły pełny skan bez cache i aktualizowały tylko WorkingCopySize.
        /// </summary>
        public async Task<(string WorkingSize, string TotalSize)> GetSizesWithCacheAsync(
            string path, CancellationToken token = default)
        {
            string cacheKey = path.Replace("\\", "/").TrimEnd('/');

            lock (_sizeCacheLock)
            {
                if (_sizeCache.TryGetValue(cacheKey, out var entry) &&
                    (DateTime.UtcNow - entry.time).TotalSeconds < 10)
                {
                    return (entry.working, entry.total);
                }
            }

            string wcRoot = await GetWorkingCopyRootAsync(path, token);
            var sizes = await GetSizesForNestedAsync(path, wcRoot, token);

            if (sizes.WorkingSize != "Size unknown")
                lock (_sizeCacheLock) { _sizeCache[cacheKey] = (DateTime.UtcNow, sizes.WorkingSize, sizes.TotalSize); }

            return sizes;
        }

        // Format na pasek: "12.30 GB / 24.87 GB" (total wyszarzony)
        private static string FormatSizeDisplay(string workingSize, string totalSize)
        {
            if (string.IsNullOrWhiteSpace(totalSize) ||
                totalSize == "unknown" || totalSize == "Size unknown")
                return workingSize ?? "...";

            return $"{workingSize} / <color=#999999>{totalSize}</color>";
        }

        // ==================== CHECKOUT NIEKOMPLETNY ====================

        /// <summary>
        /// Czy working copy ma pozycje '!' (missing) — czyli przerwany/nieukończony
        /// checkout. Tani, LOKALNY svn status (bez -u, bez sieci).
        /// </summary>
        private async Task<bool> CheckCheckoutIncompleteAsync(string path, CancellationToken token = default)
        {
            try
            {
                string status = await SvnRunner.RunAsync("status", path, token: token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(status)) return false;

                foreach (var rawLine in status.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue;

                    // format: "!      path" — status w kolumnie 0
                    if (line.Length > 0 && line[0] == '!') return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            return false;
        }

        // ==================================================

        private string ExtractValue(string info, string key)
        {
            if (string.IsNullOrEmpty(info)) return "unknown";

            int keyIndex = info.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyIndex == -1) return "unknown";

            int valueStart = keyIndex + key.Length;
            while (valueStart < info.Length && char.IsWhiteSpace(info[valueStart])) valueStart++;

            int valueEnd = info.IndexOf('\n', valueStart);
            if (valueEnd == -1) valueEnd = info.Length;

            return info.Substring(valueStart, valueEnd - valueStart).Trim();
        }

        private async Task EnsureVersionCached()
        {
            if (_svnVersionTask == null)
            {
                _svnVersionTask = SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir)
                    .ContinueWith(t => t.IsFaulted ? "?.?.?" : (t.Result ?? "?.?.?").Trim(),
                        TaskContinuationOptions.ExecuteSynchronously);
            }
            _svnVersionCached = await _svnVersionTask;
        }

        public async Task<SVNProjectInfoSnapshot> BuildSnapshotAsync(SVNProject svnProject, string path, CancellationToken token = default)
        {
            var snapshot = new SVNProjectInfoSnapshot();
            try
            {
                if (string.IsNullOrEmpty(path)) return snapshot;

                // === FIX NESTED: .svn istnieje TYLKO w korzeniu working copy (SVN 1.7+).
                // Wcześniej projekt w podfolderze większego checkoutu dawał false
                // "No working copy" mimo poprawnego WC.
                if (!SvnPathUtils.IsInsideWorkingCopy(path)) return snapshot;

                string projectName = svnProject != null && !string.IsNullOrEmpty(svnProject.projectName)
                    ? svnProject.projectName : Path.GetFileName(path);

                var infoTask = SvnRunner.GetInfoAsync(path, token);
                var remoteRevTask = SvnRunner.RunAsync("info -r HEAD --show-item last-changed-revision", path, token: token);

                string cacheKey = path.Replace("\\", "/").TrimEnd('/');
                string cachedWorking = null;
                string cachedTotal = null;

                lock (_sizeCacheLock)
                {
                    var now = DateTime.UtcNow;
                    var expiredKeys = _sizeCache.Where(kvp => (now - kvp.Value.time).TotalSeconds > 10)
                        .Select(kvp => kvp.Key).ToList();
                    foreach (var key in expiredKeys) _sizeCache.Remove(key);
                    if (_sizeCache.TryGetValue(cacheKey, out var entry) && (now - entry.time).TotalSeconds < 10)
                    {
                        cachedWorking = entry.working;
                        cachedTotal = entry.total;
                    }
                }

                Task<(string WorkingSize, string TotalSize)> sizeTask;
                if (cachedWorking != null)
                {
                    sizeTask = Task.FromResult((cachedWorking, cachedTotal));
                }
                else
                {
                    // === FIX NESTED: total liczony z KORZENIA working copy,
                    // nie z folderu projektu
                    string wcRoot = await GetWorkingCopyRootAsync(path, token);
                    sizeTask = GetSizesForNestedAsync(path, wcRoot, token);
                }

                string rawInfo = await infoTask;
                var sizes = await sizeTask;

                string remoteRevRaw = null;
                try { remoteRevRaw = await remoteRevTask; } catch { }

                if (!string.IsNullOrEmpty(sizes.WorkingSize) && cachedWorking == null)
                    lock (_sizeCacheLock) { _sizeCache[cacheKey] = (DateTime.UtcNow, sizes.WorkingSize, sizes.TotalSize); }

                if (string.IsNullOrWhiteSpace(rawInfo) || rawInfo == "unknown") return snapshot;

                snapshot.ProjectName = projectName;
                snapshot.WorkingCopySize = sizes.WorkingSize;
                snapshot.RepoTotalSize = sizes.TotalSize;
                snapshot.Revision = ExtractValue(rawInfo, "Revision:");
                snapshot.Author = ExtractValue(rawInfo, "Last Changed Author:");
                snapshot.Date = ExtractValue(rawInfo, "Last Changed Date:");

                if (!string.IsNullOrWhiteSpace(remoteRevRaw) && !remoteRevRaw.Contains("Error"))
                    snapshot.RemoteRevision = remoteRevRaw.Trim();

                snapshot.RelativeUrl = ExtractValue(rawInfo, "Relative URL:");
                snapshot.Url = ExtractValue(rawInfo, "URL:");
                snapshot.RepoRoot = ExtractValue(rawInfo, "Repository Root:");

                snapshot.IsOutdated = false;
                if (int.TryParse(snapshot.Revision, out int localRev) && int.TryParse(snapshot.RemoteRevision, out int remoteRev))
                {
                    snapshot.IsOutdated = remoteRev > localRev;
                }

                string source = snapshot.RelativeUrl != "unknown" ? snapshot.RelativeUrl : snapshot.Url;
                snapshot.Branch = "trunk";
                if (!string.IsNullOrEmpty(source) && source != "unknown")
                {
                    string branch = source.Replace("^/", "").Trim();
                    if (branch.Contains("/")) branch = Path.GetFileName(branch.TrimEnd('/'));
                    if (!string.IsNullOrEmpty(branch)) snapshot.Branch = branch;
                }

                snapshot.Server = "local";
                if (!string.IsNullOrEmpty(snapshot.Url) && snapshot.Url != "unknown")
                { try { snapshot.Server = new Uri(snapshot.Url).Host; } catch { } }

                snapshot.AppVersion = Application.version;
                await EnsureVersionCached();
                snapshot.SvnVersion = _svnVersionCached;
                snapshot.CurrentUser = svnManager.CurrentUserName ?? "Unknown";

                // === CHECKOUT NIEKOMPLETNY: pozycje '!' w svn status → pasek pokaże
                // badge. Po udanym update EndUpdate resetuje, a TEN check przelicza.
                _checkoutIncomplete = await CheckCheckoutIncompleteAsync(path, token);
                snapshot.CheckoutIncomplete = _checkoutIncomplete;

                snapshot.IsValid = true;
                return snapshot;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogError($"[SVNBar] BuildSnapshotAsync failed: {ex.Message}");
                return snapshot;
            }
        }

        /// <summary>
        /// Checkout w toku — dolny pasek wchodzi w stan Updating: monitor rozmiarów
        /// odświeża (x GB / y GB) co ~5 s, jak przy update. Minimalny snapshot
        /// jak w BeginUpdate. Po sukcesie EndCheckout buduje świeży snapshot
        /// (resetuje też badge incomplete-checkout).
        /// </summary>
        public void BeginCheckout(string projectName, string workingDir)
        {
            if (_state != BarState.Idle) return;   // update/checkout już leci — nie psuj

            if (svnManager.CurrentSnapshot == null ||
                !string.Equals(svnManager.CurrentSnapshot.ProjectName, projectName, StringComparison.Ordinal))
            {
                svnManager.CurrentSnapshot = new SVNProjectInfoSnapshot
                {
                    ProjectName = projectName,
                    WorkingCopySize = "...",
                    RepoTotalSize = "...",
                    IsValid = true
                };
            }

            _state = BarState.Updating;
            SetContent(BuildUpdatingContent(projectName,
                svnManager.CurrentSnapshot.WorkingCopySize,
                svnManager.CurrentSnapshot.RepoTotalSize), "BeginCheckout");

            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();

            _ = RunSizeMonitor(workingDir, _monitorCts.Token);
        }

        /// <summary>
        /// Koniec checkoutu (sukces) — jak EndUpdate: świeży snapshot z dysku,
        /// reset incomplete-badge, czysty cache rozmiarów, fallback nazwy.
        /// </summary>
        public async Task EndCheckout(SVNProjectInfoSnapshot fallbackSnapshot)
        {
            await EndUpdate(fallbackSnapshot).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _snapshotCts?.Cancel();
            _snapshotCts?.Dispose();
        }
    }
}