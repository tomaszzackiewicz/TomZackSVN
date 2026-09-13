using UnityEngine;

namespace SVN.Core
{
    /// <summary>
    /// JEDYNE źródło prawdy dla kolorów statusów SVN w całym projekcie.
    ///
    /// === FIX (Dim): ewolucja koloru "metadata/dim":
    ///   #888888 — zlewał się z szarym tłem panelu
    ///   #B8B8B8 — za jasny, rozmyty na szarym tle
    ///   #4A5A6E — ciemny slate blue: wyraźny niebieski odcień, czytelny
    ///              na neutralnym szarym tle, spójny z paletą (DirHex
    ///              też jest granatowy). Wygląda jak "metadata", nie
    ///              jak "highlight".
    /// </summary>
    public static class SVNColors
    {
        // =====================================================================
        //  STATUSY SVN — hex stringi (do rich text w TMP)
        // =====================================================================
        public const string StatusModified = "#FFD700";   // M  — złoty
        public const string StatusAdded = "#5CE65C";   // A  — jasny zieleń
        public const string StatusDeleted = "#FF4444";   // D  — czerwony
        public const string StatusMissing = "#FF4444";   // !  — czerwony (spójnie z D)
        public const string StatusConflict = "#FF1493";   // C  — DeepPink
        public const string StatusUnversioned = "#1E90FF";   // ?  — Dodger Blue
        public const string StatusIgnored = "#808080";   // I  — szary (status, zostaje)
        public const string StatusReplaced = "#FFA500";   // R  — pomarańczowy
        public const string StatusObstructed = "#FF8800";   // ~  — ciemny pomarańcz
        public const string StatusLockedByMe = "#00FF00";   // K
        public const string StatusLockedByOther = "#FF4444";   // O
        public const string StatusDefault = "#FFFFFF";

        // =====================================================================
        //  KOLORY UI
        // =====================================================================
        public const string Header = "#4FC3F7";   // etykiety "User:", "Branch:"
        public const string Success = "#66FF66";
        public const string Warning = "#FFAA00";
        public const string Error = "#FF5555";
        public const string Info = "#E6E6E6";
        public const string Dim = "#4A5A6E";   // === FIX: ciemny slate blue (było #B8B8B8, wcześniej #888888)
        public const string Accent = "#FFD700";
        public const string Metadata = "#A8C8E8";

        // =====================================================================
        //  STATUSY — Unity Color
        // =====================================================================
        public static readonly Color StatusModifiedColor = new(1f, 0.843f, 0f);
        public static readonly Color StatusAddedColor = new(0.361f, 0.902f, 0.361f);
        public static readonly Color StatusDeletedColor = new(1f, 0.267f, 0.267f);
        public static readonly Color StatusConflictColor = new(1f, 0.0784f, 0.5765f);  // #FF1493
        public static readonly Color StatusUnversionedColor = new(0.118f, 0.565f, 1f);
        public static readonly Color StatusIgnoredColor = new(0.4f, 0.4f, 0.4f);
        public static readonly Color StatusReplacedColor = new(1f, 0.65f, 0f);
        public static readonly Color StatusObstructedColor = new(1f, 0.53f, 0f);
        public static readonly Color StatusDefaultColor = Color.white;

        // =====================================================================
        //  TŁO WIERSZY
        // =====================================================================
        public static readonly Color RowBgModified = new(1f, 0.85f, 0f, 0.08f);
        public static readonly Color RowBgAdded = new(0.361f, 0.902f, 0.361f, 0.08f);
        public static readonly Color RowBgUnversioned = new(0.118f, 0.565f, 1f, 0.08f);
        public static readonly Color RowBgDeleted = new(1f, 0.2f, 0.2f, 0.08f);
        public static readonly Color RowBgConflict = new(1f, 0.0784f, 0.5765f, 0.10f);
        public static readonly Color RowBgIgnored = new(0.3f, 0.3f, 0.3f, 0.05f);
        public static readonly Color RowBgDefault = new(0, 0, 0, 0);

        // =====================================================================
        //  NAZWY PLIKÓW / FOLDERÓW
        // =====================================================================
        public static readonly Color DirNameColor = new Color(0.18f, 0.36f, 0.60f, 1f);   // #2E5C99
        public static readonly Color FileNameColor = new Color(0.678f, 0.847f, 0.902f, 1f); // #ADD8E6
        public const string DirHex = "#2E5C99";
        public const string FileHex = "#ADD8E6";

        // =====================================================================
        //  HELPERY
        // =====================================================================

        public static string GetStatusHex(string status) => status switch
        {
            "M" => StatusModified,
            "A" => StatusAdded,
            "D" => StatusDeleted,
            "!" => StatusMissing,
            "C" => StatusConflict,
            "?" => StatusUnversioned,
            "I" => StatusIgnored,
            "R" => StatusReplaced,
            "~" => StatusObstructed,
            "K" => StatusLockedByMe,
            "O" => StatusLockedByOther,
            _ => StatusDefault
        };

        public static Color GetStatusColor(string status) => status switch
        {
            "M" => StatusModifiedColor,
            "A" => StatusAddedColor,
            "D" => StatusDeletedColor,
            "!" => StatusDeletedColor,
            "C" => StatusConflictColor,
            "?" => StatusUnversionedColor,
            "I" => StatusIgnoredColor,
            "R" => StatusReplacedColor,
            "~" => StatusObstructedColor,
            "K" => StatusAddedColor,
            "O" => StatusDeletedColor,
            _ => StatusDefaultColor
        };

        public static Color GetRowBackgroundColor(string status) => status switch
        {
            "M" => RowBgModified,
            "A" => RowBgAdded,
            "?" => RowBgUnversioned,
            "D" or "!" => RowBgDeleted,
            "C" => RowBgConflict,
            "I" => RowBgIgnored,
            _ => RowBgDefault
        };

        public static string GetStatusIcon(string status) => status switch
        {
            "M" => $"<color={StatusModified}><b>[M]</b></color>",
            "A" => $"<color={StatusAdded}><b>[A]</b></color>",
            "I" => $"<color={StatusIgnored}>[I]</color>",
            "?" => $"<color={StatusUnversioned}><b>[?]</b></color>",
            "C" => $"<color={StatusConflict}><b>[C]</b></color>",
            "!" => $"<color={StatusMissing}><b>[!]</b></color>",
            "D" => $"<color={StatusDeleted}><b>[D]</b></color>",
            "R" => $"<color={StatusReplaced}><b>[R]</b></color>",
            _ => $"<color={Dim}>[ ]</color>"
        };

        public static string GetActionLetterHex(string action) => action switch
        {
            "Sending" => StatusModified,
            "Adding" => StatusAdded,
            "Deleting" => StatusDeleted,
            "Replacing" => StatusReplaced,
            "M" => StatusModified,
            "A" => StatusAdded,
            "D" => StatusDeleted,
            "R" => StatusReplaced,
            _ => StatusDefault
        };

        public static string Colorize(string text, string status)
            => $"<color={GetStatusHex(status)}>{text}</color>";
    }
}