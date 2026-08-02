using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    // Wymaga, aby na obiekcie był Layout Element (dodamy go w kroku 2)
    [RequireComponent(typeof(LayoutElement))]
    public class RepoTreeItemSizer : MonoBehaviour
    {
        [Tooltip("Przeciągnij tu obiekt Children Container")]
        public RectTransform childrenContainer;

        private LayoutElement _layoutElement;
        private float _rowHeight = 25f;

        void Awake()
        {
            _layoutElement = GetComponent<LayoutElement>();
        }

        public void UpdateHeight()
        {
            if (_layoutElement == null || childrenContainer == null) return;

            float targetHeight = _rowHeight;

            if (childrenContainer.gameObject.activeSelf)
            {
                // 1. Wymuszamy na dzieciach przeliczenie ich własnej wysokości
                LayoutRebuilder.ForceRebuildLayoutImmediate(childrenContainer);

                // 2. Pobieramy tę prawidłową wysokość i dodajemy do wysokości wiersza
                targetHeight += childrenContainer.rect.height;
            }

            // 3. Mówimy rodzicowi (TreeContent) jaka jest nasza nowa wysokość.
            // VerticalLayoutGroup na pewno to usłyszy i przesunie inne foldery!
            _layoutElement.preferredHeight = targetHeight;
        }
    }
}