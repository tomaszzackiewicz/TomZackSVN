namespace SVN.Core
{
    public enum SVNConflictType { Text, Manual, Tree }
    public enum SVNConflictState { Pending, ManualEditing, Resolving, Resolved }

    public class SVNConflictData
    {
        public string Path;
        public SVNConflictType Type;
        public SVNConflictState State;

        public string TreeConflictReason;
        public string TreeConflictAction;
        public string TreeConflictVictim;
        public string TreeConflictNodeKind;
    }
}