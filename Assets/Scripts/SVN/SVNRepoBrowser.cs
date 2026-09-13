using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    /// <summary>
    /// Repo Browser — drzewo katalogów z SERWERA (svn list).
    ///
    /// === FILTR: szuka WYŁĄCZNIE NA SERWERZE, w NAZWACH (ostatni segment
    /// ścieżki) — plików i folderów. Baza = svnManager.RepositoryUrl.
    /// Cache 1 min, ale KLUCZOWANY URL-em — zmiana repo natychmiast
    /// unieważnia cache (bez tego filtr zwracał wyniki ze starego repo).
    ///
    /// === RENDER: koalescencja + fingerprint + hard cap. Bez tego szybkie
    /// zmiany filtra (pisać / kasować) potrafiły wywołać kilka renderów
    /// w jednej klatce i zawiesić UI na layout rebuild loop.
    /// Kolor logów: #4FC3F7.
    ///
    /// === LAZY TAB: drzewo NIE jest ładowane przy starcie projektu.
    /// ViewTabsController wywołuje statyczne zdarzenie RepoBrowserTabOpened
    /// w momencie przełączenia na zakładkę Repo Browser.
    /// </summary>
    public class SVNRepoBrowser : SVNBase, IDisposable
    {
        // ===== LAZY TAB =====
        /// <summary>
        /// Wywoływane przez ViewTabsController, gdy user przełącza się na
        /// zakładkę Repo Browser. Dzięki temu NIE ładujemy drzewa z serwera
        /// na starcie projektu — dopiero gdy user faktycznie tego chce.
        /// </summary>
        public static event Action RepoBrowserTabOpened;

        // ===== LAZY TREE =====
        private CancellationTokenSource _cts;
        private readonly object _ctsLock = new object();
        private RepoNode _rootNode;
        private int _fetchGeneration;
        private int _disposed;
        private int _ensureLoadingFlag;

        // ===== FLAT PATH CACHE (filtr) =====
        private List<PathEntry> _allPaths;
        private DateTime _allPathsLoadedAt = DateTime.MinValue;
        private string _allPathsSourceUrl;   // z jakiego URL-a zbudowano _allPaths
        private int _allPathsLoadingFlag;
        private CancellationTokenSource _allPathsCts;
        private readonly object _allPathsCtsLock = new object();

        private const double PathsCacheMinutes = 1.0;
        private const int PathListTimeoutMinutes = 10;

        // ===== UI RENDER COALESCING / SAFETY =====
        private int _refreshUiPendingFlag;       // 1 = render już zakolejkowany
        private int _isRenderingFlag;            // 1 = RefreshUICore właśnie trwa
        private string _lastRenderFingerprint;   // ostatnio wyrenderowany widok
        private const int MaxRenderedNodes = 800;

        public SVNRepoBrowser(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            if (ui.RepoBrowserFilterInput != null)
            {
                ui.RepoBrowserFilterInput.onValueChanged.RemoveAllListeners();
                ui.RepoBrowserFilterInput.onValueChanged.AddListener(OnFilterChanged);
            }

            // Subskrypcja zdarzenia przełączenia zakładki.
            // -= przed += chroni przed duplikatami przy ponownej inicjalizacji modułu.
            RepoBrowserTabOpened -= HandleTabOpened;
            RepoBrowserTabOpened += HandleTabOpened;
        }

        // ================= LAZY TAB HANDLER =================

        private void HandleTabOpened()
        {
            // Ładuj drzewo tylko raz — EnsureLoadedAsync sam pilnuje flagami,
            // że drugie wywołanie nie odpali kolejnego svn list.
            if (IsTreeLoaded) return;
            _ = EnsureLoadedAsync();
        }

        // ================= FILTR =================

        private void OnFilterChanged(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                RefreshUI();
                UpdateStatusLabel(null);
                return;
            }

            RefreshUI();
            _ = EnsurePathsAsync();
        }

        private bool ArePathsFresh =>
            _allPaths != null &&
            (DateTime.UtcNow - _allPathsLoadedAt).TotalMinutes < PathsCacheMinutes &&
            string.Equals(
                (_allPathsSourceUrl ?? "").TrimEnd('/'),
                (svnManager?.RepositoryUrl ?? "").TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Wywoływane z SVNManager przy każdej zmianie RepositoryUrl — cache
        /// ścieżek jest per-URL, więc nowe repo = twarde unieważnienie.
        /// </summary>
        public void InvalidatePathCache()
        {
            _allPaths = null;
            _allPathsLoadedAt = DateTime.MinValue;
            _allPathsSourceUrl = null;

            // Fingerprint widoku zależał od _allPaths — unieważnij też jego.
            _lastRenderFingerprint = null;
        }

        // ================= ŁADOWANIE LISTY ŚCIEŻEK Z SERWERA =================

        private async Task EnsurePathsAsync()
        {
            if (ArePathsFresh)
            {
                UpdateStatusLabel(null);
                return;
            }

            if (Interlocked.CompareExchange(ref _allPathsLoadingFlag, 1, 0) == 1)
                return;

            CancellationTokenSource cts = null;
            Stopwatch sw = Stopwatch.StartNew();
            int scannedLines = 0;
            int lastUiUpdateMs = 0;

            try
            {
                string url = (svnManager.RepositoryUrl ?? "").TrimEnd('/');
                if (string.IsNullOrEmpty(url))
                {
                    SVNLogBridge.LogError("[RepoBrowser] RepositoryUrl is empty — cannot search server.");
                    UpdateStatusLabel("<color=#FF4444>Error: no repository URL set.</color>");
                    return;
                }

                lock (_allPathsCtsLock)
                {
                    var oldCts = _allPathsCts;
                    if (oldCts != null)
                    {
                        try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                        _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
                    }
                    cts = new CancellationTokenSource();
                    _allPathsCts = cts;
                }

                cts.CancelAfter(TimeSpan.FromMinutes(PathListTimeoutMinutes));

                SVNLogBridge.LogLine(
                    $"<color=#4FC3F7>[RepoBrowser]</color> SERVER SEARCH: " +
                    $"svn list -R \"{url}\"  (source: {svnManager.CurrentProject?.projectName ?? "?"})");

                UpdateStatusLabel("<color=#FFCC00>Searching server…</color>");

                var entries = new List<PathEntry>();

                int exitCode = await SvnRunner.RunStreamedAsync(
                    $"list -R \"{EscapeSvnArg(url)}\"",
                    svnManager.WorkingDir,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;

                        string trimmed = line.TrimEnd('\r');
                        if (string.IsNullOrEmpty(trimmed)) return;

                        bool isDir = trimmed.EndsWith("/", StringComparison.Ordinal);
                        string path = trimmed.TrimEnd('/').Replace('\\', '/').Trim();
                        if (string.IsNullOrEmpty(path)) return;

                        entries.Add(new PathEntry { Path = path, IsDirectory = isDir });
                        scannedLines++;

                        int nowMs = (int)sw.ElapsedMilliseconds;
                        if (nowMs - lastUiUpdateMs >= 250)
                        {
                            lastUiUpdateMs = nowMs;
                            int curCount = scannedLines;
                            int elapsedSec = (int)(nowMs / 1000);
                            UpdateStatusLabel(
                                $"<color=#FFCC00>Scanning server… {curCount}, {elapsedSec}s</color>");
                        }
                    },
                    cts.Token).ConfigureAwait(false);

                sw.Stop();

                if (cts.IsCancellationRequested)
                {
                    SVNLogBridge.LogLine("<color=#FF8800>[RepoBrowser] Path list load cancelled.</color>");
                    UpdateStatusLabel("<color=#FF8800>Search cancelled.</color>");
                    return;
                }

                if (entries.Count == 0)
                {
                    SVNLogBridge.LogError("[RepoBrowser] Path list is empty (server returned no paths).");
                    UpdateStatusLabel("<color=#FF4444>Server returned no paths.</color>");
                    return;
                }

                entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

                _allPaths = entries;
                _allPathsLoadedAt = DateTime.UtcNow;
                _allPathsSourceUrl = url;

                // Nowa lista = nowy widok — fingerprint ze starej listy już nieaktualny.
                _lastRenderFingerprint = null;

                SVNLogBridge.LogLine(
                    $"<color=green>[RepoBrowser]</color> Loaded {entries.Count} path(s) " +
                    $"from server in {sw.Elapsed.TotalSeconds:F1}s.");

                RefreshUI();
                RunOnMainThread(() => UpdateStatusLabel(null));
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=#FF8800>[RepoBrowser] Path list load cancelled or timed out.</color>");
                UpdateStatusLabel("<color=#FF8800>Search cancelled or timed out.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[RepoBrowser] Path list load error: {ex.Message}");
                UpdateStatusLabel($"<color=#FF4444>Search failed: {ex.Message}</color>");
            }
            finally
            {
                Interlocked.Exchange(ref _allPathsLoadingFlag, 0);

                lock (_allPathsCtsLock)
                {
                    if (ReferenceEquals(_allPathsCts, cts))
                        _allPathsCts = null;
                }
                if (cts != null)
                    _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }
        }

        private static string EscapeSvnArg(string arg) => SVNShared.EscapeSvnArg(arg);

        // ================= STATUS LABEL =================

        private void UpdateStatusLabel(string status)
        {
            if (svnUI?.RepoBrowserCurrentPathText == null) return;

            string baseUrl = svnManager?.RepositoryUrl ?? "";
            string text = status ?? $"<b>Server:</b> {baseUrl}";

            RunOnMainThread(() =>
            {
                if (svnUI?.RepoBrowserCurrentPathText != null)
                    svnUI.RepoBrowserCurrentPathText.text = text;
            });
        }

        // ================= LAZY-LOAD API =================

        public bool IsTreeLoaded => _rootNode != null && _rootNode.IsLoaded;

        public async Task EnsureLoadedAsync()
        {
            if (IsTreeLoaded) return;

            if (Interlocked.CompareExchange(ref _ensureLoadingFlag, 1, 0) == 1)
                return;

            try
            {
                if (IsTreeLoaded) return;
                await LoadInitialTreeAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _ensureLoadingFlag, 0);
            }
        }

        public async Task LoadInitialTreeAsync()
        {
            CancelOperations();

            // Reset cache filtrów razem z drzewem — nowe repo = nowa baza.
            _allPaths = null;
            _allPathsLoadedAt = DateTime.MinValue;
            _allPathsSourceUrl = null;
            _lastRenderFingerprint = null;

            string repoUrl = svnManager.RepositoryUrl;
            if (string.IsNullOrEmpty(repoUrl))
            {
                SVNLogBridge.LogError("[RepoBrowser] RepositoryUrl is empty. Cannot load tree.");
                return;
            }

            UpdatePathDisplay(repoUrl);
            SVNLogBridge.LogLine("<color=yellow>[RepoBrowser]</color> Loading server tree...");

            _rootNode = new RepoNode
            {
                Name = "Root",
                FullUrl = repoUrl.TrimEnd('/'),
                IsDirectory = true,
                IsLoaded = false,
                IsExpanded = true,
                Depth = -1,
                Children = new List<RepoNode>()
            };

            await FetchChildrenAsync(_rootNode).ConfigureAwait(false);
            RefreshUI();

            // Jeśli user ma aktywny filtr (pisał w trakcie ładowania drzewa),
            // dokończ szukanie na ŚWIEŻYM URL-u.
            string activeFilter = svnUI?.RepoBrowserFilterInput?.text;
            if (!string.IsNullOrWhiteSpace(activeFilter))
            {
                _ = EnsurePathsAsync();
            }
        }

        public async void ToggleNode(RepoNode node)
        {
            try
            {
                await ToggleNodeAsync(node);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[RepoBrowser] Toggle error: {ex.Message}");
            }
        }

        private async Task ToggleNodeAsync(RepoNode node)
        {
            if (node == null || !node.IsDirectory) return;
            if (node.IsLoading) return;

            if (node.IsExpanded)
            {
                node.IsExpanded = false;
                RefreshUI();
                return;
            }

            if (!node.IsLoaded)
            {
                await FetchChildrenAsync(node).ConfigureAwait(false);
                if (!node.IsLoaded) return;
            }

            node.IsExpanded = true;
            RefreshUI();
        }

        // ================= FETCH LAZY =================

        private async Task FetchChildrenAsync(RepoNode parentNode)
        {
            if (parentNode == null) return;

            if (Interlocked.CompareExchange(ref parentNode._isLoadingFlag, 1, 0) != 0) return;

            if (parentNode.Children == null)
                parentNode.Children = new List<RepoNode>();

            int generation = Interlocked.Increment(ref _fetchGeneration);

            CancellationTokenSource cts;
            lock (_ctsLock)
            {
                var oldCts = _cts;
                if (oldCts != null)
                {
                    try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                    _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
                }
                cts = new CancellationTokenSource();
                _cts = cts;
            }

            try
            {
                string output = await SvnRunner.RunAsync(
                    $"list --xml \"{EscapeSvnArg(parentNode.FullUrl)}\"",
                    svnManager.WorkingDir,
                    token: cts.Token).ConfigureAwait(false);

                if (generation != Volatile.Read(ref _fetchGeneration))
                    return;

                var newChildren = new List<RepoNode>();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    int xmlStartIndex = output.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
                    if (xmlStartIndex < 0)
                        xmlStartIndex = output.IndexOf("<lists", StringComparison.OrdinalIgnoreCase);
                    if (xmlStartIndex < 0)
                        xmlStartIndex = output.IndexOf("<list", StringComparison.OrdinalIgnoreCase);
                    if (xmlStartIndex > 0)
                        output = output.Substring(xmlStartIndex);

                    XDocument doc;
                    try { doc = XDocument.Parse(output); }
                    catch (Exception parseEx)
                    {
                        SVNLogBridge.LogError($"[RepoBrowser] XML parse error: {parseEx.Message}");
                        return;
                    }

                    foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "entry"))
                    {
                        string kind = (string)entry.Attribute("kind");
                        var nameElement = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "name");
                        string path = nameElement?.Value;
                        if (string.IsNullOrEmpty(path)) continue;

                        bool isDir = kind == "dir";
                        string cleanName = isDir ? path.TrimEnd('/') : path;
                        if (cleanName == "." || cleanName == "..") continue;

                        var commitElement = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "commit");
                        string rev = commitElement?.Attribute("revision")?.Value ?? "";
                        string author = commitElement?.Elements()
                            .FirstOrDefault(e => e.Name.LocalName == "author")?.Value ?? "";

                        newChildren.Add(new RepoNode
                        {
                            Name = cleanName,
                            FullUrl = $"{parentNode.FullUrl.TrimEnd('/')}/{cleanName}",
                            IsDirectory = isDir,
                            Depth = parentNode.Depth + 1,
                            IsLoaded = false,
                            IsExpanded = false,
                            Parent = parentNode,
                            LastChangedRev = rev,
                            LastChangedAuthor = author,
                            Children = new List<RepoNode>()
                        });
                    }

                    newChildren = newChildren
                        .OrderByDescending(n => n.IsDirectory)
                        .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                parentNode.Children = newChildren;
                parentNode.IsLoaded = true;

                // Struktura drzewa się zmieniła — fingerprint widoku nieaktualny.
                _lastRenderFingerprint = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[RepoBrowser] Fetch error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref parentNode._isLoadingFlag, 0);

                lock (_ctsLock)
                {
                    if (ReferenceEquals(_cts, cts))
                        _cts = null;
                }
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }
        }

        // ================= UI =================

        /// <summary>
        /// Publiczne wejście — KOALESCUJE wywołania z tej samej klatki w jeden
        /// render. Bez tego szybkie pisanie/kasowanie filtra generowało kilka
        /// pełnych renderów na klatkę (ClearRoot + Instantiate x N) i zawieszało
        /// UI na layout rebuild.
        /// </summary>
        public void RefreshUI()
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            // Jeśli render jest już zakolejkowany — nic nie rób. Kolejka
            // wykona się z AKTUALNYM stanem filtra (i tak czytamy go w RefreshUICore).
            if (Interlocked.CompareExchange(ref _refreshUiPendingFlag, 1, 0) == 1)
                return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                Interlocked.Exchange(ref _refreshUiPendingFlag, 0);
                if (Volatile.Read(ref _disposed) == 1) return;
                RefreshUICore();
            });
        }

        private void RefreshUICore()
        {
            if (Interlocked.CompareExchange(ref _isRenderingFlag, 1, 0) == 1) return;
            try
            {
                RefreshUIImpl();
            }
            finally
            {
                Interlocked.Exchange(ref _isRenderingFlag, 0);
            }
        }

        private void RefreshUIImpl()
        {
            if (svnUI?.RepoBrowserContentRoot == null) return;
            if (svnUI?.RepoBrowserItemPrefab == null) return;

            string filter = svnUI.RepoBrowserFilterInput != null
                ? svnUI.RepoBrowserFilterInput.text
                : "";

            bool isFiltering = !string.IsNullOrWhiteSpace(filter);

            List<RepoNode> nodesToDisplay;

            if (isFiltering)
            {
                nodesToDisplay = BuildFilteredNodes(filter.Trim());

                SVNLogBridge.LogLine(
                    $"<color=#4FC3F7>[RepoBrowser]</color> Filter '{filter}' [name] → " +
                    $"{nodesToDisplay.Count} match(es) of {(_allPaths?.Count ?? 0)}");

                if (nodesToDisplay.Count == 0)
                {
                    if (_allPaths == null)
                        UpdateStatusLabel("<color=#FFCC00>Searching server…</color>");
                    else
                        UpdateStatusLabel(
                            $"<color=#FF8800>No match for '{filter}' on server " +
                            $"({_allPaths.Count} paths).</color>");
                }
                else
                {
                    UpdateStatusLabel(
                        $"<color=#66FF66>{nodesToDisplay.Count} match(es) of {_allPaths.Count}</color>");
                }
            }
            else
            {
                if (_rootNode == null)
                {
                    ClearRoot();
                    _lastRenderFingerprint = null;
                    return;
                }

                nodesToDisplay = new List<RepoNode>();
                GetVisibleNodesNormal(_rootNode, nodesToDisplay);
            }

            // === FIX (freeze): fingerprint — pomiń re-render gdy nic się nie zmieniło.
            // To ucina całą kaskadę renderów z EnsurePathsAsync + OnFilterChanged.
            string fingerprint = BuildFingerprint(nodesToDisplay, isFiltering);
            if (fingerprint == _lastRenderFingerprint &&
                svnUI.RepoBrowserContentRoot.childCount > 0)
                return;

            _lastRenderFingerprint = fingerprint;

            // === FIX (freeze): twardy cap — ochrona przed eksplozją prefabów.
            bool capped = false;
            if (nodesToDisplay.Count > MaxRenderedNodes)
            {
                nodesToDisplay = nodesToDisplay.GetRange(0, MaxRenderedNodes);
                capped = true;
            }

            RenderNodeList(nodesToDisplay, isFiltering);

            if (capped)
            {
                UpdateStatusLabel(
                    $"<color=#FF8800>Showing first {MaxRenderedNodes} items — refine the filter to narrow down.</color>");
            }
        }

        private static string BuildFingerprint(List<RepoNode> nodes, bool isFiltering)
        {
            var sb = new System.Text.StringBuilder(64 + (nodes?.Count ?? 0) * 24);
            sb.Append(isFiltering ? "F|" : "T|");
            if (nodes == null || nodes.Count == 0) return sb.ToString();

            sb.Append(nodes.Count).Append('|');
            foreach (var n in nodes)
            {
                sb.Append(n.FullUrl ?? "").Append('|');
                sb.Append(n.IsDirectory ? 'D' : 'F').Append('|');
                sb.Append(n.IsExpanded ? '1' : '0').Append('|');
                sb.Append(n.LastChangedRev ?? "").Append('|');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Szuka w NAZWACH (ostatni segment ścieżki) — plików i folderów.
        /// </summary>
        private List<RepoNode> BuildFilteredNodes(string filter)
        {
            var result = new List<RepoNode>();

            if (_allPaths == null || _allPaths.Count == 0)
                return result;

            string baseUrl = (svnManager.RepositoryUrl ?? "").TrimEnd('/');

            foreach (var entry in _allPaths)
            {
                // Cap wcześniej — nie zbieraj 10k węzłów tylko po to, żeby je odciąć.
                if (result.Count >= MaxRenderedNodes) break;

                string name = GetLastSegment(entry.Path);
                if (string.IsNullOrEmpty(name)) continue;

                if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                result.Add(new RepoNode
                {
                    Name = name,
                    FullUrl = $"{baseUrl}/{entry.Path}",
                    IsDirectory = entry.IsDirectory,
                    IsLoaded = true,
                    IsExpanded = false,
                    Depth = entry.Path.Count(c => c == '/'),
                    Children = new List<RepoNode>()
                });
            }

            return result;
        }

        private static string GetLastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int idx = path.LastIndexOf('/');
            return idx < 0 ? path : path.Substring(idx + 1);
        }

        private void GetVisibleNodesNormal(RepoNode node, List<RepoNode> result)
        {
            if (node == null) return;

            // Hard cap na traversal — jeśli ktoś ma drzewo z 100k węzłów i wszystko
            // rozwinięte, nie zamrażamy main thread rekurencją.
            if (result.Count >= MaxRenderedNodes * 2) return;

            if (node.Depth >= 0)
                result.Add(node);

            if (node.IsDirectory && node.IsExpanded && node.IsLoaded && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    GetVisibleNodesNormal(child, result);
                    if (result.Count >= MaxRenderedNodes * 2) return;
                }
            }
        }

        private void RenderNodeList(List<RepoNode> nodesToDisplay, bool isFiltering)
        {
            // === FIX (freeze): wyłącz obie składowe layoutu na czas renderu.
            // VerticalLayoutGroup + ContentSizeFitter + masowe Instantiate
            // w jednej klatce potrafi wejść w layout-rebuild loop i zamrozić UI.
            var layoutGroup = svnUI.RepoBrowserContentRoot.GetComponent<VerticalLayoutGroup>();
            var sizeFitter = svnUI.RepoBrowserContentRoot.GetComponent<ContentSizeFitter>();

            bool layoutWasEnabled = layoutGroup != null && layoutGroup.enabled;
            bool fitterWasEnabled = sizeFitter != null && sizeFitter.enabled;

            if (layoutGroup != null) layoutGroup.enabled = false;
            if (sizeFitter != null) sizeFitter.enabled = false;

            try
            {
                ClearRoot();

                if (nodesToDisplay == null || nodesToDisplay.Count == 0)
                    return;

                HashSet<RepoNode> nodesWithVisibleChildren = null;
                if (isFiltering)
                {
                    nodesWithVisibleChildren = new HashSet<RepoNode>();
                    foreach (var n in nodesToDisplay)
                    {
                        if (n.Parent != null)
                            nodesWithVisibleChildren.Add(n.Parent);
                    }
                }

                foreach (var node in nodesToDisplay)
                {
                    var obj = UnityEngine.Object.Instantiate(
                        svnUI.RepoBrowserItemPrefab, svnUI.RepoBrowserContentRoot);

                    var ui = obj.GetComponent<RepoBrowserItemUI>();
                    if (ui != null)
                    {
                        ui.Initialize(node, this);

                        if (node.IsDirectory)
                        {
                            bool arrowExpanded = isFiltering
                                ? nodesWithVisibleChildren.Contains(node)
                                : node.IsExpanded;

                            ui.UpdateArrowVisual(arrowExpanded);
                        }

                        node.UiInstance = ui;
                    }
                }
            }
            finally
            {
                if (layoutGroup != null && layoutWasEnabled) layoutGroup.enabled = true;
                if (sizeFitter != null && fitterWasEnabled) sizeFitter.enabled = true;
            }
        }

        private void ClearRoot()
        {
            if (svnUI.RepoBrowserContentRoot == null) return;

            var toDestroy = new List<GameObject>(svnUI.RepoBrowserContentRoot.childCount);
            foreach (Transform child in svnUI.RepoBrowserContentRoot)
                toDestroy.Add(child.gameObject);

            foreach (var go in toDestroy)
            {
                go.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(go);
            }
        }

        public void CollapseAllToRoot()
        {
            if (_rootNode == null || !_rootNode.IsLoaded) return;

            if (_rootNode.Children != null)
            {
                foreach (var child in _rootNode.Children)
                    child.IsExpanded = false;
            }

            RefreshUI();
            SVNLogBridge.LogLine("<color=yellow>[RepoBrowser]</color> Tree collapsed to root.");
        }

        private void UpdatePathDisplay(string path)
        {
            if (svnUI.RepoBrowserCurrentPathText != null)
                svnUI.RepoBrowserCurrentPathText.text = $"<b>Server:</b> {path}";
        }

        public void GoUp()
        {
            if (_rootNode == null) return;

            if (!string.IsNullOrEmpty(svnUI.RepoBrowserFilterInput?.text))
            {
                SVNLogBridge.LogLine("<color=#8B0000>>[RepoBrowser]</color> Clear filter first.");
                return;
            }

            RepoNode deepestExpanded = null;
            FindDeepestVisibleExpanded(_rootNode, ref deepestExpanded);

            if (deepestExpanded != null)
            {
                deepestExpanded.IsExpanded = false;
                RefreshUI();
            }
            else
            {
                SVNLogBridge.LogLine("<color=#8B0000>>[RepoBrowser]</color> Already at the root.");
            }
        }

        private static void FindDeepestVisibleExpanded(RepoNode node, ref RepoNode result)
        {
            if (node == null) return;

            bool isExpandableCandidate =
                node.Depth >= 0 &&
                node.IsDirectory &&
                node.IsExpanded &&
                node.IsLoaded &&
                node.Children != null &&
                node.Children.Count > 0;

            if (isExpandableCandidate)
                result = node;

            if (node.IsExpanded && node.IsLoaded && node.Children != null)
            {
                foreach (var child in node.Children)
                    FindDeepestVisibleExpanded(child, ref result);
            }
        }

        public void CheckoutFolder(RepoNode node)
        {
            if (node == null || !node.IsDirectory) return;

            if (svnUI.CheckoutRepoUrlInput != null)
                svnUI.CheckoutRepoUrlInput.text = node.FullUrl;

            if (svnUI.CheckoutDestFolderInput != null && !string.IsNullOrEmpty(svnManager.WorkingDir))
                svnUI.CheckoutDestFolderInput.text = svnManager.WorkingDir;

            if (svnUI.CheckoutPrivateKeyInput != null)
                svnUI.CheckoutPrivateKeyInput.text = svnManager.CurrentKey;

            svnManager.PanelHandler?.Button_OpenCheckout();
            SVNLogBridge.LogLine($"<color=yellow>[RepoBrowser]</color> Ready to checkout: {node.FullUrl}");
        }

        public void CopyRelativePath(RepoNode node)
        {
            if (node == null) return;

            string baseUrl = (svnManager.RepositoryUrl ?? "").TrimEnd('/');
            string fullPath = (node.FullUrl ?? "").TrimEnd('/');

            if (!string.IsNullOrEmpty(baseUrl) &&
                fullPath.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                string relativePath = fullPath.Substring(baseUrl.Length).TrimStart('/');
                GUIUtility.systemCopyBuffer = relativePath;
                SVNLogBridge.LogLine($"<color=yellow>[RepoBrowser]</color> Copied relative path: {relativePath}");
            }
            else
            {
                GUIUtility.systemCopyBuffer = node.Name;
                SVNLogBridge.LogLine($"<color=yellow>[RepoBrowser]</color> Copied name: {node.Name}");
            }
        }

        private void RunOnMainThread(Action action)
        {
            if (action == null) return;
            UnityMainThreadDispatcher.Enqueue(action);
        }

        private void CancelOperations()
        {
            Interlocked.Increment(ref _fetchGeneration);

            lock (_ctsLock)
            {
                var oldCts = _cts;
                _cts = null;
                if (oldCts != null)
                {
                    try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                    _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            // Odsubskrybuj zdarzenie zakładki — bez tego przy re-init modułu
            // zostałby wisiący delegate trzymający this w pamięci.
            RepoBrowserTabOpened -= HandleTabOpened;

            CancelOperations();

            lock (_allPathsCtsLock)
            {
                var oldCts = _allPathsCts;
                _allPathsCts = null;
                if (oldCts != null)
                {
                    try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                    _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
                }
            }

            if (svnUI?.RepoBrowserFilterInput != null)
                svnUI.RepoBrowserFilterInput.onValueChanged.RemoveListener(OnFilterChanged);
        }

        // ================= POMOCNICZE =================

        private sealed class PathEntry
        {
            public string Path;
            public bool IsDirectory;
        }

        public static void NotifyTabOpened()
        {
            RepoBrowserTabOpened?.Invoke();
        }
    }
}