using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;

namespace SVN.Core
{
    public class SVNMerge : SVNBase, IDisposable
    {
        public event Action<MergeFileResult> OnDryRunCompleted;

        internal static string _cachedSshConfigOption;
        internal static string _lastCachedKeyPath;

        internal float _lastRevertToHeadClickTime = -10f;
        internal float _lastForceMergeClickTime = -10f;
        internal float _lastRepairMergeClickTime = -10f;

        internal bool _branchesCacheValid;
        internal string[] _cachedBranches;
        internal int _isFetchingBranchesFlag;

        internal bool _tagsCacheValid;
        internal string[] _cachedTags;
        internal int _isFetchingTagsFlag;

        internal int _isMergingFlag;
        internal string _cachedRepoRoot;
        internal string _cachedWcRoot;
        internal bool _obstructionsJustDeleted;

        internal bool _hadLocalChangesBeforeMerge;

        internal CancellationTokenSource _mergeCts;

        internal readonly SvnMergeSnapshotManager _snapshotManager;

        // FIX: Widoczne dla klas statycznych w tym samym assembly
        internal SVNManager SVNManager => base.svnManager;
        internal SVNUI SVNUI => base.svnUI;

        public SVNMerge(SVNUI ui, SVNManager manager) : base(ui, manager)
        {
            _snapshotManager = new SvnMergeSnapshotManager(
                () => _cachedWcRoot,
                msg => LogInfo(msg),
                msg => LogWarning(msg));

            manager.OnProjectChanged += OnProjectChangedHandler;
        }

        public void Dispose()
        {
            if (svnManager != null)
                svnManager.OnProjectChanged -= OnProjectChangedHandler;
            _mergeCts?.Cancel();
            _mergeCts?.Dispose();
            _mergeCts = null;
        }

        protected override TMP_Text GetConsole() => svnUI?.MergeConsoleText;

        internal static string SshConfigOption
        {
            get
            {
                string currentKey = SvnRunner.KeyPath;
                if (_cachedSshConfigOption != null &&
                    string.Equals(_lastCachedKeyPath, currentKey, StringComparison.OrdinalIgnoreCase))
                {
                    return _cachedSshConfigOption;
                }

                string sshArgs = "-o BatchMode=yes -o StrictHostKeyChecking=no";
                if (!string.IsNullOrEmpty(currentKey))
                    sshArgs = $"-i '{currentKey}' {sshArgs}";

                _cachedSshConfigOption = $"--config-option config:tunnels:ssh=\"ssh {sshArgs}\" ";
                _lastCachedKeyPath = currentKey;
                return _cachedSshConfigOption;
            }
        }

        internal void OnProjectChangedHandler(SVNProject project)
        {
            _cachedRepoRoot = null;
            _cachedWcRoot = null;
            _branchesCacheValid = false;
            _cachedBranches = null;
            _tagsCacheValid = false;
            _cachedTags = null;
            _obstructionsJustDeleted = false;
            _snapshotManager.ClearRollbackSnapshot();
        }

        internal string EnsureRepoRoot()
        {
            if (!string.IsNullOrWhiteSpace(_cachedRepoRoot)) return _cachedRepoRoot;
            if (svnManager == null || string.IsNullOrWhiteSpace(svnManager.WorkingDir)) return null;

            try
            {
                _cachedRepoRoot = svnManager.GetRepoRoot()?.Trim().TrimEnd('/');
            }
            catch (Exception ex)
            {
                LogWarning($"[SVNMerge] GetRepoRoot failed: {ex.Message}");
            }
            return _cachedRepoRoot;
        }

        // FIX: Dodano ConfigureAwait(false)
        internal async Task<string> GetRepoRootSafeAsync(CancellationToken token = default)
        {
            string root = EnsureRepoRoot();
            if (!string.IsNullOrWhiteSpace(root)) return root;

            if (svnManager != null && !string.IsNullOrWhiteSpace(svnManager.WorkingDir))
            {
                try
                {
                    string output = await SvnRunner.RunAsync("info --show-item repos-root-url", svnManager.WorkingDir, false, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        _cachedRepoRoot = output.Trim().TrimEnd('/');
                        return _cachedRepoRoot;
                    }
                }
                catch { }
            }
            return null;
        }

        // FIX: Dodano ConfigureAwait(false)
        internal async Task EnsureWcRootAsync(CancellationToken token = default)
        {
            if (!string.IsNullOrWhiteSpace(_cachedWcRoot)) return;
            try
            {
                string result = await SvnRunner.RunAsync("info --show-item wc-root", svnManager.WorkingDir, false, token).ConfigureAwait(false);
                _cachedWcRoot = result?.Trim();
            }
            catch
            {
                _cachedWcRoot = svnManager?.WorkingDir;
            }
        }

        internal bool IsReady()
        {
            if (svnManager == null) return false;
            if (string.IsNullOrWhiteSpace(svnManager.WorkingDir)) return false;
            if (!Directory.Exists(svnManager.WorkingDir)) return false;
            if (string.IsNullOrWhiteSpace(SvnRunner.KeyPath) && string.IsNullOrWhiteSpace(svnManager.CurrentKey)) return false;
            return true;
        }

        internal static string Normalize(string url) => string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/').ToLowerInvariant();

        internal bool TryEnterMerging()
        {
            if (Interlocked.CompareExchange(ref _isMergingFlag, 1, 0) != 0)
            {
                LogWarning("[Merge] Operation already in progress.");
                return false;
            }
            return true;
        }

