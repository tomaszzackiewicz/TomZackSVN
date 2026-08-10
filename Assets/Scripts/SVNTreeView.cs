using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using SVN.Core;

public class SvnTreeView : MonoBehaviour
{
    public GameObject linePrefab;
    public bool isCommitView;

    private List<SvnLineController> _pool = new List<SvnLineController>();
    private VerticalLayoutGroup _layoutGroup;

    private Coroutine _refreshCoroutine;

    private void Awake()
    {
        _layoutGroup = GetComponent<VerticalLayoutGroup>();
    }

    public void RefreshUI(List<SvnTreeElement> elements, SVNStatus manager)
    {
        if (_refreshCoroutine != null)
        {
            StopCoroutine(_refreshCoroutine);
        }

        _refreshCoroutine = StartCoroutine(RefreshUIRoutine(elements, manager));
    }

    private IEnumerator RefreshUIRoutine(List<SvnTreeElement> elements, SVNStatus manager)
    {
        if (_layoutGroup != null)
            _layoutGroup.enabled = false;

        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();

        const long maxMsPerFrame = 10;

        int poolIndex = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (!element.IsVisible) continue;

            var controller = GetOrCreateControllerByIndex(poolIndex);

            if (!controller.gameObject.activeSelf)
            {
                controller.gameObject.SetActive(true);
            }

            element.IsCommitDelegate = isCommitView;
            controller.Setup(element, manager);

            poolIndex++;

            if (stopwatch.ElapsedMilliseconds >= maxMsPerFrame)
            {
                yield return null;
                stopwatch.Restart();
            }
        }

        for (int i = poolIndex; i < _pool.Count; i++)
        {
            var ctrl = _pool[i];
            if (ctrl != null && ctrl.gameObject.activeSelf)
            {
                ctrl.gameObject.SetActive(false);
            }

            if (stopwatch.ElapsedMilliseconds >= maxMsPerFrame)
            {
                yield return null;
                stopwatch.Restart();
            }
        }

        if (_layoutGroup != null)
            _layoutGroup.enabled = true;

        _refreshCoroutine = null;
    }

    private SvnLineController GetOrCreateControllerByIndex(int index)
    {
        while (index >= _pool.Count)
        {
            GameObject newObj = Instantiate(linePrefab, transform);
            _pool.Add(newObj.GetComponent<SvnLineController>());
        }

        return _pool[index];
    }

    public void ClearView()
    {
        if (_refreshCoroutine != null)
        {
            StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = null;
        }

        if (_layoutGroup != null) _layoutGroup.enabled = false;

        foreach (var ctrl in _pool)
        {
            if (ctrl != null && ctrl.gameObject.activeSelf)
                ctrl.gameObject.SetActive(false);
        }

        if (_layoutGroup != null) _layoutGroup.enabled = true;
    }

    public void FilterTree(string filterText)
    {
        foreach (var ctrl in _pool)
        {
            if (ctrl != null && ctrl.gameObject.activeSelf)
                ctrl.ApplyFilter(filterText);
        }
    }
}