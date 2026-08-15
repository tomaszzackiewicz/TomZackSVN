using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class HelpPanel : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TextMeshProUGUI helpText;
    [SerializeField] private TMP_InputField searchInput;
    [SerializeField] private ScrollRect helpScrollRect;

    private string fullRichText;
    private string fullPlainText;

    private void Start()
    {
        if (helpText == null || searchInput == null)
        {
            Debug.LogError("HelpPanel: Przypisz helpText i searchInput w inspektorze.", this);
            return;
        }

        fullRichText = helpText.text;
        fullPlainText = StripRichTextTags(fullRichText);

        searchInput.onValueChanged.AddListener(OnSearchChanged);
    }

    private void OnDestroy()
    {
        if (searchInput != null)
            searchInput.onValueChanged.RemoveListener(OnSearchChanged);
    }

    private void OnSearchChanged(string query)
    {
        if (helpText == null) return;

        if (string.IsNullOrWhiteSpace(query))
        {
            helpText.text = fullRichText;
            ScrollToTop();
            return;
        }

        string[] richLines = fullRichText.Split(new[] { '\n' }, StringSplitOptions.None);
        string[] plainLines = fullPlainText.Split(new[] { '\n' }, StringSplitOptions.None);

        var resultLines = new List<string>();

        string escapedQuery = Regex.Escape(query);

        for (int i = 0; i < plainLines.Length; i++)
        {
            if (i >= richLines.Length) break;

            if (plainLines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string highlightedLine = HighlightText(richLines[i], escapedQuery);
                resultLines.Add(highlightedLine);
            }
        }

        if (resultLines.Count == 0)
        {
            helpText.text = "<color=#FFAA00>No results found.</color>";
        }
        else
        {
            helpText.text = string.Join("\n", resultLines);
        }

        ScrollToTop();
    }

    private string HighlightText(string richTextLine, string escapedQuery)
    {
        if (string.IsNullOrEmpty(escapedQuery)) return richTextLine;

        return Regex.Replace(richTextLine, $"(<[^>]+>)|({escapedQuery})", match =>
        {
            if (match.Groups[1].Success)
            {
                return match.Value;
            }
            else
            {
                return $"<mark=#FFFF00AA>{match.Value}</mark>";
            }
        }, RegexOptions.IgnoreCase);
    }

    private void ScrollToTop()
    {
        if (helpScrollRect != null)
        {
            helpScrollRect.verticalNormalizedPosition = 1f;
        }
        else if (helpText != null)
        {
            helpText.rectTransform.anchoredPosition = Vector2.zero;
        }
    }

    private string StripRichTextTags(string richText)
    {
        if (string.IsNullOrEmpty(richText)) return richText;

        return Regex.Replace(richText, "<[^>]*>", string.Empty);
    }
}