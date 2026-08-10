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
                LayoutRebuilder.ForceRebuildLayoutImmediate(childrenContainer);

                targetHeight += childrenContainer.rect.height;
            }

            _layoutElement.preferredHeight = targetHeight;
        }
    }
}