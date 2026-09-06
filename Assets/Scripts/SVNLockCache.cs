using System;
using System.Collections.Generic;

namespace SVN.Core
{
    // UWAGA: [Serializable] pozostaje dla kompatybilności, ale JsonUtility NIE
    // serializuje Dictionary — klasa jest wyłącznie runtime-cachesem.
    [Serializable]
    public class SVNLockCache
    {
        // === FIX K1: build-then-swap. Wcześniej publiczne pole Dictionary było
        // mutowane (Clear + indexer) z THREAD POOLU (RefreshLockCacheAsync po
        // ConfigureAwait(false)), a czytane z main thread (ApplyLocksToTree) —
        // równoległy odczyt podczas zapisu = niezdefiniowane zachowanie; czytelnik
        // widział też CZĘŚCIOWO zasiedlany cache. Teraz: jedyną mutacją jest
        // atomowa podmiana referencji — czytelnicy dostają kompletną starą lub
        // kompletną nową wersję.
        private volatile Dictionary<string, SVNLockDetails> _locks =
            new Dictionary<string, SVNLockDetails>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, SVNLockDetails> Locks => _locks;

        // DateTime (8B) — teoretycznie rozrywany na 32-bit; w praktyce Unity
        // player 64-bit, akceptowalne. Ustawiany wyłącznie w ReplaceAll/Clear.
        public DateTime LastRefreshUtc { get; private set; }

        public bool IsValid(double maxSeconds = 60.0)
        {
            if (maxSeconds <= 0) return false;
            return (DateTime.UtcNow - LastRefreshUtc).TotalSeconds < maxSeconds;
        }

        public void Clear()
        {
            _locks = new Dictionary<string, SVNLockDetails>(StringComparer.OrdinalIgnoreCase);
            LastRefreshUtc = default;
        }

        /// <summary>
        /// Atomowa podmiana zawartości. Buduj nowy słownik LOKALNIE i przekaż —
        /// nie mutuj zwracanego przez Locks (to współdzielony snapshot).
        /// </summary>
        public void ReplaceAll(Dictionary<string, SVNLockDetails> newLocks)
        {
            _locks = newLocks ?? new Dictionary<string, SVNLockDetails>(StringComparer.OrdinalIgnoreCase);
            LastRefreshUtc = DateTime.UtcNow;
        }
    }

    [Serializable]
    public class SVNLockDetails
    {
        public string Path = "";
        public string FullPath = "";
        public string Owner = "";
        public string CreationDate = "";
        public string Comment = "";
    }
}