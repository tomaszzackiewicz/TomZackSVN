using System;
using System.Collections.Generic;

namespace SVN.Core
{
    public sealed class SvnUpdateProgressUI
    {
        private const char BarFilled = '█';
        private const char BarEmpty = '░';
        private const int BarWidth = 40;
        private const int RenderThrottleMs = 100;

        private readonly TMPro.TMP_Text _target;

        private Dictionary<string, long> _sizes;
        private long _totalBytes;
        private long _doneBytes;

        private int _done;
        private int _total = -1;
        private int _conflicts;
        private string _lastDisplayLine = "";
        private string _lastProgressStr = "";
        private bool _finished;
        private int _lastRenderMs = Environment.TickCount;

        private string _lastContent = "";

        public SvnUpdateProgressUI(TMPro.TMP_Text target)
        {
            _target = target;
        }

        public void SetTotal(int totalItems, long totalBytes, Dictionary<string, long> sizes)
        {
            if (_finished) return;

            if (totalItems > 0) _total = totalItems;
            _totalBytes = totalBytes > 0 ? totalBytes : 0;
            _sizes = _totalBytes > 0 ? sizes : null;
            Render(force: true);
        }

        public void OnItem(string path, string displayLine, string progressStr, bool isConflict = false)
        {
            if (_finished) return;
            _done++;
            if (isConflict) _conflicts++;

            if (_sizes != null && !string.IsNullOrEmpty(path) &&
                _sizes.TryGetValue(path, out long size))
            {
                _doneBytes += size;
            }

            _lastDisplayLine = displayLine ?? "";
            _lastProgressStr = progressStr ?? "";
            Render(force: false);
        }

        public void Finish()
        {
            if (_total > 0 && _done < _total) _done = _total;
            if (_totalBytes > 0 && _doneBytes < _totalBytes) _doneBytes = _totalBytes;
            _finished = true;
            Render(force: true);

            _ = RemoveAfterDelayAsync(2500);
        }

        public void Clear()
        {
            _finished = true;
            SetText("");
        }

        private async System.Threading.Tasks.Task RemoveAfterDelayAsync(int delayMs)
        {
            try { await System.Threading.Tasks.Task.Delay(delayMs).ConfigureAwait(false); }
            catch { }

            string mine = _lastContent;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                var text = _target;
                if (text == null) return;
                if (string.Equals(text.text, mine, StringComparison.Ordinal))
                    text.text = "";
            });
        }

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

            string line1;
            if (_totalBytes > 0)
            {
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
                string bar = $"<color=#777777>{new string(BarEmpty, BarWidth)}</color>";
                string count = _done > 0
                    ? $" <color=#FFFF00>{_done} items</color>"
                    : "";
                line1 = $"{bar}{count}{conflict}";
            }

            string filePart = string.IsNullOrEmpty(_lastDisplayLine)
                ? "<color=#FFFF00>Preparing...</color>"
                : $"<b>[SVN]</b> <color=blue>{_lastDisplayLine}{_lastProgressStr}</color>";

            return "\n" + line1 + "\n" + filePart;
        }

        private void SetText(string content)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                var text = _target;
                if (text == null) return;
                text.text = content;
            });
            _lastContent = content ?? "";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes + " B";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb < 1024) return mb.ToString("F1") + " MB";
            return (mb / 1024.0).ToString("F2") + " GB";
        }

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