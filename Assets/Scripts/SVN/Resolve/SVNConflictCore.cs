using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNConflictCore
    {
        private readonly SVNManager _svnManager;
        private readonly SVNConflictParser _parser;
        private readonly SVNConflictCache _cache;
        private readonly SVNBackupManager _backup;
        private readonly Action<string> _logBoth;
        private readonly Action<string> _logOverwrite;

        public SVNConflictCore(
            SVNManager manager,
            SVNConflictParser parser,
            SVNConflictCache cache,
            SVNBackupManager backup,
            Action<string> logBoth,
            Action<string> logOverwrite)
        {
            _svnManager = manager;
            _parser = parser;
            _cache = cache;
            _backup = backup;
            _logBoth = logBoth;
            _logOverwrite = logOverwrite;
        }

        #region Single-file resolve

        public async Task<(bool success, string path, string error)> ResolveSingleCoreSilentAsync(
            string rawPath, string strategy, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath, "Invalid path");

            // FIX 4: tree conflicts must never go through the plain accept-word
            // path below. Example: 'mine-full' applied to a 'local missing,
            // incoming edit' tree conflict resolves the item as DELETED — the
            // file is thrown out of the project while success is reported.
            // Route every tree conflict through the structural resolver, which
            // carries the backup / restore / integrity guarantees.
            SVNConflictData conflictData = _cache.Get(path);
            if (conflictData == null)
                conflictData = _cache.Get(SVNPathUtilities.NormalizePath(path));

            if (conflictData == null)
            {
                // Cold cache: classify the conflict once before touching it.
                try
                {
                    var current = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                    string normalized = SVNPathUtilities.NormalizePath(path);
                    conflictData = current.FirstOrDefault(c =>
                        SVNPathUtilities.NormalizePath(c.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase));
                }
                catch (OperationCanceledException) { throw; }
                catch { conflictData = null; }
            }

            if (IsTreeConflict(conflictData))
            {
                var (treeSuccess, treeError) =
                    await ResolveTreeForceCoreAsync(conflictData, strategy, token).ConfigureAwait(false);
                return (treeSuccess, path, treeError);
            }

            // FIX 4 (safety net): remember whether the item exists so a
            // 'mine'/'working' resolve that silently removes it can be caught.
            string fullPath = Path.Combine(_svnManager.WorkingDir, path);
            bool existedBefore = File.Exists(fullPath) || Directory.Exists(fullPath);
            bool keepsLocalVersion =
                strategy.IndexOf("mine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                strategy.Equals("working", StringComparison.OrdinalIgnoreCase);

            if (strategy.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                var baseCached = _cache.Get(path);
                if (baseCached != null)
                {
                    _cache.AddOrUpdate(new SVNConflictData
                    {
                        Path = path,
                        Type = baseCached.Type,
                        State = SVNConflictState.Resolving,
                        TreeConflictReason = baseCached.TreeConflictReason,
                        TreeConflictAction = baseCached.TreeConflictAction,
                        TreeConflictVictim = baseCached.TreeConflictVictim,
                        TreeConflictNodeKind = baseCached.TreeConflictNodeKind
                    });
                }

                try
                {
                    await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);

                    var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                    bool stillConflicted = remaining.Any(c =>
                        SVNPathUtilities.NormalizePath(c.Path).Equals(
                            SVNPathUtilities.NormalizePath(path), StringComparison.OrdinalIgnoreCase));

                    if (stillConflicted)
                    {
                        if (baseCached != null)
                            _cache.AddOrUpdate(baseCached);
                        return (false, path, "Conflict still present after base resolve (possible tree/obstruction)");
                    }

                    _cache.Remove(SVNPathUtilities.NormalizePath(path));
                    return (true, path, null);
                }
                catch (OperationCanceledException)
                {
                    if (baseCached != null)
                        _cache.AddOrUpdate(baseCached);
                    throw;
                }
                catch (Exception ex)
                {
                    if (baseCached != null)
                        _cache.AddOrUpdate(baseCached);
                    return (false, path, ex.Message);
                }
            }

            var data = _cache.Get(path);
            if (data != null)
            {
                _cache.AddOrUpdate(new SVNConflictData
                {
                    Path = path,
                    Type = data.Type,
                    State = SVNConflictState.Resolving,
                    TreeConflictReason = data.TreeConflictReason,
                    TreeConflictAction = data.TreeConflictAction,
                    TreeConflictVictim = data.TreeConflictVictim,
                    TreeConflictNodeKind = data.TreeConflictNodeKind
                });
            }

            bool resolved = false;
            string errorMsg = null;

            try
            {
                await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                resolved = true;
            }
            catch (OperationCanceledException)
            {
                if (data != null) _cache.AddOrUpdate(data);
                throw;
            }
            catch (Exception ex) when ((ex.Message.Contains("W195024") || ex.Message.Contains("E155027"))
                                        && strategy.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
            {
                string fallbackStrategy = strategy.Replace("-full", "-conflict");
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {fallbackStrategy} \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    resolved = true;
                }
                catch (OperationCanceledException)
                {
                    if (data != null) _cache.AddOrUpdate(data);
                    throw;
                }
                catch (Exception fallbackEx) { errorMsg = fallbackEx.Message; }
            }
            catch (Exception ex) { errorMsg = ex.Message; }

            if (resolved)
            {
                // Cache cleared before cleanup — a cleanup failure must not leave
                // a phantom entry blocking the next resolve on the same path.
                _cache.Remove(SVNPathUtilities.NormalizePath(path));

                try
                {
                    await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { }

                // FIX 4 (safety net): a 'mine'/'working' resolve must never remove
                // the item from disk. If it vanished (tree conflict misrouted as a
                // text conflict), restore BASE and report failure instead of a
                // silent success.
                if (keepsLocalVersion && existedBefore &&
                    !(File.Exists(fullPath) || Directory.Exists(fullPath)))
                {
                    try
                    {
                        await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                        return (false, path, "Resolve removed the item from the project; BASE was restored. Resolve this file as a tree conflict.");

                    return (false, path, "Resolve removed the item from the project and it could not be restored.");
                }
            }
            else if (data != null)
            {
                _cache.AddOrUpdate(data);
            }

            return (resolved, path, errorMsg);
        }

        private async Task<bool> ConflictStillExistsAsync(string path, CancellationToken token)
        {
            try
            {
                var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                string norm = SVNPathUtilities.NormalizePath(path);
                return remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(norm, StringComparison.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logBoth($"<color=#FFAA00>  -> Verification failed: {SVNErrorHelper.GetShortError(ex)}</color>");
                return true;
            }
        }

        #endregion

        #region Tree force resolve

        /// <summary>
        /// Structural tree-conflict resolution. Guarantee for the user:
        /// whatever strategy was chosen, after a SUCCESSFUL return the working
        /// copy physically contains that version of the item — or, for theirs
        /// only, the repository genuinely does not have the item (legitimate
        /// deletion) — AND the item is still UNDER VERSION CONTROL (never left
        /// as unversioned '?', i.e. never silently thrown out of the project).
        /// Nothing is ever silently dropped: any local file removed during the
        /// process was backed up first, and if the resolution cannot complete
        /// the backup is restored automatically.
        /// </summary>
        public async Task<(bool success, string error)> ResolveTreeForceCoreAsync(
            SVNConflictData conflict, string strategy, CancellationToken token)
        {
            string path = conflict.Path;
            string fullPath = Path.Combine(_svnManager.WorkingDir, path);

            if (string.IsNullOrEmpty(strategy))
                return (false, "Unknown strategy: (null)");

            _logBoth($"<color=green>[TREE RESOLVE] structural fix for: {path} (strategy={strategy})</color>");

            bool isTheirs = strategy.Contains("theirs", StringComparison.OrdinalIgnoreCase);
            // 'working' means "keep what is on disk" — same guarantee as MINE.
            bool isMine = strategy.Contains("mine", StringComparison.OrdinalIgnoreCase)
                          || strategy.Equals("working", StringComparison.OrdinalIgnoreCase);
            bool isBase = strategy.Equals("base", StringComparison.OrdinalIgnoreCase);

            // State for the theirs-flow, consumed by the integrity gate below.
            // Locals are enough: everything happens within this single call.
            string treeBackupPath = null;
            bool treeUpdateOk = false;

            try
            {
                bool itemExists = File.Exists(fullPath) || Directory.Exists(fullPath);

                // FIX 2: safety backup BEFORE any svn action. If
                // 'resolve --accept theirs-full' (Phase 1) deletes a versioned
                // item outright, the user still has a restorable copy and the
                // integrity gate can report where it is.
                if (isTheirs && itemExists)
                {
                    treeBackupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);
                    if (treeBackupPath != null)
                        _logBoth($"<color=#AAAAAA>  -> Safety backup created: {SVNPathUtilities.ForDisplay(treeBackupPath)}</color>");
                    else
                        _logBoth("<color=#FFAA00>  -> WARNING: safety backup could not be created.</color>");
                }

                // Phase 1: standard resolve. RunAsync throws only on non-zero exit;
                // SVN sometimes exits 0 with W195024 on stderr that RunAsync discards
                // — verify against the WC before declaring success.
                try
                {
                    await SvnRunner.RunAsync($"resolve --accept {strategy} \"{path}\"",
                        _svnManager.WorkingDir, true, token).ConfigureAwait(false);

                    if (!await ConflictStillExistsAsync(path, token).ConfigureAwait(false))
                    {
                        _cache.Remove(SVNPathUtilities.NormalizePath(path));
                        // FIX 1+2: no early success without the integrity gate.
                        // serverConfirmed: svn itself applied the requested side.
                        return await VerifyItemIntegrityAsync(
                            path, fullPath, isTheirs, isMine, isBase,
                            treeBackupPath, serverConfirmed: true, token: token).ConfigureAwait(false);
                    }

                    _logBoth("<color=#AAAAAA>  -> Standard resolve did not clear the conflict. Using structural resolution.</color>");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (SVNErrorHelper.IsInapplicableOrObstruction(ex))
                {
                    _logBoth("<color=#AAAAAA>  -> Standard resolve not applicable for this conflict type. Using structural resolution.</color>");
                }

                // Phase 2: fallback -conflict (same integrity gate on success).
                if (strategy.EndsWith("-full", StringComparison.OrdinalIgnoreCase))
                {
                    string conflictVariant = strategy.Replace("-full", "-conflict");
                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept {conflictVariant} \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);

                        if (!await ConflictStillExistsAsync(path, token).ConfigureAwait(false))
                        {
                            _cache.Remove(SVNPathUtilities.NormalizePath(path));
                            return await VerifyItemIntegrityAsync(
                                path, fullPath, isTheirs, isMine, isBase,
                                treeBackupPath, serverConfirmed: true, token: token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                // Phase 3: structural fix.
                if (isTheirs)
                {
                    // ---- Step 1: clear the path so 'svn update' can work ----------
                    bool wasVersioned = await IsVersionedAsync(path, token).ConfigureAwait(false);

                    if (wasVersioned)
                    {
                        // FIX 5: a versioned item cannot be an obstruction —
                        // reverting to BASE is enough, 'svn update' then brings
                        // the repository version. 'svn delete --force' here would
                        // only schedule a deletion that update silently SKIPS,
                        // leaving the item deleted from the project even though
                        // the repository still has it.
                        try
                        {
                            await SvnRunner.RunAsync($"revert \"{path}\"",
                                _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            _logBoth("<color=#AAAAAA>  -> [1/3] Item reverted to BASE (versioned — update will fetch the repository version).</color>");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception revEx)
                        {
                            _logBoth($"<color=#FF4444>  -> revert failed: {SVNErrorHelper.GetShortError(revEx)} — aborting.</color>");
                            return (false, $"revert failed: {SVNErrorHelper.GetShortError(revEx)}");
                        }
                    }
                    else if (itemExists)
                    {
                        // ---- Unversioned obstruction: backup + remove ------------
                        // (safety backup may already exist from FIX 2)
                        string backupPath = treeBackupPath;
                        if (backupPath == null)
                        {
                            _logBoth($"<color=yellow>  -> [1/3] Creating backup before removing obstruction: {path}</color>");
                            backupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);
                            treeBackupPath = backupPath;
                            if (backupPath != null)
                                _logBoth($"<color=#AAAAAA>  -> Backup stored at: {SVNPathUtilities.ForDisplay(backupPath)}</color>");
                            else
                                _logBoth("<color=#FFAA00>  -> WARNING: backup could not be created — proceeding without a restore point.</color>");
                        }

                        try
                        {
                            await SvnRunner.RunAsync($"delete \"{path}\" --force",
                                _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            _logBoth("<color=#AAAAAA>  -> [1/3] Obstruction removed (svn delete --force).</color>");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception delEx)
                        {
                            _logBoth($"<color=#FFAA00>  -> svn delete failed: {SVNErrorHelper.GetShortError(delEx)}</color>");

                            if (!SVNErrorHelper.IsInapplicableOrObstruction(delEx))
                            {
                                _logBoth("<color=#FF4444>  -> Non-obstruction error — aborting (file preserved, nothing removed).</color>");
                                return (false, $"svn delete failed: {SVNErrorHelper.GetShortError(delEx)}");
                            }

                            // Obstruction-class failure: remove the unversioned blocker
                            // locally. Always via PermanentDelete so a failure is logged —
                            // this matters most when no backup exists.
                            if (!PermanentDelete(fullPath, _logBoth))
                            {
                                _logBoth("<color=#FF4444>  -> Could not remove the obstruction (both svn delete and local delete failed).</color>");
                                _logBoth("<color=#FF4444>  -> Aborting — the file was NOT deleted. Resolve manually.</color>");
                                return (false, "Failed to remove obstruction (both svn delete and local delete failed)");
                            }
                            _logBoth("<color=#AAAAAA>  -> [1/3] Obstruction removed locally (unversioned item).</color>");
                        }

                        // If svn delete scheduled a versioned item for deletion, revert
                        // MUST un-schedule it and restore BASE. Without this, svn update
                        // skips the item and resolve --accept working below would record
                        // "working state = missing" — silently accepting a deletion the
                        // user never chose. For unversioned items revert simply fails and
                        // is ignored.
                        try
                        {
                            await SvnRunner.RunAsync($"revert \"{path}\"",
                                _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            _logBoth("<color=#AAAAAA>  -> Working copy restored to pristine (BASE) state.</color>");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            _logBoth("<color=#AAAAAA>  -> Revert not applicable (item not versioned) — continuing.</color>");
                        }
                    }

                    // ---- Step 2: update — bring the REPOSITORY version into the WC -----
                    // This must happen BEFORE any resolve writes state into wc.db.
                    bool updateOk = false;
                    try
                    {
                        await SvnRunner.RunAsync($"update \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        updateOk = true;
                        _logBoth("<color=#AAAAAA>  -> [2/3] Repository version fetched (svn update).</color>");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception upEx)
                    {
                        _logBoth($"<color=#FFAA00>  -> [2/3] svn update failed: {SVNErrorHelper.GetShortError(upEx)}</color>");
                        if (SVNErrorHelper.IsInapplicableOrObstruction(upEx))
                            _logBoth("<color=#AAAAAA>  -> (Parent directory may still be conflicted — child items cannot be updated yet.)</color>");
                    }
                    treeUpdateOk = updateOk;

                    // ---- Step 3: resolve — now "working" physically equals "theirs" ----
                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        _logBoth("<color=#AAAAAA>  -> [3/3] Conflict resolved (working == repository version).</color>");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        try
                        {
                            await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"",
                                _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            _logBoth("<color=#AAAAAA>  -> [3/3] Conflict resolved (theirs-full).</color>");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception resEx)
                        {
                            _logBoth($"<color=#FFAA00>  -> resolve failed: {SVNErrorHelper.GetShortError(resEx)}</color>");
                        }
                    }
                }
                else if (isMine)
                {
                    if (!itemExists)
                    {
                        // Missing on disk + MINE: revert is REQUIRED to restore BASE
                        // before resolve --accept working. Silent failure would mark
                        // the conflict resolved while the file is still absent.
                        try
                        {
                            await SvnRunner.RunAsync($"revert \"{path}\"",
                                _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                            _logBoth("<color=#AAAAAA>  -> Local file was missing — restored BASE as the closest available local version.</color>");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception revEx)
                        {
                            _logBoth($"<color=#FF4444>  -> revert failed: {SVNErrorHelper.GetShortError(revEx)} — aborting (cannot restore base for MINE).</color>");
                            return (false, $"revert failed: {SVNErrorHelper.GetShortError(revEx)}");
                        }
                    }
                    else
                    {
                        _logBoth("<color=#AAAAAA>  -> Keeping the LOCAL version (no changes to the file).</color>");
                    }

                    try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception resEx)
                    {
                        _logBoth($"<color=#FFAA00>  -> resolve working failed: {SVNErrorHelper.GetShortError(resEx)}</color>");
                    }
                }
                else if (isBase)
                {
                    try
                    {
                        await SvnRunner.RunAsync($"revert \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        _logBoth("<color=#AAAAAA>  -> File reverted to BASE (pre-conflict) version.</color>");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception resEx)
                    {
                        _logBoth($"<color=#FFAA00>  -> resolve working failed: {SVNErrorHelper.GetShortError(resEx)}</color>");
                    }
                }
                else
                {
                    return (false, $"Unknown strategy: {strategy}");
                }

                // Phase 4: verify the conflict record is gone from the WC.
                try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { }

                if (await ConflictStillExistsAsync(path, token).ConfigureAwait(false))
                    return (false, "Conflict still present after force structural fix");

                _cache.Remove(SVNPathUtilities.NormalizePath(path));

                // Phase 5: integrity gate — see VerifyItemIntegrityAsync.
                return await VerifyItemIntegrityAsync(
                    path, fullPath, isTheirs, isMine, isBase,
                    treeBackupPath, treeUpdateOk, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// FIX 1: final integrity gate for tree-conflict resolution. A reported
        /// SUCCESS means: (a) the chosen version physically exists in the working
        /// copy — or, for theirs only, the repository genuinely does not have the
        /// item (legitimate deletion); and (b) an item that exists on disk is
        /// still UNDER VERSION CONTROL — an item that fell out of the project
        /// (svn status '?') is re-attached automatically; a missing item is
        /// restored from the backup.
        ///
        /// FIX 11/12: this method does NOT append its own green summary after a
        /// successful re-attach. ReattachToVersionControlAsync owns the logging
        /// (it reports user-content restored / repository kept / unversioned
        /// kept precisely). Verify only decides success/failure.
        /// </summary>
        private async Task<(bool success, string error)> VerifyItemIntegrityAsync(
            string path, string fullPath, bool isTheirs, bool isMine, bool isBase,
            string backupPath, bool serverConfirmed, CancellationToken token)
        {
            bool existsNow = File.Exists(fullPath) || Directory.Exists(fullPath);

            if (!existsNow)
            {
                if (isTheirs)
                {
                    // update succeeded / svn itself applied theirs and the repo truly
                    // has no such item -> "theirs" legitimately IS a deletion.
                    if (serverConfirmed)
                    {
                        _logBoth($"<color=yellow>  -> Note: '{path}' does not exist in the repository — 'theirs' here means the item is deleted on the server.</color>");
                        _logBoth("<color=yellow>  -> Working copy now matches the repository (item absent).</color>");
                        if (backupPath != null)
                            _logBoth($"<color=yellow>  -> Your previous local copy was backed up to: {SVNPathUtilities.ForDisplay(backupPath)}</color>");
                        return (true, null);
                    }

                    // update FAILED and the item is gone -> resolution is
                    // incomplete. Restore the user's file from the backup.
                    _logBoth("<color=#FF4444>  -> Item is missing and the repository version could not be fetched (update failed).</color>");
                    if (TryRestoreFromBackup(backupPath, fullPath, _logBoth))
                    {
                        _logBoth("<color=#00FF88>  -> Your file was restored from the backup — nothing was lost.</color>");
                        return (false, "Repository version could not be fetched; local file restored from backup. Retry after resolving parent conflicts.");
                    }

                    _logBoth("<color=#FF4444>  -> Item is missing and no backup was available to restore it.</color>");
                    return (false, "Resolution incomplete: item missing, update failed, backup unavailable.");
                }

                if (isMine || isBase)
                {
                    // MINE/BASE must exist on disk. Try one last revert to put a
                    // version of the item back.
                    try { await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }

                    existsNow = File.Exists(fullPath) || Directory.Exists(fullPath);
                    if (existsNow)
                    {
                        if (isMine)
                            _logBoth("<color=#FFAA00>  -> Warning: your local edits were not present on disk; BASE version restored instead.</color>");
                        else if (isBase)
                            _logBoth("<color=#FFAA00>  -> Warning: the item was missing after the resolve; it was restored to the pre-conflict (BASE) version.</color>");
                        return (true, null);
                    }

                    return (false, isMine
                        ? "MINE resolution failed: the item does not exist on disk and could not be restored."
                        : "BASE resolution failed: the item could not be restored to its pre-conflict version.");
                }

                return (false, "Resolution incomplete: the item is missing from the working copy.");
            }

            // FIX 3: the item exists on disk — make sure it did not fall out of
            // the project (left as unversioned '?') during the resolution.
            if (!await IsVersionedAsync(path, token).ConfigureAwait(false))
            {
                if (isTheirs)
                {
                    bool reattached = await ReattachToVersionControlAsync(
                        path, fullPath, false, false, _logBoth, token).ConfigureAwait(false);

                    // FIX 12: ReattachToVersionControlAsync already reports the
                    // outcome (repository version fetched, or repository does not
                    // have the item and it stays unversioned on disk). No extra
                    // green summary here — that would duplicate its log.
                    return (reattached, null);
                }

                bool ok = await ReattachToVersionControlAsync(
                    path, fullPath, true, true, _logBoth, token).ConfigureAwait(false);
                if (!ok)
                    return (false, "Item resolved but left UNVERSIONED ('?') and could not be re-attached to the project automatically.");

                // FIX 11: logging owned by ReattachToVersionControlAsync — it
                // reports whether the user content was restored on top of the
                // repository version, or whether only the repository version
                // was kept (with the backup remaining). No summary here.
                return (true, null);
            }

            // Item exists and is versioned — report which version the user got.
            if (isTheirs)
                _logBoth($"<color=green>  -> Repository version of '{path}' is now in your working copy.</color>");
            else if (isMine)
                _logBoth($"<color=green>  -> Your local version of '{path}' was kept.</color>");
            else if (isBase)
                _logBoth($"<color=green>  -> '{path}' was restored to the pre-conflict (BASE) version.</color>");

            return (true, null);
        }

        /// <summary>
        /// Exit-code probe: 'svn info' succeeds (exit 0) only for items under
        /// version control; unversioned items fail with E200009 / W155007.
        /// Unknown errors (lock, busy wc.db, ...) are treated as "versioned"
        /// so the recovery never fires on a healthy item.
        /// </summary>
        private async Task<bool> IsVersionedAsync(string path, CancellationToken token)
        {
            try
            {
                await SvnRunner.RunAsync($"info \"{path}\"",
                    _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                string message = ex.Message ?? string.Empty;
                if (message.IndexOf("E200009", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("W155007", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("E155007", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("W155010", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("not versioned", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("not under version control", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }

                // Unknown failure — assume versioned (conservative).
                return true;
            }
        }

        /// <summary>
        /// FIX 3: re-attaches an item that survived on disk but fell out of
        /// version control (svn status '?') — the "file thrown out of the
        /// project" case. The user's content wins for MINE/BASE
        /// (restoreUserContent = true); for THEIRS the repository content wins.
        /// Sequence: backup -> remove unversioned item -> svn update ->
        /// overwrite with the user's content (MINE/BASE) or keep the server
        /// version (THEIRS); when the repository does not have the item:
        /// restore the local file and 'svn add' it (allowAdd). Returns true
        /// when the item is under version control afterwards.
        /// </summary>
        private async Task<bool> ReattachToVersionControlAsync(
            string path, string fullPath, bool restoreUserContent, bool allowAdd,
            Action<string> log, CancellationToken token)
        {
            log($"<color=#FFAA00>  -> '{path}' is NOT under version control after the resolve — re-attaching it to the project.</color>");

            // 1. Safety backup of what is on disk right now.
            string backupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);

            if (backupPath == null)
            {
                // No restore point -> never delete anything. Best effort: 'svn add'.
                if (allowAdd)
                {
                    try
                    {
                        await SvnRunner.RunAsync($"add \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        log("<color=#00FF88>  -> Item scheduled for addition (svn add) — back in the project.</color>");
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception addEx)
                    {
                        log($"<color=#FFAA00>  -> svn add failed: {SVNErrorHelper.GetShortError(addEx)} — item left on disk as unversioned.</color>");
                        return false;
                    }
                }

                log("<color=#FFAA00>  -> No backup could be created — item left on disk as unversioned (add it manually).</color>");
                return false;
            }

            // 2. Remove the unversioned item locally (a backup exists).
            if (!PermanentDelete(fullPath, log))
            {
                log("<color=#FF4444>  -> Could not remove the unversioned item — it stays on disk as '?' (add it manually).</color>");
                return false;
            }

            // 3. Bring the repository version, if the repository has one.
            bool serverHasItem = false;
            try
            {
                await SvnRunner.RunAsync($"update \"{path}\"",
                    _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                serverHasItem = File.Exists(fullPath) || Directory.Exists(fullPath);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (serverHasItem)
            {
                if (restoreUserContent)
                {
                    if (CopyUserContentOver(backupPath, fullPath, log))
                    {
                        log("<color=#00FF88>  -> Repository version fetched and overwritten with your local content — item is back in the project (status 'M').</color>");

                        // Backup consumed — the user's content is back inside the
                        // item, so the backup copy is no longer needed. Best-effort:
                        // a failure changes nothing (retention cleans it up later).
                        PermanentDelete(backupPath);
                    }
                    else
                    {
                        // FIX 13: the item IS back in the project (repository
                        // version), only the user's content could not be laid on
                        // top. Amber warning, not red error: the backup remains at
                        // the reported path for a manual restore.
                        log("<color=#FFAA00>  -> Your content could NOT be restored on top of the repository version — the backup remains at the path reported above. Restore it manually if needed.</color>");
                    }
                }
                else
                {
                    log("<color=#00FF88>  -> Repository version fetched — item is back in the project.</color>");
                }
            }
            else
            {
                // 4. The repository does not have the item: put the local file back.
                if (!TryRestoreFromBackup(backupPath, fullPath, log))
                {
                    log("<color=#FF4444>  -> Could not restore the local item after fetching — it remains available in the backup.</color>");
                    return false;
                }

                if (allowAdd)
                {
                    try
                    {
                        await SvnRunner.RunAsync($"add \"{path}\"",
                            _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                        log("<color=#00FF88>  -> Item restored and scheduled for addition (svn add) — back in the project.</color>");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception addEx)
                    {
                        log($"<color=#FFAA00>  -> svn add failed: {SVNErrorHelper.GetShortError(addEx)} — item is on disk but still unversioned (add it manually).</color>");
                        return false;
                    }
                }
                else
                {
                    // theirs: the repository does not have this item, so 'theirs'
                    // means it is not part of the project. Nothing was thrown out —
                    // the file is still on disk.
                    log("<color=yellow>  -> The repository does not have this item — it stays on disk as unversioned (nothing was deleted).</color>");
                }
            }

            return await IsVersionedAsync(path, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Copies the user's content (from the backup) over a versioned item
        /// that was just fetched from the repository. Files are overwritten;
        /// directories are merged file-by-file (user content wins). Returns
        /// true only when the content actually landed on disk — every silent
        /// failure path (unexpected target state, source/target mismatch,
        /// per-file copy error) returns false so the caller can report it
        /// instead of logging a false success.
        /// </summary>
        private static bool CopyUserContentOver(string source, string target, Action<string> log)
        {
            try
            {
                if (File.Exists(source))
                {
                    if (File.Exists(target))
                    {
                        File.SetAttributes(target, FileAttributes.Normal);
                        File.Copy(source, target, overwrite: true);
                        return true;
                    }

                    // Target is missing or is a directory (e.g. the repository
                    // has a directory where the user had a file) — do not guess,
                    // report and keep the backup intact.
                    log?.Invoke("<color=#FFAA00>  -> Unexpected target state while restoring content — file not restored, backup kept.</color>");
                    return false;
                }

                if (Directory.Exists(source) && Directory.Exists(target))
                {
                    bool anyFailed = false;
                    foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string relative = file.Substring(source.Length).TrimStart('\\', '/');
                            string destination = Path.Combine(target, relative);
                            string destinationDir = Path.GetDirectoryName(destination);
                            if (!string.IsNullOrEmpty(destinationDir))
                                Directory.CreateDirectory(destinationDir);
                            if (File.Exists(destination))
                                File.SetAttributes(destination, FileAttributes.Normal);
                            File.Copy(file, destination, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            log?.Invoke($"<color=#FFAA00>  -> Copying '{SVNPathUtilities.ForDisplay(file)}' failed: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                            anyFailed = true;
                        }
                    }
                    return !anyFailed;
                }

                log?.Invoke("<color=#FFAA00>  -> Source/target mismatch while restoring content — file not restored, backup kept.</color>");
                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"<color=#FF4444>  -> Copying your local content back failed: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return false;
            }
        }

        /// <summary>
        /// Tree conflicts are recognized by the conflict type or the tree
        /// descriptor fields (Reason / Action). TreeConflictVictim is
        /// deliberately NOT consulted: the parser fills it for EVERY conflict
        /// (fallback = the item's own path), so it is not a tree indicator —
        /// testing it made every text/binary/manual conflict look like a tree
        /// conflict and routed it into the structural resolver.
        /// </summary>
        private static bool IsTreeConflict(SVNConflictData data)
        {
            if (data == null) return false;

            if (data.Type == SVNConflictType.Tree) return true;

            // Manual conflicts can originate from tree conflicts (parser changes
            // Type to Manual when the user opened the item in an editor, but the
            // tree fields are still populated from the current svn status XML).
            return !string.IsNullOrEmpty(data.TreeConflictReason)
                || !string.IsNullOrEmpty(data.TreeConflictAction);
        }

        /// <summary>
        /// Restores a file/directory from its backup copy. Files and directories
        /// are moved back — a restore hands the item to the working copy and the
        /// backup copy is no longer needed. Returns true if the target exists
        /// afterwards.
        /// </summary>
        private static bool TryRestoreFromBackup(string backupPath, string targetPath, Action<string> log)
        {
            try
            {
                if (string.IsNullOrEmpty(backupPath))
                    return false;

                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                    return true;

                string dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, targetPath);
                    return Directory.Exists(targetPath);
                }

                if (File.Exists(backupPath))
                {
                    File.Move(backupPath, targetPath);
                    return File.Exists(targetPath);
                }

                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"<color=#FF4444>  -> Restore from backup failed: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return false;
            }
        }

        #endregion

        #region Delete obstruction

        public async Task<bool> DeleteObstructionCoreAsync(string rawPath, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return false;

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            try
            {
                var allConflicts = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                if (SVNPathUtilities.HasUnresolvedParentConflict(path, allConflicts))
                    _logBoth("<color=#FFAA00>Warning:</color> Parent directory also has a conflict. Resolve children first, then the parent.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logBoth($"<color=#FFAA00>Conflict pre-check failed:</color> {ex.Message}"); }

            string fullPath = Path.Combine(_svnManager.WorkingDir, path);
            bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);

            var conflictInfo = _cache.Get(path);
            string reason = conflictInfo?.TreeConflictReason ?? "unknown";
            string nodeKind = conflictInfo?.TreeConflictNodeKind ?? (fileExists ? "file/dir" : "missing");

            _logBoth($"[TREE RESOLVE] {path}");
            _logBoth($"   Reason : <color=#FFAA00>{reason}</color>");
            _logBoth($"   Kind   : {nodeKind} | Exists on disk: {fileExists}");

            if (fileExists)
            {
                string backupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);

                try
                {
                    await SvnRunner.RunAsync($"delete \"{path}\" --force", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    _logBoth($"<color=yellow>Scheduled for deletion:</color> {path}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logBoth($"<color=#FFAA00>svn delete --force failed:</color> {ex.Message}");

                    if (backupPath != null)
                    {
                        bool deleted = PermanentDelete(fullPath, _logBoth);
                        if (!deleted)
                        {
                            _logBoth("<color=#FF4444>Local delete also failed — aborting.</color>");
                            return false;
                        }
                    }
                    else
                    {
                        await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false);

                        if (File.Exists(fullPath) || Directory.Exists(fullPath))
                        {
                            _logBoth("<color=#FF4444>SafeDelete failed — file preserved. Aborting.</color>");
                            return false;
                        }
                    }

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    return false;
                }
            }
            else
            {
                try { await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception revertEx)
                {
                    _logBoth($"<color=#AAAAAA>Revert failed:</color> {revertEx.Message} → trying theirs-full...");
                    try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception theirsEx) { _logBoth($"<color=#FFAA00>theirs-full failed:</color> {theirsEx.Message}"); }
                }

                try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception workingEx) { _logBoth($"<color=#FFAA00>resolve working failed:</color> {workingEx.Message}"); }
            }

            try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }

            bool stillExists;
            try
            {
                var remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false);
                string normalizedPath = SVNPathUtilities.NormalizePath(path);
                stillExists = remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logBoth($"<color=#FFAA00>Post-resolve verification failed:</color> {ex.Message}");
                return false;
            }

            if (stillExists)
            {
                _logBoth($"<color=#FF4444>Tree conflict still exists:</color> {path}");
                return false;
            }

            _cache.Remove(SVNPathUtilities.NormalizePath(path));
            _logBoth($"<color=green>Tree conflict resolved:</color> {path}");
            return true;
        }

        public async Task<(bool success, string path)> DeleteObstructionCoreSilentAsync(string rawPath, CancellationToken token)
        {
            if (!SVNPathUtilities.TryGetRelativePath(_svnManager.WorkingDir, rawPath, out string path))
                return (false, rawPath);

            await _svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

            string fullPath = Path.Combine(_svnManager.WorkingDir, path);
            bool fileExists = File.Exists(fullPath) || Directory.Exists(fullPath);

            if (fileExists)
            {
                string backupPath = await _backup.BackupAsync(fullPath, token).ConfigureAwait(false);

                try
                {
                    await SvnRunner.RunAsync($"delete \"{path}\" --force", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception delEx)
                {
                    // Only obstruction-class failures justify destroying the local
                    // file. Lock/permission/wc.db errors must preserve it.
                    if (!SVNErrorHelper.IsInapplicableOrObstruction(delEx))
                        return (false, path);

                    if (backupPath != null)
                    {
                        PermanentDelete(fullPath);
                    }
                    else
                    {
                        try { await _backup.SafeDeleteAsync(fullPath, token).ConfigureAwait(false); }
                        catch { }
                    }

                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                        return (false, path);

                    try
                    {
                        await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    return (false, path);
                }
            }
            else
            {
                try { await SvnRunner.RunAsync($"revert \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try { await SvnRunner.RunAsync($"resolve --accept theirs-full \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }

                try { await SvnRunner.RunAsync($"resolve --accept working \"{path}\"", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            try { await SvnRunner.RunAsync("cleanup", _svnManager.WorkingDir, true, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }

            List<SVNConflictData> remaining;
            try { remaining = await _parser.GetConflictsAsync(_svnManager.WorkingDir, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return (false, path);
            }

            string normalizedPath = SVNPathUtilities.NormalizePath(path);
            bool stillExists = remaining.Any(c => SVNPathUtilities.NormalizePath(c.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (stillExists) return (false, path);

            _cache.Remove(normalizedPath);
            return (true, path);
        }

        #endregion

        #region Conflict markers check

        public async Task<bool> HasConflictMarkersAsync(string fullPath, CancellationToken token = default)
        {
            if (!File.Exists(fullPath)) return false;

            // Binary files never contain conflict markers. This short-circuit avoids:
            //  - reading 100 MB DLLs line-by-line on every UI refresh (seconds wasted),
            //  - false positives from compiled string literals like "<<<<<<<" in the DLL.
            if (SVNPathUtilities.IsBinaryFile(fullPath)) return false;

            try
            {
                token.ThrowIfCancellationRequested();

                return await Task.Run(() =>
                {
                    bool hasStart = false, hasSeparator = false;
                    using var stream = new StreamReader(fullPath, Encoding.UTF8, true, 8192);
                    string line;
                    while ((line = stream.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("<<<<<<<", StringComparison.Ordinal))
                        {
                            hasStart = true;
                            hasSeparator = false;
                        }
                        else if (hasStart && trimmed.StartsWith("=======", StringComparison.Ordinal))
                        {
                            hasSeparator = true;
                        }
                        else if (hasStart && hasSeparator && trimmed.StartsWith(">>>>>>>", StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                    return false;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Utility

        private static bool PermanentDelete(string path, Action<string> log = null)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return true;
                }

                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(dir, FileAttributes.Normal);
                    Directory.Delete(path, true);
                    return true;
                }
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"<color=#FF4444>PermanentDelete failed for {SVNPathUtilities.ForDisplay(path)}: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return false;
            }
        }

        #endregion
    }
}