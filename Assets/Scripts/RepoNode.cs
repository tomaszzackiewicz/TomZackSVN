using System;
using System.Collections.Generic;
using System.Threading;

namespace SVN.Core
{
    public class RepoNode
    {
        public string Name { get; set; }
        public string FullUrl { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsExpanded { get; set; }
        public int Depth { get; set; }
        public RepoNode Parent { get; set; }
        public string LastChangedRev { get; set; } = "";
        public string LastChangedAuthor { get; set; } = "";
        public List<RepoNode> Children { get; set; } = new List<RepoNode>();

        public RepoBrowserItemUI UiInstance { get; set; }

        // === FIX K1: atomowe IsLoading (Interlocked) — zwykły bool przy szybkim
        // double-click przechodził check-then-set dwa razy → równoległe fetche
        // tego samego węzła rozwidlały drzewo. Pole publiczne (konwencja SVNStatus.SVNStatusElement).
        public int _isLoadingFlag;   // 0 = false, 1 = true

        public bool IsLoading
        {
            get => Interlocked.CompareExchange(ref _isLoadingFlag, 0, 0) == 1;
            set => Interlocked.Exchange(ref _isLoadingFlag, value ? 1 : 0);
        }
    }
}