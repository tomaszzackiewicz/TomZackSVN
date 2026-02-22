using System;

namespace SVN.Core
{
    [Serializable]
    public class SvnTreeElement
    {
        public string FullPath;
        public string Name;
        public int Depth;
        public string Status;
        public string Size;
        public bool IsFolder;
        public bool IsExpanded = true;
        public bool IsVisible = true;
        public bool IsChecked = false;
        public bool IsCommitDelegate;

        public SvnTreeElement Clone()
        {
            return (SvnTreeElement)this.MemberwiseClone();
        }
    }
}