        internal void ExitMerging() => Interlocked.Exchange(ref _isMergingFlag, 0);

        // FIX: Dodano ConfigureAwait(false)
        internal async Task<string[]> GetRepoListAsync(string url, CancellationToken token = default)
        {
            try
            {
                string command = $"{SshConfigOption}list {SvnMergeUrlResolver.EscapeSvnArg(url)} --non-interactive";
                string output = await SvnRunner.RunAsync(command, svnManager.WorkingDir, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();

                return output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().TrimEnd('/'))
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("*"))
                    .Where(x => x.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) < 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogWarning($"[SVN] Failed to list '{url}': {ex.Message}");
                return Array.Empty<string>();
            }
        }

        // FIX: Dodano ConfigureAwait(false)
        internal async Task<bool> HasPendingMergeChanges(CancellationToken token = default)
        {
            try
            {
                string status = await SvnRunner.RunAsync("status --depth=infinity", svnManager.WorkingDir, false, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(status)) return false;

                foreach (string line in status.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.TrimStart();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    char col1 = trimmed.Length > 0 ? trimmed[0] : ' ';
                    char col2 = trimmed.Length > 1 ? trimmed[1] : ' ';
                    if (col1 != ' ' || col2 != ' ') return true;
                }
                return false;
            }
            catch { return true; }
        }

        // FIX: Dodano ConfigureAwait(false)
        internal async Task RefreshResolveUI()
        {
            try { await svnManager.RefreshStatus().ConfigureAwait(false); }
            catch (Exception ex) { LogWarning($"[RefreshResolveUI] {ex.Message}"); }
        }

        // FIX: Dodano ConfigureAwait(false)
        internal async Task SafeCleanupAfterCancel()
        {
            try
            {
                if (_hadLocalChangesBeforeMerge)
                {
                    LogWarning("[SafeCleanup] Local changes existed before merge – automatic revert skipped.");
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, timeoutCts.Token).ConfigureAwait(false);
                    return;
                }

                LogWarning("[SafeCleanup] Reverting unfinished merge changes...");
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await SvnRunner.RunAsync("revert -R .", svnManager.WorkingDir, true, cleanupCts.Token).ConfigureAwait(false);
                await SvnRunner.RunAsync("cleanup", svnManager.WorkingDir, true, cleanupCts.Token).ConfigureAwait(false);
                LogInfo("[SafeCleanup] Working copy restored.");
            }
            catch (Exception ex)
            {
                LogWarning($"[SafeCleanup] {ex.Message}");
            }
        }

        internal void LogInfoBlock(string title, string message)
        {
            LogInfo("====================================");
            LogInfo($"[{title}]");
            if (!string.IsNullOrEmpty(message))
                foreach (var line in message.Split('\n'))
                    LogInfo(line);
            LogInfo("====================================");
        }

        internal void LogSuccessBlock(string title, string message)
        {
            LogSuccess("====================================");
            LogSuccess($"[{title}]");
            if (!string.IsNullOrEmpty(message))
                foreach (var line in message.Split('\n'))
                    LogSuccess(line);
            LogSuccess("====================================");
        }

        internal void LogWarningBlock(string title, string message)
        {
            LogWarning("====================================");
            LogWarning($"[{title}]");
            foreach (var line in message.Split('\n'))
                LogWarning(line);
            LogWarning("====================================");
        }

        internal void RaiseDryRunCompleted(MergeFileResult result)
        {
            OnDryRunCompleted?.Invoke(result);
        }

        public Task CancelMerge()
        {
            _mergeCts?.Cancel();
            LogWarning("[Merge] Cancel requested by user.");
            return Task.CompletedTask;
        }

        public Task ExecuteMerge(string sourceInput, bool isDryRun)
            => SvnMergeOperations.ExecuteMergeAsync(this, sourceInput, isDryRun);

        public Task UndoLastMerge(bool autoCommit = false)
            => SvnMergeOperations.UndoLastMergeAsync(this, autoCommit);

        public Task CancelLocalMerge()
            => SvnMergeOperations.CancelLocalMergeAsync(this);

        public Task RevertToHead()
            => SvnMergeOperations.RevertToHeadAsync(this);

        public Task CompareWithTrunk()
            => SvnMergeOperations.CompareWithTrunkAsync(this);

        public Task<string[]> FetchAvailableBranches(bool force = false)
            => SvnMergeDiscovery.FetchAvailableBranchesAsync(this, force);

        public Task<string[]> FetchAvailableTags(bool force = false)
            => SvnMergeDiscovery.FetchAvailableTagsAsync(this, force);

        public Task RefreshIfEmpty()
            => SvnMergeDiscovery.RefreshIfEmptyAsync(this);

        public Task ForceMergeFromTrunk(string sourceInput = null)
            => SvnMergeOperations.ForceMergeFromTrunkAsync(this, sourceInput);

        public Task RepairMergeHistory()
            => SvnMergeOperations.RepairMergeHistoryAsync(this);

        public class MergeFileResult
        {
            public List<MergeFileInfo> Files = new List<MergeFileInfo>();
            public List<string> SkippedPaths = new List<string>();
            public int Added;
            public int Updated;
            public int Deleted;
            public int Conflicts;
            public int Skipped;
            public bool MergeInfoUpdated;
            public int RealChanges;
            public bool HasTreeConflicts;
        }

        public class MergeFileInfo
        {
            public char State;
            public string Path;
        }
    }
}