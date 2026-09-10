using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    [RequireComponent(typeof(LayoutElement))]
    public class RepoTreeItemSizer : MonoBehaviour
    {
        public RectTransform childrenContainer;

        private LayoutElement _layoutElement;
        private float _rowHeight = 25f;

        // === FIX (layout-rebuild loop): batch API. Wcześniej każde
        // UpdateHeight() wołało ForceRebuildLayoutImmediate — przy N węzłach
        // i N wywołań w jednej klatce dostajesz N² pracy na layoucie.
        // Teraz: MarkDirty() kolejkuje, FlushDirty() odpala RAZ na klatkę.
        private static readonly List<RepoTreeItemSizer> _dirtyQueue = new List<RepoTreeItemSizer>();
        private static bool _flushScheduled;

        void Awake()
        {
            _layoutElement = GetComponent<LayoutElement>();
        }

        private void OnDisable()
        {
            // Unikaj trzymania martwej referencji w kolejce po zniszczeniu.
            _dirtyQueue.Remove(this);
        }

        /// <summary>
        /// Natychmiastowe przeliczenie (zachowanie wsteczne). Jeśli wołasz to
        /// z pętli po wielu węzłach — użyj MarkDirty() zamiast tego.
        /// </summary>
        public void UpdateHeight()
        {
            if (_layoutElement == null || childrenContainer == null) return;

            float targetHeight = _rowHeight;

            if (childrenContainer.gameObject.activeSelf)
            {
                // ForceRebuild to ostateczność — tylko dla pojedynczego wywołania.
                LayoutRebuilder.ForceRebuildLayoutImmediate(childrenContainer);
                targetHeight += childrenContainer.rect.height;
            }

            _layoutElement.preferredHeight = targetHeight;
        }

        /// <summary>
        /// === FIX (perf): wersja batched. Wołaj zamiast UpdateHeight(), gdy
        /// zmieniasz wiele węzłów w jednej klatce (np. CollapseAll / rozwijanie
        /// całej ścieżki). Wewnętrzny flush odpala się raz na Canvas.willRenderCanvases.
        /// </summary>
        public void MarkDirty()
        {
            if (_dirtyQueue.Contains(this)) return;

            _dirtyQueue.Add(this);

            if (_flushScheduled) return;
            _flushScheduled = true;

            Canvas.willRenderCanvases += FlushDirty;
        }

        private static void FlushDirty()
        {
            Canvas.willRenderCanvases -= FlushDirty;
            _flushScheduled = false;

            // Kopia — UpdateHeight może (teoretycznie) dodać kolejne do kolejki.
            var snapshot = _dirtyQueue.ToArray();
            _dirtyQueue.Clear();

            foreach (var sizer in snapshot)
            {
                if (sizer == null) continue;
                sizer.UpdateHeight();
            }
        }
    }
}