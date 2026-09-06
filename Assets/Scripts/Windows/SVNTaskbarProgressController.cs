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
        ResetTaskbar();
    }

    private void HandleProcessingState(bool isProcessing)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
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
            WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Error);

            if (_errorResetRoutine != null)
                StopCoroutine(_errorResetRoutine);

            _errorResetRoutine = StartCoroutine(ResetErrorAfterDelay(2f));
        });
    }

    private IEnumerator ResetErrorAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
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