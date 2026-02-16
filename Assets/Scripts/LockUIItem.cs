using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System;

public class LockUIItem : MonoBehaviour
{
    public TextMeshProUGUI fileText;
    public TextMeshProUGUI infoText;
    public TextMeshProUGUI commentText;
    public Button stealButton;

    public void Setup(string path, string owner, string date, string comment, bool isMe, Action onStealAction)
    {
        fileText.text = $"<b>Path:</b> {path}";

        string formattedDate = date;
        if (DateTime.TryParse(date, out DateTime parsedDate))
        {
            formattedDate = parsedDate.ToString("yyyy-MM-dd HH:mm");
        }

        infoText.text = $"<b>Owner:</b> {(isMe ? "<color=green>YOU</color>" : owner)}\n" +
                        $"<size=90%><color=#E6E6E6>Date: {formattedDate}</color></size>";

        commentText.text = string.IsNullOrEmpty(comment) ? "" : $"<i>\"{comment}\"</i>";

        stealButton.gameObject.SetActive(!isMe);

        stealButton.onClick.RemoveAllListeners();
        stealButton.onClick.AddListener(() => onStealAction?.Invoke());
    }
}