using System;
using System.Collections.Generic;

namespace SVN.Core
{
    /// <summary>
    /// Postęp update'u/checkoutu — dwie linie podmieniane in-place:
    ///
    ///   (pusta)
    ///   ████████████░░░░░░░░░░░░░░ ~2.3 / 5.4 GB (42%) C:1
    ///   (pusta)
    ///   [SVN] = Updated: Assets/Scenes/Main.unity (105/172)
    ///
    /// LINIA 1: pasek + % WAŻONE BAJTAMI (rozmiary z svn list --xml -R);
    ///   gdy rozmiary niedostępne — fallback na liczenie per plik.
    /// LINIA 2: aktualny plik NIEBIESKI + (x/y) LICZBA PLIKÓW — format
    ///   oryginalny (displayLine + progressStr).
    /// </summary>
    public sealed class SvnUpdateProgressUI
    {
        // Jeśli font nie ma █/░ (pokażą się kwadraty) — podmień na '#'/'.'
        private const char BarFilled = '█';
        private const char BarEmpty = '░';
        private const int BarWidth = 24;
        private const int RenderThrottleMs = 100;

        private readonly TMPro.TMP_Text _target;

        // === tryb ważony bajtami (aktywny, gdy _totalBytes > 0) ===
        private Dictionary<string, long> _sizes;   // ścieżka → bajty (z svn list)
        private long _totalBytes;
        private long _doneBytes;

        // === liczniki plików (linia 2 + fallback trybu bajtowego) ===
        private int _done;
        private int _total = -1;                    // -1 = estymacja niedostępna
        private int _conflicts;
        private string _lastDisplayLine = "";
        private string _lastProgressStr = "";
        private bool _finished;
        private int _lastRenderMs = Environment.TickCount;

        // === FIX: ostatnia treść napisana przez nas — do bezpiecznego clearu
        // (RemoveAfterDelayAsync czyści TYLKO gdy nikt inny nie nadpisał pola,
        // np. raportem końcowym operacji; wcześniej bezwarunkowy clear po 2.5 s
        // wymazywał ten raport).
        private string _lastContent = "";

        public SvnUpdateProgressUI(TMPro.TMP_Text target)
        {
            _target = target;
        }

        /// <summary>
        /// totalItems — estymacja z status -u ('*'); totalBytes + sizes — wagi
        /// bajtowe. totalBytes <= 0 → automatyczny fallback na liczenie per plik.
        /// </summary>
        public void SetTotal(int totalItems, long totalBytes, Dictionary<string, long> sizes)
        {
            if (totalItems > 0) _total = totalItems;
            _totalBytes = totalBytes > 0 ? totalBytes : 0;
            _sizes = _totalBytes > 0 ? sizes : null;
            Render(force: true);
        }

        /// <summary>
        /// Per przetworzona pozycja update'u.
        /// path — ścieżka znormalizowana (jak w displayLine) do wagi bajtowej;
        /// displayLine + progressStr — identyczne ze starym LogLine.
        /// </summary>
        public void OnItem(string path, string displayLine, string progressStr, bool isConflict = false)
        {
            if (_finished) return;
            _done++;
            if (isConflict) _conflicts++;

            // === waga bajtowa: brak w mapie (D/deletion/external/nieznany) = 0 B —
            // te pozycje są szybkie; pasek domknie snap w Finish().
            if (_sizes != null && !string.IsNullOrEmpty(path) &&
                _sizes.TryGetValue(path, out long size))
            {
                _doneBytes += size;
            }

            _lastDisplayLine = displayLine ?? "";
            _lastProgressStr = progressStr ?? "";
            Render(force: false);
        }

        /// <summary>Sukces — snap do 100% (pliki i bajty), po 2.5 s tekst znika
        /// (o ile nikt inny nie napisał w międzyczasie do pola).</summary>
        public void Finish()
        {
            if (_total > 0 && _done < _total) _done = _total;
            if (_totalBytes > 0 && _doneBytes < _totalBytes) _doneBytes = _totalBytes;
            _finished = true;
            Render(force: true);

            _ = RemoveAfterDelayAsync(2500);
        }

        /// <summary>Cancel/błąd — natychmiastowe wyczyszczenie (raport w LogLine).</summary>
        public void Clear()
        {
            _finished = true;
            SetText("");
        }

