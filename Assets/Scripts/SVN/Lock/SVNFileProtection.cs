using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    /// <summary>
    /// MODUŁ: Ochrona plików przed przypadkową edycją przez innych (svn:needs-lock).
    /// Kolory: sukces #66FF66 / ostrzeżenie #FFA500 / błąd #FF5555 / BLOCKED #FF4500 /
    /// nagłówki #1E90FF, reszta tekstu BIAŁA (czytelność na szarym tle panelu).
    /// </summary>
    public class SVNFileProtection : SVNBase
    {
        private int _processingFlag;

        private const bool RunTransportDiagnostics = false;

        /// <summary>
        /// Polityka Unprotect:
        ///  false (default) — pliki BEZ locka: dozwolone z ostrzeżeniem;
        ///                    pliki z CUDZYM lockiem: zawsze zablokowane (przed propdelem!).
        ///  true (strict)   — do zdjęcia ochrony wymagany TWÓJ lock.
        /// </summary>
        private const bool RequireLockToUnprotect = false;

        public SVNFileProtection(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager) { }

        private bool TryEnterProcessing()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1) return false;
            IsProcessing = true;
            return true;
        }

        private void ExitProcessing()
        {
            IsProcessing = false;
            Interlocked.Exchange(ref _processingFlag, 0);
        }

        // ===================================================================
        //  KLASYFIKACJA BŁĘDÓW SVN
        // ===================================================================

        private enum PropFailKind { NotVersioned, DirectoryNode, NoProperty, Unknown }

        private static PropFailKind ClassifyPropError(string msg, bool isPropdel)
        {
            if (string.IsNullOrEmpty(msg))
                return PropFailKind.Unknown;

            if (isPropdel &&
                (msg.Contains("nonexistent property") ||
                 msg.Contains("E200017")))
                return PropFailKind.NoProperty;

            // === FIX E155010: "The node '...' was not found" — propset na pliku
            // '?' (Twój klient raportuje to jako E155010 zamiast E155007).
            // Frazy PRZED kodami (dokładnie jak przy E200009 — kody bywają
            // generyczne i różnią się między subkomendami/klientami):
            if (msg.Contains("not under version control") ||
                msg.Contains("not a working copy") ||
                msg.Contains("was not found") ||           // ← E155010 phrasing
                msg.Contains("E155007") ||
                msg.Contains("W155007") ||
                msg.Contains("E155010"))
                return PropFailKind.NotVersioned;

            if (msg.Contains("directory"))
                return PropFailKind.DirectoryNode;

            if (msg.Contains("E200009"))
                return PropFailKind.NotVersioned;

            return PropFailKind.Unknown;
        }

        // ===================================================================
        //  GUARD: czy SVN mówi o NASZYM pliku?
        // ===================================================================

        private static List<string> ExtractQuotedPaths(string msg)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(msg)) return result;

            int i = 0;
            while (i < msg.Length)
            {
                int s = msg.IndexOf('\'', i);
                if (s < 0) break;
                int e = msg.IndexOf('\'', s + 1);
                if (e < 0) break;

                string token = msg.Substring(s + 1, e - s - 1);
                if (token.IndexOf('/') >= 0 || token.IndexOf('\\') >= 0)
                    result.Add(token);

                i = e + 1;
            }
            return result;
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            string na = a.Replace('\\', '/').Trim().TrimEnd('/');
            string nb = b.Replace('\\', '/').Trim().TrimEnd('/');
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAncestorOrEqual(string maybeAncestor, string path)
        {
            if (string.IsNullOrWhiteSpace(maybeAncestor) || string.IsNullOrWhiteSpace(path)) return false;
            string a = maybeAncestor.Replace('\\', '/').Trim().TrimEnd('/');
            string p = path.Replace('\\', '/').Trim().TrimEnd('/');
            return string.Equals(a, p, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ErrorAddressesDifferentPath(string msg, string clean, string rel)
        {
            var quoted = ExtractQuotedPaths(msg);
            if (quoted.Count == 0) return false;

            foreach (var q in quoted)
            {
                if (PathsEqual(q, clean) || PathsEqual(q, rel)) return false;
                if (IsAncestorOrEqual(q, clean)) return false;
            }
            return true;
        }

        // ===================================================================
        //  DIAGNOSTYKA TRANSPORTU (wyłączona)
        // ===================================================================

        private static string OneLine(string s) =>
            string.IsNullOrEmpty(s) ? "(empty)" : s.Replace("\r", "").Replace("\n", " / ").Trim();

        private static async Task<(int exit, string stdout, string stderr)> RunDirectSvnAsync(
            string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "svn",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            if (!string.IsNullOrEmpty(workingDir))
                psi.WorkingDirectory = workingDir;

            using var p = Process.Start(psi) ?? throw new Exception("Process.Start(svn) returned null.");
            string so = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string se = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } p.WaitForExit(5000); }
            return (p.ExitCode, so, se);
        }

        private async Task RunTransportDiagnosticsAsync(string clean, string root)
        {
            LogToLockPanel("<color=#FFA500><b>=== TRANSPORT DIAGNOSTICS ===</b></color>");
            try
            {
                string rel = MakeRelativePath(root, clean) ?? clean;

                LogToLockPanel($"  <color=#FFFFFF>env.CurrentDirectory : {Environment.CurrentDirectory}</color>");
                LogToLockPanel($"  <color=#FFFFFF>manager.WorkingDir   : {root}</color>");

                var v = await RunDirectSvnAsync("--version", null).ConfigureAwait(false);
                LogToLockPanel($"  svn --version [DIRECT] : exit {v.exit}");

                var st = await SvnRunner.RunDetailedAsync($"status \"{rel}\"", root, false, false).ConfigureAwait(false);
                LogToLockPanel($"  [SvnRunner] status  \"{rel}\" → exit {st.exitCode} | out: {OneLine(st.output)} | err: {OneLine(st.error)}");

                var pg = await SvnRunner.RunDetailedAsync($"propget svn:needs-lock \"{rel}\"", root, false, false).ConfigureAwait(false);
                LogToLockPanel($"  [SvnRunner] propget \"{rel}\" → exit {pg.exitCode} | err: {OneLine(pg.error)}");

                var ps = await SvnRunner.RunDetailedAsync($"propset svn:needs-lock \"\" \"{rel}\"", root, false, false).ConfigureAwait(false);
                LogToLockPanel($"  [SvnRunner] propset(\"\") \"{rel}\" → exit {ps.exitCode} | err: {OneLine(ps.error)}");
            }
            catch (Exception ex)
            {
                LogToLockPanel($"  <color=#FF5555>diagnostics error: {ex.Message}</color>");
            }
            LogToLockPanel("<color=#FFA500><b>=== END DIAGNOSTICS ===</b></color>");
        }

        // ===================================================================
        //  SNAPSHOT STANÓW OCHRONY
        // ===================================================================

        public enum ProtectionState { Protected, PendingAdd, PendingRemoval }

        public sealed class ProtectionInfo
        {
            public string Path;
            public ProtectionState State;
            public string LockOwner;
            public bool LockIsMine;
        }

        public async Task<List<ProtectionInfo>> GetProtectionSnapshotAsync(CancellationToken token = default)
        {
            var result = new List<ProtectionInfo>();
            string root = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(root)) return result;

            var active = await SvnRunner.GetProtectedPathsAsync(root, token).ConfigureAwait(false);
            var propertyModified = await GetPropertyModifiedPathsAsync(root, token).ConfigureAwait(false);

            await EnsureLockCacheFreshAsync().ConfigureAwait(false);

            foreach (var p in active)
            {
                var info = new ProtectionInfo
                {
                    Path = p,
                    State = propertyModified.Contains(p) ? ProtectionState.PendingAdd : ProtectionState.Protected
                };
                FillLockInfo(info);
                result.Add(info);
            }

            foreach (var p in propertyModified)
            {
                if (active.Contains(p)) continue;

                if (await IsNeedsLockInBaseAsync(p, root, token).ConfigureAwait(false))
                {
                    var info = new ProtectionInfo { Path = p, State = ProtectionState.PendingRemoval };
                    FillLockInfo(info);
                    result.Add(info);
                }
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return result;
        }

        private static async Task<HashSet<string>> GetPropertyModifiedPathsAsync(string root, CancellationToken token = default)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(root)) return set;

            try
            {
                var st = await SvnRunner.RunDetailedAsync("status", root, false, false, token)
                    .ConfigureAwait(false);

                foreach (var raw in (st.output ?? "").Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length < 9) continue;

                    if (line[1] == 'M')
                    {
                        string p = SvnRunner.CleanSvnPath(line.Substring(8));
                        if (!string.IsNullOrWhiteSpace(p)) set.Add(p);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            return set;
        }

        private static async Task<bool> IsNeedsLockInBaseAsync(string rel, string root, CancellationToken token = default)
        {
            try
            {
                var r = await SvnRunner.RunDetailedAsync(
                    $"proplist -r BASE --xml \"{rel}\"", root, false, false, token).ConfigureAwait(false);
                return (r.output ?? "").Contains("svn:needs-lock");
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private void FillLockInfo(ProtectionInfo info)
        {
            info.LockOwner = null;
            info.LockIsMine = false;
            try
            {
                if (svnManager.LockCache.Locks.TryGetValue(info.Path, out var li))
                {
                    info.LockOwner = (li.Owner ?? "").Trim();
                    string me = (svnManager.CurrentUserName ?? "").Trim();
                    info.LockIsMine = !string.IsNullOrEmpty(info.LockOwner) &&
                        info.LockOwner.Equals(me, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
        }

        private async Task EnsureLockCacheFreshAsync()
        {
            var lockModule = svnManager.GetModule<SVNLock>();
            if (lockModule != null && !svnManager.LockCache.IsValid())
                await lockModule.RefreshLockCacheAsync(true).ConfigureAwait(false);
        }

        // ===================================================================
        //  PROTECT
        // ===================================================================

        public async Task ProtectFiles(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                LogToLockPanel("<color=#FFA500>[Protect] No files selected.</color>");
                return;
            }
            if (!TryEnterProcessing()) return;

            string root = svnManager.WorkingDir;
            LogToLockPanel($"<b><color=#1E90FF>[Protect]</color></b> Processing {paths.Count} item(s)...", append: false);

            foreach (var p in paths)
                LogToLockPanel($"<color=#FFFFFF> - {p}</color>");

            var protectedFiles = new List<string>();
            int addedCount = 0;
            int skippedDirs = 0;
            int mismatchCount = 0;
            int plumbingCount = 0;
            int failed = 0;

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                if (RunTransportDiagnostics)
                {
                    string firstClean = SvnRunner.ForceCleanPath(paths[0] ?? "");
                    if (File.Exists(firstClean))
                        await RunTransportDiagnosticsAsync(firstClean, root).ConfigureAwait(false);
                }

                foreach (var file in paths)
                {
                    if (string.IsNullOrWhiteSpace(file)) { failed++; continue; }

                    string clean = SvnRunner.ForceCleanPath(file);
                    if (string.IsNullOrWhiteSpace(clean)) { failed++; continue; }

                    if (Directory.Exists(clean))
                    {
                        LogToLockPanel($"<color=#FFA500>Skipped (folder — protection applies to files only): {clean}</color>");
                        skippedDirs++;
                        continue;
                    }

                    if (!File.Exists(clean))
                    {
                        LogToLockPanel($"<color=#FFA500>Skipped (not found on disk): {clean}</color>");
                        failed++;
                        continue;
                    }

                    string rel = MakeRelativePath(root, clean);
                    if (string.IsNullOrWhiteSpace(rel))
                    {
                        LogToLockPanel($"<color=#FF5555>Skipped (outside working copy): {clean}</color>");
                        failed++;
                        continue;
                    }

                    bool wasAdded = false;
                    bool ok = false;

                    try
                    {
                        await SvnRunner.RunAsync($"propset svn:needs-lock \"\" \"{clean}\"", root)
                            .ConfigureAwait(false);
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        string msg = ex.Message ?? "";

                        if (ErrorAddressesDifferentPath(msg, clean, rel))
                        {
                            LogPlumbingBug(clean, rel, root, msg);
                            plumbingCount++;
                        }
                        else if (ClassifyPropError(msg, isPropdel: false) == PropFailKind.NotVersioned)
                        {
                            try
                            {
                                await SvnRunner.RunAsync($"add --parents \"{clean}\"", root)
                                    .ConfigureAwait(false);
                                await SvnRunner.RunAsync($"propset svn:needs-lock \"\" \"{clean}\"", root)
                                    .ConfigureAwait(false);
                                wasAdded = true;
                                ok = true;
                            }
                            catch (Exception ex2)
                            {
                                string msg2 = ex2.Message ?? "";

                                if (ErrorAddressesDifferentPath(msg2, clean, rel))
                                {
                                    LogPlumbingBug(clean, rel, root, msg2);
                                    plumbingCount++;
                                }
                                else
                                {
                                    try { await SvnRunner.RunAsync($"revert \"{clean}\"", root).ConfigureAwait(false); }
                                    catch { }

                                    LogToLockPanel(
                                        $"<color=#FF5555>Failed (could not add to SVN): {clean}</color>\n" +
                                        $"<color=#FFFFFF>SVN: {msg2}</color>");
                                    failed++;
                                }
                            }
                        }
                        else if (ClassifyPropError(msg, isPropdel: false) == PropFailKind.DirectoryNode)
                        {
                            LogMismatch(clean, rel, msg);
                            mismatchCount++;
                        }
                        else
                        {
                            LogToLockPanel(
                                $"<color=#FF5555>Failed: {clean}</color>\n" +
                                $"<color=#FFFFFF>SVN: {msg}</color>");
                            failed++;
                        }
                    }

                    if (ok)
                    {
                        protectedFiles.Add(clean);
                        if (wasAdded) addedCount++;

                        LogToLockPanel(wasAdded
                            ? $"<color=#66FF66>Added to SVN + protected (local):</color> {Path.GetFileName(clean)}"
                            : $"<color=#66FF66>Protected (local):</color> {Path.GetFileName(clean)}");
                    }
                }

                if (protectedFiles.Count == 0)
                {
                    var s = new StringBuilder("<color=#FF5555>[Protect] Nothing protected.</color>");
                    if (mismatchCount > 0)
                        s.Append($"\n<color=#FF5555>{mismatchCount} item(s): working copy out of sync — run Update / 'svn cleanup' and retry.</color>");
                    if (plumbingCount > 0)
                        s.Append($"\n<color=#FF5555>{plumbingCount} command(s) addressed the WRONG path (details above). Do NOT run cleanup for those.</color>");
                    if (skippedDirs > 0)
                        s.Append($"\n<color=#FFA500>{skippedDirs} folder(s) skipped — files only.</color>");
                    if (failed > 0)
                        s.Append($"\n<color=#FF5555>{failed} item(s) failed.</color>");
                    LogToLockPanel(s.ToString());
                    return;
                }

                await EnsureLockCacheFreshAsync().ConfigureAwait(false);
                string me = (svnManager.CurrentUserName ?? "").Trim();

                var committable = new List<string>();
                var foreignLocked = new List<(string path, string owner)>();

                foreach (var clean in protectedFiles)
                {
                    string lockKey = MakeRelativePath(root, clean) ?? "";
                    if (!string.IsNullOrEmpty(lockKey) &&
                        svnManager.LockCache.Locks.TryGetValue(lockKey, out var li))
                    {
                        string owner = (li.Owner ?? "").Trim();
                        if (!string.IsNullOrEmpty(owner) &&
                            !owner.Equals(me, StringComparison.OrdinalIgnoreCase))
                        {
                            foreignLocked.Add((clean, owner));
                            continue;
                        }
                    }
                    committable.Add(clean);
                }

                bool commitOk = true;
                if (committable.Count > 0)
                {
                    try
                    {
                        await RunWithTargetsAsync(
                            "commit -m \"[SVN] protect files (svn:needs-lock)\" --no-unlock",
                            committable, root).ConfigureAwait(false);
                    }
                    catch (Exception cex)
                    {
                        commitOk = false;
                        LogToLockPanel(
                            $"<color=#FF5555>Commit FAILED: {cex.Message}</color>\n" +
                            "<color=#FFA500>→ Protection is saved LOCALLY (PENDING ADD). Fix the issue above, " +
                            "then run Protect again to publish (or commit the files manually).</color>");
                    }
                }

                var summary = new StringBuilder();
                if (commitOk && committable.Count > 0)
                    summary.Append($"<color=#66FF66>Published to repository: {committable.Count} file(s) — read-only for everyone without a lock.</color>");
                else if (!commitOk)
                    summary.Append($"<color=#FFA500>Saved locally, NOT committed: {committable.Count} file(s) (PENDING ADD) — run Protect again after fixing the commit error above.</color>");
                else
                    summary.Append("<color=#FFA500>Nothing committed — see details below.</color>");

                if (addedCount > 0)
                    summary.Append($"\n<color=#FFFFFF>{addedCount} file(s) were new — added to SVN in the same commit.</color>");
                if (foreignLocked.Count > 0)
                {
                    summary.Append($"\n<color=#8B0000>{foreignLocked.Count} file(s) saved locally but NOT committed — locked by another user:</color>");
                    foreach (var fl in foreignLocked)
                        summary.Append($"\n<color=#8B0000>   - {Path.GetFileName(fl.path)} (lock: {fl.owner})</color>");
                    summary.Append("\n<color=#FFFFFF>→ Steal/release the lock in the Locks panel, then run Protect again to publish.</color>");
                }
                if (mismatchCount > 0)
                    summary.Append($"\n<color=#FFA500>{mismatchCount} item(s) skipped — working copy out of sync (run Update / cleanup).</color>");
                if (plumbingCount > 0)
                    summary.Append($"\n<color=#FF5555>{plumbingCount} command(s) addressed the WRONG path (details above).</color>");
                if (skippedDirs > 0)
                    summary.Append($"\n<color=#FFA500>{skippedDirs} folder(s) skipped (files only).</color>");
                if (failed > 0)
                    summary.Append($"\n<color=#FF5555>{failed} item(s) failed.</color>");
                if (!commitOk || foreignLocked.Count > 0)
                    summary.Append("\n<color=#FFFFFF>→ Tip: 'Show Protected' lists ALL current states (including pending).</color>");
                LogToLockPanel(summary.ToString());

                svnManager.DiskChangesDetected = true;
                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=#FFA500>[Protect] Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FF5555>Protect Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ===================================================================
        //  UNPROTECT — z POLITYKĄ locków i nowym kolorem BLOCKED
        // ===================================================================

        public async Task UnprotectFiles(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                LogToLockPanel("<color=#FFA500>[Unprotect] No files selected.</color>");
                return;
            }
            if (!TryEnterProcessing()) return;

            string root = svnManager.WorkingDir;
            LogToLockPanel($"<b><color=#1E90FF>[Unprotect]</color></b> Processing {paths.Count} item(s)...", append: false);

            foreach (var p in paths)
                LogToLockPanel($"<color=#FFFFFF> - {p}</color>");

            var committable = new List<string>();
            int skippedDirs = 0;
            int mismatchCount = 0;
            int plumbingCount = 0;
            int notProtected = 0;
            int failed = 0;
            int lockRequiredCount = 0;
            var foreignBlocked = new List<(string path, string owner)>();

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var activeSet = await SvnRunner.GetProtectedPathsAsync(root).ConfigureAwait(false);
                var propertyModified = await GetPropertyModifiedPathsAsync(root).ConfigureAwait(false);
                await EnsureLockCacheFreshAsync().ConfigureAwait(false);
                string me = (svnManager.CurrentUserName ?? "").Trim();

                foreach (var file in paths)
                {
                    if (string.IsNullOrWhiteSpace(file)) { failed++; continue; }

                    string clean = SvnRunner.ForceCleanPath(file);
                    if (string.IsNullOrWhiteSpace(clean)) { failed++; continue; }

                    if (Directory.Exists(clean))
                    {
                        LogToLockPanel($"<color=#FFA500>Skipped (folder): {clean}</color>");
                        skippedDirs++;
                        continue;
                    }

                    if (!File.Exists(clean))
                    {
                        LogToLockPanel($"<color=#FFA500>Skipped (not found on disk): {clean}</color>");
                        failed++;
                        continue;
                    }

                    string rel = MakeRelativePath(root, clean);
                    if (string.IsNullOrWhiteSpace(rel))
                    {
                        LogToLockPanel($"<color=#FF5555>Skipped (outside working copy): {clean}</color>");
                        failed++;
                        continue;
                    }

                    bool pendingRemoval = propertyModified.Contains(rel) && !activeSet.Contains(rel);

                    string lockOwner = null;
                    bool lockIsMine = false;
                    if (svnManager.LockCache.Locks.TryGetValue(rel, out var li))
                    {
                        lockOwner = (li.Owner ?? "").Trim();
                        lockIsMine = !string.IsNullOrEmpty(lockOwner) &&
                                     lockOwner.Equals(me, StringComparison.OrdinalIgnoreCase);
                    }

                    if (lockOwner != null && !lockIsMine)
                    {
                        LogToLockPanel(
                            $"<color=#8B0000>[BLOCKED] {Path.GetFileName(clean)} — locked by <b>{lockOwner}</b>.</color>\n" +
                            (pendingRemoval
                                ? "<color=#FFFFFF>   (pending removal from an earlier run is waiting — it will be committed once YOU hold the lock; Steal it first)</color>"
                                : "<color=#FFFFFF>   File is being worked on — nothing was changed. Ask the owner or Steal the lock.</color>"));
                        foreignBlocked.Add((clean, lockOwner));
                        continue;
                    }

                    if (lockOwner == null && RequireLockToUnprotect)
                    {
                        LogToLockPanel(
                            $"<color=#FFA500>[LOCK REQUIRED] {Path.GetFileName(clean)} — nobody holds a lock.</color>\n" +
                            "<color=#FFFFFF>   Take the lock first (Locks panel), then Unprotect again.</color>");
                        lockRequiredCount++;
                        continue;
                    }

                    if (lockOwner == null)
                    {
                        LogToLockPanel(
                            $"<color=#FFA500>Warning: no lock held — removing protection and publishing the change to the whole team</color>\n" +
                            $"<color=#FFFFFF>   ({Path.GetFileName(clean)}; removal will be visible in svn log with your name)</color>");
                    }

                    if (pendingRemoval)
                    {
                        committable.Add(clean);
                        LogToLockPanel($"<color=#FFA500>[PENDING-] {Path.GetFileName(clean)} — removal saved locally (earlier run), will be committed now.</color>");
                        continue;
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"propdel svn:needs-lock \"{clean}\"", root)
                            .ConfigureAwait(false);

                        committable.Add(clean);
                        LogToLockPanel($"<color=#66FF66>Protection removed (local):</color> {Path.GetFileName(clean)}");
                    }
                    catch (Exception ex)
                    {
                        string msg = ex.Message ?? "";

                        if (ErrorAddressesDifferentPath(msg, clean, rel))
                        {
                            LogPlumbingBug(clean, rel, root, msg);
                            plumbingCount++;
                            continue;
                        }

                        switch (ClassifyPropError(msg, isPropdel: true))
                        {
                            case PropFailKind.NoProperty:
                                LogToLockPanel($"<color=#FFA500>Was not protected (nothing to remove): {Path.GetFileName(clean)}</color>");
                                notProtected++;
                                break;

                            case PropFailKind.NotVersioned:
                                LogToLockPanel($"<color=#FFA500>Skipped (not in SVN — no protection to remove): {clean}</color>");
                                notProtected++;
                                break;

                            case PropFailKind.DirectoryNode:
                                LogMismatch(clean, rel, msg);
                                mismatchCount++;
                                break;

                            default:
                                LogToLockPanel(
                                    $"<color=#FF5555>Failed: {clean}</color>\n" +
                                    $"<color=#FFFFFF>SVN: {msg}</color>");
                                failed++;
                                break;
                        }
                    }
                }

                if (committable.Count == 0)
                {
                    var s = new StringBuilder("<color=#FFA500>[Unprotect] Nothing to commit.</color>");
                    if (foreignBlocked.Count > 0)
                        s.Append($"\n<color=#8B0000>{foreignBlocked.Count} file(s) BLOCKED — locked by another user (details above). Nothing was changed.</color>");
                    if (lockRequiredCount > 0)
                        s.Append($"\n<color=#FFA500>{lockRequiredCount} file(s) skipped — lock required first (strict mode).</color>");
                    if (mismatchCount > 0)
                        s.Append($"\n<color=#FFA500>{mismatchCount} item(s): working copy out of sync — run Update / 'svn cleanup' and retry.</color>");
                    if (plumbingCount > 0)
                        s.Append($"\n<color=#FF5555>{plumbingCount} command(s) addressed the WRONG path (details above).</color>");
                    if (notProtected > 0)
                        s.Append($"\n<color=#FFA500>{notProtected} file(s) had no protection.</color>");
                    if (skippedDirs > 0)
                        s.Append($"\n<color=#FFA500>{skippedDirs} folder(s) skipped.</color>");
                    if (failed > 0)
                        s.Append($"\n<color=#FF5555>{failed} item(s) failed.</color>");
                    LogToLockPanel(s.ToString());
                    return;
                }

                bool commitOk = true;
                try
                {
                    await RunWithTargetsAsync(
                        "commit -m \"[SVN] remove file protection\" --no-unlock",
                        committable, root).ConfigureAwait(false);
                }
                catch (Exception cex)
                {
                    commitOk = false;
                    LogToLockPanel(
                        $"<color=#FF5555>Commit FAILED: {cex.Message}</color>\n" +
                        "<color=#FFA500>→ Removal is saved LOCALLY (PENDING REMOVAL) — repo still has the protection. " +
                        "Fix the issue above, then run Unprotect again (or 'svn revert <file>' to restore).</color>");
                }

                var summary = new StringBuilder();
                if (commitOk)
                    summary.Append($"<color=#66FF66>Removal published to repository: {committable.Count} file(s). " +
                                   "Files become editable for everyone after their Update.</color>");
                else
                    summary.Append($"<color=#FFA500>Removal saved locally, NOT committed: {committable.Count} file(s) (PENDING REMOVAL) — " +
                                   "repository still protects them. Run Unprotect again after fixing the error above.</color>");

                if (foreignBlocked.Count > 0)
                {
                    summary.Append($"\n<color=#8B0000>{foreignBlocked.Count} file(s) BLOCKED (locked by another user — untouched):</color>");
                    foreach (var fb in foreignBlocked)
                        summary.Append($"\n<color=#8B0000>   - {Path.GetFileName(fb.path)} (lock: {fb.owner})</color>");
                    summary.Append("\n<color=#FFFFFF>   → Ask the owner, or Steal in the Locks panel, then Unprotect again.</color>");
                }
                if (lockRequiredCount > 0)
                    summary.Append($"\n<color=#FFA500>{lockRequiredCount} file(s) skipped — lock required first (strict mode).</color>");
                if (mismatchCount > 0)
                    summary.Append($"\n<color=#FFA500>{mismatchCount} item(s) skipped — working copy out of sync.</color>");
                if (plumbingCount > 0)
                    summary.Append($"\n<color=#FF5555>{plumbingCount} command(s) addressed the WRONG path (details above).</color>");
                if (notProtected > 0)
                    summary.Append($"\n<color=#FFA500>{notProtected} file(s) had no protection (skipped).</color>");
                if (skippedDirs > 0)
                    summary.Append($"\n<color=#FFA500>{skippedDirs} folder(s) skipped.</color>");
                if (failed > 0)
                    summary.Append($"\n<color=#FF5555>{failed} item(s) failed.</color>");
                if (!commitOk || foreignBlocked.Count > 0 || lockRequiredCount > 0)
                    summary.Append("\n<color=#FFFFFF>→ Tip: 'Show Protected' lists ALL current states (including pending).</color>");
                LogToLockPanel(summary.ToString());

                svnManager.DiskChangesDetected = true;
                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=#FFA500>[Unprotect] Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FF5555>Unprotect Error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ===================================================================
        //  PANEL: SHOW PROTECTED
        // ===================================================================

        public async void ShowProtectedButton() => await ShowProtectedFiles();

        public async Task ShowProtectedFiles()
        {
            if (!TryEnterProcessing()) return;

            string root = svnManager.WorkingDir;
            LogToLockPanel($"<b><color=#1E90FF>[Protected]</color></b> Scanning working copy: {root}", append: false);

            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                var snapshot = await GetProtectionSnapshotAsync().ConfigureAwait(false);

                if (snapshot.Count == 0)
                {
                    LogToLockPanel(
                        "<color=#FFFFFF>No protection states in this working copy.</color>\n" +
                        "<color=#FFFFFF>Property svn:needs-lock is versioned — it reaches your copy only after Update.</color>");
                    return;
                }

                var committed = snapshot.Where(i => i.State == ProtectionState.Protected).ToList();
                var pendingAdds = snapshot.Where(i => i.State == ProtectionState.PendingAdd).ToList();
                var pendingRemovals = snapshot.Where(i => i.State == ProtectionState.PendingRemoval).ToList();

                var sb = new StringBuilder();
                sb.Append($"<b><color=#1E90FF>=== PROTECTION STATUS — {root} ===</color></b>");

                if (committed.Count > 0)
                {
                    sb.Append($"\n\n<color=#66FF66><b>PROTECTED — active in repository ({committed.Count}):</b></color>\n");
                    foreach (var i in committed)
                    {
                        if (i.LockIsMine)
                            sb.Append($"  <color=#66FF66>[EDITABLE]</color>  {i.Path}   <color=#FFFFFF>← lock: you</color>\n");
                        else if (i.LockOwner != null)
                            sb.Append($"  <color=#FF5555>[READ-ONLY]</color> {i.Path}   <color=#FFFFFF>← lock: {i.LockOwner} (not yours)</color>\n");
                        else
                            sb.Append($"  <color=#FFA500>[READ-ONLY]</color>  {i.Path}   <color=#FFFFFF>← no lock — read-only for EVERYONE (you too) until locked</color>\n");
                    }
                }

                if (pendingAdds.Count > 0)
                {
                    sb.Append($"\n<color=#FFFFFF><b>PENDING ADD — saved locally, NOT committed ({pendingAdds.Count}):</b></color>\n");
                    foreach (var i in pendingAdds)
                        sb.Append($"  <color=#FFFFFF>[PENDING+]</color> {i.Path}\n");
                    sb.Append("  <color=#FFFFFF>→ Publish: run Protect again (or Commit). Cancel: Unprotect the file / 'svn revert <file>'.</color>\n");
                }

                if (pendingRemovals.Count > 0)
                {
                    sb.Append($"\n<color=#FFA500><b>PENDING REMOVAL — removal saved locally, repository STILL protected ({pendingRemovals.Count}):</b></color>\n");
                    foreach (var i in pendingRemovals)
                    {
                        sb.Append($"  <color=#FFA500>[PENDING-]</color> {i.Path}");
                        if (i.LockOwner != null && !i.LockIsMine)
                            sb.Append($"   <color=#8B0000>← blocked: lock {i.LockOwner}</color>");
                        sb.Append("\n");
                    }
                    sb.Append("  <color=#FFFFFF>→ Finish: release/Steal the lock (Locks panel), then Unprotect again. " +
                              "Restore protection: 'svn revert <file>'.</color>\n");
                }

                sb.Append("\n<color=#FFFFFF>LEGEND: [EDITABLE] you hold the lock | [READ-ONLY] no/foreign lock | " +
                          "[PENDING+]/[PENDING-] local change awaiting commit.\n" +
                          "Note: the read-only attribute follows your LOCAL property state — after removal the file is " +
                          "editable in your copy immediately, but the repository (and other users) still protect it " +
                          "until the commit lands and they run Update.</color>");

                LogToLockPanel(sb.ToString(), append: false);
            }
            catch (OperationCanceledException)
            {
                LogToLockPanel("<color=#FFA500>[Protected] Operation cancelled.</color>");
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FF5555>Protected list error:</color> {ex.Message}");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // ===================================================================
        //  UNPROTECT ALL
        // ===================================================================

        public async void UnprotectAllButton()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrWhiteSpace(root)) return;

            List<ProtectionInfo> snapshot;
            try
            {
                snapshot = await GetProtectionSnapshotAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FF5555>Unprotect All: snapshot failed — {ex.Message}</color>");
                return;
            }

            if (snapshot.Count == 0)
            {
                LogToLockPanel("<color=#FFFFFF>[Unprotect All] No protection states in this working copy.</color>", append: false);
                return;
            }

            var targets = snapshot
                .Select(i => Path.Combine(root, i.Path.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();

            await UnprotectFiles(targets);
        }

        // ===================================================================
        //  Pomocnicze
        // ===================================================================

        private void LogMismatch(string clean, string rel, string svnMsg)
        {
            LogToLockPanel(
                $"<color=#FF5555>Mismatch (SVN reports this node as a directory): {clean}</color>\n" +
                $"<color=#FFFFFF>SVN: {svnMsg}</color>\n" +
                $"<color=#FF5555>→ Run Update / 'svn cleanup' and retry. If that doesn't help, re-register the node:</color>\n" +
                $"<color=#FFFFFF>   svn rm --keep-local \"{rel}\"  then: svn add \"{rel}\"</color>");
        }

        private void LogPlumbingBug(string clean, string rel, string root, string msg)
        {
            var reported = ExtractQuotedPaths(msg);
            string reportedStr = reported.Count > 0 ? string.Join("  |  ", reported) : "(no path in message)";

            LogToLockPanel(
                $"<color=#FF5555>[PLUMBING] SVN addressed a DIFFERENT path than requested!</color>\n" +
                $"   requested : {clean}\n" +
                $"   SVN says  : {reportedStr}\n" +
                $"<color=#FFFFFF>   SVN: {msg}</color>\n" +
                $"<color=#FF5555>   → If the cited path is a subdirectory of the CWD: your svn client EXPANDS wildcards in arguments (MSYS/Cygwin-style) — remove any '*' from commands and/or point SVN_SvnExePath at a native svn.exe.</color>");
        }

        private static string MakeRelativePath(string root, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath))
                return null;

            string r = root.Replace('\\', '/').TrimEnd('/');
            string f = fullPath.Replace('\\', '/').TrimEnd('/');

            if (string.Equals(f, r, StringComparison.OrdinalIgnoreCase))
                return ".";

            if (f.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
                return f.Substring(r.Length + 1);

            return null;
        }

        private static async Task RunWithTargetsAsync(string command, List<string> files, string root)
        {
            string targetsFile = Path.Combine(Path.GetTempPath(), $"svn_prot_{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(targetsFile, files, new UTF8Encoding(false)).ConfigureAwait(false);
            try
            {
                await SvnRunner.RunAsync($"{command} --targets \"{targetsFile}\"", root).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(targetsFile)) File.Delete(targetsFile); } catch { }
            }
        }

        private void LogToLockPanel(string message, bool append = true)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI?.LockDisplayArea != null)
                    SVNLogBridge.UpdateUIField(svnUI.LockDisplayArea, message, append: append);
                else
                    SVNLogBridge.LogLine(message, append);
            });
        }

        // ===================================================================
        //  PANEL PROTECTED (widok z przyciskami per plik)
        // ===================================================================

        /// <summary>
        /// Cofa LOKALNĄ zmianę property (svn revert): PENDING- → przywraca
        /// ochronę, PENDING+ → anuluje niedomknięty Protect.
        /// BEZPIECZNIK: revert cofa też zmiany TREŚCI pliku — dozwolony tylko
        /// przy czystej kolumnie 1 statusu (zmieniona property, czysta treść).
        /// </summary>
        public async Task RevertProtectionChangeAsync(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            if (!TryEnterProcessing()) return;

            string root = svnManager.WorkingDir;
            try
            {
                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                // Bezpiecznik treści:
                var st = await SvnRunner.RunDetailedAsync($"status \"{fullPath}\"", root, false, false)
                    .ConfigureAwait(false);
                string output = (st.output ?? "").Trim();
                if (output.Length > 0 && output[0] != ' ')
                {
                    LogToLockPanel(
                        $"<color=#FF5555>Revert refused: {Path.GetFileName(fullPath)} has uncommitted CONTENT changes " +
                        "(svn revert would discard them). Commit the content first, or revert manually.</color>");
                    return;
                }

                await SvnRunner.RunAsync($"revert \"{fullPath}\"", root).ConfigureAwait(false);

                LogToLockPanel($"<color=green>Local property change reverted:</color> {Path.GetFileName(fullPath)}");

                svnManager.DiskChangesDetected = true;
                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                    await statusModule.RefreshAfterAction().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogToLockPanel($"<color=#FF5555>Revert error:</color> {Path.GetFileName(fullPath)} — {ex.Message}");
            }
            finally { ExitProcessing(); }
        }
    }
}