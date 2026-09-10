using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SVN.Core
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        // === FIX (allocation): LinkedList zamiast Queue. Re-queue "reszty" przy
        // przekroczeniu budżetu klatki wcześniej alokował 2 kolekcje + 2 iteracje.
        // LinkedList pozwala AddFirst w O(1) bez realokacji.
        private static readonly LinkedList<Action> ExecutionQueue = new LinkedList<Action>();
        private static UnityMainThreadDispatcher _instance;

        // === ID wątku głównego złapane w bootstrap — dla IsMainThread.
        private static int _mainThreadId = -1;

        // === Budżet CZASOWY zamiast sztywnej liczby akcji.
        private const float MaxProcessingMsPerFrame = 8f;

        // === Edge-triggered raportowanie backlogu (start + resolved).
        // Komunikaty idą TYLKO do pliku logu (LogToFile), nie do konsoli UI —
        // to diagnostyka techniczna, nie informacja dla użytkownika.
        private static bool _backlogReported;
        private const int BacklogThreshold = 100;

        // === Bootstrap w RuntimeInitializeOnLoadMethod — ZAWSZE main thread,
        // ZAWSZE przed pierwszym Enqueue.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            CreateInstance();
        }

        /// <summary>
        /// Czy bieżący wątek to main thread Unity. Przed Bootstrap (teoretycznie
        /// tylko poza play mode) — bezpieczne 'true'.
        /// </summary>
        public static bool IsMainThread =>
            _mainThreadId == -1 ||
            Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (ExecutionQueue)
            {
                ExecutionQueue.AddLast(action);
            }
        }

        private void Update()
        {
            if (ExecutionQueue.Count == 0)
            {
                if (_backlogReported)
                {
                    _backlogReported = false;
                    SVNLogBridge.LogToFile("[Dispatcher] Queue backlog resolved.", "WARN");
                }
                return;
            }

            var actionsToExecute = new List<Action>(Math.Min(ExecutionQueue.Count, 512));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            int pendingTotal;
            lock (ExecutionQueue)
            {
                int count = Math.Min(ExecutionQueue.Count, 512);
                for (int i = 0; i < count; i++)
                {
                    var node = ExecutionQueue.First;
                    if (node == null) break;

                    actionsToExecute.Add(node.Value);
                    ExecutionQueue.RemoveFirst();
                }

                pendingTotal = actionsToExecute.Count + ExecutionQueue.Count;
            }

            if (pendingTotal > BacklogThreshold && !_backlogReported)
            {
                _backlogReported = true;
                SVNLogBridge.LogToFile(
                    $"[Dispatcher] Queue backlog: {pendingTotal} pending actions — UI updates lagging (slow actions or throttled main loop).",
                    "WARN");
            }

            int executed = 0;
            foreach (var action in actionsToExecute)
            {
                // === Budżet czasowy; resztę ODŁÓŻ na POCZĄTEK kolejki
                // (FIFO zachowane, brak utraty akcji).
                if (executed > 0 && stopwatch.ElapsedMilliseconds > MaxProcessingMsPerFrame)
                {
                    // === FIX (allocation): AddFirst w odwrotnej kolejności — bez
                    // budowania pośredniej listy ani nowej Queue. O(rest.Count) zamiast
                    // O(rest.Count + ExecutionQueue.Count).
                    lock (ExecutionQueue)
                    {
                        for (int i = actionsToExecute.Count - 1; i >= executed; i--)
                            ExecutionQueue.AddFirst(actionsToExecute[i]);
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

        // === NIE niszczymy hosta — dispatcher często siedzi na GŁÓWNYM obiekcie UI
        // razem z SVNManager/SVNUI/PanelHandler. Redundantna kopia ze sceny jest
        // neutralizowana (komponent off), bootstrapowa pompa działa.
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
            {
                _instance = null;

                lock (ExecutionQueue)
                {
                    ExecutionQueue.Clear();
                }
            }
        }
    }
}