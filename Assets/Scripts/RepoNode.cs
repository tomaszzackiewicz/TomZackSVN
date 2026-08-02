using System.Collections.Generic;

namespace SVN.Core
{
    public class RepoNode
    {
        public string Name { get; set; }
        public string FullUrl { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsLoading { get; set; }
        public bool IsExpanded { get; set; }
        public int Depth { get; set; }
        public RepoNode Parent { get; set; }
        public string LastChangedRev { get; set; } = "";
        public string LastChangedAuthor { get; set; } = "";
        public List<RepoNode> Children { get; set; } = new List<RepoNode>();

        public RepoBrowserItemUI UiInstance { get; set; }
    }
}