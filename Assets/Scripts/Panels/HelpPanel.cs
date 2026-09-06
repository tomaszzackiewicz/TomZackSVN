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
    private string[] _richLines;    // === FIX: parsowanie RAZ (per keystroke tylko filtrowanie)
    private string[] _plainLines;

    private void Start()
    {
        if (helpText == null)
        {
            Debug.LogError("HelpPanel: Przypisz helpText w inspektorze.", this);
            return;   // bez helpText klasa nie ma sensu
        }

        fullRichText = helpText.text;
        fullPlainText = StripRichTextTags(fullRichText);

        // === FIX K2: normalizacja \r\n przy podziale — linie bez wiszących '\r'.
        _richLines = fullRichText.Replace("\r\n", "\n").Split('\n');
        _plainLines = fullPlainText.Replace("\r\n", "\n").Split('\n');

        // === FIX K3: brak searchInput = brak filtrowania, ale panel działa (pokazuje help).
        if (searchInput != null)
            searchInput.onValueChanged.AddListener(OnSearchChanged);
    }

    private void OnDestroy()
    {
        if (searchInput != null)
            searchInput.onValueChanged.RemoveListener(OnSearchChanged);
    }

    private void OnSearchChanged(string query)
    {
        if (helpText == null || _plainLines == null) return;

        if (string.IsNullOrWhiteSpace(query))
        {
            helpText.text = fullRichText;
            ScrollToTop();
            return;
        }

        var resultLines = new List<string>();
        string escapedQuery = Regex.Escape(query);

        for (int i = 0; i < _plainLines.Length; i++)
        {
            if (i >= _richLines.Length) break;

            if (_plainLines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // UWAGA (K1, znane ograniczenie): trafienie przecinające tagi
                // (np. "Commit Selected" przy "Commit <b>Selected</b>") pokaże linię
                // bez podświetlenia — HighlightText działa na rich, tag przerywa match.
                resultLines.Add(HighlightText(_richLines[i], escapedQuery));
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