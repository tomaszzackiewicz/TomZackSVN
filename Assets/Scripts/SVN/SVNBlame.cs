using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using UnityEngine;
using SFB;

namespace SVN.Core
{
    public class SVNBlame : SVNBase
    {
        public SVNBlame(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            var console = svnUI.BlameConsoleText != null ? svnUI.BlameConsoleText : svnUI.CommitConsoleContent;
            SVNLogBridge.UpdateUIField(console, msg, "BLAME", true);
        }

        public async void ExecuteBlame()
        {
            if (IsProcessing) return;

            string relativePath = svnUI.BlameTargetFileInput?.text.Trim();
            if (string.IsNullOrEmpty(relativePath))
            {
                LogBoth("<color=yellow>Please select a file path first.</color>");
                return;
            }

            IsProcessing = true;
            try
            {
                await ShowBlame(relativePath);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        public async Task ShowBlame(string relativePath)
        {
            if (string.IsNullOrEmpty(svnManager.WorkingDir))
            {
                LogBoth("<color=red>Error:</color> Working Directory not set!");
                return;
            }

            LogBoth($"Fetching Annotations for: <color=green>{relativePath}</color>...");

            try
            {
                // Uruchomienie komendy
                string output = await SvnRunner.RunAsync($"blame \"{relativePath}\"", svnManager.WorkingDir, false);

                if (string.IsNullOrWhiteSpace(output))
                {
                    LogBoth("<color=white>No data received. File might be unversioned or empty.</color>");
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"<size=120%><b>BLAME: {Path.GetFileName(relativePath)}</b></size>");
                sb.AppendLine("<color=#444444>LINE | REV   | AUTHOR     | CONTENT</color>");
                sb.AppendLine("--------------------------------------------------");

                using (var reader = new StringReader(output))
                {
                    string line;
                    int lineCount = 1;

                    while ((line = reader.ReadLine()) != null)
                    {
                        // Bardziej elastyczny Regex: 
                        // ^\s*(\d+|-) -> Rewizja (liczba lub myślnik)
                        // \s+([^\s]+)  -> Autor (wszystko do pierwszej spacji)
                        // \s(.*)$      -> Treść (reszta linii)
                        var match = Regex.Match(line, @"^\s*(\d+|-)\s+([^\s]+)\s?(.*)$");

                        if (match.Success)
                        {
                            string rev = match.Groups[1].Value.Trim();
                            string author = match.Groups[2].Value.Trim();
                            string content = match.Groups[3].Value;

                            // Formaty kolorowania
                            string lineNumStr = $"<color=#666666>{lineCount:D3}</color> | ";
                            string revStr = $"<color=#FFD700>{rev.PadRight(5)}</color> | ";
                            string authorStr = $"<color=#00E5FF>{author.PadRight(10)}</color> | ";

                            sb.AppendLine($"{lineNumStr}{revStr}{authorStr}{content}");
                        }
                        else
                        {
                            // Jeśli linia nie pasuje do wzorca (np. pusta), dodaj ją surową
                            sb.AppendLine($"<color=#666666>{lineCount:D3}</color> | {line}");
                        }
                        lineCount++;
                    }
                }

                // Finalna aktualizacja UI
                if (svnUI.BlameDisplayArea != null)
                {
                    SVNLogBridge.UpdateUIField(svnUI.BlameDisplayArea, sb.ToString(), "BLAME_RESULT", append: false);
                    LogBoth("<color=green>Blame completed successfully.</color>");
                }
                else
                {
                    Debug.LogWarning("BlameDisplayArea is null! Outputting to console.");
                    LogBoth(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                // To nam powie dokładnie, co się wywaliło
                LogBoth($"<color=red>Parsing Error:</color> {ex.Message}");
                Debug.LogException(ex);
            }
        }
    }
}