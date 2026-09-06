using UnityEngine;
using TMPro;
using System.Text;
using System.Text.RegularExpressions;
using System;
using SVN.Core;

public class ClipboardPrefab : MonoBehaviour
{
    [Header("References")]
    public TextMeshProUGUI summaryText;
    public Transform scrollContent;

    // === FIX K1: whitelist tagów TMP — goły '<.*?>' zjadał legalne znaki < >
    // w treści (np. 'a < b' w komunikatach commitów).
    private static readonly Regex RichTextTagRegex = new Regex(
        @"</?(color|size|b|i|u|s|mark|noparse|mspace|sprite|sub|sup|br|pos|font|style|a|link|width|page|indent|line-height)(\s[^>]*)?/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // UWAGA: GUIUtility.systemCopyBuffer wymaga main thread — klasa wołana
    // wyłącznie z przycisków UI (gwarancja main). Nie wołać z modułów async.
    public void Button_CopyEverythingToClipboard()
    {
        if (scrollContent == null)
        {
            SVNLogBridge.LogError("Clipboard: 'Scroll Content' is not assigned!");
            return;
        }

        StringBuilder sb = new StringBuilder();

        if (summaryText != null && !string.IsNullOrEmpty(summaryText.text))
        {
            string cleanSummary = RichTextTagRegex.Replace(summaryText.text, string.Empty).Trim();
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
                    string cleanLine = RichTextTagRegex.Replace(tmp.text, string.Empty).Trim();
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

                string cleanLine = RichTextTagRegex.Replace(tmp.text, string.Empty).Trim();
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
            SVNLogBridge.LogLine($"<color=green><b>SUCCESS!</b></color> Copied {fileCount} files to clipboard.");
        }
    }
}