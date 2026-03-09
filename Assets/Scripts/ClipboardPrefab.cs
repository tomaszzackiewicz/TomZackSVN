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
            Debug.LogError("Clipboard: 'Scroll Content' is not assigned!");
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

        SVNFileItem[] fileItems = scrollContent.GetComponentsInChildren<SVNFileItem>(true);
        int fileCount = 0;

        if (fileItems.Length > 0)
        {
            foreach (var item in fileItems)
            {
                TextMeshProUGUI tmp = item.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    string cleanLine = Regex.Replace(tmp.text, "<.*?>", string.Empty).Trim();
                    if (!string.IsNullOrEmpty(cleanLine))
                    {
                        sb.AppendLine(cleanLine);
                        fileCount++;
                    }
                }
            }
        }
        else
        {
            TextMeshProUGUI[] allTexts = scrollContent.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var tmp in allTexts)
            {
                if (tmp == summaryText) continue;

                string cleanLine = Regex.Replace(tmp.text, "<.*?>", string.Empty).Trim();
                if (!string.IsNullOrEmpty(cleanLine))
                {
                    sb.AppendLine(cleanLine);
                    fileCount++;
                }
            }
        }

        string finalResult = sb.ToString().Trim();

        if (!string.IsNullOrEmpty(finalResult))
        {
            GUIUtility.systemCopyBuffer = finalResult;
            Debug.Log($"<color=green><b>SUCCESS!</b></color> Copied {fileCount} files to clipboard.");
        }
    }
}