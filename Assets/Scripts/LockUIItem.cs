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
    //public Image statusColorBar;

    public void Setup(string path, string owner, string date, string comment, bool isMe, Action onStealAction)
    {
        fileText.text = $"<b>Path:</b> {path}";
        infoText.text = $"<b>Owner:</b> {(isMe ? "<color=green>YOU</color>" : owner)} | <b>Date:</b> {date}";
        commentText.text = $"<i>{comment}</i>";

        stealButton.gameObject.SetActive(!isMe);

        stealButton.onClick.RemoveAllListeners();
        stealButton.onClick.AddListener(() => onStealAction?.Invoke());

        // if (statusColorBar != null)
        //     statusColorBar.color = isMe ? Color.green : Color.red;
    }
}