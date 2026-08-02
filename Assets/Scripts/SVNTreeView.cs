using UnityEngine;
using UnityEngine.UI; // To jest wymagane dla VerticalLayoutGroup
using System.Collections.Generic;
using SVN.Core;

public class SvnTreeView : MonoBehaviour
{
    public GameObject linePrefab;
    public bool isCommitView;

    private List<GameObject> _pool = new List<GameObject>();
    private VerticalLayoutGroup _layoutGroup;

    private void Awake()
    {
        // Pobieramy komponent układu raz, przy starcie
        _layoutGroup = GetComponent<VerticalLayoutGroup>();
    }

    public void RefreshUI(List<SvnTreeElement> elements, SVNStatus manager)
    {
        // 1. ZABEZPIECZENIE PRZED ZAWIESZENIEM: Wyłączamy Layout Group
        if (_layoutGroup != null)
            _layoutGroup.enabled = false;

        // 2. Ukrywamy wszystko
        foreach (var obj in _pool)
        {
            if (obj.activeSelf) obj.SetActive(false);
        }

        // 3. Pokazujemy tylko to, co widoczne (pobierając z puli po kolei, BEZ SetSiblingIndex)
        int poolIndex = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (!element.IsVisible) continue;

            GameObject line = GetOrCreateLineByIndex(poolIndex);
            line.SetActive(true);

            var controller = line.GetComponent<SvnLineController>();
            if (controller != null)
            {
                element.IsCommitDelegate = isCommitView;
                controller.Setup(element, manager);
            }

            poolIndex++;
        }

        // 4. WŁĄCZAMY Layout Group z powrotem. 
        // Unity przeliczy pozycje RAZ, szybko i bez zacinania się.
        if (_layoutGroup != null)
            _layoutGroup.enabled = true;
    }

    private GameObject GetOrCreateLineByIndex(int index)
    {
        // Jeśli potrzebujemy więcej obiektów niż mamy w puli, tworzymy nowe
        while (index >= _pool.Count)
        {
            GameObject newObj = Instantiate(linePrefab, transform);
            newObj.SetActive(false);
            _pool.Add(newObj);
        }

        return _pool[index];
    }

    public void ClearView()
    {
        // Tutaj też warto zabezpieczyć czyszczenie
        if (_layoutGroup != null) _layoutGroup.enabled = false;

        foreach (var obj in _pool)
        {
            if (obj != null && obj.activeSelf)
                obj.SetActive(false);
        }

        if (_layoutGroup != null) _layoutGroup.enabled = true;
    }

    public void FilterTree(string filterText)
    {
        foreach (var lineController in GetComponentsInChildren<SvnLineController>(true))
        {
            lineController.ApplyFilter(filterText);
        }
    }
}