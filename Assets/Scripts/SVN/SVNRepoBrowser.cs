using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRepoBrowser : SVNBase
    {
        private CancellationTokenSource _cts;
        private RepoNode _rootNode;

        public SVNRepoBrowser(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            if (ui.RepoBrowserFilterInput != null)
            {
                ui.RepoBrowserFilterInput.onValueChanged.RemoveAllListeners();
                ui.RepoBrowserFilterInput.onValueChanged.AddListener(OnFilterChanged);
            }
        }

        private void OnFilterChanged(string filterText)
        {
            RefreshUI();
        }

        public async Task LoadInitialTreeAsync()
        {
            CancelOperations();

            string repoUrl = svnManager.RepositoryUrl;
            if (string.IsNullOrEmpty(repoUrl)) return;

            UpdatePathDisplay(repoUrl);
            SVNLogBridge.LogLine($"<color=yellow>[RepoBrowser]</color> Loading server tree...");

            _rootNode = new RepoNode
            {
                Name = "Root",
                FullUrl = repoUrl.TrimEnd('/'),
                IsDirectory = true,
                IsLoaded = false,
                IsExpanded = true,
                Depth = -1
            };

            await FetchChildrenAsync(_rootNode);
            RefreshUI();
        }

        public async void ToggleNode(RepoNode node)
        {
            if (node == null || !node.IsDirectory) return;

            if (!string.IsNullOrEmpty(svnUI.RepoBrowserFilterInput?.text)) return;

            if (node.IsLoading) return;

            if (node.IsExpanded)
            {
                node.IsExpanded = false;
                RefreshUI();
            }
            else
            {
                if (!node.IsLoaded)
                {
                    await FetchChildrenAsync(node);

                    if (!node.IsLoaded) return;
                }
                node.IsExpanded = true;
                RefreshUI();
            }
        }

        private async Task FetchChildrenAsync(RepoNode parentNode)
        {
            if (parentNode == null || parentNode.IsLoading) return;

            parentNode.IsLoading = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                string output = await SvnRunner.RunAsync($"list --xml \"{parentNode.FullUrl}\"", svnManager.WorkingDir, token: _cts.Token);
                parentNode.Children.Clear();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    int xmlStartIndex = output.IndexOf("<", StringComparison.Ordinal);
                    if (xmlStartIndex > 0)
                    {
                        output = output.Substring(xmlStartIndex);
                    }

                    var doc = XDocument.Parse(output);

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
                        string author = commitElement?.Elements().FirstOrDefault(e => e.Name.LocalName == "author")?.Value ?? "";

                        parentNode.Children.Add(new RepoNode
                        {
                            Name = cleanName,
                            FullUrl = $"{parentNode.FullUrl.TrimEnd('/')}/{cleanName}",
                            IsDirectory = isDir,
                            Depth = parentNode.Depth + 1,
                            IsLoaded = false,
                            IsExpanded = false,
                            Parent = parentNode,
                            LastChangedRev = rev,
                            LastChangedAuthor = author
                        });
                    }

                    parentNode.Children = parentNode.Children
                        .OrderByDescending(n => n.IsDirectory)
                        .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                parentNode.IsLoaded = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[RepoBrowser] Fetch error: {ex.Message}");
            }
            finally
            {
                parentNode.IsLoading = false;
            }
        }

        public void RefreshUI()
        {
            if (_rootNode == null) return;

            string filter = svnUI.RepoBrowserFilterInput != null ? svnUI.RepoBrowserFilterInput.text : "";
            bool isFiltering = !string.IsNullOrWhiteSpace(filter);

            List<RepoNode> nodesToDisplay;

            if (isFiltering)
            {
                nodesToDisplay = GetFilteredNodes(filter.Trim().ToLowerInvariant());
            }
            else
            {
                nodesToDisplay = new List<RepoNode>();
                GetVisibleNodesNormal(_rootNode, nodesToDisplay);
            }

            RenderNodeList(nodesToDisplay, isFiltering);
        }

        private void GetVisibleNodesNormal(RepoNode node, List<RepoNode> result)
        {
            if (node == null) return;

            if (node.Depth >= 0)
            {
                result.Add(node);
            }

            if (node.IsDirectory && node.IsExpanded && node.IsLoaded && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    GetVisibleNodesNormal(child, result);
                }
            }
        }

        private List<RepoNode> GetFilteredNodes(string lowerFilter)
        {
            var result = new List<RepoNode>();
            if (_rootNode == null || _rootNode.Children == null) return result;

            foreach (var child in _rootNode.Children)
            {
                CollectFilteredNodes(child, lowerFilter, parentMatched: false, result);
            }

            return result;
        }

        private bool CollectFilteredNodes(RepoNode node, string filter, bool parentMatched, List<RepoNode> result)
        {
            if (node == null) return false;

            bool selfMatches = parentMatched || node.Name.ToLowerInvariant().Contains(filter);
            var childrenMatches = new List<RepoNode>();
            bool hasMatchingChildren = false;

            if (node.IsDirectory && node.IsLoaded && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (CollectFilteredNodes(child, filter, selfMatches, childrenMatches))
                    {
                        hasMatchingChildren = true;
                    }
                }
            }

            if (selfMatches || hasMatchingChildren)
            {
                result.Add(node);
                result.AddRange(childrenMatches);
                return true;
            }

            return false;
        }

        private void RenderNodeList(List<RepoNode> nodesToDisplay, bool isFiltering)
        {
            if (svnUI.RepoBrowserContentRoot == null || svnUI.RepoBrowserItemPrefab == null) return;

            var layoutGroup = svnUI.RepoBrowserContentRoot.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();

            if (layoutGroup != null) layoutGroup.enabled = false;

            ClearRoot();

            HashSet<RepoNode> nodesWithVisibleChildren = null;
            if (isFiltering)
            {
                nodesWithVisibleChildren = new HashSet<RepoNode>();
                foreach (var n in nodesToDisplay)
                {
                    if (n.Parent != null) nodesWithVisibleChildren.Add(n.Parent);
                }
            }

            foreach (var node in nodesToDisplay)
            {
                var obj = UnityEngine.Object.Instantiate(svnUI.RepoBrowserItemPrefab, svnUI.RepoBrowserContentRoot);
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

            if (layoutGroup != null) layoutGroup.enabled = true;
        }

        private void ClearRoot()
        {
            if (svnUI.RepoBrowserContentRoot == null) return;

            List<Transform> childrenToDestroy = new List<Transform>();
            foreach (Transform child in svnUI.RepoBrowserContentRoot)
            {
                childrenToDestroy.Add(child);
            }

            foreach (Transform child in childrenToDestroy)
            {
                child.SetParent(null);
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }

        public void CollapseAllToRoot()
        {
            if (_rootNode == null || !_rootNode.IsLoaded) return;

            foreach (var child in _rootNode.Children)
            {
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

            RepoNode deepestExpanded = null;
            FindDeepestVisibleExpanded(_rootNode, ref deepestExpanded);

            if (deepestExpanded != null)
            {
                deepestExpanded.IsExpanded = false;
                RefreshUI();
            }
            else
            {
                SVNLogBridge.LogLine("<color=orange>[RepoBrowser]</color> Already at the root.");
            }
        }

        private void FindDeepestVisibleExpanded(RepoNode node, ref RepoNode result)
        {
            if (node == null) return;

            bool isExpandableCandidate = node.Depth >= 0 &&
                                        node.IsDirectory &&
                                        node.IsExpanded &&
                                        node.IsLoaded &&
                                        node.Children.Count > 0;

            if (isExpandableCandidate)
            {
                result = node;
            }

            if (node.IsExpanded && node.IsLoaded && node.Children.Count > 0)
            {
                foreach (var child in node.Children)
                {
                    FindDeepestVisibleExpanded(child, ref result);
                }
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

            string baseUrl = svnManager.RepositoryUrl.TrimEnd('/');
            string fullPath = node.FullUrl.TrimEnd('/');

            if (fullPath.StartsWith(baseUrl))
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

        private void CancelOperations()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}