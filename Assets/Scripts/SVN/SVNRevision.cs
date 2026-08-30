using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNRevision : SVNBase
    {
        private const float DoubleClickThreshold = 5.0f;

        private float _lastUpdateToRevClickTime;
        private string _pendingRevision;

        private float _lastRevertClickTime;
        private string _pendingRevertInput;

        private int _processingFlag;

        public SVNRevision(SVNUI svnUI, SVNManager svnManager) : base(svnUI, svnManager) { }

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

        private void LogToRevisionPanel(string message, bool append = true)
        {
            if (svnUI?.RevisionDisplayArea != null)
            {
                SVNLogBridge.UpdateUIField(svnUI.RevisionDisplayArea, message);
            }
            else
            {
                SVNLogBridge.LogLine(message, append);
            }
        }

        public async void UpdateToRevisionButton()
        {
            if (IsProcessing) return;

            if (svnUI?.UpdateRevisionInput == null)
            {
                LogToRevisionPanel("<color=red>Error: UpdateRevisionInput is not assigned in SVNUI Inspector!</color>");
                return;
            }

            string rev = svnUI.UpdateRevisionInput.text?.Trim();

            if (string.IsNullOrWhiteSpace(rev))
            {
                LogToRevisionPanel("<color=yellow>No revision specified. Executing standard update to HEAD...</color>", append: false);
                svnManager.GetModule<SVNUpdate>()?.Update();
                return;
            }

            rev = rev.TrimStart('r', 'R');

            if (!int.TryParse(rev, out int _))
            {
                LogToRevisionPanel("<color=red>Error: Invalid format. Please enter just numbers (e.g. 150).</color>");
                return;
            }

            var updateModule = svnManager.GetModule<SVNUpdate>();
            if (updateModule == null)
            {
                LogToRevisionPanel("<color=red>Error: SVNUpdate module is not available.</color>");
                return;
            }

            float now = Time.time;

            // === S5: szczegółowy dirty-state zamiast płaskiej blokady —
            // unversioned NIE blokują (svn update -r ich nie dotyka), tylko informują.
            var dirty = await svnManager.GetWorkingCopyDirtyStateAsync(svnManager.WorkingDir).ConfigureAwait(false);

            if (dirty.IsBlockingDirty)
            {
                string conflicts = dirty.ConflictedCount > 0 ? $", conflicts: {dirty.ConflictedCount}" : "";
                LogToRevisionPanel(
                    $"<color=#FFAA00>Cannot update to a specific revision: you have uncommitted versioned changes{conflicts}.\n" +
                    "Commit or revert them first.</color>");
                return;
            }

            if (dirty.UnversionedCount > 0)
            {
                LogToRevisionPanel(
                    $"<color=yellow>Note: {dirty.UnversionedCount} unversioned file(s) detected — they will be left untouched by this update.</color>");
            }

            float timeSinceLastClick = now - _lastUpdateToRevClickTime;

            if (timeSinceLastClick < DoubleClickThreshold && _pendingRevision == rev)
            {
                _pendingRevision = null;
                LogToRevisionPanel($"<color=green>Executing update to revision {rev}...</color>", append: false);
                updateModule.UpdateToRevision(rev);
            }
            else
            {
                _lastUpdateToRevClickTime = now;
                _pendingRevision = rev;
                LogToRevisionPanel(
                    $"<color=#FFAA00><b>ATTENTION:</b> Click <b>ONE MORE TIME</b> within 5 seconds to confirm update to revision {rev}.\n" +
                    "This will overwrite local files!</color>", append: false);
            }
        }

        public async void RevertCommitsButton() => await RevertCommitsFromInputAsync();

        private async Task RevertCommitsFromInputAsync()
        {
            if (svnUI?.UpdateRevisionInput == null)
            {
                LogToRevisionPanel("<color=#FFAA00>[Revert] Revision input field not assigned in UI.</color>");
                return;
            }

            string inputText = svnUI.UpdateRevisionInput.text?.Trim();
            if (string.IsNullOrWhiteSpace(inputText))
            {
                LogToRevisionPanel("<color=#FFAA00>[Revert] Please enter revision numbers (e.g. 150, 148:150, 155).</color>");
                return;
            }

            var revisionItems = SvnRevisionRangeParser.Parse(inputText);

            if (revisionItems.Count == 0)
            {
                LogToRevisionPanel("<color=#FFAA00>[Revert] No valid revision numbers entered.</color>");
                return;
            }

            var cleanRevs = new List<string>();
            foreach (var item in revisionItems)
            {
                if (item.IsRange)
                    cleanRevs.Add($"r{item.Start}:r{item.End}");
                else
                    cleanRevs.Add($"r{item.Start}");
            }

            string revListString = cleanRevs.Count == 1
                ? $"revision {cleanRevs[0]}"
                : $"revisions: {string.Join(", ", cleanRevs)}";

            // Time.time przed pierwszym await — OK (main thread, wywołanie z przycisku).
            float timeSinceLastClick = Time.time - _lastRevertClickTime;

            if (timeSinceLastClick < DoubleClickThreshold && _pendingRevertInput == inputText)
            {
                _pendingRevertInput = null;
            }
            else
            {
                _lastRevertClickTime = Time.time;
                _pendingRevertInput = inputText;

                LogToRevisionPanel(
                    $"<color=#FFAA00><b>ATTENTION:</b> Click <b>ONE MORE TIME</b> within {DoubleClickThreshold} seconds to confirm undoing changes from {revListString}.\n" +
                    "This will modify your working copy!</color>", append: false);
                return;
            }

            if (!TryEnterProcessing()) return;

            try
            {
                string workingDir = svnManager?.WorkingDir;
                if (string.IsNullOrWhiteSpace(workingDir))
                {
                    LogToRevisionPanel("<color=#FFAA00>[Revert] Working directory not set.</color>");
                    return;
                }

                // === FIX S1: zakres [A..B] jako JEDEN argument '-r B:(A-1)' zamiast
                // (B-A+1) × '-c -N'. Odwrócenie zmian wprowadzonych w [A..B] to
                // merge -r B:(A-1); stary zapis rozsadzał linię komend przy dużych
                // zakresach (limit ~32k znaków → Process.Start padał).
                var revArgs = new StringBuilder();
                foreach (var item in revisionItems)
                {
                    if (item.IsRange)
                        revArgs.Append($"-r {item.End}:{item.Start - 1} ");
                    else
                        revArgs.Append($"-c -{item.Start} ");
                }

                LogToRevisionPanel($"[REVERT COMMITS] Undoing changes from {revListString}...", append: false);
                LogToRevisionPanel("[Revert] Bringing working copy to a uniform revision...");

                try
                {
                    await SvnRunner.RunAsync("update", workingDir, true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogToRevisionPanel($"<color=yellow>[Revert] Update failed (non-fatal): {ex.Message}</color>");
                }

                string args = $"merge {revArgs}. --non-interactive --accept postpone";
                LogToRevisionPanel($"[Revert] Executing: svn {args}");

                string output = await SvnRunner.RunAsync(args, workingDir, true, CancellationToken.None).ConfigureAwait(false);

                // === FIX: wykrywanie konfliktów bez 'IndexOf("C ")' — dopasowywało
                // dowolny tekst zawierający "C " (np. nazwy plików) i zjadało
                // komunikat sukcesu.
                bool hasConflicts = output.IndexOf("conflict", StringComparison.OrdinalIgnoreCase) >= 0;

                if (string.IsNullOrWhiteSpace(output) || output.Contains("No changes") || output.Contains("Already merged"))
                {
                    LogToRevisionPanel($"<color=yellow>[Revert] {revListString} has no effect on current working copy.</color>");
                }
                else if (hasConflicts)
                {
                    LogToRevisionPanel("<color=yellow>[REVERT CONFLICTS] Reverting caused conflicts! Please use the Resolve panel to fix them before committing.</color>");
                }
                else
                {
                    LogToRevisionPanel($"<color=green>[Revert] Successfully reverted {revListString}.</color>");
                    LogToRevisionPanel("<color=#FFFF00>IMPORTANT: You MUST commit these changes now to finalize the undo.</color>");
                }

                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogToRevisionPanel("<color=yellow>[Revert] Cancelled by user.</color>");
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=#FFAA00>[Revert Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // === FIX S4: metoda nie ma żadnego await — guard TryEnterProcessing był
        // zwalniany natychmiast (chronił nic) i generował CS1998. Delegowanie do
        // SVNExternal (moduł ma własny guard); przycisk wywołuje synchronicznie.
        public void ExportRevisionButton() => ExportRevisionFromInput();

        private void ExportRevisionFromInput()
        {
            if (svnUI?.UpdateRevisionInput == null)
            {
                LogToRevisionPanel("<color=red>Error: UpdateRevisionInput is not assigned in SVNUI Inspector!</color>");
                return;
            }

            string rev = svnUI.UpdateRevisionInput.text?.Trim()?.TrimStart('r', 'R');
            if (string.IsNullOrWhiteSpace(rev))
            {
                LogToRevisionPanel("<color=#FFAA00>[Export] Please enter a valid revision number to export.</color>");
                return;
            }

            if (!int.TryParse(rev, out int _))
            {
                LogToRevisionPanel("<color=red>[Export] Error: Invalid revision format. Please enter numbers only (e.g. 150).</color>");
                return;
            }

            var externalModule = svnManager.GetModule<SVNExternal>();

            if (externalModule != null)
            {
                LogToRevisionPanel($"<color=green>[Export] Initiating export for revision r{rev}...</color>");
                externalModule.ExportRevision(rev);
            }
            else
            {
                LogToRevisionPanel("<color=red>[Export Error] SVNExternal module was not found in SVNManager!</color>");
            }
        }

        // === FIX K1 (krytyczny): 'svn cat' przez strumień BAJTÓW (RunToFileAsync).
        // Stara wersja (RunAsync → string → WriteAllText) dekodowała UTF-8 i sklejała
        // linie — binaria (.png/.asset/.unity) były NIEODWRACALNIE uszkadzane,
        // a plik lądował w working copy → po commicie uszkodzenie trafiało do REPO.
        public async Task RestoreSingleFileAsync(string relativeFilePath, string revision)
        {
            if (IsProcessing) return;
            if (!TryEnterProcessing()) return;

            try
            {
                string cleanPath = relativeFilePath.Replace('\\', '/').TrimStart('/');

                LogToRevisionPanel($"<color=green>[Restore File] Fetching {cleanPath} at r{revision}...</color>");

                string fullDiskPath = Path.Combine(svnManager.WorkingDir, cleanPath.Replace('/', Path.DirectorySeparatorChar));

                // === FIX S3: plik mógł być usunięty razem z katalogiem-nadrzędnym —
                // bez tego WriteAllText/Create rzucał DirectoryNotFoundException.
                string destDir = Path.GetDirectoryName(fullDiskPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                string args = $"cat -r {revision} {SvnMergeUrlResolver.EscapeSvnArg(cleanPath)}";
                var (exitCode, error) = await SvnRunner.RunToFileAsync(args, svnManager.WorkingDir, fullDiskPath)
                    .ConfigureAwait(false);

                if (exitCode != 0)
                {
                    LogToRevisionPanel($"<color=yellow>[Restore File] Failed (code {exitCode}): {error?.Trim()}</color>");
                    try { if (File.Exists(fullDiskPath)) File.Delete(fullDiskPath); } catch { }
                    return;
                }

                LogToRevisionPanel($"<color=green>[Restore File] Successfully restored: {cleanPath}</color>");

                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=red>[Restore File Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // === FIX K1: jw. — binarnie bezpieczny zapis do wskazanej lokalizacji.
        public async Task ExtractSingleFileToAsync(string relativeFilePath, string revision, string destinationPath)
        {
            if (IsProcessing) return;
            if (!TryEnterProcessing()) return;

            try
            {
                string cleanPath = relativeFilePath.Replace('\\', '/').TrimStart('/');

                LogToRevisionPanel($"<color=green>[Extract File] Fetching {cleanPath} at r{revision}...</color>");

                string args = $"cat -r {revision} {SvnMergeUrlResolver.EscapeSvnArg(cleanPath)}";
                var (exitCode, error) = await SvnRunner.RunToFileAsync(args, svnManager.WorkingDir, destinationPath)
                    .ConfigureAwait(false);

                if (exitCode != 0)
                {
                    LogToRevisionPanel($"<color=yellow>[Extract File] Failed (code {exitCode}): {error?.Trim()}</color>");
                    return;
                }

                LogToRevisionPanel($"<color=green>[Extract File] Saved to: {destinationPath}</color>");
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=red>[Extract File Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        // UWAGA: celowo usuwam stare 'Directory.CreateDirectory' z Extract —
        // RunToFileAsync FileStream(Create) NIE tworzy katalogów; zostało przeniesione
        // do wnętrza (patrz wyżej — destDir tworzony przed wywołaniem w Restore;
        // dla Extract dodajemy poniżej).
        public async Task ExtractFolderToAsync(string relativeFolderPath, string revision, string targetLocalPath)
        {
            if (string.IsNullOrWhiteSpace(relativeFolderPath) || string.IsNullOrWhiteSpace(targetLocalPath))
                throw new ArgumentException("Folder path and target path cannot be empty.");

            if (string.IsNullOrWhiteSpace(revision))
                throw new ArgumentException("Revision cannot be empty.", nameof(revision));

            if (!TryEnterProcessing()) return;

            try
            {
                string normalizedPath = SvnRunner.NormalizeRepositoryPath(relativeFolderPath);
                string rev = revision.TrimStart('r', 'R');

                LogToRevisionPanel($"<color=yellow>[SVN Revision]</color> Resolving URL for folder: {normalizedPath}...");

                // === FIX S2: 'info' na ścieżce nieistniejącej lokalnie RZUCA
                // (RunAsync ma throwOnError) — cały fallback "construct URL manually"
                // był MARTWY (wyjątek przeskakiwał prosto do catch). Teraz łapiemy
                // i realnie schodzimy do ścieżki ręcznej.
                string folderUrl = null;
                bool infoFailed = false;
                try
                {
                    folderUrl = await SvnRunner.RunAsync($"info --show-item url \"{normalizedPath}\"", svnManager.WorkingDir)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    infoFailed = true;
                    string firstLine = (ex.Message ?? "").Split('\n')[0];
                    LogToRevisionPanel($"<color=yellow>[SVN Revision] Local info failed ({firstLine.Trim()}). Constructing URL manually...</color>");
                }

                if (infoFailed || string.IsNullOrWhiteSpace(folderUrl))
                {
                    string rootUrl = await SvnRunner.RunAsync("info --show-item url", svnManager.WorkingDir)
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(rootUrl))
                        throw new Exception("Cannot determine repository URL. Is this a valid SVN working copy?");

                    folderUrl = $"{rootUrl.TrimEnd('/')}/{normalizedPath.TrimStart('/')}";
                }

                LogToRevisionPanel($"<color=yellow>[SVN Revision]</color> Exporting r{rev} from: {folderUrl}");
                LogToRevisionPanel($"<color=yellow>[SVN Revision]</color> To local path: {targetLocalPath}");

                string command = $"export -r {rev} \"{folderUrl}\" \"{targetLocalPath}\" --force";
                await SvnRunner.RunAsync(command, svnManager.WorkingDir, true, CancellationToken.None).ConfigureAwait(false);

                LogToRevisionPanel("<color=green>[SVN Revision]</color> Folder successfully extracted.");
                LogToRevisionPanel($"<color=green>Folder from r{rev} saved to: {targetLocalPath}</color>");
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=#FFAA00>[Extract Folder Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }

        public async Task RevertPathAsync(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                LogToRevisionPanel("<color=#FFAA00>[Revert Path] Path cannot be empty.</color>");
                return;
            }

            if (!TryEnterProcessing()) return;

            try
            {
                string cleanPath = relativePath.Replace('\\', '/').TrimStart('/');

                LogToRevisionPanel($"<color=green>[Revert Path] Reverting {cleanPath} to BASE...</color>");

                await SvnRunner.RunAsync($"revert \"{cleanPath}\"", svnManager.WorkingDir, true, CancellationToken.None)
                    .ConfigureAwait(false);

                LogToRevisionPanel($"<color=green>[Revert Path] Successfully reverted: {cleanPath}</color>");

                await svnManager.RefreshStatus().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogToRevisionPanel($"<color=#FFAA00>[Revert Path Error] {ex.Message}</color>");
            }
            finally
            {
                ExitProcessing();
            }
        }
    }
}