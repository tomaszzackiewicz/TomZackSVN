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
            if (maxSeconds <= 0) return false;
            return (DateTime.UtcNow - LastRefreshUtc).TotalSeconds < maxSeconds;
        }

        public void Clear() => Locks.Clear();
    }

    [Serializable]
    public class SVNLockDetails
    {
        public string Path = "";
        public string FullPath = "";
        public string Owner = "";
        public string CreationDate = "";
        public string Comment = "";
    }
}