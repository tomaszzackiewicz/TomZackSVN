using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SVN.Core
{
    public class SVNBase
    {
        protected SVNManager svnManager;
        protected SVNUI svnUI;

        protected bool IsProcessing
        {
            get => svnManager.IsProcessing;
            set => svnManager.IsProcessing = value;
        }

        public SVNBase(SVNUI ui, SVNManager manager)
        {
            this.svnUI = ui;
            this.svnManager = manager;

            if (ui == null || manager == null)
            {
                UnityEngine.Debug.LogError($"{this.GetType().Name}: UI or Manager is NULL!");
            }
        }

        protected string StripBanner(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string pattern = @"\*+\s*WARNING![\s\S]*?@{5,}";

            string cleaned = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);

            string[] lines = cleaned.Split(new[] { "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            var finalLines = new System.Collections.Generic.List<string>();

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("*****") || trimmed.Contains("@@@@@")) continue;
                finalLines.Add(line);
            }

            return string.Join("\n", finalLines);
        }
    }
}
