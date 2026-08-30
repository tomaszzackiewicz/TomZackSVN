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

        // === FIX K1: guard po fallbacku — brak TMP_Text (pole puste + komponent
        // nie na tym obiekcie) = czytelny błąd zamiast NRE w Awake.
        if (targetText == null)
        {
            Debug.LogError("[VersionTMPReplacer] No TMP_Text assigned and none found on this GameObject.", this);
            return;
        }

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