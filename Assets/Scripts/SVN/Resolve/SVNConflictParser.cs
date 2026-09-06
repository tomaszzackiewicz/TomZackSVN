using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SVN.Core
{
    public class SVNConflictParser
    {
        private readonly SVNManager _svnManager;
        private readonly SVNConflictCache _cache;
        private readonly Action<string> _log;

        public SVNConflictParser(SVNManager manager, SVNConflictCache cache, Action<string> log)
        {
            _svnManager = manager;
            _cache = cache;
            _log = log;
        }

        public async Task<List<SVNConflictData>> GetConflictsAsync(string root, CancellationToken token = default)
        {
            // === FIX 5: retryOnLock = true — parser jest wołany przy KAŻDYM odświeżeniu
            // listy konfliktów; przy chwilowo zajętym wc.db (inny proces svn) odczyt
            // czeka 500ms i ponawia (ścieżka read+retry w SvnRunner) zamiast rzucać.
            string xml = await SvnRunner.RunAsync("status --xml", root, true, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml)) return new List<SVNConflictData>();

            var result = new List<SVNConflictData>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var stringReader = new StringReader(xml))
            using (var reader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreWhitespace = true
            }))
            {
                string currentPath = null; string item = null; string props = null; string tree = null;
                string tcReason = null; string tcAction = null; string tcVictim = null; string tcNodeKind = null;
                bool insideTreeConflict = false;

                // === FIX 4: część klientów svn NIE ustawia atrybutu
                // tree-conflicted="true" na <wc-status>, a raportuje tree conflict
                // wyłącznie elementem potomnym <tree-conflict>. Bez tej flagi takie
                // konflikty były klasyfikowane jako Text (złe przyciski w UI,
                // force-flow nieosiągalny).
                bool sawTreeConflictElement = false;

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();

                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "entry":
                                currentPath = reader.GetAttribute("path");
                                item = props = tree = null;
                                tcReason = tcAction = tcVictim = tcNodeKind = null;
                                insideTreeConflict = false;
                                sawTreeConflictElement = false;
                                break;
                            case "wc-status":
                                item = reader.GetAttribute("item");
                                props = reader.GetAttribute("props");
                                tree = reader.GetAttribute("tree-conflicted");
                                break;
                            case "tree-conflict":
                                insideTreeConflict = true;
                                sawTreeConflictElement = true;
                                tcVictim = reader.GetAttribute("victim");
                                tcNodeKind = reader.GetAttribute("kind");
                                tcAction = reader.GetAttribute("operation") ?? reader.GetAttribute("action");
                                break;
                            case "reason" when insideTreeConflict:
                                tcReason = reader.GetAttribute("name")
                                        ?? reader.GetAttribute("value")
                                        ?? reader.GetAttribute("reason");
                                if (string.IsNullOrEmpty(tcReason) && !reader.IsEmptyElement)
                                {
                                    try
                                    {
                                        string content = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                        if (!string.IsNullOrWhiteSpace(content)) tcReason = content.Trim();
                                    }
                                    catch { }
                                }
                                break;
                            case "action" when insideTreeConflict:
                                tcAction = reader.GetAttribute("name")
                                        ?? reader.GetAttribute("value")
                                        ?? reader.GetAttribute("action")
                                        ?? tcAction;
                                break;
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.Text && insideTreeConflict)
                    {
                        string text = reader.Value?.Trim();
                        if (!string.IsNullOrEmpty(text) && string.IsNullOrEmpty(tcReason)) tcReason = text;
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        if (reader.Name == "tree-conflict") insideTreeConflict = false;
                        else if (reader.Name == "entry")
                        {
                            bool isConflict = item == "conflicted" || props == "conflicted" ||
                                              tree == "true" || sawTreeConflictElement;
                            if (isConflict && !string.IsNullOrWhiteSpace(currentPath))
                            {
                                string path = NormalizePath(currentPath);
                                if (seen.Add(path))
                                {
                                    var type = (tree == "true" || sawTreeConflictElement)
                                        ? SVNConflictType.Tree
                                        : SVNConflictType.Text;
                                    var cached = _cache.Get(path);
                                    if (cached?.State == SVNConflictState.ManualEditing)
                                        type = SVNConflictType.Manual;

                                    var data = new SVNConflictData
                                    {
                                        Path = path,
                                        Type = type,
                                        State = cached?.State ?? SVNConflictState.Pending,
                                        TreeConflictReason = tcReason,
                                        TreeConflictAction = tcAction,
                                        TreeConflictVictim = string.IsNullOrEmpty(tcVictim) ? path : NormalizePath(tcVictim),
                                        TreeConflictNodeKind = tcNodeKind
                                    };

                                    if (type == SVNConflictType.Tree && string.IsNullOrEmpty(data.TreeConflictReason))
                                        data.TreeConflictReason = BuildFallbackTreeReason(data);

                                    _cache.AddOrUpdate(data);
                                    result.Add(data);
                                }
                            }
                        }
                    }
                }
            }

            _cache.SynchronizeFrom(result);

            // === FIX 15: usunięty log-and-rethrow — dublował komunikat błędu
            // (callerzy: RefreshConflictUIAsync / RunWithLockAsync logują sami).
            return result.OrderBy(x => x.Path).ToList();
        }

        private static string BuildFallbackTreeReason(SVNConflictData data)
        {
            if (!string.IsNullOrEmpty(data.TreeConflictAction)) return $"operation: {data.TreeConflictAction}";
            if (!string.IsNullOrEmpty(data.TreeConflictNodeKind)) return $"tree conflict ({data.TreeConflictNodeKind})";
            return "tree conflict (details unavailable)";
        }

        private static string NormalizePath(string path) =>
            string.IsNullOrWhiteSpace(path)
                ? ""
                : path.Replace('\\', '/').Replace("\r", "").Replace("\n", "").Trim();
    }
}