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
    public TextMeshProUGUI stealButtonText;
    public Button breakButton;
    public TextMeshProUGUI breakButtonText;

    private string _path;
    private Action _stealAction;
    private Action _breakAction;
    private TMP_Text _panelConsole;

    private bool _awaitingStealConfirmation = false;
    private float _stealTimer = 0f;
    private Color _originalStealColor;

    private bool _awaitingBreakConfirmation = false;
    private float _breakTimer = 0f;
    private Color _originalBreakColor;

    private const float ConfirmationTimeout = 3f;

    public void Setup(string path, string owner, string date, string comment, bool isMe, Action onStealAction, Action onBreakAction = null, TMP_Text panelConsole = null)
    {
        _path = path;
        _stealAction = onStealAction;
        _breakAction = onBreakAction;
        _panelConsole = panelConsole;

        ResetStealState();
        ResetBreakState();

        if (fileText == null || infoText == null || stealButton == null)
        {
            Debug.LogError("[LockUIItem] Missing UI references in Inspector!", this);
            return;
        }

        string safePath = SanitizeRichText(path);
        string safeOwner = SanitizeRichText(owner);
        string safeComment = SanitizeRichText(comment);

        fileText.text = $"<b>Path:</b> {safePath}";

        string formattedDate = date;
        if (DateTime.TryParse(date, out DateTime parsedDate))
            formattedDate = parsedDate.ToString("yyyy-MM-dd HH:mm");

        infoText.text = $"<b>Owner:</b> {(isMe ? "<color=green>YOU</color>" : safeOwner)}\n" +
                        $"<size=90%><color=#E6E6E6>Date: {formattedDate}</color></size>";

        if (commentText != null)
            commentText.text = string.IsNullOrEmpty(safeComment) ? "" : $"<i>\"{safeComment}\"</i>";

        stealButton.gameObject.SetActive(!isMe);
        if (breakButton != null) breakButton.gameObject.SetActive(!isMe);

        stealButton.onClick.RemoveAllListeners();
        stealButton.onClick.AddListener(OnStealClick);

        if (breakButton != null)
        {
            breakButton.onClick.RemoveAllListeners();
            breakButton.onClick.AddListener(OnBreakClick);

            // === FIX K2: _originalBreakColor inicjalizowany ZAWSZE gdy breakButton
            // istnieje — wcześniej przy Setup z isMe=true (przycisk ukrywany) pole
            // zostawało default(0,0,0,0) i recykling itemu na isMe=false ustawiał
            // przezroczysty normalColor.
            var colors = breakButton.colors;
            _originalBreakColor = colors.normalColor;
            _originalBreakColor.a = 1f;
            colors.normalColor = _originalBreakColor;
            breakButton.colors = colors;
        }

        var stealColors = stealButton.colors;
        _originalStealColor = stealColors.normalColor;
        _originalStealColor.a = 1f;
        stealColors.normalColor = _originalStealColor;
        stealButton.colors = stealColors;

        // === FIX K1: Update domyślnie wyłączone — włącza się tylko na czas
        // aktywnego potwierdzenia. Przy 50 lockach: 0-2 Update/frame zamiast 50.
        enabled = false;
    }

    // === FIX K1: całe ciało Update wykonywane tylko gdy component enabled
    // (ustawiane w OnStealClick/OnBreakClick; reset wyłącza gdy oba stany nieaktywne).
    private void Update()
    {
        if (_awaitingStealConfirmation)
        {
            _stealTimer -= Time.deltaTime;
            if (_stealTimer <= 0f) ResetStealState();
        }

        if (_awaitingBreakConfirmation)
        {
            _breakTimer -= Time.deltaTime;
            if (_breakTimer <= 0f) ResetBreakState();
        }
    }

    private void OnStealClick()
    {
        if (!_awaitingStealConfirmation)
        {
            _awaitingStealConfirmation = true;
            _stealTimer = ConfirmationTimeout;
            ResetBreakState();
            enabled = true;   // === FIX K1

            if (stealButtonText != null) stealButtonText.text = "SURE?";
            var colors = stealButton.colors;
            colors.normalColor = new Color(1f, 0f, 0f, 1f);
            stealButton.colors = colors;

            LogToPanel("<color=yellow>[Steal]</color> Click <b>SURE?</b> again to confirm force steal.");
        }
        else
        {
            LogToPanel($"<color=orange>[Steal]</color> Force stealing lock: <b>{_path}</b>...");
            _stealAction?.Invoke();
            ResetStealState();
        }
    }

    private void OnBreakClick()
    {
        if (!_awaitingBreakConfirmation)
        {
            _awaitingBreakConfirmation = true;
            _breakTimer = ConfirmationTimeout;
            ResetStealState();
            enabled = true;   // === FIX K1

            if (breakButtonText != null) breakButtonText.text = "SURE?";
            var colors = breakButton.colors;
            colors.normalColor = new Color(1f, 0.5f, 0f, 1f);
            breakButton.colors = colors;

            LogToPanel("<color=yellow>[Break]</color> Click <b>SURE?</b> again to confirm force break.");
        }
        else
        {
            LogToPanel($"<color=orange>[Break]</color> Force breaking lock on server: <b>{_path}</b>...");
            _breakAction?.Invoke();
            ResetBreakState();
        }
    }

    private void ResetStealState()
    {
        _awaitingStealConfirmation = false;
        _stealTimer = 0f;
        if (stealButtonText != null) stealButtonText.text = "Steal Lock";
        if (stealButton != null)
        {
            var colors = stealButton.colors;
            colors.normalColor = _originalStealColor;
            stealButton.colors = colors;
        }

        // === FIX K1: gdy oba stany nieaktywne — wyłącz Update (timery niepotrzebne).
        if (!_awaitingBreakConfirmation)
            enabled = false;
    }

    private void ResetBreakState()
    {
        _awaitingBreakConfirmation = false;
        _breakTimer = 0f;
        if (breakButtonText != null) breakButtonText.text = "Break Lock";
        if (breakButton != null)
        {
            var colors = breakButton.colors;
            colors.normalColor = _originalBreakColor;
            breakButton.colors = colors;
        }

        // === FIX K1: jw.
        if (!_awaitingStealConfirmation)
            enabled = false;
    }

    private void LogToPanel(string msg)
    {
        if (_panelConsole != null)
            _panelConsole.text += msg + "\n";
    }

    private string SanitizeRichText(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return input.Replace("<", "").Replace(">", "");
    }
}