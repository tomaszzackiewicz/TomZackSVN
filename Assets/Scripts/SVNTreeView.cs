using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using SVN.Core;

public class SvnTreeView : MonoBehaviour
{
    public GameObject linePrefab;
    public bool isCommitView;

    [SerializeField] private int itemsPerFrame = 4;

    private List<SvnLineController> _pool = new List<SvnLineController>();

    private int _currentVisibleCount = 0;

    private Coroutine _refreshCoroutine;

    public void RefreshUI(List<SvnTreeElement> elements, SVNStatus manager)
    {
        if (_refreshCoroutine != null)
        {
            StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = null;
        }

        for (int i = 0; i < _pool.Count; i++)
        {
            var ctrl = _pool[i];
            if (ctrl != null && ctrl.gameObject.activeSelf)
                ctrl.gameObject.SetActive(false);
        }
        _currentVisibleCount = 0;

        if (linePrefab == null)
        {
            SVNLogBridge.LogError("[SvnTreeView] linePrefab is not assigned in Inspector!");
            return;
        }

        if (elements == null) return;

        _refreshCoroutine = StartCoroutine(RefreshUIRoutine(elements, manager));
    }

    private IEnumerator RefreshUIRoutine(List<SvnTreeElement> elements, SVNStatus manager)
    {
        var elementsSnapshot = new List<SvnTreeElement>(elements);

        int processedInThisFrame = 0;
        int poolIndex = 0;
        int perFrame = Math.Max(1, itemsPerFrame);

        for (int i = 0; i < elementsSnapshot.Count; i++)
        {
            var element = elementsSnapshot[i];
            if (element == null || !element.IsVisible) continue;

            var controller = GetOrCreateControllerByIndex(poolIndex);
            if (controller == null)
            {
                _refreshCoroutine = null;
                yield break;
            }

            if (!controller.gameObject.activeSelf)
            {
                controller.gameObject.SetActive(true);
            }

            element.IsCommitDelegate = isCommitView;

            try
            {
                controller.Setup(element, manager);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError(
                    $"[SvnTreeView] Setup failed for '{element.FullPath}': {ex.Message}");
            }

            poolIndex++;
            processedInThisFrame++;

            if (processedInThisFrame >= perFrame)
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
            var ctrl = newObj != null ? newObj.GetComponent<SvnLineController>() : null;

            if (ctrl == null)
            {
                SVNLogBridge.LogError(
                    "[SvnTreeView] linePrefab has no SvnLineController component!");
                if (newObj != null) Destroy(newObj);
                return null;
            }

            _pool.Add(ctrl);
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