using System;
using System.IO;
using UnityEngine;

namespace SVN.Core
{
    public class SvnMergeSnapshotManager
    {
        private const string PrefMergeSource = "SVN_UndoMerge_Source";
        private const string PrefMergeRevBefore = "SVN_UndoMerge_RevBefore";
        private const string PrefMergeRevAfter = "SVN_UndoMerge_RevAfter";
        private const string PrefHasRollback = "SVN_UndoMerge_HasRollback";
        private const string PrefMergeTimestamp = "SVN_UndoMerge_Timestamp";

        private readonly Func<string> _getWcRoot;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;

        public string LastMergeSource { get; private set; }
        public bool HasRollbackPoint { get; private set; }
        public string LastMergeRevisionBefore { get; private set; }
        public string LastMergeRevisionAfter { get; private set; }

        public SvnMergeSnapshotManager(Func<string> getWcRoot, Action<string> logInfo, Action<string> logWarning)
        {
            _getWcRoot = getWcRoot;
            _logInfo = logInfo;
            _logWarning = logWarning;
            LoadRollbackSnapshot();
        }

        public void SetSnapshot(string source, string revBefore, string revAfter)
        {
            LastMergeSource = source;
            LastMergeRevisionBefore = revBefore;
            LastMergeRevisionAfter = revAfter;
            HasRollbackPoint = true;
        }

        public void SaveRollbackSnapshot()
        {
            if (!HasRollbackPoint) return;
            try
            {
                string path = GetSnapshotFilePath();
                if (path == null) return;

                var data = new SnapshotData
                {
                    Source = LastMergeSource,
                    RevisionBefore = LastMergeRevisionBefore,
                    RevisionAfter = LastMergeRevisionAfter,
                    Timestamp = DateTime.Now.ToString("o")
                };

                File.WriteAllText(path, JsonUtility.ToJson(data, true));
                _logInfo?.Invoke($"[Snapshot] Saved → {LastMergeSource} | r{LastMergeRevisionBefore} → r{LastMergeRevisionAfter}");
            }
            catch (Exception ex)
            {
                _logWarning?.Invoke($"[Snapshot] File save failed: {ex.Message}");
            }
        }

        public void ClearRollbackSnapshot()
        {
            HasRollbackPoint = false;
            LastMergeSource = null;
            LastMergeRevisionBefore = null;
            LastMergeRevisionAfter = null;

            try
            {
                string path = GetSnapshotFilePath();
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch { }

            _logInfo?.Invoke("[Snapshot] Cleared from memory and file.");
        }

        public void LoadRollbackSnapshot()
        {
            if (PlayerPrefs.GetInt(PrefHasRollback, 0) == 1)
            {
                string src = PlayerPrefs.GetString(PrefMergeSource, "");
                string before = PlayerPrefs.GetString(PrefMergeRevBefore, "");
                string after = PlayerPrefs.GetString(PrefMergeRevAfter, "");

                if (!string.IsNullOrWhiteSpace(src) &&
                    !string.IsNullOrWhiteSpace(before) &&
                    !string.IsNullOrWhiteSpace(after))
                {
                    SetSnapshot(src, before, after);
                    string ts = PlayerPrefs.GetString(PrefMergeTimestamp, "unknown");
                    _logInfo?.Invoke($"[Snapshot] Migrated from PlayerPrefs → {src} | r{before} → r{after} | Timestamp: {ts}");
                    SaveRollbackSnapshot();
                }

                PlayerPrefs.DeleteKey(PrefMergeSource);
                PlayerPrefs.DeleteKey(PrefMergeRevBefore);
                PlayerPrefs.DeleteKey(PrefMergeRevAfter);
                PlayerPrefs.DeleteKey(PrefHasRollback);
                PlayerPrefs.DeleteKey(PrefMergeTimestamp);
                PlayerPrefs.Save();
                return;
            }

            try
            {
                string path = GetSnapshotFilePath();
                if (path == null || !File.Exists(path)) return;

                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<SnapshotData>(json);

                if (data == null ||
                    string.IsNullOrWhiteSpace(data.Source) ||
                    string.IsNullOrWhiteSpace(data.RevisionBefore) ||
                    string.IsNullOrWhiteSpace(data.RevisionAfter))
                    return;

                SetSnapshot(data.Source, data.RevisionBefore, data.RevisionAfter);
                _logInfo?.Invoke($"[Snapshot] Loaded from file: {data.Source} | r{data.RevisionBefore} → r{data.RevisionAfter} | Timestamp: {data.Timestamp}");
            }
            catch (Exception ex)
            {
                _logWarning?.Invoke($"[Snapshot] File load failed: {ex.Message}");
            }
        }

        private string GetSnapshotFilePath()
        {
            string wcRoot = _getWcRoot?.Invoke();
            if (string.IsNullOrWhiteSpace(wcRoot)) return null;
            return Path.Combine(wcRoot, ".svn", "merge_snapshot.json");
        }

        [Serializable]
        private class SnapshotData
        {
            public string Source;
            public string RevisionBefore;
            public string RevisionAfter;
            public string Timestamp;
        }
    }
}