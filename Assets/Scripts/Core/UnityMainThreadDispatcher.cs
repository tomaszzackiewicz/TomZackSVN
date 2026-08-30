using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SVN.Core
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static readonly Queue<Action> ExecutionQueue = new Queue<Action>();
        private static UnityMainThreadDispatcher _instance;

        // === NOWE (fix GetString off-thread #2): ID wątku głównego złapane w
        // Bootstrap — dla IsMainThread (szybka ścieżka odczytów main-only API,
        // np. PlayerPrefs w SVNManager.GetSettingWithFallback).
        private static int _mainThreadId = -1;

        // === FIX Ś1: budżet CZASOWY zamiast sztywnej liczby akcji — 256 akcji
        // ciężkich (render tekstu) potrafi zjeść klatkę; przy zalewie (checkout
        // 10k+ linii) stary limit powodował rosnący backlog mimo warningów.
        private const float MaxProcessingMsPerFrame = 8f;

        // === FIX K1: bootstrap w RuntimeInitializeOnLoadMethod — ZAWSZE main
        // thread, ZAWSZE przed pierwszym Enqueue z jakiejkolwiek inicjalizacji
        // modułów.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            // === NOWE: zapisz ID main threadu ZANIM cokolwiek innego.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            CreateInstance();
        }

        /// <summary>
        /// Czy bieżący wątek to main thread Unity.
        /// Przed Bootstrap (teoretycznie tylko poza play mode) — bezpieczne 'true'.
        /// </summary>
        public static bool IsMainThread =>
            _mainThreadId == -1 ||
            Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (ExecutionQueue)
            {
                ExecutionQueue.Enqueue(action);
            }
        }

        private void Update()
        {
            if (ExecutionQueue.Count == 0) return;

            var actionsToExecute = new List<Action>(Math.Min(ExecutionQueue.Count, 512));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool backlogWarned = false;

            lock (ExecutionQueue)
            {
                if (ExecutionQueue.Count > 100)
                {
                    backlogWarned = true;
                }

                int count = Math.Min(ExecutionQueue.Count, 512);
                for (int i = 0; i < count; i++)
                {
                    actionsToExecute.Add(ExecutionQueue.Dequeue());
                }
            }

            if (backlogWarned)
                SVNLogBridge.LogWarning($"[Dispatcher] Queue backlog: {actionsToExecute.Count + ExecutionQueue.Count}");

            int executed = 0;
            foreach (var action in actionsToExecute)
            {
                // === FIX Ś1: szanuj budżet czasowy; resztę ODŁÓŻ z powrotem
                // na POCZĄTEK kolejki — FIFO zachowane, brak utraty akcji.
                if (executed > 0 && stopwatch.ElapsedMilliseconds > MaxProcessingMsPerFrame)
                {
                    lock (ExecutionQueue)
                    {
                        var rest = actionsToExecute.GetRange(executed, actionsToExecute.Count - executed);
                        var combined = new Queue<Action>(rest.Count + ExecutionQueue.Count);
                        foreach (var a in rest) combined.Enqueue(a);
                        while (ExecutionQueue.Count > 0)
                            combined.Enqueue(ExecutionQueue.Dequeue());
                        ExecutionQueue.Clear();
                        foreach (var a in combined) ExecutionQueue.Enqueue(a);
                    }
                    break;
                }

                try
                {
                    action?.Invoke();
                    executed++;
                }
                catch (Exception ex)
                {
                    executed++;
                    Debug.LogError($"[UnityMainThreadDispatcher] Action failed: {ex}");
                }
            }
        }

        public static void EnsureExists()
        {
            if (_instance != null) return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                return;
#endif

            if (Application.isPlaying && _instance == null)
            {
                var ctx = SynchronizationContext.Current;
                if (ctx != null && ctx.GetType().Name.Contains("Unity"))
                    CreateInstance();
            }
        }

        private static void CreateInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("SVN_MainThreadDispatcher");
            _instance = go.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }

        // === FIX (korekta K2): NIE niszczymy hosta — dispatcher często siedzi na
        // GŁÓWNYM obiekcie UI razem z SVNManager/SVNUI/PanelHandler. Redundantna
        // kopia ze sceny jest neutralizowana (komponent off), bootstrapowa pompa działa.
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                enabled = false;   // wyłącz KOMPONENT, nie obiekt!
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}