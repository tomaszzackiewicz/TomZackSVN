using SVN.Core;
using TMPro;
using UnityEngine;

public class MergeFileItem : MonoBehaviour
{
    [SerializeField]
    private TMP_Text filePathText;

    [SerializeField]
    private TMP_Text stateText;

    public void Setup(SVNMerge.MergeFileInfo info)
    {
        filePathText.text = info.Path;
        stateText.text = info.State.ToString();

        Color stateColor = info.State switch
        {
            'A' => new Color(0.33f, 1f, 0.33f),
            'D' => new Color(1f, 0.33f, 0.33f),
            'M' => new Color(1f, 1f, 0.33f),
            'C' => new Color(1f, 0f, 1f),
            _ => Color.white
        };
        stateText.color = stateColor;
    }
}