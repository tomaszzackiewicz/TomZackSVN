using UnityEngine;

namespace SVN.Core
{
    /// <summary>
    /// JEDYNE źródło prawdy dla kolorów statusów SVN w całym projekcie.
    ///
    /// ZASADY:
    ///  - Wszystkie statusy używają rozróżnialnych, czytelnych kolorów.
    ///  - BEZ neonowego zielonego (#00FF41) — zbyt agresywny; A używa #5CE65C.
    ///  - Statusy [?] używa Dodger Blue (#1E90FF).
    ///  - Metadane (rev/author) — jasny steel blue (#A8C8E8).
    ///  - Nazwy plików — białe (spójnie z drzewem zmian).
    ///  - BEZ ciemnych zieleni typu #33CC66 — słabo widoczne na szarym tle;
    ///    StatusAdded jest teraz w jasnej zieleni #5CE65C (jak Git success).
    /// </summary>
    public static class SVNColors
    {
        // =====================================================================
        //  STATUSY SVN — hex stringi (do rich text w TMP)
        // =====================================================================
        public const string StatusModified = "#FFD700";   // M  — złoty
        public const string StatusAdded = "#5CE65C";   // A  — jasny zieleń (widoczny na szarym)
        public const string StatusDeleted = "#FF4444";   // D  — czerwony
        public const string StatusMissing = "#FF00FF";   // !  — magenta
        public const string StatusConflict = "#FF00FF";   // C  — magenta
        public const string StatusUnversioned = "#1E90FF";   // ?  — Dodger Blue
        public const string StatusIgnored = "#808080";   // I  — szary
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
        public const string Dim = "#888888";
        public const string Accent = "#FFD700";
        public const string Metadata = "#A8C8E8";   // metadane (rev/author)

        // =====================================================================
        //  STATUSY — Unity Color
        // =====================================================================
        public static readonly Color StatusModifiedColor = new(1f, 0.843f, 0f);
        public static readonly Color StatusAddedColor = new(0.361f, 0.902f, 0.361f);  // #5CE65C
        public static readonly Color StatusDeletedColor = new(1f, 0.267f, 0.267f);
        public static readonly Color StatusConflictColor = new(1f, 0f, 1f);
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
        public static readonly Color RowBgConflict = new(1f, 0f, 1f, 0.1f);
        public static readonly Color RowBgIgnored = new(0.3f, 0.3f, 0.3f, 0.05f);
        public static readonly Color RowBgDefault = new(0, 0, 0, 0);

        // =====================================================================
        //  NAZWY PLIKÓW / FOLDERÓW
        // =====================================================================
        // === FIX (czytelność): #003366 był bardzo ciemny — na szarym tle panelu
        // napis "Folders: 12" zlewał się z tłem. #4F7BB8 to ten sam granat, ale
        // 2 stopnie jaśniejszy — nadal rozpoznawalny jako "granatowy", czytelny
        // na szarym tle. Wartość RGB dobrana tak, żeby kontrast vs panel ≥ 4.5:1
        // (WCAG AA dla normalnego tekstu).
        // === FIX (czytelność): #003366 był zbyt ciemny (zlewał się z tłem),
        // #4F7BB8 i #3D6BA8 okazały się za jasne. #2E5C99 — wyraźnie ciemniejszy
        // granat, ale wciąż czytelny na szarym tle panelu (kontrast ≥ 4.5:1).
        public static readonly Color DirNameColor = new Color(0.18f, 0.36f, 0.60f, 1f);   // #2E5C99
        public static readonly Color FileNameColor = new Color(0.678f, 0.847f, 0.902f, 1f); // #ADD8E6 — jak FileHex
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
            _ => "<color=#444444>[ ]</color>"
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