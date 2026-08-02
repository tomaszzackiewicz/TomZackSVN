using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ViewTabsController : MonoBehaviour
{
    public GameObject localChangesPanel;
    public GameObject repoBrowserPanel;

    public Button tabLocalBtn;
    public Button tabRepoBtn;

    private void Start()
    {
        Button_ShowLocalChanges();
    }

    public void Button_ShowLocalChanges()
    {
        if (localChangesPanel != null) localChangesPanel.SetActive(true);
        if (repoBrowserPanel != null) repoBrowserPanel.SetActive(false);

        SetButtonTextColor(tabLocalBtn, Color.white);
        SetButtonTextColor(tabRepoBtn, Color.gray);
    }

    public void Button_ShowRepoBrowser()
    {
        if (localChangesPanel != null) localChangesPanel.SetActive(false);
        if (repoBrowserPanel != null) repoBrowserPanel.SetActive(true);

        SetButtonTextColor(tabLocalBtn, Color.gray);
        SetButtonTextColor(tabRepoBtn, Color.white);
    }

    private void SetButtonTextColor(Button btn, Color color)
    {
        if (btn == null) return;

        // Najpierw sprawdzamy TextMeshPro, a jeśli go nie ma, używamy standardowego Text
        var tmpText = btn.GetComponentInChildren<TextMeshProUGUI>();
        if (tmpText != null)
        {
            tmpText.color = color;
            return;
        }

        var legacyText = btn.GetComponentInChildren<Text>();
        if (legacyText != null)
        {
            legacyText.color = color;
        }
    }
}