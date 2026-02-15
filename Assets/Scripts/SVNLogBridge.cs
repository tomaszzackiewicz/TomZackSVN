using UnityEngine;
using System.Text.RegularExpressions;
using TMPro;
using System;
using UnityEngine.UI;

namespace SVN.Core
{
    public static class SVNLogBridge
    {
        private static readonly Regex RichTextRegex = new Regex(@"<[^>]*>");

        public static void LogLine(string message, bool append = true)
        {
            if (SVNUI.Instance == null || SVNUI.Instance.LogText == null) return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string fullMessage = $"[{timestamp}] {message}";

            // Update UI
            if (append)
                SVNUI.Instance.LogText.text += fullMessage + "\n";
            else
                SVNUI.Instance.LogText.text = fullMessage + "\n";

            // Direct file log (skipping Unity Console if you want)
            string cleanMessage = StripRichText(fullMessage);
            SVNLogger.LogToFile(cleanMessage, "INFO");

            if (append && SVNUI.Instance.LogScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                SVNUI.Instance.LogScrollRect.verticalNormalizedPosition = 0f;
            }
        }

        public static void LogError(string message)
        {
            LogLine($"<color=red>[ERROR] {message}</color>", true);
            SVNLogger.LogToFile(StripRichText(message), "ERROR");
        }

        public static void UpdateUIField(TextMeshProUGUI uiField, string content, string logLabel = "UI", bool append = false)
        {
            if (uiField == null) return;

            if (append)
                uiField.text += content + "\n";
            else
                uiField.text = content;

            string cleanContent = StripRichText(content);
            if (!string.IsNullOrEmpty(cleanContent))
            {
                SVNLogger.LogToFile(cleanContent, logLabel);
            }
        }

        private static string StripRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return RichTextRegex.Replace(input, string.Empty);
        }
    }
}