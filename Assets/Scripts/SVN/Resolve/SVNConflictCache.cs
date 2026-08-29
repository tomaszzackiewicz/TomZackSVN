using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SVN.Core
{
    public class SVNConflictCache
    {
        private readonly ConcurrentDictionary<string, SVNConflictData> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public SVNConflictData Get(string path)
        {
            _cache.TryGetValue(path, out var data);
            return data;
        }

        public void AddOrUpdate(SVNConflictData data)
        {
            if (data != null && !string.IsNullOrWhiteSpace(data.Path))
                _cache[data.Path] = data;
        }

        public void Remove(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                _cache.TryRemove(path, out _);
        }

        public void Clear() => _cache.Clear();

        public IEnumerable<SVNConflictData> Values => _cache.Values;

        public void SynchronizeFrom(List<SVNConflictData> latest)
        {
            if (latest == null) return;

            foreach (var c in latest)
                _cache[c.Path] = c;

            var valid = new HashSet<string>(latest.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _cache.Keys.ToList())
            {
                if (!valid.Contains(key))
                    _cache.TryRemove(key, out _);
            }
        }
    }
}