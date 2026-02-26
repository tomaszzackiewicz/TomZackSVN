using UnityEngine;
using TMPro;
using System.Text;
using System.Text.RegularExpressions;
using System;

public class ClipboardPrefab : MonoBehaviour
{
    [Header("References")]
    public TextMeshProUGUI summaryText;
    public Transform scrollContent;

    public void Button_CopyEverythingToClipboard()
    {
        if (scrollContent == null)
        {
            Debug.LogError("Clipboard: 'Scroll Content' is not assigned in the Inspector!");
            return;
        }

        StringBuilder sb = new StringBuilder();

        if (summaryText != null && !string.IsNullOrEmpty(summaryText.text))
        {
            string cleanSummary = Regex.Replace(summaryText.text, "<.*?>", string.Empty).Trim();
            sb.AppendLine("=== SUMMARY ===");
            sb.AppendLine(cleanSummary);
            sb.AppendLine("---------------------------");
        }

        TextMeshProUGUI[] fileTexts = scrollContent.GetComponentsInChildren<TextMeshProUGUI>(true);
        int fileCount = 0;

        foreach (var tmp in fileTexts)
        {
            if (tmp == summaryText) continue;

            string cleanPath = Regex.Replace(tmp.text, "<.*?>", string.Empty).Trim();
            if (!string.IsNullOrEmpty(cleanPath))
            {
                sb.AppendLine(cleanPath);
                fileCount++;
            }
        }

        string finalResult = sb.ToString().Trim();

        if (!string.IsNullOrEmpty(finalResult))
        {
            try
            {
                GUIUtility.systemCopyBuffer = finalResult;

                TextEditor te = new TextEditor();
                te.text = finalResult;
                te.SelectAll();
                te.Copy();

                Debug.Log($"<color=green><b>SUCCESS!</b></color> Copied {fileCount} lines.\n<b>Clipboard content:</b>\n{finalResult}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Clipboard critical error: {e.Message}");
            }
        }
        else
        {
            Debug.LogWarning("Clipboard: No text found to copy.");
        }
    }
}