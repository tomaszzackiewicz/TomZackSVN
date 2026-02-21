using UnityEngine;
using System.Collections.Generic;
using SVN.Core;

public class SvnTreeView : MonoBehaviour
{
    public GameObject linePrefab;
    public bool isCommitView;

    private List<GameObject> _pool = new List<GameObject>();

    public void RefreshUI(List<SvnTreeElement> elements, SVNStatus manager)
    {
        foreach (var obj in _pool) obj.SetActive(false);

        int activeIndex = 0;
        foreach (var element in elements)
        {
            if (!element.IsVisible) continue;

            GameObject line = GetOrCreateLine();
            line.transform.SetSiblingIndex(activeIndex);
            line.SetActive(true);

            var controller = line.GetComponent<SvnLineController>();
            if (controller != null)
            {
                element.IsCommitDelegate = isCommitView;
                controller.Setup(element, manager);
            }
            activeIndex++;
        }
    }

    private GameObject GetOrCreateLine()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            if (!_pool[i].activeSelf) return _pool[i];
        }

        GameObject newObj = Instantiate(linePrefab, transform);
        _pool.Add(newObj);
        return newObj;
    }

    public void ClearView()
    {
        foreach (var obj in _pool)
        {
            if (obj != null) obj.SetActive(false);
        }
    }
}