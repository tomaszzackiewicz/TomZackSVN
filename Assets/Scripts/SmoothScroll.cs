using UnityEngine;
using UnityEngine.UI;
using System.Collections;

namespace SVN.Core
{
    public class SmoothScroll : MonoBehaviour
    {
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private float scrollSpeed = 20f;
        [SerializeField] private bool autoScroll = true;

        private float _targetPosition = -1f;
        private RectTransform _content;
        private float _lastHeight;

        private void Start()
        {
            if (scrollRect != null) _content = scrollRect.content;
        }

        private void Update()
        {
            if (autoScroll && _content != null && _content.rect.height != _lastHeight)
            {
                _lastHeight = _content.rect.height;
                ScrollToBottom();
            }

            if (_targetPosition >= 0f)
            {
                scrollRect.verticalNormalizedPosition = Mathf.Lerp(
                    scrollRect.verticalNormalizedPosition,
                    _targetPosition,
                    Time.deltaTime * scrollSpeed
                );

                if (Mathf.Abs(scrollRect.verticalNormalizedPosition - _targetPosition) < 0.001f)
                {
                    scrollRect.verticalNormalizedPosition = _targetPosition;
                    _targetPosition = -1f;
                }
            }
        }

        public void ScrollToBottom()
        {
            if (gameObject.activeInHierarchy)
                StartCoroutine(EnsureScroll());
        }

        private IEnumerator EnsureScroll()
        {

            yield return new WaitForEndOfFrame();
            Canvas.ForceUpdateCanvases();
            _targetPosition = 0f;
            yield return null;
            _targetPosition = 0f;
        }
    }
}