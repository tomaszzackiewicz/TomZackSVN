using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using UnityEngine;

namespace SVN.Core
{
    public static class WindowsTaskbarProgress
    {
        public enum TaskbarState
        {
            NoProgress = 0,
            Indeterminate = 1, //Loading
            Normal = 2,        // Green
            Error = 4,         // Red
            Paused = 8         // Yellow
        }

        [ComImport]
        [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, bool fFullscreen);
            void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            void SetProgressState(IntPtr hwnd, TaskbarState tbpFlags);
        }

        [ComImport]
        [Guid("56452417-b475-11d1-b92f-00a0c90312e1")]
        private class TaskbarInstance { }

        private static ITaskbarList3 _taskbarInstance;
        private static IntPtr _windowHandle = IntPtr.Zero;

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        private static void EnsureInitialized()
        {
            if (_taskbarInstance == null)
            {
                try
                {
                    _taskbarInstance = (ITaskbarList3)new TaskbarInstance();
                    _taskbarInstance.HrInit();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[SVN] Taskbar Init Failed: {ex.Message}");
                }
            }

            if (_windowHandle == IntPtr.Zero)
            {
                _windowHandle = Process.GetCurrentProcess().MainWindowHandle;

                if (_windowHandle == IntPtr.Zero)
                {
                    _windowHandle = FindWindow(null, Application.productName);
                }

                if (_windowHandle == IntPtr.Zero)
                {
                    _windowHandle = GetActiveWindow();
                }
            }
        }

        public static void SetProgress(float value, float max = 1.0f)
        {
            EnsureInitialized();
            if (_taskbarInstance == null || _windowHandle == IntPtr.Zero) return;

            ulong completed = (ulong)(Mathf.Clamp01(value / max) * 100);
            _taskbarInstance.SetProgressValue(_windowHandle, completed, 100);
        }

        public static void SetState(TaskbarState state)
        {
            EnsureInitialized();
            if (_taskbarInstance == null || _windowHandle == IntPtr.Zero) return;

            if (state != TaskbarState.NoProgress)
            {
                _taskbarInstance.SetProgressValue(_windowHandle, 1, 100);
            }

            _taskbarInstance.SetProgressState(_windowHandle, state);
        }

        public static void Flash()
        {
            EnsureInitialized();
            if (_windowHandle != IntPtr.Zero)
            {
                FlashWindow(_windowHandle, true);
            }
        }

        public static void Reset()
        {
            SetState(TaskbarState.NoProgress);
        }
    }
}