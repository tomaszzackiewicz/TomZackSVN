using System.Collections;
using UnityEngine;
using SVN.Core;

public class SVNTaskbarProgressController : MonoBehaviour
{
    private Coroutine _errorResetRoutine;

    private void OnEnable()
    {
        // === FIX: diagnostyka do PLIKU (wcześniej LogLine → główna konsola UI
        // + plik przy każdym włączeniu/wyłączeniu — spam).
        SVNLogBridge.LogToFile("[Taskbar] Controller enabled.", "TASKBAR");
        SvnRunner.OnProcessingStateChanged += HandleProcessingState;
        SvnRunner.OnOperationError += HandleOperationError;
    }

    private void OnDisable()
    {
        SVNLogBridge.LogToFile("[Taskbar] Controller disabled.", "TASKBAR");
        SvnRunner.OnProcessingStateChanged -= HandleProcessingState;
        SvnRunner.OnOperationError -= HandleOperationError;

        // === FIX (Release): zwolnij cache COM (RCW) i uchwyt okna na koniec
        // życia kontrolera. Wcześniej statyczne _taskbarList / _mainWindowHandle
        // żyły do końca procesu — w edytorze z wyłączonym Domain Reload
        // przechodziły na kolejną sesję Play (bez realnego buga, bo Unity
        // trzyma ten sam HWND edytora, ale to niespójne z intencją Release()).
        // Release() sam gasi pasek (NoProgress) PRZED zwolnieniem referencji —
        // bezpieczniejsze niż ResetTaskbar przez dispatcher w teardown sceny
        // (dispatcher może już nie pompować).
        WindowsTaskbarProgress.Release();

        ResetTaskbar();
    }

    private void HandleProcessingState(bool isProcessing)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null) return;

            if (isProcessing)
                WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Indeterminate);
            else
                WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.NoProgress);
        });
    }

    private void HandleOperationError(string errorMessage)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null) return;

            WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Error);

            if (_errorResetRoutine != null)
                StopCoroutine(_errorResetRoutine);

            _errorResetRoutine = StartCoroutine(ResetErrorAfterDelay(2f));
        });
    }

    private IEnumerator ResetErrorAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);

        if (this == null) yield break;

        WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.NoProgress);
        _errorResetRoutine = null;
    }

    private void ResetTaskbar()
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (this == null) return;

            if (_errorResetRoutine != null)
            {
                StopCoroutine(_errorResetRoutine);
                _errorResetRoutine = null;
            }

            WindowsTaskbarProgress.Reset();
        });
    }
}