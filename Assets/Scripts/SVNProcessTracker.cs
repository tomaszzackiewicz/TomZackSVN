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
    public static class SvnProcessTracker
    {
        private static readonly object LockObject = new();
        private static readonly HashSet<Process> ActiveProcesses = new();

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InitEditorQuitHook()
        {
            EditorApplication.quitting -= KillAll;
            EditorApplication.quitting += KillAll;

            AppDomain.CurrentDomain.DomainUnload -= OnDomainUnload;
            AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;

            static void OnDomainUnload(object sender, EventArgs e) => KillAll();
        }
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitRuntimeQuitHook()
        {
            Application.quitting -= KillAll;
            Application.quitting += KillAll;
        }
#endif

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

                process.Exited += (_, __) => Unregister(process);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN Tracker] Register failed: {ex.Message}");
            }
        }

        public static void Unregister(Process process)
        {
            if (process == null)
                return;

            lock (LockObject)
            {
                ActiveProcesses.Remove(process);
            }
        }

        public static void Kill(Process process)
        {
            if (process == null)
                return;

            try
            {
                KillTree(process);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SvnProcessTracker] Kill error: {ex.Message}");
            }
            finally
            {
                Unregister(process);

                try
                {
                    process.Dispose();
                }
                catch { }
            }
        }

        private static void KillTree(Process process)
        {
            try
            {
                if (process == null)
                    return;

                // === FIX (shutdown noise): proces mógł zakończyć się naturalnie I zostać
                // zdisposowany przez właściciela (SvnRunner 'using'), zanim handler Exited
                // (threadpool) wyrejestrował go ze zbioru. HasExited/Id na takim obiekcie
                // rzuca "No process is associated with this object" (lub ObjectDisposedException,
                // który jest PODKLASĄ InvalidOperationException — dlatego jeden catch na
                // bazową klasę załatwia oba). Dla killa: już martwy = cichy powrót.
                try
                {
                    if (process.HasExited)
                        return;
                }
                catch (InvalidOperationException) { return; }        // ⊇ ObjectDisposedException ("No process...")
                catch (System.ComponentModel.Win32Exception) { return; }   // np. brak dostępu do uchwytu

                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {process.Id} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                killer?.WaitForExit(500);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SvnProcessTracker] KillTree error: {ex.Message}");
            }
        }

        public static void KillAll()
        {
            Process[] processesToKill;

            lock (LockObject)
            {
                if (ActiveProcesses.Count == 0)
                    return;

                processesToKill = ActiveProcesses.ToArray();
                ActiveProcesses.Clear();
            }

            SVNLogBridge.LogLine(
                $"[SvnProcessTracker] Shutdown → killing {processesToKill.Length} SVN processes..."
            );

            foreach (var process in processesToKill)
            {
                if (process == null)
                    continue;

                try
                {
                    KillTree(process);
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogError(
                        $"[SvnProcessTracker] KillAll error: {ex.Message}"
                    );
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch { }
                }
            }
        }

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