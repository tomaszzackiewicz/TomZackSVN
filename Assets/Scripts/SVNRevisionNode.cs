using System.Collections.Generic;

namespace SVN.Core
{
    [System.Serializable]
    public class SVNRevisionNode
    {
        public long Revision;
        public string Author;
        public string Date;
        public string Message;
        public List<string> ChangedPaths = new List<string>();
        public bool HasMergeInfoChange;

        public string CopyFromPath; //"/trunk"
        public long CopyFromRev;    //1050
    }
}