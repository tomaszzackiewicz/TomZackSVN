namespace SVN.Core
{
    public readonly struct SVNBackupCleanupResult
    {
        public readonly int BackupsRemoved;      // usunięte całe backupy (foldery-stamp)
        public readonly int LegacyFilesRemoved;  // usunięte pliki starego layoutu
        public readonly long BytesFreed;         // uwolnione bajty
        public readonly long BytesRemaining;     // pozostałe w backup root
        public readonly int BackupsRemaining;    // ile folderów-stampów zostało
        public readonly bool OverCap;            // czy NADAL ponad limitem (ochrona 24h)
        public readonly long DiskFreeBytes;      // wolne miejsce na dysku (-1 = nieznane)

        public SVNBackupCleanupResult(int backupsRemoved, int legacyFilesRemoved, long bytesFreed,
            long bytesRemaining, int backupsRemaining, bool overCap, long diskFree)
        {
            BackupsRemoved = backupsRemoved;
            LegacyFilesRemoved = legacyFilesRemoved;
            BytesFreed = bytesFreed;
            BytesRemaining = bytesRemaining;
            BackupsRemaining = backupsRemaining;
            OverCap = overCap;
            DiskFreeBytes = diskFree;
        }
    }
}