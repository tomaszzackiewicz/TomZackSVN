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
            Indeterminate = 1, // Loading (pulsate)
            Normal = 2,        // Green
            Error = 4,         // Red
            Paused = 8         // Yellow
        }

        #region COM Interfaces
        [ComImport]
        [Guid("56452417-b475-11d1-b92f-00a0c90312e1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        [ComImport]
        [Guid("602d4995-b13d-421b-a634-1463e5d0adeb")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList2 : ITaskbarList
        {
            void MarkFullscreenWindow(IntPtr hwnd, bool fFullscreen);
        }

        [ComImport]
        [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3 : ITaskbarList2
        {
            new void HrInit();
            new void AddTab(IntPtr hwnd);
            new void DeleteTab(IntPtr hwnd);
            new void ActivateTab(IntPtr hwnd);
            new void SetActiveAlt(IntPtr hwnd);
            new void MarkFullscreenWindow(IntPtr hwnd, bool fFullscreen);
            void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            void SetProgressState(IntPtr hwnd, TaskbarState tbpFlags);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
            void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
        }

        [ComImport]
        [Guid("56452417-b475-11d1-b92f-00a0c90312e1")]
        private class TaskbarInstance { }
        #endregion

        private static ITaskbarList3 _taskbarInstance;
        private static IntPtr _windowHandle = IntPtr.Zero;
        private static bool _disabled = false;

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        private static void EnsureInitialized()
        {
            // Jeśli system jest wyłączony (bo wystąpił błąd), nie próbuj ponownie
            if (_disabled) return;

            if (_taskbarInstance == null)
            {
                try
                {
                    // Sprawdzamy platformę
                    if (Application.platform != RuntimePlatform.WindowsEditor &&
                        Application.platform != RuntimePlatform.WindowsPlayer)
                    {
                        _disabled = true;
                        return;
                    }

                    _taskbarInstance = (ITaskbarList3)new TaskbarInstance();
                    _taskbarInstance.HrInit();
                }
                catch (Exception ex)
                {
                    _disabled = true;
                    UnityEngine.Debug.LogWarning($"[SVN] Windows Taskbar Feature is not available or class is not registered. Error: {ex.Message}");
                    return;
                }
            }

            if (_windowHandle == IntPtr.Zero)
            {
                _windowHandle = Process.GetCurrentProcess().MainWindowHandle;

                if (_windowHandle == IntPtr.Zero)
                    _windowHandle = FindWindow(null, Application.productName);

                if (_windowHandle == IntPtr.Zero)
                    _windowHandle = GetActiveWindow();
            }
        }

        public static void SetProgress(float value, float max = 1.0f)
        {
            EnsureInitialized();
            if (_disabled || _taskbarInstance == null || _windowHandle == IntPtr.Zero) return;

            try
            {
                ulong completed = (ulong)(Mathf.Clamp01(value / max) * 100);
                _taskbarInstance.SetProgressValue(_windowHandle, completed, 100);
            }
            catch { _disabled = true; }
        }

        public static void SetState(TaskbarState state)
        {
            EnsureInitialized();
            if (_disabled || _taskbarInstance == null || _windowHandle == IntPtr.Zero) return;

            try
            {
                if (state != TaskbarState.NoProgress)
                {
                    // Ustawienie wartości na 1, aby stan (kolor) był widoczny
                    _taskbarInstance.SetProgressValue(_windowHandle, 1, 100);
                }
                _taskbarInstance.SetProgressState(_windowHandle, state);
            }
            catch { _disabled = true; }
        }

        public static void Flash()
        {
            EnsureInitialized();
            if (_disabled || _windowHandle == IntPtr.Zero) return;

            try
            {
                FlashWindow(_windowHandle, true);
            }
            catch { }
        }

        public static void Reset()
        {
            if (_disabled) return;
            SetState(TaskbarState.NoProgress);
        }
    }
}