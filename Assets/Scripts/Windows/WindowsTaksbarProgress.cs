using SVN.Core;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

public static class WindowsTaskbarProgress
{
    public enum TaskbarState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN

    // === FIX (sygnatury HRESULT): native metody ITaskbarList3 zwracają HRESULT
    // (int, S_OK == 0). [PreserveSig] + void było technicznie działające
    // (marshaller ignorował wynik), ale formalnie nadużyciem — teraz wszystkie
    // zwracają int i sprawdzamy HrInit. Vtable kolejności bez zmian:
    //   ITaskbarList → ITaskbarList2 → ITaskbarList3.
    // Metody poniżej SetProgressState istnieją w interfejsie, ale nie są
    // używane — pominięte legalnie (nie wołamy ich).
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] int SetProgressState(IntPtr hwnd, TaskbarState tbpFlags);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class CTaskbarList { }

    private const int S_OK = 0;

    private static ITaskbarList3 _taskbarList;
    private static IntPtr _mainWindowHandle;

    private static bool Initialize()
    {
        if (_taskbarList != null && _mainWindowHandle != IntPtr.Zero)
            return true;

        try
        {
            // UWAGA (zachowanie): w EDYTORZE zwraca okno Unity Editora — progres
            // pojawi się na ikonie edytora, nie Game View. W playerze: okno gry.
            _mainWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
            if (_mainWindowHandle == IntPtr.Zero)
                return false;

            _taskbarList = (ITaskbarList3)new CTaskbarList();

            int hr = _taskbarList.HrInit();
            if (hr != S_OK)
            {
                // HrInit nie powiodło się — interfejs niedostępny na tym systemie.
                // Zwolnij referencję, żeby kolejne SetState próbowały od nowa.
                _taskbarList = null;
                _mainWindowHandle = IntPtr.Zero;
                return false;
            }

            return true;
        }
        catch
        {
            _taskbarList = null;
            _mainWindowHandle = IntPtr.Zero;
            return false;
        }
    }

    public static void SetState(TaskbarState state)
    {
        if (!Initialize()) return;

        try
        {
            _taskbarList.SetProgressState(_mainWindowHandle, state);
        }
        catch { }
    }

    public static void SetProgress(int current, int total)
    {
        if (!Initialize()) return;

        try
        {
            if (total <= 0) return;

            // === Defensywnie: klamp przed rzutowaniem na ulong. Ujemne wartości
            // po rzutowaniu na ulong stają się ogromne → pasek dostawał
            // nonsensowne "current > total" (wizualny glitch). Caller i tak
            // powinien podawać poprawne, ale to jednolinijkowa ochrona.
            if (current < 0) current = 0;
            if (current > total) current = total;

            _taskbarList.SetProgressValue(_mainWindowHandle, (ulong)current, (ulong)total);
        }
        catch { }
    }

    public static void Reset()
    {
        SetState(TaskbarState.NoProgress);
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    public static void Flash(uint count = 3, uint timeout = 0, uint flags = 0x00000003)
    {
        // === FIX K1: pełny guard + try/catch — wcześniej DllNotFoundException/
        // InvalidOperationException potrafiły uciec nieprzechwycone.
        try
        {
            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;

            FLASHWINFO fw = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = hwnd,
                dwFlags = flags,
                uCount = count,
                dwTimeout = timeout
            };

            FlashWindowEx(ref fw);
        }
        catch { }
    }

    public static void Release()
    {
        // === FIX: najpierw zgas stan paska, potem zwolnij referencje
        // (wcześniej Error potrafił zostać na pasku do końca sesji).
        try { SetState(TaskbarState.NoProgress); } catch { }

        _taskbarList = null;
        _mainWindowHandle = IntPtr.Zero;
    }

#else
    // === FIX K2: no-op na macOS/Linux — cała funkcjonalność wyłącznie Windows.

    public static void SetState(TaskbarState state) { }
    public static void SetProgress(int current, int total) { }
    public static void Reset() { }
    public static void Flash(uint count = 3, uint timeout = 0, uint flags = 0x00000003) { }
    public static void Release() { }
#endif
}