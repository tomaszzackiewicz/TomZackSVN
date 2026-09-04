using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBackupManager
    {
        private readonly SVNManager _svnManager;
        private readonly Action<string> _log;

        // === RETENCJA: klucze prefs + domyślna polityka ===
        private const string KEY_RETENTION_DAYS = "SVN_BackupRetentionDays";
        private const string KEY_MAX_SIZE_MB = "SVN_BackupMaxSizeMB";
        private const string KEY_AUTO_CLEAN = "SVN_BackupAutoClean";
        private const string KEY_LAST_AUTOCLEAN = "SVN_BackupLastAutoCleanTicks";
        private const string KEY_BACKUP_ENABLED = "SVN_BackupEnabled";          // NOWE: master toggle
        private const string KEY_PREFS_VERSION = "SVN_BackupPrefsVersion";      // NOWE: migracja

        public const int DefaultRetentionDays = 14;       // 0 = retencja wiekowa wyłączona
        public const int DefaultMaxSizeMB = 10240;        // 10 GB; 0 = bez limitu (ZMIANA: było 2048)
        public const bool DefaultBackupEnabled = true;    // NOWE: backup domyślnie ON

        // Backupy młodsze niż 24h NIGDY nie są usuwane automatycznie —
        // okno na odkrycie pomyłkowego resolve jeszcze tego samego dnia.
        private static readonly TimeSpan MinProtectedAge = TimeSpan.FromHours(24);

        // Auto-clean najwyżej raz na 12h (per instalacja, znacznik w prefs).
        private static readonly TimeSpan AutoCleanInterval = TimeSpan.FromHours(12);

        private static readonly Regex TimestampFolderRegex = new Regex(@"^\d{8}_\d{6}$", RegexOptions.Compiled);
        private static readonly object AutoCleanLock = new object();

        public SVNBackupManager(SVNManager manager, Action<string> log)
        {
            _svnManager = manager;
            _log = log;
            MigratePrefsOnce();
        }

        // === MIGRACJA: jednorazowy bump starego defaultu 2048 → 10240.
        // Kto miał ŚWIADOMIE ustawione 2048 też dostanie 10240 (może wrócić
        // cyklem/inputem). Wartości custom (np. 5120) nietknięte.
        // UWAGA: ctor musi działać na main thread (PlayerPrefs/SVNPrefs).
        private void MigratePrefsOnce()
        {
            if (GetPrefInt(KEY_PREFS_VERSION, 0) >= 1) return;

            if (GetPrefInt(KEY_MAX_SIZE_MB, DefaultMaxSizeMB) == 2048)
                SetPrefInt(KEY_MAX_SIZE_MB, DefaultMaxSizeMB);

            SetPrefInt(KEY_PREFS_VERSION, 1);
        }

        #region Konfiguracja (prefs)

        // === FIX (prefs): GetString/SetString gwarantowane w SVNPrefs; jeśli Twoja
        // wersja ma GetInt/SetInt — podmień GetPrefInt/SetPrefInt na nie 1:1.
        private static int GetPrefInt(string key, int fallback) =>
            int.TryParse(SVNPrefs.GetString(key, fallback.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static void SetPrefInt(string key, int value) =>
            SVNPrefs.SetString(key, value.ToString(CultureInfo.InvariantCulture));

        public int RetentionDays
        {
            get { int v = GetPrefInt(KEY_RETENTION_DAYS, DefaultRetentionDays); return v < 0 ? 0 : v; }
            set { SetPrefInt(KEY_RETENTION_DAYS, Math.Max(0, value)); }
        }

        public int MaxSizeMB
        {
            get { int v = GetPrefInt(KEY_MAX_SIZE_MB, DefaultMaxSizeMB); return v < 0 ? 0 : v; }
            set { SetPrefInt(KEY_MAX_SIZE_MB, Math.Max(0, value)); }
        }

        public bool AutoCleanEnabled
        {
            get => GetPrefInt(KEY_AUTO_CLEAN, 1) != 0;
            set => SetPrefInt(KEY_AUTO_CLEAN, value ? 1 : 0);
        }

        /// <summary>
        /// Master toggle backupów. OFF = BackupAsync natychmiast zwraca null
        /// (SVNConflictCore traktuje to jak "brak backupu" → gałąź
        /// SafeDeleteAsync), a SafeDeleteAsync usuwa TRWALE zamiast przenosić
        /// do backupu. Default: ON.
        /// </summary>
        public bool BackupEnabled
        {
            get => GetPrefInt(KEY_BACKUP_ENABLED, DefaultBackupEnabled ? 1 : 0) != 0;
            set => SetPrefInt(KEY_BACKUP_ENABLED, value ? 1 : 0);
        }

        #endregion

        #region Backup / SafeDelete (layout: backupRoot/<stamp>/<relative>)

        public Task<string> BackupAsync(string path, CancellationToken token = default)
        {
            return Task.Run(() => BackupCore(path, token), token);
        }

        private string BackupCore(string path, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // === TOGGLE: backup OFF → nic nie kopiujemy (szybki resolve przy
            // dużej liczbie konfliktów). Null jest spójny z istniejącymi
            // fallbackami w SVNConflictCore (gałąź SafeDeleteAsync).
            // Celowo BEZ logu — przy 200 konfliktach byłby spam.
            if (!BackupEnabled) return null;

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return null;

                token.ThrowIfCancellationRequested();

                string backupRoot = GetBackupRoot();
                if (string.IsNullOrEmpty(backupRoot))
                {
                    _log("<color=#FFAA00>[Backup]</color> Failed to create backup folder.");
                    return null;
                }

                string relative = GetRelativeToWorkingDir(path);

                // === NOWY LAYOUT: każdy backup = jeden top-level folder ze stampem.
                // Rationale: File.Copy zachowuje LastWriteTime ŹRÓDŁA (3-letni asset
                // ma LastWriteTime sprzed 3 lat nawet w świeżym backupie) → timestampy
                // plików NIE NADAJĄ SIĘ do age-cleanupu. Nazwa folderu = jedyne
                // wiarygodne źródło czasu backupu + backup usuwalny atomowo.
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string destPath = Path.Combine(backupRoot, stamp, relative);
                destPath = MakeUniquePath(destPath);

                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    File.Copy(path, destPath, true);
                }
                else if (Directory.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    CopyDirectory(path, destPath, token);
                }

                _log($"<color=00FF88><b>[Backup]</b></color> Backup created:");
                _log($"<color=yellow>  Source :</color> {SVNPathUtilities.ForDisplay(path)}");
                _log($"<color=yellow>  Backup :</color> <color=yellow>{SVNPathUtilities.ForDisplay(destPath)}</color>");
                _log($"<color=yellow>  Backup folder: {SVNPathUtilities.ForDisplay(backupRoot)}</color>");

                return destPath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"<color=#FFAA00>[Backup] Failed to create backup: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return null;
            }
        }

        public Task SafeDeleteAsync(string path, CancellationToken token = default)
        {
            return Task.Run(() => SafeDeleteCore(path, token), token);
        }

        private void SafeDeleteCore(string path, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // === TOGGLE: backup OFF → usuwamy TRWALE (szybkość kosztem siatki
            // bezpieczeństwa — jawna decyzja użytkownika). Callerzy sprawdzają
            // File.Exists/Directory.Exists po SafeDelete → porażka usunięcia
            // = abort, identycznie jak dotychczas.
            if (!BackupEnabled)
            {
                DeletePermanentlyCore(path, token);
                return;
            }

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return;

                token.ThrowIfCancellationRequested();

                string backupRoot = GetBackupRoot();
                if (string.IsNullOrEmpty(backupRoot))
                {
                    _log("<color=#FF4444>[Backup] Failed to create backup folder — deletion ABORTED (file preserved).</color>");
                    _log($"<color=#FF4444>  Path preserved: {SVNPathUtilities.ForDisplay(path)}</color>");
                    _log("<color=#FFAA00>  Resolve manually (check disk space / permissions).</color>");
                    return;
                }

                string relative = GetRelativeToWorkingDir(path);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string destPath = Path.Combine(backupRoot, stamp, relative);
                destPath = MakeUniquePath(destPath);

                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Move(path, destPath);
                }
                else if (Directory.Exists(path))
                {
                    token.ThrowIfCancellationRequested();
                    Directory.Move(path, destPath);
                }

                _log($"<color=00FF88><b>[Backup]</b></color> File moved to backup:");
                _log($"<color=yellow>  Source :</color> {SVNPathUtilities.ForDisplay(path)}");
                _log($"<color=yellow>  Backup :</color> <color=yellow>{SVNPathUtilities.ForDisplay(destPath)}</color>");
                _log($"<color=yellow>  Backup folder: {SVNPathUtilities.ForDisplay(backupRoot)}</color>");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log("<color=#FF4444><b>[Backup] Failed to move file — deletion ABORTED (file preserved).</b></color>");
                _log($"<color=#FF4444>  Path preserved: {SVNPathUtilities.ForDisplay(path)}</color>");
                _log($"<color=#FF4444>  Reason: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                _log("<color=#FFAA00>  Resolve manually (file is still on disk).</color>");
            }
        }

        // === TOGGLE OFF: trwałe usunięcie obstructionu (bez backupu).
        private void DeletePermanentlyCore(string path, CancellationToken token)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;

                token.ThrowIfCancellationRequested();

                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                else
                {
                    foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    foreach (var d in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(d, FileAttributes.Normal); } catch { }
                    Directory.Delete(path, true);
                }

                _log($"<color=#FFAA00>[Backup OFF] Permanently deleted: {SVNPathUtilities.ForDisplay(path)}</color>");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"<color=#FF4444>[Backup OFF] Delete failed: {SVNPathUtilities.ForDisplay(ex.Message)} — file preserved.</color>");
            }
        }

        #endregion

        #region Janitor: cleanup / info / auto

        /// <summary>Manualne sprzątanie (natychmiast, polityka z prefs).</summary>
        public Task<SVNBackupCleanupResult> CleanupAsync(CancellationToken token = default)
        {
            return Task.Run(() => CleanupCore(token), token);
        }

        /// <summary>Auto-clean z throttlem raz/12h (znacznik w prefs).</summary>
        public Task AutoCleanupIfNeededAsync(CancellationToken token = default)
        {
            if (!AutoCleanEnabled) return Task.CompletedTask;

            lock (AutoCleanLock)
            {
                long lastTicks = 0;
                long.TryParse(SVNPrefs.GetString(KEY_LAST_AUTOCLEAN, "0"), out lastTicks);
                var last = new DateTime(lastTicks, DateTimeKind.Utc);
                if (DateTime.UtcNow - last < AutoCleanInterval)
                    return Task.CompletedTask;

                SVNPrefs.SetString(KEY_LAST_AUTOCLEAN, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            return Task.Run(() => CleanupCore(token), token);
        }

        /// <summary>Info o zajętości backupów do UI.</summary>
        public Task<string> DescribeBackupsAsync(CancellationToken token = default)
        {
            return Task.Run(() =>
            {
                string root = GetBackupRoot();
                if (string.IsNullOrEmpty(root))
                    return "<color=#FFAA00>[Backup] Backup folder unavailable.</color>";

                string rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                long total = GetTreeSizeBytes(rootFull);
                int count = 0;
                DateTime oldest = DateTime.MaxValue;

                try
                {
                    foreach (var d in Directory.GetDirectories(rootFull))
                        if (IsTimestampFolder(Path.GetFileName(d), out DateTime ts))
                        {
                            count++;
                            if (ts < oldest) oldest = ts;
                        }
                }
                catch { }

                long free;
                try { free = new DriveInfo(rootFull).AvailableFreeSpace; }
                catch { free = -1; }

                return (BackupEnabled ? "" : "<color=#FF4444>[Backup] DISABLED — no new backups; deletes are permanent.</color>\n") +
                       $"<color=yellow>[Backup] {FormatBytes(total)} in {count} backup(s)</color>\n" +
                       $"<color=yellow>  Folder : {SVNPathUtilities.ForDisplay(root)}</color>\n" +
                       (oldest != DateTime.MaxValue ? $"  Oldest : {oldest:yyyy-MM-dd HH:mm} UTC\n" : "") +
                       $"  Policy : backup {(BackupEnabled ? "on" : "OFF")} | retention {RetentionDays} d | cap {(MaxSizeMB <= 0 ? "unlimited" : FormatBytes((long)MaxSizeMB * 1024 * 1024))} | disk free {FormatBytes(free)}";
            }, token);
        }

        private SVNBackupCleanupResult CleanupCore(CancellationToken token)
        {
            int backupsRemoved = 0, legacyFilesRemoved = 0, backupsRemaining = 0;
            long bytesFreed = 0, bytesRemaining = 0, diskFree = -1;
            bool overCap = false;

            try
            {
                string root = GetBackupRoot();
                if (string.IsNullOrEmpty(root))
                {
                    _log("<color=#FFAA00>[Backup] Cleanup skipped — backup folder unavailable.</color>");
                    return new SVNBackupCleanupResult(0, 0, 0, 0, 0, false, -1);
                }

                string rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                DateTime nowUtc = DateTime.UtcNow;
                int retentionDays = RetentionDays;                 // 0 = wiek off
                long capBytes = (long)MaxSizeMB * 1024L * 1024L;   // 0 = bez limitu

                // --- 1. Klasyfikacja top-level: eventy (stamp) vs legacy ---
                var events = new List<(string path, DateTime ts, long bytes)>();
                var legacyDirs = new List<string>();
                var legacyUnits = new List<(string path, long size, DateTime createdUtc)>();

                string[] topDirs;
                try { topDirs = Directory.GetDirectories(rootFull); }
                catch { topDirs = Array.Empty<string>(); }

                foreach (var dir in topDirs)
                {
                    if (IsTimestampFolder(Path.GetFileName(dir), out DateTime ts))
                        events.Add((dir, ts, 0));
                    else
                        legacyDirs.Add(dir);
                }

                for (int i = 0; i < events.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var e = events[i];
                    events[i] = (e.path, e.ts, GetTreeSizeBytes(e.path));
                }

                // Legacy = stary layout. Znacznik: CreationTimeUtc.
                foreach (var ld in legacyDirs)
                {
                    token.ThrowIfCancellationRequested();
                    CollectLegacyFiles(ld, legacyUnits);
                }

                string[] topFiles;
                try { topFiles = Directory.GetFiles(rootFull); }
                catch { topFiles = Array.Empty<string>(); }
                foreach (var f in topFiles)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        legacyUnits.Add((f, fi.Length, fi.CreationTimeUtc));
                    }
                    catch { }
                }

                // --- 2. Retencja wiekowa: całe backupy (foldery stamp) ---
                var survivors = new List<(string path, DateTime ts, long bytes)>();
                foreach (var e in events)
                {
                    token.ThrowIfCancellationRequested();
                    bool expired = retentionDays > 0 && (nowUtc - e.ts) >= TimeSpan.FromDays(retentionDays);
                    if (!expired) { survivors.Add(e); continue; }

                    if (TryDeleteEventTree(rootFull, e.path, out long freed))
                    {
                        bytesFreed += freed;
                        backupsRemoved++;
                    }
                    else survivors.Add(e); // nie udało się → zostaje na następny raz
                }

                // --- 3. Retencja wiekowa: pliki legacy ---
                if (retentionDays > 0)
                {
                    DateTime cutoffUtc = nowUtc - TimeSpan.FromDays(retentionDays);
                    foreach (var u in legacyUnits)
                    {
                        token.ThrowIfCancellationRequested();
                        if (u.createdUtc >= cutoffUtc) continue;
                        if (TryDeleteBackupFile(rootFull, u.path, out long freed))
                        {
                            bytesFreed += freed;
                            legacyFilesRemoved++;
                        }
                    }
                }

                // --- 4. Limit rozmiaru (najstarsze pierwsze, ochrona 24h) ---
                var units = new List<(string path, long size, DateTime timeUtc, bool isEvent)>();
                foreach (var s in survivors)
                    units.Add((s.path, s.bytes, s.ts, true));
                foreach (var u in legacyUnits)
                    if (File.Exists(u.path)) // odfiltruj usunięte w kroku 3
                        units.Add((u.path, u.size, u.createdUtc, false));

                long total = 0;
                foreach (var u in units) total += u.size;

                if (capBytes > 0 && total > capBytes)
                {
                    units.Sort((a, b) => a.timeUtc.CompareTo(b.timeUtc));

                    foreach (var u in units)
                    {
                        if (total <= capBytes) break;

                        // Posortowane rosnąco: pierwsza chroniona = wszystkie dalsze młodsze.
                        if (nowUtc - u.timeUtc < MinProtectedAge) break;

                        if (u.isEvent)
                        {
                            if (TryDeleteEventTree(rootFull, u.path, out long freed))
                            {
                                bytesFreed += freed;
                                total -= u.size;
                                backupsRemoved++;
                            }
                        }
                        else
                        {
                            if (TryDeleteBackupFile(rootFull, u.path, out long freed))
                            {
                                bytesFreed += freed;
                                total -= u.size;
                                legacyFilesRemoved++;
                            }
                        }
                    }
                    overCap = total > capBytes;
                }

                // --- 5. Sprzątanie pustych katalogów (legacy po kasowaniu plików) ---
                PruneEmptyDirectories(rootFull);

                // --- 6. Podsumowanie ---
                try
                {
                    foreach (var d in Directory.GetDirectories(rootFull))
                        if (IsTimestampFolder(Path.GetFileName(d), out _))
                            backupsRemaining++;
                }
                catch { }
                bytesRemaining = GetTreeSizeBytes(rootFull);
                try { diskFree = new DriveInfo(rootFull).AvailableFreeSpace; } catch { }

                _log($"<color=00FF88><b>[Backup] Cleanup complete:</b></color> removed {backupsRemoved} backup(s) + {legacyFilesRemoved} legacy file(s) — {FormatBytes(bytesFreed)} freed.");
                _log($"<color=yellow>  Remaining : {FormatBytes(bytesRemaining)} in {backupsRemaining} backup(s) | Disk free: {FormatBytes(diskFree)}</color>");
                _log($"<color=yellow>  Policy    : backup {(BackupEnabled ? "on" : "OFF")}, retention {retentionDays} d, cap {(capBytes <= 0 ? "unlimited" : FormatBytes(capBytes))}, protected < {MinProtectedAge.TotalHours:0} h</color>");
                if (overCap)
                    _log("<color=#FFAA00>  Still over cap — backups younger than 24h are protected. Raise the cap or clean manually.</color>");

                return new SVNBackupCleanupResult(backupsRemoved, legacyFilesRemoved, bytesFreed,
                    bytesRemaining, backupsRemaining, overCap, diskFree);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"<color=#FFAA00>[Backup] Cleanup failed: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return new SVNBackupCleanupResult(backupsRemoved, legacyFilesRemoved, bytesFreed,
                    bytesRemaining, backupsRemaining, overCap, diskFree);
            }
        }

        #endregion

        #region Purge (explicit, user-confirmed)

        /// <summary>
        /// Usuwa CAŁOŚĆ zawartości backup root (foldery-stampy + legacy pliki).
        /// Nie dotyka samego folderu root ani NICZEGO poza nim (containment
        /// sprawdzany per-item). Celowo IGNORUJE ochronę 24h — to jawna,
        /// dwukrotnie potwierdzona akcja użytkownika. Pliki źródłowe w working
        /// copy pozostają nietknięte (backup to kopie/przeniesienia, nie źródła).
        /// </summary>
        public Task<SVNBackupCleanupResult> PurgeAllAsync(CancellationToken token = default)
        {
            return Task.Run(() => PurgeAllCore(token), token);
        }

        private SVNBackupCleanupResult PurgeAllCore(CancellationToken token)
        {
            int removed = 0;
            long freed = 0, remaining = 0, diskFree = -1;

            try
            {
                string root = GetBackupRoot();
                if (string.IsNullOrEmpty(root))
                {
                    _log("<color=#FFAA00>[Backup] Purge skipped — backup folder unavailable.</color>");
                    return new SVNBackupCleanupResult(0, 0, 0, 0, 0, false, -1);
                }

                string rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string[] dirs;
                try { dirs = Directory.GetDirectories(rootFull); }
                catch { dirs = Array.Empty<string>(); }

                foreach (var dir in dirs)
                {
                    token.ThrowIfCancellationRequested();
                    if (TryDeleteEventTree(rootFull, dir, out long b)) { freed += b; removed++; }
                }

                string[] files;
                try { files = Directory.GetFiles(rootFull); }
                catch { files = Array.Empty<string>(); }

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (TryDeleteBackupFile(rootFull, file, out long b)) { freed += b; removed++; }
                }

                PruneEmptyDirectories(rootFull);

                remaining = GetTreeSizeBytes(rootFull);
                try { diskFree = new DriveInfo(rootFull).AvailableFreeSpace; } catch { }

                _log($"<color=#FF4444><b>[Backup] PURGE complete:</b></color> removed {removed} item(s), {FormatBytes(freed)} freed.");
                _log($"<color=yellow>  Backup folder kept (empty): {SVNPathUtilities.ForDisplay(root)}</color>");
                _log("<color=yellow>  Working copy sources NOT touched.</color>");

                return new SVNBackupCleanupResult(removed, 0, freed, remaining, 0, false, diskFree);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"<color=#FFAA00>[Backup] Purge failed: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return new SVNBackupCleanupResult(removed, 0, freed, remaining, 0, false, diskFree);
            }
        }

        /// <summary>Backup root dla UI (Open Folder).</summary>
        public string GetBackupRootForUi() => GetBackupRoot();

        #endregion

        #region Janitor helpers

        private static bool IsTimestampFolder(string name, out DateTime tsUtc)
        {
            tsUtc = default;
            if (string.IsNullOrEmpty(name) || !TimestampFolderRegex.IsMatch(name)) return false;
            return DateTime.TryParseExact(name, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out tsUtc);
        }

        private static void CollectLegacyFiles(string dir, List<(string path, long size, DateTime createdUtc)> into)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        into.Add((f, fi.Length, fi.CreationTimeUtc));
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static long GetTreeSizeBytes(string dir)
        {
            long total = 0;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        // === BEZPIECZEŃSTWO: nic poza backup root nigdy nie jest usuwane.
        private static bool IsUnderRoot(string rootFull, string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private bool TryDeleteEventTree(string rootFull, string dir, out long bytes)
        {
            bytes = 0;
            try
            {
                if (!Directory.Exists(dir)) return true; // już nie ma = sukces (0 B)
                if (!IsUnderRoot(rootFull, dir))
                {
                    _log($"<color=#FF4444>[Backup] Cleanup: path outside backup root — SKIPPED: {SVNPathUtilities.ForDisplay(dir)}</color>");
                    return false;
                }
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    _log($"<color=#FFAA00>[Backup] Cleanup: reparse point skipped: {SVNPathUtilities.ForDisplay(dir)}</color>");
                    return false;
                }

                long size = GetTreeSizeBytes(dir);

                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                { try { File.SetAttributes(f, FileAttributes.Normal); } catch { } }
                foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                { try { File.SetAttributes(d, FileAttributes.Normal); } catch { } }

                Directory.Delete(dir, true);

                if (Directory.Exists(dir)) return false;
                bytes = size; // licz tylko po udanym usunięciu
                return true;
            }
            catch (Exception ex)
            {
                _log($"<color=#FFAA00>[Backup] Cleanup: could not remove {SVNPathUtilities.ForDisplay(dir)}: {SVNPathUtilities.ForDisplay(ex.Message)}</color>");
                return false;
            }
        }

        private bool TryDeleteBackupFile(string rootFull, string file, out long bytes)
        {
            bytes = 0;
            try
            {
                if (!File.Exists(file)) return true;
                if (!IsUnderRoot(rootFull, file)) return false;

                var fi = new FileInfo(file);
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) return false;

                long size = fi.Length;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);

                if (File.Exists(file)) return false;
                bytes = size;
                return true;
            }
            catch { return false; }
        }

        private static int PruneEmptyDirectories(string rootFull)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(rootFull, "*", SearchOption.AllDirectories); }
            catch { return 0; }

            // Najgłębiej pierwsze: dziecko przed rodzicem.
            Array.Sort(dirs, (a, b) =>
            {
                int depthA = a.Count(c => c == Path.DirectorySeparatorChar);
                int depthB = b.Count(c => c == Path.DirectorySeparatorChar);
                if (depthA != depthB) return depthB.CompareTo(depthA);
                return string.CompareOrdinal(b, a);
            });

            int pruned = 0;
            foreach (var d in dirs)
            {
                try { Directory.Delete(d); pruned++; } // IOException = niepusty → pomijamy
                catch { }
            }
            return pruned;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "n/a";
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double d = bytes;
            while (d >= 1024 && i < suffix.Length - 1) { d /= 1024.0; i++; }
            return $"{d:0.#} {suffix[i]}";
        }

        #endregion

        #region Backup path helpers

        private string GetBackupRoot()
        {
            try
            {
                string projectName = Application.productName;
                if (string.IsNullOrWhiteSpace(projectName))
                    projectName = "SVN_Project";

                foreach (char c in Path.GetInvalidFileNameChars())
                    projectName = projectName.Replace(c, '_');

                string backupRoot = Path.Combine(SVNPrefs.PersistentDataPath, $"{projectName}_Backup");

                if (!Directory.Exists(backupRoot))
                    Directory.CreateDirectory(backupRoot);

                return backupRoot;
            }
            catch
            {
                return null;
            }
        }

        private string GetRelativeToWorkingDir(string fullPath)
        {
            try
            {
                string root = Path.GetFullPath(_svnManager.WorkingDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(fullPath);

                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length)
                               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return Path.GetFileName(fullPath);
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        private static string MakeUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;

            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            string candidate = Path.Combine(dir, $"{name}_{timestamp}{ext}");
            int counter = 1;
            while (File.Exists(candidate) || Directory.Exists(candidate))
                candidate = Path.Combine(dir, $"{name}_{timestamp}_{counter++}{ext}");

            return candidate;
        }

        private static void CopyDirectory(string sourceDir, string destDir, CancellationToken token)
        {
            if (!Directory.Exists(sourceDir))
                return;

            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                token.ThrowIfCancellationRequested();
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                token.ThrowIfCancellationRequested();
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir, token);
            }
        }

        #endregion
    }
}