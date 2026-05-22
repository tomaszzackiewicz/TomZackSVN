using UnityEngine;
using TMPro;

public class VersionTMPReplacer : MonoBehaviour
{
    [SerializeField] private TMP_Text targetText;

    private const string PLACEHOLDER = "{VERSION}";

    private void Awake()
    {
        if (targetText == null)
            targetText = GetComponent<TMP_Text>();

        string version = GetVersion();

        targetText.text = targetText.text.Replace(PLACEHOLDER, version);
    }

    private string GetVersion()
    {
#if UNITY_EDITOR
        return UnityEditor.PlayerSettings.bundleVersion;
#else
        return Application.version;
#endif
    }
}