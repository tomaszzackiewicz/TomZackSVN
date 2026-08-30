using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    /// <summary>
    /// S5: JEDNA semantyka "brudna kopia" dla całej aplikacji.
    /// Rozróżnia zmiany wersjonowane (blokują) od unversioned (informują).
    /// </summary>
    public class WorkingCopyDirtyState
    {
        public bool HasVersionedChanges;   // M/A/D/R/C/!/~/props w kol 0 lub 1
        public int UnversionedCount;       // '?' — NIE blokuje (svn update ich nie rusza)
        public int ConflictedCount;        // C w kol 0 lub 1

        /// <summary>Cokolwiek lokalnie Modified/Added/Deleted/Renamed/Conflicted/unversioned.</summary>
        public bool IsDirty => HasVersionedChanges || UnversionedCount > 0;

        /// <summary>Tylko to, co realnie blokuje operacje merge/update -r/reintegrate.</summary>
        public bool IsBlockingDirty => HasVersionedChanges;
    }
}