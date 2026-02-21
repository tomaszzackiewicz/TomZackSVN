using UnityEngine;
using UnityEngine.UI;

namespace SVN.Core
{
    public class SmoothScroll : MonoBehaviour
    {
        [Header("Settings")]
        public ScrollRect scrollRect;
        public float scrollSpeed = 10f; // Prędkość płynnego przewijania

        private float _targetPosition = -1f;

        private void Update()
        {
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
            if (scrollRect == null) return;

            // Wymuszamy przeliczenie UI, żeby znać nową wysokość tekstu
            Canvas.ForceUpdateCanvases();
            _targetPosition = 0f; // 0 to dół w ScrollRect
        }

        // Funkcja do natychmiastowego skoku (np. przy otwieraniu okna)
        public void InstantScrollToBottom()
        {
            if (scrollRect == null) return;
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
            _targetPosition = -1f;
        }
    }
}