        private async System.Threading.Tasks.Task RemoveAfterDelayAsync(int delayMs)
        {
            try { await System.Threading.Tasks.Task.Delay(delayMs).ConfigureAwait(false); }
            catch { }

            // === FIX: czyść TYLKO gdy nikt nie nadpisał pola po nas — porównujemy
            // z ostatnią treścią, którą sami napisaliśmy. FIFO dispatchera
            // zachowuje kolejność, więc raport (jeśli był) zdążył się zapisać
            // i to porównanie go ochroni.
            string mine = _lastContent;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                var text = _target;
                if (text == null) return;   // obejmuje zniszczony (Unity null)
                if (string.Equals(text.text, mine, StringComparison.Ordinal))
                    text.text = "";
            });
        }

        // ==================== render ====================

        private void Render(bool force)
        {
            int now = Environment.TickCount;
            if (!force && (now - _lastRenderMs) < RenderThrottleMs) return;
            _lastRenderMs = now;

            SetText(BuildContent());
        }

        private string BuildContent()
        {
            string conflict = _conflicts > 0
                ? $" <color=#FF4444><b>C:{_conflicts}</b></color>"
                : "";

            // --- LINIA 1: pasek + procent (żółty — czytelność przy pasku) ---
            string line1;
            if (_totalBytes > 0)
            {
                // === TRYB BAJTOWY: % i wypełnienie ważone rozmiarami plików ===
                long doneBytes = Math.Min(_doneBytes, _totalBytes);
                int pct = (int)Math.Min(100, _doneBytes * 100 / _totalBytes);
                int filled = (int)Math.Round(BarWidth * doneBytes / (double)_totalBytes);
                if (filled < 0) filled = 0;
                if (filled > BarWidth) filled = BarWidth;

                string bar = $"<color=#FFFF00>{new string(BarFilled, filled)}</color>" +
                             $"<color=#777777>{new string(BarEmpty, BarWidth - filled)}</color>";
                string bytes = $" <color=#FFFF00>~{FormatBytes(doneBytes)} / {FormatBytes(_totalBytes)} ({pct}%)</color>";
                line1 = $"{bar}{bytes}{conflict}";
            }
            else if (_total > 0)
            {
                // === FALLBACK: rozmiary niedostępne — liczenie per plik ===
                int pct = (int)Math.Min(100, (long)_done * 100 / _total);
                int filled = (int)Math.Round(BarWidth * Math.Min(_done, _total) / (double)_total);
                if (filled < 0) filled = 0;
                if (filled > BarWidth) filled = BarWidth;

                string bar = $"<color=#FFFF00>{new string(BarFilled, filled)}</color>" +
                             $"<color=#777777>{new string(BarEmpty, BarWidth - filled)}</color>";
                line1 = $"{bar} <color=#FFFF00>({pct}%)</color>{conflict}";
            }
            else
            {
                line1 = $"<color=#FFFF00>Updating...</color>{conflict}";
            }

            // --- LINIA 2: aktualny plik na niebiesko + (x/y) plików — oryginalny format ---
            string filePart = string.IsNullOrEmpty(_lastDisplayLine)
                ? "<color=#FFFF00>Preparing...</color>"
                : $"<b>[SVN]</b> <color=blue>{_lastDisplayLine}{_lastProgressStr}</color>";

            // === SEPARATORY: pusta linia nad paskiem i między paskiem a plikiem
            return "\n" + line1 + "\n\n" + filePart;
        }

        // ==================== zapis ====================

        private void SetText(string content)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                var text = _target;
                if (text == null) return;   // obejmuje zniszczony (Unity null)
                text.text = content;
            });
            _lastContent = content ?? "";   // === FIX: pamiętamy, co napisaliśmy
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes + " B";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb < 1024) return mb.ToString("F1") + " MB";
            return (mb / 1024.0).ToString("F2") + " GB";
        }

        /// <summary>
        /// === CHECKOUT: przerwanie — ostatnia podmieniana linia jasno mówi, że to
        /// NIE jest całe repo (katalog częściowy, wznowienie przez Resume).
        /// Zamiast czyszczenia (Clear) — zostaje widoczny komunikat.
        /// </summary>
        public void FinishIncomplete(string reason)
        {
            _finished = true;

            string filesPart = _total > 0 ? $" ({_done}/{_total} files)" : $" ({_done} files)";
            string bytesPart = _totalBytes > 0
                ? $", ~{FormatBytes(_doneBytes)} of {FormatBytes(_totalBytes)}"
                : "";

            string line = $"<color=#FF4444><b>[{reason} — CHECKOUT INCOMPLETE]</b></color>" +
                          $"<color=#FFAA00>{filesPart}{bytesPart}</color>";

            SetText(line);
        }
    }
}