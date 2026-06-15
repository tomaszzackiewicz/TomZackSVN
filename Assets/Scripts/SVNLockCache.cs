using System;
using System.Collections.Generic;

namespace SVN.Core
{
    [Serializable]
    public class SVNLockCache
    {
        public Dictionary<string, SVNLockDetails> Locks =
            new Dictionary<string, SVNLockDetails>(StringComparer.OrdinalIgnoreCase);

        public DateTime LastRefreshUtc;

        public bool IsValid(double maxSeconds = 60.0)
        {
            return (DateTime.UtcNow - LastRefreshUtc).TotalSeconds < maxSeconds;
        }

        public void Clear()
        {
            Locks.Clear();
        }
    }
}