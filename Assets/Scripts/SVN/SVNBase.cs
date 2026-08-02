using System;
using System.Text.RegularExpressions;
using System.Threading;
using TMPro;

namespace SVN.Core
{
    public abstract class SVNBase
    {
        protected SVNManager svnManager;
        protected SVNUI svnUI;

        private int _isProcessing = 0; // 0 = false, 1 = true

        public bool IsProcessing
        {
            get => Interlocked.CompareExchange(ref _isProcessing, 0, 0) == 1;
            set => Interlocked.Exchange(ref _isProcessing, value ? 1 : 0);
        }

        protected bool TryStart()
        {
            return Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0;
        }

        protected void End()
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }

        protected SVNBase(SVNUI ui, SVNManager manager)
        {
            if (ui == null)
                throw new ArgumentNullException(nameof(ui), $"{GetType().Name}: UI is NULL!");
            if (manager == null)
                throw new ArgumentNullException(nameof(manager), $"{GetType().Name}: Manager is NULL!");

            svnUI = ui;
            svnManager = manager;
        }

        protected string StripBanner(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string pattern = @"\*+\s*WARNING![\s\S]*?@{5,}";
            string cleaned = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);

            string[] lines = cleaned.Split(
                new[] { "\n", "\r" },
                StringSplitOptions.RemoveEmptyEntries);

            var finalLines = new System.Collections.Generic.List<string>(lines.Length);

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("*****") || trimmed.Contains("@@@@@"))
                    continue;

                finalLines.Add(line);
            }

            return string.Join("\n", finalLines);
        }

        protected virtual TMP_Text GetConsole()
        {
            return null;
        }

        protected void Append(string msg, string color)
        {
            var console = GetConsole();
            if (console != null)
                console.text += $"<color={color}>{msg}</color>\n";
        }

        protected void LogInfo(string msg)
            => Append(msg, "#0400ff");

        protected void LogSuccess(string msg)
            => Append(msg, "#01ff09");

        protected void LogWarning(string msg)
            => Append(msg, "#FFEB3B");

        protected void LogErrorLocal(string msg)
        {
            Append(msg, "#610402");
            SVNLogBridge.LogError(msg);
        }
    }
}