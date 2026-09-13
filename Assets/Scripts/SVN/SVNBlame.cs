using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TMPro;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBlame : SVNBase, IDisposable
    {
        // ===================================================================
        //  ARCHITEKTURA HYBRID:
        //
        //  Twój klient SVN (1.8.x) NIE emituje <line> w XML — daje tylko
        //  <commit> (rev + author). Content trzeba wziąć skądinąd.
        //
        //  Rozwiązanie:
        //   1. `svn blame --xml` → pełni autorzy + rewizje per linia.
        //   2. Jeśli XML ma content (nowszy SVN) → używamy go.
        //   3. Jeśli NIE:
        //      a. plik na dysku — TYLKO gdy plik jest CLEAN (brak lokalnych
        //         zmian). Jeśli ma M/A/?, dysk kłamie (BASE rev vs current
        //         content) i mapping line-number → index się przesuwa.
        //      b. tekstowy `svn blame` — fallback zawsze poprawny.
        //
        //  Poprawki:
        //   - Local-mods detection: M/A/? → text blame (nie dysk).
        //   - Mismatch warning: XML vs content line count różni się.
        //   - BOM handling: UTF-8 + strip \uFEFF z pierwszej linii.
        //   - CLEAR PANEL: nowy blame czyści poprzedni wynik przed startem.
        // ===================================================================

        private const int MaxPreviewLines = 2000;

        private CancellationTokenSource _cts;
        private int _disposed;

        public SVNBlame(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        protected override TMP_Text GetConsole() => svnUI?.BlameConsoleText;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            SVNShared.CancelAndDisposeLater(Interlocked.Exchange(ref _cts, null));
        }

        public void Cancel()
        {
            try { Volatile.Read(ref _cts)?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private bool TryEnterProcessing()
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            return TryStart();
        }

        private void ExitProcessing() => End();

        private void PostLog(string msg) => PostToMainThread(() => LogBoth(msg));

        private void SafeFireAndForget(Func<Task> operation) => _ = FireAndForget(operation);

        private async Task FireAndForget(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PostLog($"<color=#FFAA00>Unhandled:</color> {EscapeRichText(ex.Message)}"); }
        }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            if (svnUI?.BlameConsoleText != null)
                SVNLogBridge.UpdateUIField(svnUI.BlameConsoleText, msg, "BLAME", true);
        }

        // === FIX (clear): czyści panel Blame przed nowym blame. Nie dotyka
        // LogText (główny log). Wywoływane raz na starcie operacji, żeby user
        // nie widział starego raportu pomieszanego z "Fetching Annotations...".
        private void ClearBlamePanel()
        {
            PostToMainThread(() =>
            {
                try
                {
                    if (svnUI?.BlameConsoleText != null)
                        svnUI.BlameConsoleText.text = string.Empty;

                    if (svnUI?.BlameDisplayArea != null)
                        svnUI.BlameDisplayArea.text = string.Empty;
                }
                catch { }
            });
        }

        // ===================================================================
        //  PUBLICZNE API
        // ===================================================================

        public void ExecuteBlame()
        {
            string relativePath = svnUI?.BlameTargetFileInput?.text?.Trim();
            string uiTextEditorPath = svnUI?.SettingsMergeToolPathInput?.text;
            SafeFireAndForget(() => RunBlameAsync(relativePath, new BlameOptions
            {
                OpenReport = true,
                UiTextEditorPath = uiTextEditorPath
            }));
        }

        public Task ShowBlame(string relativePath, CancellationToken token = default)
            => RunBlameAsync(relativePath, new BlameOptions(), token);

        public Task ShowBlameInExternalEditor(string relativePath)
            => RunBlameAsync(relativePath, new BlameOptions
            {
                OpenReport = true,
                AllowDefaultOpen = true
            });

        public Task ShowBlameInMainConsole(string relativePath)
            => RunBlameAsync(relativePath, new BlameOptions
            {
                ForceMainConsole = true
            });

        public Task ShowBlameAtRevision(string relativePath, string revision, CancellationToken token = default)
            => RunBlameAsync(relativePath, new BlameOptions { Revision = revision }, token);

        private sealed class BlameOptions
        {
            public bool ForceMainConsole;
            public bool OpenReport;
            public bool AllowDefaultOpen;
            public string UiTextEditorPath;
            public string Revision;
        }

        // ===================================================================
        //  RDZEŃ
        // ===================================================================

        private async Task RunBlameAsync(string relativePath, BlameOptions options, CancellationToken externalToken = default)
        {
            if (!TryEnterProcessing())
            {
                PostLog("<color=yellow>Blame already in progress – cancelling it. Try again in a moment.</color>");
                Cancel();
                return;
            }

            CancellationTokenSource cts = externalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                : new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts);

            try
            {
                if (string.IsNullOrEmpty(relativePath))
                {
                    PostLog("<color=yellow>Please select a file path first.</color>");
                    return;
                }

                bool isServer = SVNShared.IsServerPath(relativePath);
                string workDir = svnManager?.WorkingDir;

                if (!isServer)
                {
                    if (string.IsNullOrWhiteSpace(workDir))
                    {
                        PostLog("<color=#FFAA00>Error:</color> Working Directory not set!");
                        return;
                    }
                    if (!SVNShared.TryGetRelativePath(workDir, relativePath, out string safeRelative))
                    {
                        PostLog("<color=#FFAA00>Invalid path or outside working directory.</color>");
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(safeRelative))
                    {
                        PostLog("<color=yellow>No file selected.</color>");
                        return;
                    }
                    relativePath = safeRelative;
                }

                // === FIX (clear): wyczyść panel przed startem. Robimy to PO
                // walidacji (żeby błędne wywołania nie kasowały poprzedniego
                // raportu), ale PRZED "Fetching Annotations..." (żeby user
                // widział czysty start).
                ClearBlamePanel();

                await svnManager.CancelBackgroundTasksAsync().ConfigureAwait(false);
                await ShowBlameInternal(relativePath, isServer, options, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostLog("<color=#8B0000>Blame cancelled.</color>");
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>Blame failed:</color> {EscapeRichText(ex.Message)}");
            }
            finally
            {
                Interlocked.CompareExchange(ref _cts, null, cts);
                SVNShared.CancelAndDisposeLater(cts);
                ExitProcessing();
            }
        }

        private async Task ShowBlameInternal(string safePath, bool isServer, BlameOptions options, CancellationToken token)
        {
            string workDir = svnManager.WorkingDir;
            string fullLocalPath = isServer ? "" : Path.GetFullPath(Path.Combine(workDir, safePath));

            string svnTarget;
            if (isServer)
            {
                if (safePath.Contains("://"))
                {
                    svnTarget = safePath;
                }
                else
                {
                    string repoUrl = svnManager.RepositoryUrl?.TrimEnd('/');
                    if (string.IsNullOrEmpty(repoUrl))
                    {
                        PostLog("<color=#FFAA00>Error:</color> Cannot blame server path, Repository URL is empty.");
                        return;
                    }
                    svnTarget = repoUrl + safePath;
                }
            }
            else
            {
                svnTarget = safePath;
            }

            string revArg = string.IsNullOrWhiteSpace(options.Revision)
                ? ""
                : $"-r {SVNShared.EscapeSvnArg(options.Revision)} ";
            string revLabel = string.IsNullOrWhiteSpace(options.Revision)
                ? ""
                : $" (r{EscapeRichText(options.Revision)})";

            PostLog($"Fetching Annotations{revLabel} for: <color=green>{EscapeRichText(safePath)}</color>...");

            // ===== KROK 1: XML blame → metadane (rev + author) =====
            string xmlRaw;
            try
            {
                xmlRaw = await SvnRunner.RunAsync(
                    $"blame --xml {revArg}\"{SVNShared.EscapeSvnArg(svnTarget)}\"",
                    workDir, false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                PostLog($"<color=#FF4444>SVN Error:</color> {EscapeRichText(ex.Message)}");
                return;
            }

            if (string.IsNullOrWhiteSpace(xmlRaw))
            {
                await DiagnoseEmptyBlameAsync(safePath, isServer, fullLocalPath, token).ConfigureAwait(false);
                return;
            }

            string xmlTrimmed = xmlRaw.TrimStart();
            if (xmlTrimmed.StartsWith("svn: E") || xmlTrimmed.StartsWith("svn: W"))
            {
                PostLog($"<color=#FF4444>SVN Error:</color> {EscapeRichText(xmlRaw.Trim())}");
                return;
            }

            XDocument doc;
            try
            {
                int xmlStart = xmlRaw.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
                if (xmlStart < 0) xmlStart = xmlRaw.IndexOf("<blame", StringComparison.OrdinalIgnoreCase);
                string xmlOnly = xmlStart > 0 ? xmlRaw.Substring(xmlStart) : xmlRaw;
                doc = XDocument.Parse(xmlOnly);
            }
            catch (Exception ex)
            {
                PostLog($"<color=#FF4444>Failed to parse blame XML:</color> {EscapeRichText(ex.Message)}");
                return;
            }

            var xmlEntries = new List<(int LineNum, string Rev, string Author, string XmlContent)>();
            int entryIndex = 0;

            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "entry"))
            {
                token.ThrowIfCancellationRequested();
                entryIndex++;

                int lineNum = entryIndex;
                if (int.TryParse(entry.Attribute("line-number")?.Value, out int ln))
                    lineNum = ln;

                var commitEl = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "commit");

                string rev;
                string author;
                if (commitEl == null)
                {
                    rev = "local";
                    author = "(uncommitted)";
                }
                else
                {
                    rev = commitEl.Attribute("revision")?.Value ?? "-";
                    if (rev == "0") rev = "local";
                    author = commitEl.Elements().FirstOrDefault(e => e.Name.LocalName == "author")?.Value;
                    if (string.IsNullOrEmpty(author)) author = "(no author)";
                }

                string xmlContent = ExtractBlameContentFromXml(entry, commitEl);
                xmlEntries.Add((lineNum, rev, author, xmlContent));
            }

            if (xmlEntries.Count == 0)
            {
                await DiagnoseEmptyBlameAsync(safePath, isServer, fullLocalPath, token).ConfigureAwait(false);
                return;
            }

            // ===== KROK 2: content =====
            bool xmlHasContent = xmlEntries.Any(e => !string.IsNullOrEmpty(e.XmlContent));

            List<string> contents;

            if (xmlHasContent)
            {
                contents = xmlEntries.Select(e => e.XmlContent).ToList();
            }
            else
            {
                contents = null;

                bool canUseDiskFile =
                    !isServer &&
                    string.IsNullOrWhiteSpace(options.Revision) &&
                    !string.IsNullOrEmpty(fullLocalPath) &&
                    File.Exists(fullLocalPath);

                if (canUseDiskFile)
                {
                    string wcStatus = await GetFileWcStatusAsync(safePath, token).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(wcStatus))
                    {
                        PostLog($"<color={SVNColors.Dim}>[Blame] File has local changes ({EscapeRichText(wcStatus)}) — using text blame instead of disk file.</color>");
                        canUseDiskFile = false;
                    }
                }

                if (canUseDiskFile)
                {
                    try
                    {
                        var diskLines = await File.ReadAllLinesAsync(fullLocalPath, Encoding.UTF8, token).ConfigureAwait(false);
                        if (diskLines.Length > 0 && diskLines[0].Length > 0 && diskLines[0][0] == '\uFEFF')
                            diskLines[0] = diskLines[0].Substring(1);

                        contents = diskLines.ToList();
                        PostLog($"<color={SVNColors.Dim}>[Blame] XML has no <line> — using local file content ({contents.Count} lines).</color>");
                    }
                    catch (Exception ex)
                    {
                        PostLog($"<color=#FFAA00>[Blame] Could not read local file:</color> {EscapeRichText(ex.Message)}");
                        contents = null;
                    }
                }

                if (contents == null)
                {
                    string textRaw;
                    try
                    {
                        textRaw = await SvnRunner.RunAsync(
                            $"blame {revArg}\"{SVNShared.EscapeSvnArg(svnTarget)}\"",
                            workDir, false, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        PostLog($"<color=#FF4444>SVN text blame error:</color> {EscapeRichText(ex.Message)}");
                        return;
                    }

                    contents = ParseTextBlameContents(textRaw);
                    PostLog($"<color={SVNColors.Dim}>[Blame] XML has no <line> — parsed text blame ({contents.Count} lines).</color>");
                }

                if (contents.Count != xmlEntries.Count)
                {
                    PostLog($"<color=#FFAA00>[Blame] Line count mismatch: XML={xmlEntries.Count}, content={contents.Count}.</color>");
                }

                var finalEntries = new List<(int LineNum, string Rev, string Author, string Content)>();
                foreach (var e in xmlEntries)
                {
                    int idx = e.LineNum - 1;
                    string content = (idx >= 0 && idx < contents.Count) ? contents[idx] : "";
                    finalEntries.Add((e.LineNum, e.Rev, e.Author, content));
                }

                await BuildReportAsync(finalEntries, safePath, isServer, svnTarget, fullLocalPath, options, token).ConfigureAwait(false);
                return;
            }

            var result = new List<(int LineNum, string Rev, string Author, string Content)>();
            for (int i = 0; i < xmlEntries.Count; i++)
            {
                result.Add((xmlEntries[i].LineNum, xmlEntries[i].Rev, xmlEntries[i].Author, contents[i]));
            }

            await BuildReportAsync(result, safePath, isServer, svnTarget, fullLocalPath, options, token).ConfigureAwait(false);
        }

        // ===================================================================
        //  Helper: pojedynczy plik — status SVN (short form)
        // ===================================================================
        private async Task<string> GetFileWcStatusAsync(string safePath, CancellationToken token)
        {
            try
            {
                string statusRaw = await SvnRunner.RunAsync(
                    $"status --xml \"{SVNShared.EscapeSvnArg(safePath)}\"",
                    svnManager.WorkingDir, false, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(statusRaw))
                    return "?";

                int xmlStart = statusRaw.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
                if (xmlStart < 0) xmlStart = statusRaw.IndexOf("<status", StringComparison.OrdinalIgnoreCase);
                string xmlOnly = xmlStart > 0 ? statusRaw.Substring(xmlStart) : statusRaw;

                var statusDoc = XDocument.Parse(xmlOnly);
                var wcStatus = statusDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "wc-status");
                string item = wcStatus?.Attribute("item")?.Value ?? "?";

                return item switch
                {
                    "added" => "A",
                    "modified" => "M",
                    "deleted" => "D",
                    "unversioned" => "?",
                    "ignored" => "I",
                    "missing" => "!",
                    "conflicted" => "C",
                    "replaced" => "R",
                    "normal" => "",
                    _ => item
                };
            }
            catch (OperationCanceledException) { throw; }
            catch { return "?"; }
        }

        // ===================================================================
        //  Budowa raportu
        // ===================================================================
        private async Task BuildReportAsync(
            List<(int LineNum, string Rev, string Author, string Content)> entries,
            string safePath, bool isServer, string svnTarget, string fullLocalPath,
            BlameOptions options, CancellationToken token)
        {
            if (entries == null || entries.Count == 0)
            {
                PostLog("<color=#FFAA00>No annotatable lines found.</color>");
                return;
            }

            int maxRevLen = 3;
            int maxAuthorLen = 6;
            int maxLineNumLen = 4;

            foreach (var e in entries)
            {
                string revStr = e.Rev ?? "";
                string authorStr = e.Author ?? "";
                string lineNumStr = e.LineNum.ToString();
                if (revStr.Length > maxRevLen) maxRevLen = revStr.Length;
                if (authorStr.Length > maxAuthorLen) maxAuthorLen = authorStr.Length;
                if (lineNumStr.Length > maxLineNumLen) maxLineNumLen = lineNumStr.Length;
            }

            var richReport = new StringBuilder(32 * 1024);
            var plainReport = new StringBuilder(32 * 1024);

            string revLabel = string.IsNullOrWhiteSpace(options.Revision) ? "" : $" (r{EscapeRichText(options.Revision)})";

            richReport.AppendLine($"<size=120%><b>BLAME: {EscapeRichText(Path.GetFileName(safePath))}{revLabel}</b></size>");
            richReport.AppendLine($"<color={SVNColors.Dim}> LINE | {"REV".PadRight(maxRevLen)} | {"AUTHOR".PadRight(maxAuthorLen)} | CONTENT</color>");
            richReport.AppendLine(new string('-', maxLineNumLen + 3 + maxRevLen + 3 + maxAuthorLen + 3 + 20));

            plainReport.AppendLine("SVN BLAME REPORT");
            plainReport.AppendLine($"File: {safePath}");
            if (!string.IsNullOrWhiteSpace(options.Revision)) plainReport.AppendLine($"Revision: {options.Revision}");
            plainReport.AppendLine($"Generated: {DateTime.Now}");
            plainReport.AppendLine($"Lines: {entries.Count}");
            plainReport.AppendLine(new string('-', 60));
            plainReport.AppendLine($" LINE | {"REV".PadRight(maxRevLen)} | {"AUTHOR".PadRight(maxAuthorLen)} | CONTENT");
            plainReport.AppendLine(new string('-', 60));

            for (int i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var e = entries[i];

                string lineNumPadded = e.LineNum.ToString().PadLeft(maxLineNumLen);
                string revPadded = (e.Rev ?? "").PadRight(maxRevLen);
                string authorPadded = (e.Author ?? "").PadRight(maxAuthorLen);

                if (i < MaxPreviewLines)
                {
                    richReport.AppendLine(
                        $"<color={SVNColors.Dim}>{lineNumPadded}</color> | " +
                        $"<color=#FFD700>{EscapeRichText(revPadded)}</color> | " +
                        $"<color=#00E5FF>{EscapeRichText(authorPadded)}</color> | {EscapeRichText(e.Content ?? "")}");
                }

                plainReport.AppendLine($"{lineNumPadded} | {revPadded} | {authorPadded} | {e.Content ?? ""}");
            }

            string cacheFolder = Path.Combine(SVNPrefs.TemporaryCachePath, "SVN_Cache");
            Directory.CreateDirectory(cacheFolder);

            string reportPath = "";
            try
            {
                reportPath = Path.GetFullPath(Path.Combine(cacheFolder,
                    $"Blame_{Path.GetFileNameWithoutExtension(safePath)}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.txt"));
                await File.WriteAllTextAsync(reportPath, plainReport.ToString(), new UTF8Encoding(false), token).ConfigureAwait(false);
                SVNShared.CleanupOldFiles(cacheFolder, "Blame_*.txt", TimeSpan.FromHours(24));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                PostLog($"<color=#FFAA00>[Blame] Failed to save full report:</color> {EscapeRichText(ex.Message)}");
                reportPath = "";
            }

            if (entries.Count > MaxPreviewLines)
                richReport.AppendLine($"\n<color=#FFAA00>... Preview truncated ({entries.Count - MaxPreviewLines} more lines) – full report in file below.</color>");

            richReport.AppendLine($"\n<color={SVNColors.Dim}>Full report ({entries.Count} lines):</color> " +
                (reportPath.Length > 0 ? EscapeRichText(reportPath) : "<color=red>not saved</color>"));

            string finalReport = richReport.ToString();
            bool forceMain = options.ForceMainConsole;

            // === FIX (clear): raport ZASTĘPUJE panel (nie append). Panel był
            // już wyczyszczony na starcie RunBlameAsync — teraz SetBlameReport
            // wpisuje finalny raport jako jedyną treść.
            PostToMainThread(() => SetBlameReport(finalReport, forceMain));

            PostLog($"<color=green>Blame OK:</color> {entries.Count} lines" +
                (reportPath.Length > 0 ? $", full report: {EscapeRichText(reportPath)}" : ""));

            string blameToolPath = ResolveBlameToolPath();
            if (blameToolPath.Length > 0 && File.Exists(blameToolPath))
            {
                string targetForTool = isServer ? svnTarget : fullLocalPath;
                string tool = blameToolPath;
                string args = IsTortoiseProc(tool)
                    ? $"/command:blame /path:\"{targetForTool}\""
                    : $"\"{targetForTool}\"";

                PostToMainThread(() =>
                {
                    if (SVNShared.TryStartProcess(new ProcessStartInfo
                    {
                        FileName = tool,
                        Arguments = args,
                        UseShellExecute = true
                    },
                    ex => LogBoth($"<color=#FFAA00>[Blame] Failed to launch tool ({ex.GetType().Name}):</color> {EscapeRichText(ex.Message)}")))
                    {
                        LogBoth($"<color=green>[Blame] Opened in external tool:</color> {EscapeRichText(Path.GetFileName(safePath))}");
                    }
                });
                return;
            }

            if (options.OpenReport && reportPath.Length > 0)
            {
                string textEditorPath = ResolveTextEditorPath(options.UiTextEditorPath);

                if (textEditorPath.Length > 0 && File.Exists(textEditorPath))
                {
                    string editor = textEditorPath;
                    string fileToOpen = reportPath;
                    PostToMainThread(() =>
                    {
                        if (!SVNShared.TryStartProcess(new ProcessStartInfo
                        {
                            FileName = editor,
                            Arguments = $"\"{fileToOpen}\"",
                            UseShellExecute = true
                        },
                        ex => LogBoth($"<color=#FFAA00>[Blame] Failed to launch text editor:</color> {EscapeRichText(ex.Message)}")))
                        {
                            LogBoth("<color=#FFAA00>[Blame] Could not launch text editor – report saved at:</color> " + EscapeRichText(fileToOpen));
                        }
                    });
                }
                else if (options.AllowDefaultOpen)
                {
                    string fileToOpen = reportPath;
                    PostToMainThread(() => SVNShared.TryStartProcess(
                        new ProcessStartInfo { FileName = fileToOpen, UseShellExecute = true },
                        ex => LogBoth($"<color=#FFAA00>[Blame] Failed to open report:</color> {EscapeRichText(ex.Message)}")));
                }
            }
        }

        // ===================================================================
        //  EXTRACTION: content z XML (jeśli jest — nowsze SVN)
        // ===================================================================
        private static string ExtractBlameContentFromXml(XElement entry, XElement commitEl)
        {
            var lineEl = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "line");
            if (lineEl != null) return lineEl.Value ?? "";

            lineEl = entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "line");
            if (lineEl != null) return lineEl.Value ?? "";

            lineEl = entry.Elements().FirstOrDefault(e =>
                e.Name.LocalName.IndexOf("line", StringComparison.OrdinalIgnoreCase) >= 0);
            if (lineEl != null) return lineEl.Value ?? "";

            foreach (var child in entry.Elements())
            {
                if (child.Name.LocalName == "commit") continue;
                if (child == commitEl) continue;
                if (!string.IsNullOrEmpty(child.Value)) return child.Value;
            }

            var textNodes = entry.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToList();
            if (textNodes.Count > 0) return string.Concat(textNodes.Select(t => t.Value));

            return "";
        }

        // ===================================================================
        //  PARSER: tekstowy `svn blame` → List<string> content per linia
        // ===================================================================
        private static List<string> ParseTextBlameContents(string textRaw)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(textRaw)) return result;

            var regexPrimary = new Regex(@"^\s*\d+\s+\S+ (.*)$", RegexOptions.Compiled);
            var regexFallback = new Regex(@"^\s*\d+\s+\S+\s+(.*)$", RegexOptions.Compiled);

            foreach (var line in textRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
            {
                if (string.IsNullOrEmpty(line)) continue;

                if (!char.IsDigit(line.TrimStart().FirstOrDefault())) continue;

                var m = regexPrimary.Match(line);
                if (m.Success)
                {
                    result.Add(m.Groups[1].Value);
                    continue;
                }

                m = regexFallback.Match(line);
                if (m.Success)
                {
                    result.Add(m.Groups[1].Value);
                    continue;
                }

                result.Add("");
            }

            return result;
        }

        // ===================================================================
        //  DIAGNOSTYKA: 0 entries z blame
        // ===================================================================
        private async Task DiagnoseEmptyBlameAsync(string safePath, bool isServer, string fullLocalPath, CancellationToken token)
        {
            if (isServer)
            {
                PostLog($"<color=#FFAA00>No annotations returned for server path:</color> " +
                        $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                        $"<color={SVNColors.Dim}>The file may be empty or binary in that revision.</color>");
                return;
            }

            long fileSize = 0;
            bool exists = false;
            bool isBinary = false;

            try
            {
                if (File.Exists(fullLocalPath))
                {
                    exists = true;
                    var fi = new FileInfo(fullLocalPath);
                    fileSize = fi.Length;

                    if (fileSize > 0)
                    {
                        try
                        {
                            byte[] head = new byte[(int)Math.Min(8192, fileSize)];
                            using var fs = File.OpenRead(fullLocalPath);
                            int read = fs.Read(head, 0, head.Length);
                            for (int i = 0; i < read; i++)
                                if (head[i] == 0) { isBinary = true; break; }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (!exists)
            {
                PostLog($"<color=#FF4444>File not found on disk:</color> " +
                        $"<color=#FFD700>{EscapeRichText(fullLocalPath)}</color>");
                return;
            }

            if (fileSize == 0)
            {
                PostLog($"<color=yellow>File is empty (0 bytes) — nothing to blame:</color> " +
                        $"<color=#FFD700>{EscapeRichText(safePath)}</color>");
                return;
            }

            if (isBinary)
            {
                PostLog($"<color=#8B0000>File appears to be binary — blame not available:</color> " +
                        $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                        $"<color={SVNColors.Dim}>Size: {FormatFileSize(fileSize)}.</color>");
                return;
            }

            string svnStatus = await GetFileWcStatusAsync(safePath, token).ConfigureAwait(false);
            string sizeStr = FormatFileSize(fileSize);
            string dimOpen = $"<color={SVNColors.Dim}>";
            string dimClose = "</color>";

            switch (svnStatus)
            {
                case "A":
                    PostLog($"<color=#FFAA00>File is scheduled for addition but NOT YET COMMITTED:</color> " +
                            $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                            $"{dimOpen}Size: {sizeStr}. SVN has no revision history for it yet.{dimClose}\n" +
                            "<color=green>Tip:</color> Commit the file first, then run blame.");
                    break;

                case "?":
                    PostLog($"<color=#FFAA00>File is unversioned (not tracked by SVN):</color> " +
                            $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                            $"{dimOpen}Size: {sizeStr}. SVN cannot blame untracked files.{dimClose}\n" +
                            "<color=green>Tip:</color> Add + Commit it first, or verify the path.");
                    break;

                case "I":
                    PostLog($"<color=yellow>File is in svn:ignore list:</color> " +
                            $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                            $"{dimOpen}Size: {sizeStr}.{dimClose}");
                    break;

                case "M":
                    PostLog($"<color=#FFAA00>File has local modifications and no committed history yet:</color> " +
                            $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                            $"{dimOpen}Size: {sizeStr}.{dimClose}\n" +
                            "<color=green>Tip:</color> Commit to establish the first revision, then blame works.");
                    break;

                case "C":
                    PostLog($"<color=#FF4444>File is in a conflicted state — resolve first:</color> " +
                            $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                            $"{dimOpen}Size: {sizeStr}.{dimClose}");
                    break;

                case "":
                    PostLog($"<color=yellow>SVN returned no annotation entries for a tracked file:</color> " +
                            $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                            $"{dimOpen}Size: {sizeStr}. Content: might be entirely blank lines or unsupported encoding.{dimClose}");
                    break;

                default:
                    PostLog($"<color=yellow>No annotations for file (SVN status: {EscapeRichText(svnStatus)}):</color> " +
                            $"<color=#FFD700>{EscapeRichText(safePath)}</color>\n" +
                            $"{dimOpen}Size: {sizeStr}.{dimClose}");
                    break;
            }
        }

        private void SetBlameReport(string report, bool forceMainConsole)
        {
            if (forceMainConsole)
            {
                if (svnUI?.LogText != null) svnUI.LogText.text = report;
                else SVNLogBridge.LogLine(report);
                return;
            }

            if (svnUI?.BlameConsoleText != null) svnUI.BlameConsoleText.text = report;
            else if (svnUI?.BlameDisplayArea != null) svnUI.BlameDisplayArea.text = report;
            else if (svnUI?.LogText != null) svnUI.LogText.text = report;
            else SVNLogBridge.LogLine(report);
        }

        private string ResolveBlameToolPath()
        {
            string path = svnManager?.BlameToolPath;
            if (string.IsNullOrWhiteSpace(path))
                path = SVNPrefs.GetString(SVNManager.KEY_BLAME_TOOL, "");
            path = path?.Trim().Trim('"');
            return path ?? "";
        }

        private string ResolveTextEditorPath(string uiSnapshot)
        {
            string path = SVNPrefs.GetString(SVNManager.KEY_TEXTEDITOR_TOOL, "");

            if (string.IsNullOrWhiteSpace(path))
            {
                path = svnManager?.MergeToolPath;
                if (IsTortoiseTool(path)) path = "";
            }

            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(uiSnapshot))
            {
                path = uiSnapshot;
                if (IsTortoiseTool(path)) path = "";
            }

            path = path?.Trim().Trim('"');
            return path ?? "";
        }

        private static bool IsTortoiseTool(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            return p.IndexOf("TortoiseMerge", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("TortoiseProc", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTortoiseProc(string p)
            => !string.IsNullOrEmpty(p) && p.IndexOf("TortoiseProc", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} B";
        }

        private static string EscapeRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}