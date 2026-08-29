using System.Collections;
using UnityEngine;
using SVN.Core;

public class TaskbarDemo : MonoBehaviour
{
    IEnumerator Start()
    {
        // faza 1: "pracuję, nie wiem jak długo" — pulsujący pasek
        WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Indeterminate);
        yield return new WaitForSeconds(1.5f);

        // faza 2: znany postęp — zielony pasek 0→100%
        for (int i = 0; i <= 100; i += 5)
        {
            WindowsTaskbarProgress.SetProgress(i, 100);
            yield return new WaitForSeconds(0.08f);
        }

        // faza 3: błąd — czerwony pasek
        WindowsTaskbarProgress.SetState(WindowsTaskbarProgress.TaskbarState.Error);
        yield return new WaitForSeconds(1.5f);

        // koniec — sprzątanie
        WindowsTaskbarProgress.Reset();
    }
}