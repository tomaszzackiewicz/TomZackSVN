using UnityEngine;
using TMPro;
using SVN.Core;

public class Clipboard : MonoBehaviour
{
    public TextMeshProUGUI consoleTextElement;

    public void Button_CopyCleanTextToClipboard()
    {
        if (consoleTextElement != null)
        {
            string cleanText = consoleTextElement.GetParsedText();

            GUIUtility.systemCopyBuffer = cleanText;

            ShowCopyFeedback();
        }
    }

    private void ShowCopyFeedback()
    {
        SVNLogBridge.LogLine("Text copied!");
    }
}