using UnityEngine;
using System.Text.RegularExpressions;
using TMPro;
using System;
using System.Threading.Tasks;

namespace SVN.Core
{
    public static class SVNLogBridge
    {
        private static readonly Regex RichTextRegex = new Regex(@"<[^>]*>");
        private const float DefaultNotificationDuration = 5f;

        public static void LogLine(string message, bool append = true, string level = "INFO")
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string uiMessage = $"[{timestamp}] {message}";
            string cleanMessage = StripRichText(message);

            _ = Task.Run(() => SVNLogger.LogToFile(cleanMessage, level));

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.LogText == null) return;

                if (append)
                    SVNUI.Instance.LogText.text += uiMessage + "\n";
                else
                    SVNUI.Instance.LogText.text = uiMessage + "\n";

                if (append && SVNUI.Instance.LogScrollRect != null)
                {
                    Canvas.ForceUpdateCanvases();
                    SVNUI.Instance.LogScrollRect.verticalNormalizedPosition = 0f;
                }
            });
        }

        public static void LogError(string message, bool append = true)
        {
            string errorMessage = $"<color=#FF8800><b>[ERROR]</b> {message}</color>";
            LogLine(errorMessage, append, "ERROR");
        }

        public static void UpdateUIField(TextMeshProUGUI uiField, string content, string logLabel = "UI", bool append = false)
        {
            if (uiField == null) return;

            string cleanContent = StripRichText(content);
            if (!string.IsNullOrEmpty(cleanContent))
            {
                _ = Task.Run(() => SVNLogger.LogToFile(cleanContent, logLabel));
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (uiField == null) return;

                if (append)
                    uiField.text += content + "\n";
                else
                    uiField.text = content;
            });
        }

        private static string StripRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return RichTextRegex.Replace(input, string.Empty);
        }

        public static void ShowNotification(string message)
        {
            LogLine($"<color=blue>[NOTIFY]</color> {message}");

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null) return;
                SVNUI.Instance.ShowNotificationWithTimer(message, DefaultNotificationDuration);
            });
        }

        public static void LogTooltip(string message)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;
                SVNUI.Instance.TooltipText.text = $"<color=#CCCCCC>{message}</color>";
            });
        }

        public static void ClearTooltip()
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.TooltipText == null) return;
                SVNUI.Instance.TooltipText.text = "";
            });
        }

        public static void LogCheckoutConsole(string message)
        {
            string cleanMessage = StripRichText(message);
            if (!string.IsNullOrEmpty(cleanMessage))
            {
                _ = Task.Run(() => SVNLogger.LogToFile(cleanMessage, "CHECKOUT"));
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (SVNUI.Instance == null || SVNUI.Instance.CheckoutedFilesText == null) return;
                SVNUI.Instance.CheckoutedFilesText.text = message;
            });
        }

        public static void LogException(Exception ex, bool append = true)
        {
            if (ex == null) return;

            string message = $"<color=#FF8800><b>[EXCEPTION]</b> {ex.Message}</color>\n<color=#AAAAAA>{ex.StackTrace}</color>";
            LogLine(message, append, "ERROR");
        }
    }
}