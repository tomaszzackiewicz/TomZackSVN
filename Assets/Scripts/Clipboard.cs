using UnityEngine;
using TMPro;

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
        Debug.Log("Text copied!");
    }
}