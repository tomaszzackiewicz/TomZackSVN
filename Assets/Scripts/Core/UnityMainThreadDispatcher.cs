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
            var actionsToExecute = new List<Action>(MAX_ACTIONS_PER_FRAME);

            lock (ExecutionQueue)
            {
                if (ExecutionQueue.Count > 100)
                    SVNLogBridge.LogWarning($"[Dispatcher] Queue backlog: {ExecutionQueue.Count}");

                int count = Mathf.Min(ExecutionQueue.Count, MAX_ACTIONS_PER_FRAME);
                for (int i = 0; i < count; i++)
                {
                    actionsToExecute.Add(ExecutionQueue.Dequeue());
                }
            }

            foreach (var action in actionsToExecute)
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnityMainThreadDispatcher] Action failed: {ex}");
                }
            }
        }

        public static void EnsureExists()
        {
            if (_instance != null) return;

            if (Thread.CurrentThread.ManagedThreadId == 1)
            {
                CreateInstance();
            }
            else
            {
                Enqueue(CreateInstance);
            }
        }

        private static void CreateInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("SVN_MainThreadDispatcher");
            _instance = go.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
    }
}