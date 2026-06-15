using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ShelfItemUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI nameText;
    [SerializeField] private TextMeshProUGUI dateText;
    [SerializeField] private TextMeshProUGUI filesLabel;
    [SerializeField] private TextMeshProUGUI sizeLabel;
    [SerializeField] private Button restoreButton;
    [SerializeField] private Button deleteButton;

    public TextMeshProUGUI NameText => nameText;
    public TextMeshProUGUI DateText => dateText;
    public TextMeshProUGUI FilesLabel => filesLabel;
    public TextMeshProUGUI SizeLabel => sizeLabel;
    public Button RestoreButton => restoreButton;
    public Button DeleteButton => deleteButton;
}