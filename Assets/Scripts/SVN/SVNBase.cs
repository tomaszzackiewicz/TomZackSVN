using System;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace SVN.Core
{
    public abstract class SVNBase
    {
        protected SVNManager svnManager;
        protected SVNUI svnUI;

        private int _isProcessingFlag = 0;

        // =====================================================
        // 🔒 GLOBAL PROCESS LOCK (per module instance)
        // =====================================================
        protected bool TryStart()
            => Interlocked.Exchange(ref _isProcessingFlag, 1) == 0;

        protected void End()
            => Interlocked.Exchange(ref _isProcessingFlag, 0);

        protected bool IsProcessing
        {
            get => svnManager.IsProcessing;
            set => svnManager.IsProcessing = value;
        }

        // =====================================================
        // 🧠 CONSTRUCTOR
        // =====================================================
        protected SVNBase(SVNUI ui, SVNManager manager)
        {
            svnUI = ui;
            svnManager = manager;

            if (ui == null || manager == null)
            {
                SVNLogBridge.LogError($"{GetType().Name}: UI or Manager is NULL!");
            }
        }

        // =====================================================
        // 🧹 UTIL
        // =====================================================
        protected string StripBanner(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string pattern = @"\*+\s*WARNING![\s\S]*?@{5,}";
            string cleaned = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase);

            string[] lines = cleaned.Split(
                new[] { "\n", "\r" },
                StringSplitOptions.RemoveEmptyEntries);

            var finalLines = new System.Collections.Generic.List<string>();

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("*****") || trimmed.Contains("@@@@@"))
                    continue;

                finalLines.Add(line);
            }

            return string.Join("\n", finalLines);
        }

        // =====================================================
        // 🟦 LOG SYSTEM (MODULAR UI)
        // =====================================================

        // 🔥 KLUCZ: każdy moduł wskazuje własną konsolę
        protected virtual TMPro.TMP_Text GetConsole()
        {
            return null; // base = brak UI (muszą override)
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