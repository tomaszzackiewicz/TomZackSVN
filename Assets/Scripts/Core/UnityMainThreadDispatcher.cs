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
                ExecutionQueue.Enqueue(action);
            }
        }

        private void Update()
        {
            if (ExecutionQueue.Count == 0)
            {
                // === Domknięcie epizodu backlogu — tylko do pliku logu.
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
                    actionsToExecute.Add(ExecutionQueue.Dequeue());
                }

                pendingTotal = actionsToExecute.Count + ExecutionQueue.Count;
            }

            // === Edge-triggered — jeden komunikat na epizod, TYLKO do pliku logu.
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

                // === Przy wyłączonym domain reload statyczna kolejka przeżywa
                // sesje — akcje z poprzedniego play (pump umarł z obiektem, więc
                // nigdy się nie wykonały) odpaliłyby się na starcie NASTĘPNEJ sesji,
                // uderzając w zniszczone obiekty. Obiekt jest DontDestroyOnLoad,
                // więc OnDestroy = koniec sesji/aplikacji — nic wartościowego nie tracimy.
                lock (ExecutionQueue)
                {
                    ExecutionQueue.Clear();
                }
            }
        }
    }
}