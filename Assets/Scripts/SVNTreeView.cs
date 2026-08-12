using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using SVN.Core;

public class SvnTreeView : MonoBehaviour
{
    public GameObject linePrefab;
    public bool isCommitView;

    private List<SvnLineController> _pool = new List<SvnLineController>();
    private VerticalLayoutGroup _layoutGroup;

    private int _currentVisibleCount = 0;

    private Coroutine _refreshCoroutine;

    private const int ItemsPerFrame = 4;

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
        var elementsSnapshot = new List<SvnTreeElement>(elements);

        int processedInThisFrame = 0;
        int poolIndex = 0;

        for (int i = 0; i < elementsSnapshot.Count; i++)
        {
            var element = elementsSnapshot[i];
            if (!element.IsVisible) continue;

            var controller = GetOrCreateControllerByIndex(poolIndex);

            if (!controller.gameObject.activeSelf)
            {
                controller.gameObject.SetActive(true);
            }

            element.IsCommitDelegate = isCommitView;
            controller.Setup(element, manager);

            poolIndex++;
            processedInThisFrame++;

            if (processedInThisFrame >= ItemsPerFrame)
            {
                processedInThisFrame = 0;
                yield return null;
            }
        }

        _currentVisibleCount = poolIndex;

        for (int i = poolIndex; i < _pool.Count; i++)
        {
            var ctrl = _pool[i];
            if (ctrl != null && ctrl.gameObject.activeSelf)
            {
                ctrl.gameObject.SetActive(false);
            }
        }

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

        _currentVisibleCount = 0;

        foreach (var ctrl in _pool)
        {
            if (ctrl != null && ctrl.gameObject.activeSelf)
                ctrl.gameObject.SetActive(false);
        }
    }

    public void FilterTree(string filterText)
    {
        int count = Mathf.Min(_currentVisibleCount, _pool.Count);

        for (int i = 0; i < count; i++)
        {
            if (_pool[i] != null)
            {
                _pool[i].ApplyFilter(filterText);
            }
        }
    }
}