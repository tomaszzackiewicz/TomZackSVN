using UnityEngine;
using System.Text.RegularExpressions;
using TMPro;
using System;

namespace SVN.Core
{
    public static class SVNLogBridge
    {
        private static readonly Regex RichTextRegex = new Regex(@"<[^>]*>");

        public static void LogLine(string message, bool append = true, string level = "INFO")
        {
            if (SVNUI.Instance == null || SVNUI.Instance.LogText == null) return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss");

            string uiMessage = $"[{timestamp}] {message}";

            if (append)
                SVNUI.Instance.LogText.text += uiMessage + "\n";
            else
                SVNUI.Instance.LogText.text = uiMessage + "\n";
            string cleanMessage = StripRichText(uiMessage);
            SVNLogger.LogToFile(cleanMessage, level);

            if (append && SVNUI.Instance.LogScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                SVNUI.Instance.LogScrollRect.verticalNormalizedPosition = 0f;
            }
        }

        public static void LogError(string message, bool append = true)
        {
            string errorMessage = $"<color=#FF8800><b>[ERROR]</b> {message}</color>";

            LogLine(errorMessage, append, "ERROR");
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

        public static void ShowNotification(string message)
        {
            if (SVNUI.Instance == null) return;

            SVNUI.Instance.ShowNotificationWithTimer(message, 5f);

            LogLine($"<color=blue>[NOTIFY]</color> {message}");
        }
        public static void LogTooltip(string message)
        {
            if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;

            SVNUI.Instance.TooltipText.text = $"<color=#CCCCCC>{message}</color>";
        }

        public static void ClearTooltip()
        {
            if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;
            SVNUI.Instance.TooltipText.text = "";
        }
    }
}