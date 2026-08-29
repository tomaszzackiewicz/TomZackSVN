using System.Collections;
using UnityEngine;
using SVN.Core;

public class SVNTaskbarProgressController : MonoBehaviour
{
    private Coroutine _errorResetRoutine;

    private void OnEnable()
    {
        SVNLogBridge.LogLine("[Taskbar] Controller enabled – subscribing to SvnRunner events.");
        SvnRunner.OnProcessingStateChanged += HandleProcessingState;
        SvnRunner.OnOperationError += HandleOperationError;
    }

    private void OnDisable()
    {
        SVNLogBridge.LogLine("[Taskbar] Controller disabled – unsubscribing from SvnRunner events.");
        SvnRunner.OnProcessingStateChanged -= HandleProcessingState;
        SvnRunner.OnOperationError -= HandleOperationError;
        ResetTaskbar();
    }

    //private IEnumerator Start()
    //{
    //    SVNLogBridge.LogLine("[Taskbar] Direct test: Indeterminate for 5 seconds...");
    //    WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Indeterminate);
    //    yield return new WaitForSeconds(5f);
    //    WindowsTaskbarProgress.Reset();
    //    SVNLogBridge.LogLine("[Taskbar] Direct test finished.");
    //}

    private void HandleProcessingState(bool isProcessing)
    {
        // Przełączamy na główny wątek Unity
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            if (isProcessing)
            {
                WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Indeterminate);
            }
            else
            {
                WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.NoProgress);
            }
        });
    }

    private void HandleOperationError(string errorMessage)
    {
        UnityMainThreadDispatcher.Enqueue(() =>
        {
            WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Error);

            if (_errorResetRoutine != null)
            {
                StopCoroutine(_errorResetRoutine);
            }
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
            if (_errorResetRoutine != null)
            {
                StopCoroutine(_errorResetRoutine);
                _errorResetRoutine = null;
            }
            WindowsTaskbarProgress.Reset();
        });
    }
}