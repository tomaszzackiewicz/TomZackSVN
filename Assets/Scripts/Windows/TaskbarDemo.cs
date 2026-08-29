using System.Collections;
using UnityEngine;

public class TaskbarDemo : MonoBehaviour
{
    IEnumerator Start()
    {
        WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Indeterminate);
        yield return new WaitForSeconds(1.5f);

        for (int i = 0; i <= 100; i += 5)
        {
            WindowsTaskbarProgress.SetProgress(i, 100);
            yield return new WaitForSeconds(0.08f);
        }

        WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Error);
        yield return new WaitForSeconds(1.5f);

        WindowsTaskbarProgress.Reset();
    }
}