using UnityEngine;

namespace SVN.Core
{
    public class SVNNotificationAudio : MonoBehaviour
    {
        public static SVNNotificationAudio Instance { get; private set; }

        [SerializeField] private AudioSource audioSource;

        [Range(0f, 1f)][SerializeField] private float volume = 1.0f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);   // ← było Destroy(gameObject) — komponent na współdzielonym obiekcie
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void PlayCommitSound()
        {
            if (audioSource != null)
            {
                // === FIX K1: pole volume było martwe — suwak w Inspectorze nic nie robił.
                audioSource.volume = volume;
                audioSource.Play();
            }
        }
    }
}