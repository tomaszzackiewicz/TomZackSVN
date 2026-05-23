using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SVN.Core
{
    /// <summary>
    /// Tracks all SVN/SSH processes created by this application
    /// and safely terminates them on shutdown/cancel.
    /// </summary>
    public static class SvnProcessTracker
    {
        private static readonly object LockObject = new();
        private static readonly HashSet<Process> ActiveProcesses = new();

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InitEditorQuitHook()
        {
            EditorApplication.quitting += KillAll;

            AppDomain.CurrentDomain.DomainUnload += (_, __) => KillAll();
        }
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitRuntimeQuitHook()
        {
            Application.quitting += KillAll;
        }
#endif

        /// <summary>
        /// Register newly started process.
        /// </summary>
        public static void Register(Process process)
        {
            if (process == null)
                return;

            lock (LockObject)
            {
                ActiveProcesses.Add(process);
            }

            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, __) =>
                {
                    Unregister(process);
                };
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN Tracker] Register failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove finished process from tracking.
        /// </summary>
        public static void Unregister(Process process)
        {
            if (process == null)
                return;

            lock (LockObject)
            {
                ActiveProcesses.Remove(process);
            }
        }

        /// <summary>
        /// Kill single process safely.
        /// </summary>
        public static void Kill(Process process)
        {
            if (process == null) return;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SvnProcessTracker] Wyjątek podczas Kill: {ex.Message}");
            }

            Unregister(process);
        }

        public static void KillAll()
        {
            Process[] processesToKill;

            lock (LockObject)
            {
                if (ActiveProcesses == null || ActiveProcesses.Count == 0) return;
                processesToKill = ActiveProcesses.ToArray();
                ActiveProcesses.Clear();
            }

            SVNLogBridge.LogLine($"[SvnProcessTracker] Zamykanie aplikacji. Zabijanie {processesToKill.Length} aktywnych procesów SVN...");

            foreach (var process in processesToKill)
            {
                if (process == null) continue;

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogError($"[SvnProcessTracker] Błąd podczas zabijania procesu: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// Returns true if any SVN operation is still running.
        /// </summary>
        public static bool HasRunningProcesses()
        {
            lock (LockObject)
            {
                ActiveProcesses.RemoveWhere(p =>
                {
                    try
                    {
                        return p == null || p.HasExited;
                    }
                    catch
                    {
                        return true;
                    }
                });

                return ActiveProcesses.Count > 0;
            }
        }

        /// <summary>
        /// Returns number of active SVN operations.
        /// </summary>
        public static int ActiveCount
        {
            get
            {
                lock (LockObject)
                {
                    return ActiveProcesses.Count;
                }
            }
        }
    }
}