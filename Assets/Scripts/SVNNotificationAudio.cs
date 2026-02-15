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
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        public void PlayCommitSound()
        {
            if (audioSource != null)
            {
                audioSource.Play();
            }
        }
    }
}