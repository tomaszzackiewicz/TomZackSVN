using System;
using System.Collections.Generic;
using UnityEngine;

namespace SVN.Core
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static readonly Queue<Action> ExecutionQueue = new Queue<Action>();
        private static UnityMainThreadDispatcher _instance;
        private const int MAX_ACTIONS_PER_FRAME = 256;

        public static void Enqueue(Action action)
        {
            lock (ExecutionQueue)
            {
                ExecutionQueue.Enqueue(action);
            }
        }

        private void Update()
        {
            if (ExecutionQueue.Count > 10)
                SVNLogBridge.LogLine($"[Dispatcher] Queue size: {ExecutionQueue.Count}");

            int processed = 0;

            while (processed < MAX_ACTIONS_PER_FRAME)
            {
                Action action = null;

                lock (ExecutionQueue)
                {
                    if (ExecutionQueue.Count == 0)
                        break;

                    action = ExecutionQueue.Dequeue();
                }

                action?.Invoke();

                processed++;
            }
        }

        public static void EnsureExists()
        {
            if (_instance != null) return;
            _instance = new GameObject("SVN_MainThreadDispatcher").AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(_instance.gameObject);
        }
    }
}