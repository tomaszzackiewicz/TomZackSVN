
namespace SVN.Core
{
    public struct SvnStats
    {
        public int FolderCount;
        public int FileCount;
        public int ModifiedCount;
        public int AddedCount;
        public int NewFilesCount; // ?
        public int DeletedCount;  // D, !
        public int ConflictsCount; // C
        public int IgnoredCount;
        public int LockedByMeCount;    // K
        public int LockedByOthersCount; // O
        public int IgnoredFileCount;
        public int IgnoredFolderCount;

        public string ToRichText(bool isIgnoredView)
        {
            if (isIgnoredView)
            {
                return $"<color=#666666><b>VIEW: IGNORED</b></color> | Total Ignored: <color=#FFFFFF>{IgnoredCount}</color>";
            }

            string lockStats = "";
            if (LockedByMeCount > 0 || LockedByOthersCount > 0)
            {
                lockStats = $" | <color=#00FF00>Lock (Me): {LockedByMeCount}</color> | <color=#FF4444>Lock (Other): {LockedByOthersCount}</color>";
            }

            return $"Folders: {FolderCount} | Files: {FileCount} | " +
                   $"<color=#FFD700>Mod (M): {ModifiedCount}</color> | " +
                   $"<color=#00FF00>Add (A): {AddedCount}</color> | " +
                   $"<color=#00E5FF>New (?): {NewFilesCount}</color> | " +
                   $"<color=#FF4444>Del (D/!): {DeletedCount}</color> | " +
                   $"<color=#FF00FF>Conf (C): {ConflictsCount}</color>{lockStats}";
        }
    }
}